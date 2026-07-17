using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Data;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Server;

/// <summary>
/// Glue between the network transport and the authoritative <see cref="World.World"/>. It owns the
/// per-peer session state, runs the handshake, validates and dispatches inbound messages, and each
/// tick broadcasts area-of-interest snapshots and combat events to the relevant players.
///
/// Everything here runs on the single simulation thread. Inbound bytes are treated as hostile:
/// malformed packets are dropped, never trusted, never allowed to throw past the dispatch loop.
/// </summary>
public sealed class GameServer
{
    private readonly IServerTransport _transport;
    private readonly World.World _world;
    private readonly Dictionary<PeerId, PlayerSession> _sessions = new();
    private readonly PacketWriter _writer = new();
    private readonly Action<string> _log;

    public GameServer(IServerTransport transport, GameData? gameData = null, Action<string>? log = null)
    {
        _transport = transport;
        _world = new World.World(gameData);
        _log = log ?? (_ => { });

        SpawnStartingMonsters();
    }

    public World.World World => _world;

    public int PlayerCount => _sessions.Count(s => s.Value.HandshakeComplete);

    /// <summary>Drain and handle all pending transport events. Call once at the top of each tick.</summary>
    public void ProcessNetwork()
    {
        while (_transport.Poll(out ServerTransportEvent evt))
        {
            switch (evt.Kind)
            {
                case TransportEventKind.PeerConnected:
                    _sessions[evt.Peer] = new PlayerSession();
                    break;

                case TransportEventKind.PacketReceived:
                    HandlePacket(evt.Peer, evt.Payload);
                    break;

                case TransportEventKind.PeerDisconnected:
                    HandleDisconnect(evt.Peer);
                    break;
            }
        }
    }

    /// <summary>Advance the world, then send snapshots and combat events to the relevant players.</summary>
    public void Tick(float dt)
    {
        _world.Step(dt);
        BroadcastSnapshots();
        BroadcastCombatEvents(_world.DrainCombatEvents());

        // Self status (progression + inventory) changes rarely; send it a few times a second.
        if (_world.Tick % 10 == 0)
        {
            BroadcastPlayerState();
        }
    }

    private void SpawnStartingMonsters()
    {
        // A small starter pack near the origin so a freshly connected player can find a fight.
        _world.SpawnMonster(monsterId: 1, new Vec2(10f, 6f));   // Goblin Grunt
        _world.SpawnMonster(monsterId: 1, new Vec2(12f, 9f));   // Goblin Grunt
        _world.SpawnMonster(monsterId: 2, new Vec2(-8f, 10f));  // Dire Wolf
    }

    private void HandlePacket(PeerId peer, byte[] payload)
    {
        if (payload.Length == 0 || !_sessions.TryGetValue(peer, out PlayerSession? session))
        {
            return;
        }

        try
        {
            var reader = new PacketReader(payload);
            var type = (MessageType)reader.ReadByte();

            if (!session.HandshakeComplete)
            {
                if (type == MessageType.ConnectRequest)
                {
                    HandleConnectRequest(peer, session, ref reader);
                }

                return; // Nothing else is valid before the handshake completes.
            }

            switch (type)
            {
                case MessageType.InputCommand:
                    InputCommand input = InputCommand.Read(ref reader);
                    _world.ApplyInput(session.EntityId, input.Sequence, input.MoveDirection);
                    break;

                case MessageType.UseAbility:
                    UseAbility ability = UseAbility.Read(ref reader);
                    _world.TryUseAbility(session.EntityId, ability.AbilityId, ability.TargetEntityId);
                    break;

                case MessageType.UseRacial:
                    _ = UseRacial.Read(ref reader);
                    _world.TryUseRacial(session.EntityId);
                    break;

                case MessageType.LootCorpse:
                    LootCorpse loot = LootCorpse.Read(ref reader);
                    _world.TryLootCorpse(session.EntityId, loot.CorpseEntityId);
                    break;

                case MessageType.Ping:
                    Ping ping = Ping.Read(ref reader);
                    Send(peer, new Pong(ping.ClientTimeMs, Environment.TickCount64));
                    break;

                case MessageType.Disconnect:
                    _transport.Kick(peer);
                    break;

                default:
                    break; // Unknown or wrong-direction message — ignore.
            }
        }
        catch (MalformedPacketException)
        {
            // A client sent garbage. Drop the packet; do not disturb the simulation.
        }
    }

    private void HandleConnectRequest(PeerId peer, PlayerSession session, ref PacketReader reader)
    {
        ConnectRequest request = ConnectRequest.Read(ref reader);

        if (request.ProtocolVersion != SimulationConstants.ProtocolVersion)
        {
            Send(peer, new ConnectRejected(
                $"Protocol mismatch: server v{SimulationConstants.ProtocolVersion}, client v{request.ProtocolVersion}."));
            _transport.Kick(peer);
            return;
        }

        // Enforce the balance matrix: a race may only play its allowed classes.
        if (!_world.GameData.IsClassAllowedForRace(request.RaceId, request.ClassId))
        {
            string raceName = _world.GameData.GetRace(request.RaceId).Name;
            string className = _world.GameData.GetClass(request.ClassId).Name;
            Send(peer, new ConnectRejected($"{raceName} cannot play the {className} class."));
            _transport.Kick(peer);
            return;
        }

        ServerEntity entity = _world.SpawnPlayer(peer, request.Name, request.RaceId, request.ClassId, request.Gender);
        _world.GrantStarterKit(entity);
        session.EntityId = entity.Id;
        session.Name = entity.Name;
        session.HandshakeComplete = true;

        Send(peer, new ConnectAccepted(entity.Id, (byte)SimulationConstants.TickRate));

        string race = _world.GameData.GetRace(entity.RaceId).Name;
        string cls = _world.GameData.GetClass(entity.ClassId).Name;
        _log($"'{session.Name}' joined as {entity.Faction} {race} {cls} ({entity.Gender}, " +
             $"entity {entity.Id}, {peer}). Players online: {PlayerCount}.");
    }

    private void HandleDisconnect(PeerId peer)
    {
        if (_sessions.Remove(peer, out PlayerSession? session) && session.HandshakeComplete)
        {
            _world.Despawn(session.EntityId);
            _log($"'{session.Name}' left (entity {session.EntityId}). Players online: {PlayerCount}.");
        }
    }

    private void BroadcastSnapshots()
    {
        foreach ((PeerId peer, PlayerSession session) in _sessions)
        {
            if (!session.HandshakeComplete ||
                !_world.Entities.TryGetValue(session.EntityId, out ServerEntity? self))
            {
                continue;
            }

            // A dead player still gets snapshots centred on where they fell, so they can watch the
            // world (and their killer) while waiting to respawn.
            List<EntitySnapshot> visible = _world.BuildAreaSnapshot(self.Position);
            Send(peer, new SnapshotMessage(_world.Tick, visible));
        }
    }

    private void BroadcastCombatEvents(IReadOnlyList<CombatEventMessage> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        float aoiSq = SimulationConstants.AreaOfInterestRadius * SimulationConstants.AreaOfInterestRadius;

        foreach (CombatEventMessage evt in events)
        {
            bool haveAttacker = _world.Entities.TryGetValue(evt.AttackerId, out ServerEntity? attacker);
            bool haveTarget = _world.Entities.TryGetValue(evt.TargetId, out ServerEntity? target);

            foreach ((PeerId peer, PlayerSession session) in _sessions)
            {
                if (!session.HandshakeComplete ||
                    !_world.Entities.TryGetValue(session.EntityId, out ServerEntity? self))
                {
                    continue;
                }

                bool near =
                    (haveAttacker && Vec2.DistanceSquared(self.Position, attacker!.Position) <= aoiSq) ||
                    (haveTarget && Vec2.DistanceSquared(self.Position, target!.Position) <= aoiSq);

                if (near)
                {
                    Send(peer, evt);
                }
            }
        }
    }

    private void BroadcastPlayerState()
    {
        ProgressionConfig progression = _world.GameData.Progression;

        foreach ((PeerId peer, PlayerSession session) in _sessions)
        {
            if (!session.HandshakeComplete ||
                !_world.Entities.TryGetValue(session.EntityId, out ServerEntity? self))
            {
                continue;
            }

            Send(peer, new PlayerStatus(
                self.Level, self.TotalXp, progression.XpForNextLevel(self.TotalXp), self.Inventory.Gold));
            Send(peer, new InventoryState(self.EquippedWeaponId, self.EquippedArmorId, self.Inventory.Stacks));
        }
    }

    private void Send(PeerId peer, ConnectAccepted msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, ConnectRejected msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, Pong msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, SnapshotMessage msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, CombatEventMessage msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, PlayerStatus msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, InventoryState msg) => SendWith(peer, msg.Write);

    private void SendWith(PeerId peer, Action<PacketWriter> write)
    {
        _writer.Reset();
        write(_writer);
        _transport.Send(peer, _writer.WrittenSpan);
    }

    private sealed class PlayerSession
    {
        public int EntityId { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool HandshakeComplete { get; set; }
    }
}
