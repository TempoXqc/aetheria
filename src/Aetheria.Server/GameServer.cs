using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Server;

/// <summary>
/// Glue between the network transport and the authoritative <see cref="World.World"/>. It owns the
/// per-peer session state, runs the handshake, validates and dispatches inbound messages, and each
/// tick broadcasts an area-of-interest snapshot to every connected player.
///
/// Everything here runs on the single simulation thread. Inbound bytes are treated as hostile:
/// malformed packets are dropped, never trusted, never allowed to throw past the dispatch loop.
/// </summary>
public sealed class GameServer
{
    private readonly IServerTransport _transport;
    private readonly World.World _world = new();
    private readonly Dictionary<PeerId, PlayerSession> _sessions = new();
    private readonly PacketWriter _writer = new();
    private readonly Action<string> _log;

    public GameServer(IServerTransport transport, Action<string>? log = null)
    {
        _transport = transport;
        _log = log ?? (_ => { });
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

    /// <summary>Advance the world one step, then send each player their personalized snapshot.</summary>
    public void Tick(float dt)
    {
        _world.Step(dt);
        BroadcastSnapshots();
    }

    private void HandlePacket(PeerId peer, byte[] payload)
    {
        if (payload.Length == 0)
        {
            return;
        }

        if (!_sessions.TryGetValue(peer, out PlayerSession? session))
        {
            // Packet from an endpoint we have no session for (e.g. after a kick). Ignore.
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

                case MessageType.Ping:
                    Ping ping = Ping.Read(ref reader);
                    Send(peer, new Pong(ping.ClientTimeMs, Environment.TickCount64));
                    break;

                case MessageType.Disconnect:
                    _transport.Kick(peer);
                    break;

                default:
                    // Unknown or wrong-direction message for a handshaked peer — ignore.
                    break;
            }
        }
        catch (MalformedPacketException)
        {
            // A client sent us garbage. Drop the packet; do not disturb the simulation.
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

        ServerEntity entity = _world.SpawnPlayer(peer);
        session.EntityId = entity.Id;
        session.Name = string.IsNullOrWhiteSpace(request.Name) ? $"Player{entity.Id}" : request.Name;
        session.HandshakeComplete = true;

        Send(peer, new ConnectAccepted(entity.Id, (byte)SimulationConstants.TickRate));
        _log($"'{session.Name}' joined as entity {entity.Id} ({peer}). Players online: {PlayerCount}.");
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

            List<EntitySnapshot> visible = _world.BuildAreaSnapshot(self.Position);
            Send(peer, new SnapshotMessage(_world.Tick, visible));
        }
    }

    private void Send(PeerId peer, ConnectAccepted msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, ConnectRejected msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, Pong msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, SnapshotMessage msg) => SendWith(peer, msg.Write);

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
