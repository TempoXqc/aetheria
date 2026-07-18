using System.Security.Cryptography;
using System.Text;
using Aetheria.Server.Items;
using Aetheria.Server.Persistence;
using Aetheria.Server.Social;
using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Data;
using Aetheria.Shared.Items;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Server;

/// <summary>
/// Glue between the network transport and the simulation. It owns per-peer session state, runs the
/// handshake, validates and dispatches inbound messages, and each tick advances every running world
/// (the shared open world plus per-group instances) and broadcasts area-of-interest snapshots and
/// combat events to the players in each world.
///
/// Everything here runs on the single simulation thread. Inbound bytes are treated as hostile:
/// malformed packets are dropped, never trusted, never allowed to throw past the dispatch loop.
/// </summary>
public sealed class GameServer
{
    private readonly IServerTransport _transport;
    private readonly WorldManager _worlds;
    private readonly PartyManager _parties = new(SimulationConstants.MaxPartySize);
    private readonly Dictionary<PeerId, PlayerSession> _sessions = new();
    private readonly PacketWriter _writer = new();
    private readonly Action<string> _log;

    // Character names are unique server-wide (across both factions). Case-insensitive.
    // NOTE: this is uniqueness among currently-active characters; durable uniqueness arrives with
    // persistence (M4), where names are reserved in the database.
    private readonly HashSet<string> _activeNames = new(StringComparer.OrdinalIgnoreCase);

    // Live account banks, keyed by account id, hydrated from durable state at boot.
    private readonly Dictionary<string, Inventory> _banks = new(StringComparer.OrdinalIgnoreCase);

    // Durable state (accounts, secrets, banks, characters, the server-wide name registry).
    private readonly IPersistenceStore _store;
    private readonly ServerState _state;
    private const int SaveIntervalTicks = SimulationConstants.TickRate * 5; // flush every ~5s

    public GameServer(
        IServerTransport transport, GameData? gameData = null, Action<string>? log = null,
        IPersistenceStore? store = null)
    {
        _transport = transport;
        _worlds = new WorldManager(gameData);
        _log = log ?? (_ => { });
        _store = store ?? new InMemoryPersistenceStore();
        _state = _store.Load();

        // Hydrate live banks from durable records.
        foreach (AccountRecord account in _state.Accounts.Values)
        {
            _banks[account.AccountId] =
                CharacterMapper.RestoreBank(account, SimulationConstants.BankCapacity, _worlds.GameData);
        }

        if (_state.Accounts.Count > 0)
        {
            _log($"Persistence: loaded {_state.Accounts.Count} account(s), {_state.Names.Count} reserved name(s).");
        }

        SpawnOpenWorldContent();
    }

    /// <summary>The shared open world (kept as `World` for compatibility and tests).</summary>
    public World.World World => _worlds.OpenWorld;

    public WorldManager Worlds => _worlds;

    public int PlayerCount => _sessions.Count(s => s.Value.HandshakeComplete);

    /// <summary>Drain and handle all pending transport events. Call once at the top of each tick.</summary>
    public void ProcessNetwork()
    {
        while (_transport.Poll(out ServerTransportEvent evt))
        {
            switch (evt.Kind)
            {
                case TransportEventKind.PeerConnected:
                    _sessions[evt.Peer] = new PlayerSession(_worlds.OpenWorld);
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

    /// <summary>Advance every world, then send snapshots and combat events per world.</summary>
    public void Tick(float dt)
    {
        foreach (World.World world in _worlds.AllWorlds.ToArray())
        {
            world.Step(dt);
        }

        BroadcastSnapshots();

        bool playerDied = false;
        foreach (World.World world in _worlds.AllWorlds.ToArray())
        {
            IReadOnlyList<CombatEventMessage> events = world.DrainCombatEvents();
            BroadcastCombatEvents(world, events);

            foreach (CombatEventMessage evt in events)
            {
                if (evt.TargetKilled && FindSessionByEntity(evt.TargetId) is not null)
                {
                    playerDied = true; // a player death (permadeath!) must hit disk immediately
                }
            }
        }

        if (playerDied)
        {
            PersistAll();
        }

        // Self status (progression + inventory) changes rarely; send it a few times a second.
        if (_worlds.OpenWorld.Tick % 10 == 0)
        {
            BroadcastPlayerState();
        }

        // Periodic durability flush: capture every online character + bank and save.
        if (_worlds.OpenWorld.Tick % SaveIntervalTicks == 0)
        {
            PersistAll();
        }
    }

    private void SpawnOpenWorldContent()
    {
        World.World open = _worlds.OpenWorld;

        // Starter camp near the origin so a freshly connected player can find a fight.
        open.SpawnMonster(monsterId: 1, new Vec2(10f, 6f));
        open.SpawnMonster(monsterId: 1, new Vec2(12f, 9f));
        open.SpawnMonster(monsterId: 2, new Vec2(-8f, 10f));

        // Open-world DUNGEON: a non-instanced elite camp. It lives in the shared world, so rival
        // factions can meet — and fight — over it.
        open.SpawnMonster(monsterId: 1, new Vec2(38f, 38f));
        open.SpawnMonster(monsterId: 1, new Vec2(42f, 40f));
        open.SpawnMonster(monsterId: 2, new Vec2(40f, 44f));
        open.SpawnMonster(monsterId: 3, new Vec2(45f, 45f)); // Goblin King (dungeon boss, contested)

        // WORLD RAID BOSS: raid-difficulty, in the open world, never instanced — PvP possible.
        open.SpawnMonster(monsterId: 4, new Vec2(80f, 80f)); // Ashmaw the Devourer
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

            World.World world = session.CurrentWorld;

            switch (type)
            {
                case MessageType.InputCommand:
                    InputCommand input = InputCommand.Read(ref reader);
                    world.ApplyInput(session.EntityId, input.Sequence, input.MoveDirection, input.FacingRadians);
                    break;

                case MessageType.UseAbility:
                    UseAbility ability = UseAbility.Read(ref reader);
                    world.TryUseAbility(session.EntityId, ability.AbilityId, ability.TargetEntityId);
                    break;

                case MessageType.UseRacial:
                    _ = UseRacial.Read(ref reader);
                    world.TryUseRacial(session.EntityId);
                    break;

                case MessageType.LootCorpse:
                    LootCorpse loot = LootCorpse.Read(ref reader);
                    world.TryLootCorpse(session.EntityId, loot.CorpseEntityId);
                    SendCorpseContents(peer, session, loot.CorpseEntityId); // now empty → client closes
                    SendSelfState(peer, session);
                    break;

                case MessageType.OpenCorpse:
                    OpenCorpse open = OpenCorpse.Read(ref reader);
                    SendCorpseContents(peer, session, open.CorpseEntityId);
                    break;

                case MessageType.LootItem:
                    LootItem lootItem = LootItem.Read(ref reader);
                    world.TryLootItem(session.EntityId, lootItem.CorpseEntityId, lootItem.ItemId);
                    SendCorpseContents(peer, session, lootItem.CorpseEntityId);
                    SendSelfState(peer, session);
                    break;

                case MessageType.BankTransaction:
                    BankTransaction tx = BankTransaction.Read(ref reader);
                    HandleBankTransaction(peer, session, tx);
                    break;

                case MessageType.PartyInvite:
                    PartyInvite invite = PartyInvite.Read(ref reader);
                    HandlePartyInvite(peer, session, invite.TargetEntityId);
                    break;

                case MessageType.PartyRespond:
                    PartyRespond respond = PartyRespond.Read(ref reader);
                    HandlePartyRespond(peer, respond.Accept);
                    break;

                case MessageType.PartyLeave:
                    _ = PartyLeave.Read(ref reader);
                    HandlePartyLeave(peer);
                    break;

                case MessageType.EnterInstance:
                    EnterInstance enter = EnterInstance.Read(ref reader);
                    HandleEnterInstance(peer, session, enter.InstanceDefId);
                    break;

                case MessageType.LeaveInstance:
                    _ = LeaveInstance.Read(ref reader);
                    HandleLeaveInstance(peer, session, notify: true);
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
        GameData data = _worlds.GameData;

        if (request.ProtocolVersion != SimulationConstants.ProtocolVersion)
        {
            Send(peer, new ConnectRejected(
                $"Protocol mismatch: server v{SimulationConstants.ProtocolVersion}, client v{request.ProtocolVersion}."));
            _transport.Kick(peer);
            return;
        }

        // Enforce the balance matrix: a race may only play its allowed classes.
        if (!data.IsClassAllowedForRace(request.RaceId, request.ClassId))
        {
            Send(peer, new ConnectRejected(
                $"{data.GetRace(request.RaceId).Name} cannot play the {data.GetClass(request.ClassId).Name} class."));
            _transport.Kick(peer);
            return;
        }

        string accountId = (request.AccountId ?? string.Empty).Trim();
        if (accountId.Length is < 1 or > 32)
        {
            Send(peer, new ConnectRejected("A valid account id (1-32 characters) is required."));
            _transport.Kick(peer);
            return;
        }

        // Account auth: the first connect sets the secret; later connects must match it.
        AccountRecord account = GetOrCreateAccount(accountId);
        string secretHash = HashSecret(request.AccountSecret);
        if (string.IsNullOrEmpty(account.SecretHash))
        {
            account.SecretHash = secretHash;
        }
        else if (!string.Equals(account.SecretHash, secretHash, StringComparison.Ordinal))
        {
            Send(peer, new ConnectRejected("Wrong account secret."));
            _transport.Kick(peer);
            return;
        }

        // Character name: well-formed, not currently online, and durably owned (server-wide,
        // across both factions; a name reserved by another account stays taken even offline).
        if (!TryClaimName(request.Name, accountId, out string name, out string nameError))
        {
            Send(peer, new ConnectRejected(nameError));
            _transport.Kick(peer);
            return;
        }

        ServerEntity entity = _worlds.OpenWorld.SpawnPlayer(peer, name, request.RaceId, request.ClassId, request.Gender);

        // Returning character: restore its saved progression/inventory. New one: starter kit.
        if (account.Characters.TryGetValue(name.ToLowerInvariant(), out CharacterRecord? saved))
        {
            CharacterMapper.Restore(_worlds.OpenWorld, entity, saved);
        }
        else
        {
            _worlds.OpenWorld.GrantStarterKit(entity);
            account.Characters[name.ToLowerInvariant()] = CharacterMapper.Capture(entity);
        }

        session.EntityId = entity.Id;
        session.Name = entity.Name;
        session.AccountId = accountId;
        session.CurrentWorld = _worlds.OpenWorld;
        session.HandshakeComplete = true;

        Send(peer, new ConnectAccepted(entity.Id, (byte)SimulationConstants.TickRate));
        SendBankState(peer, accountId); // the account bank survives permadeath and is shown on join

        _log($"'{session.Name}' joined as {entity.Faction} {data.GetRace(entity.RaceId).Name} " +
             $"{data.GetClass(entity.ClassId).Name} ({entity.Gender}, entity {entity.Id}, {peer}, " +
             $"{(saved is null ? "new" : "restored")}). Players online: {PlayerCount}.");
    }

    private AccountRecord GetOrCreateAccount(string accountId)
    {
        string key = accountId.ToLowerInvariant();
        if (!_state.Accounts.TryGetValue(key, out AccountRecord? account))
        {
            account = new AccountRecord { AccountId = accountId };
            _state.Accounts[key] = account;
        }

        return account;
    }

    private static string HashSecret(string? secret)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret ?? string.Empty)));

    private void HandleDisconnect(PeerId peer)
    {
        if (!_sessions.Remove(peer, out PlayerSession? session) || !session.HandshakeComplete)
        {
            return;
        }

        Party? left = _parties.Leave(peer.Value);
        if (left is not null)
        {
            BroadcastPartyState(left);
        }

        // Persist the departing character before it despawns, then flush everything.
        if (TryGetEntity(session, out ServerEntity? departing))
        {
            AccountRecord account = GetOrCreateAccount(session.AccountId);
            account.Characters[session.Name.ToLowerInvariant()] = CharacterMapper.Capture(departing!);
        }

        _activeNames.Remove(session.Name); // no longer online (the durable name claim remains)
        session.CurrentWorld.Despawn(session.EntityId);
        _worlds.DestroyInstanceIfEmpty(session.CurrentWorld);
        PersistAll();
        _log($"'{session.Name}' left (entity {session.EntityId}). Players online: {PlayerCount}.");
    }

    // ------------------------------------------------------------------ Party

    private void HandlePartyInvite(PeerId inviterPeer, PlayerSession inviterSession, int targetEntityId)
    {
        (PeerId targetPeer, PlayerSession targetSession)? target = FindSessionByEntity(targetEntityId);
        if (target is null)
        {
            return;
        }

        // Same-camp only: parties cannot span factions.
        if (!TryGetEntity(inviterSession, out ServerEntity? inviter) ||
            !TryGetEntity(target.Value.targetSession, out ServerEntity? invitee) ||
            inviter!.Faction != invitee!.Faction)
        {
            return;
        }

        if (_parties.Invite(inviterPeer.Value, target.Value.targetPeer.Value, out _))
        {
            Send(target.Value.targetPeer, new PartyInviteNotice(inviterSession.Name));
        }
    }

    private void HandlePartyRespond(PeerId peer, bool accept)
    {
        if (!accept)
        {
            _parties.Decline(peer.Value);
            return;
        }

        Party? party = _parties.Accept(peer.Value);
        if (party is not null)
        {
            BroadcastPartyState(party);
        }
    }

    private void HandlePartyLeave(PeerId peer)
    {
        Party? party = _parties.Leave(peer.Value);
        if (party is not null)
        {
            BroadcastPartyState(party);
            SendPartyStateTo(peer); // the leaver sees an empty roster
        }
    }

    /// <summary>Send the roster to every current member of the party (leader first).</summary>
    private void BroadcastPartyState(Party party)
    {
        foreach (int member in party.Members.ToArray())
        {
            SendPartyStateTo(new PeerId(member));
        }
    }

    private void SendPartyStateTo(PeerId peer)
    {
        if (!_sessions.ContainsKey(peer))
        {
            return;
        }

        Party? party = _parties.GetParty(peer.Value);
        if (party is null)
        {
            Send(peer, new PartyState(string.Empty, []));
            return;
        }

        string leaderName = _sessions.TryGetValue(new PeerId(party.Leader), out PlayerSession? ls)
            ? ls.Name : "?";
        var names = new List<string>(party.Count);
        foreach (int member in party.Members)
        {
            if (_sessions.TryGetValue(new PeerId(member), out PlayerSession? ms))
            {
                names.Add(ms.Name);
            }
        }

        Send(peer, new PartyState(leaderName, names));
    }

    // -------------------------------------------------------------- Instances

    private void HandleEnterInstance(PeerId peer, PlayerSession session, byte instanceDefId)
    {
        if (!_worlds.GameData.TryGetInstance(instanceDefId, out InstanceDefinition def))
        {
            Send(peer, new InstanceResult(false, instanceDefId, "Unknown instance."));
            return;
        }

        if (!ReferenceEquals(session.CurrentWorld, _worlds.OpenWorld))
        {
            Send(peer, new InstanceResult(false, instanceDefId, "Leave your current instance first."));
            return;
        }

        // The entering group: the sender's whole party (leader-triggered), or just the sender solo.
        Party? party = _parties.GetParty(peer.Value);
        List<(PeerId peerId, PlayerSession session)> group = new();
        if (party is null)
        {
            group.Add((peer, session));
        }
        else
        {
            if (party.Leader != peer.Value)
            {
                Send(peer, new InstanceResult(false, instanceDefId, "Only the party leader can start an instance."));
                return;
            }

            foreach (int member in party.Members)
            {
                var memberPeer = new PeerId(member);
                if (_sessions.TryGetValue(memberPeer, out PlayerSession? memberSession) &&
                    memberSession.HandshakeComplete &&
                    ReferenceEquals(memberSession.CurrentWorld, _worlds.OpenWorld))
                {
                    group.Add((memberPeer, memberSession));
                }
            }
        }

        if (!WorldManager.CanEnter(def, group.Count, out string reason))
        {
            Send(peer, new InstanceResult(false, instanceDefId, reason));
            return;
        }

        World.World instance = _worlds.CreateInstance(def, group.Count);

        int slot = 0;
        foreach ((PeerId memberPeer, PlayerSession memberSession) in group)
        {
            Vec2 entry = new(slot * 2f, 0f); // spread the group across the entrance
            slot++;
            if (WorldManager.TransferPlayer(memberSession.CurrentWorld, instance, memberSession.EntityId, entry))
            {
                memberSession.CurrentWorld = instance;
                Send(memberPeer, new InstanceResult(true, instanceDefId,
                    $"Entered {def.Name} ({group.Count} player(s); monsters scaled accordingly)."));
            }
        }

        _log($"Instance '{def.Name}' created for {group.Count} player(s).");
    }

    private void HandleLeaveInstance(PeerId peer, PlayerSession session, bool notify)
    {
        if (ReferenceEquals(session.CurrentWorld, _worlds.OpenWorld))
        {
            if (notify)
            {
                Send(peer, new InstanceResult(false, 0, "You are not in an instance."));
            }

            return;
        }

        World.World instance = session.CurrentWorld;
        if (WorldManager.TransferPlayer(instance, _worlds.OpenWorld, session.EntityId, Vec2.Zero))
        {
            session.CurrentWorld = _worlds.OpenWorld;
            _worlds.DestroyInstanceIfEmpty(instance);
            if (notify)
            {
                Send(peer, new InstanceResult(true, 0, "Returned to the open world."));
            }
        }
    }

    // ------------------------------------------------------------------ Bank

    private void HandleBankTransaction(PeerId peer, PlayerSession session, BankTransaction tx)
    {
        if (!TryGetEntity(session, out ServerEntity? self) || self!.IsDead)
        {
            return;
        }

        Inventory bank = GetOrCreateBank(session.AccountId);
        Inventory inv = self.Inventory;
        GameData data = _worlds.GameData;

        switch (tx.Op)
        {
            case BankOp.DepositGold: BankService.DepositGold(inv, bank, tx.Amount); break;
            case BankOp.WithdrawGold: BankService.WithdrawGold(inv, bank, tx.Amount); break;
            case BankOp.DepositItem: BankService.DepositItem(inv, bank, tx.ItemId, tx.Amount, data); break;
            case BankOp.WithdrawItem: BankService.WithdrawItem(inv, bank, tx.ItemId, tx.Amount, data); break;
            default: return;
        }

        SendBankState(peer, session.AccountId);
        Send(peer, new InventoryState(self.EquippedWeaponId, self.EquippedArmorId, self.Inventory.Stacks));
    }

    private Inventory GetOrCreateBank(string accountId)
    {
        if (!_banks.TryGetValue(accountId, out Inventory? bank))
        {
            bank = new Inventory(SimulationConstants.BankCapacity);
            _banks[accountId] = bank;
        }

        return bank;
    }

    private void SendBankState(PeerId peer, string accountId)
    {
        Inventory bank = GetOrCreateBank(accountId);
        Send(peer, new BankState(bank.Gold, bank.Stacks));
    }

    /// <summary>Send a corpse's current contents (empty contents = spent; the client closes its window).</summary>
    private void SendCorpseContents(PeerId peer, PlayerSession session, int corpseId)
    {
        if (session.CurrentWorld.TryOpenCorpse(session.EntityId, corpseId, out int gold, out var items))
        {
            Send(peer, new CorpseContentsMessage(corpseId, gold, items));
        }
        else
        {
            Send(peer, new CorpseContentsMessage(corpseId, 0, []));
        }
    }

    /// <summary>Push the player's own status + inventory right away (after loot/bank changes).</summary>
    private void SendSelfState(PeerId peer, PlayerSession session)
    {
        if (!TryGetEntity(session, out ServerEntity? self))
        {
            return;
        }

        ProgressionConfig progression = _worlds.GameData.Progression;
        Send(peer, new PlayerStatus(
            self!.Level, self.TotalXp, progression.XpForNextLevel(self.TotalXp), self.Inventory.Gold,
            self.EffectiveAttackPower, self.EffectiveDefense));
        Send(peer, new InventoryState(self.EquippedWeaponId, self.EquippedArmorId, self.Inventory.Stacks));
    }

    // ------------------------------------------------------------- Broadcast

    private void BroadcastSnapshots()
    {
        foreach ((PeerId peer, PlayerSession session) in _sessions)
        {
            if (!session.HandshakeComplete || !TryGetEntity(session, out ServerEntity? self))
            {
                continue;
            }

            // A dead player still gets snapshots centred on where they fell, so they can watch the
            // world (and their killer) while waiting to respawn.
            List<EntitySnapshot> visible = session.CurrentWorld.BuildAreaSnapshot(self!.Position);
            Send(peer, new SnapshotMessage(session.CurrentWorld.Tick, visible));
        }
    }

    private void BroadcastCombatEvents(World.World world, IReadOnlyList<CombatEventMessage> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        float aoiSq = SimulationConstants.AreaOfInterestRadius * SimulationConstants.AreaOfInterestRadius;

        foreach (CombatEventMessage evt in events)
        {
            bool haveAttacker = world.Entities.TryGetValue(evt.AttackerId, out ServerEntity? attacker);
            bool haveTarget = world.Entities.TryGetValue(evt.TargetId, out ServerEntity? target);

            foreach ((PeerId peer, PlayerSession session) in _sessions)
            {
                if (!session.HandshakeComplete ||
                    !ReferenceEquals(session.CurrentWorld, world) || // combat events stay world-local
                    !TryGetEntity(session, out ServerEntity? self))
                {
                    continue;
                }

                bool near =
                    (haveAttacker && Vec2.DistanceSquared(self!.Position, attacker!.Position) <= aoiSq) ||
                    (haveTarget && Vec2.DistanceSquared(self!.Position, target!.Position) <= aoiSq);

                if (near)
                {
                    Send(peer, evt);
                }
            }
        }
    }

    private void BroadcastPlayerState()
    {
        ProgressionConfig progression = _worlds.GameData.Progression;

        foreach ((PeerId peer, PlayerSession session) in _sessions)
        {
            if (!session.HandshakeComplete || !TryGetEntity(session, out ServerEntity? self))
            {
                continue;
            }

            Send(peer, new PlayerStatus(
                self!.Level, self.TotalXp, progression.XpForNextLevel(self.TotalXp), self.Inventory.Gold,
            self.EffectiveAttackPower, self.EffectiveDefense));
            Send(peer, new InventoryState(self.EquippedWeaponId, self.EquippedArmorId, self.Inventory.Stacks));
        }
    }

    // --------------------------------------------------------------- Helpers

    private bool TryGetEntity(PlayerSession session, out ServerEntity? entity)
        => session.CurrentWorld.Entities.TryGetValue(session.EntityId, out entity);

    private (PeerId targetPeer, PlayerSession targetSession)? FindSessionByEntity(int entityId)
    {
        foreach ((PeerId peer, PlayerSession session) in _sessions)
        {
            if (session.HandshakeComplete && session.EntityId == entityId)
            {
                return (peer, session);
            }
        }

        return null;
    }

    /// <summary>
    /// Validate a requested character name and claim it durably for <paramref name="accountId"/>.
    /// A name is refused if malformed, currently online, or durably owned by ANOTHER account —
    /// ownership survives disconnects and server restarts. The same account may of course log its
    /// own character back in.
    /// </summary>
    private bool TryClaimName(string? requested, string accountId, out string display, out string error)
    {
        display = (requested ?? string.Empty).Trim();
        error = string.Empty;

        if (display.Length is < 2 or > 16)
        {
            error = "Name must be between 2 and 16 characters.";
            return false;
        }

        foreach (char c in display)
        {
            if (!char.IsLetterOrDigit(c))
            {
                error = "Name may contain only letters and digits.";
                return false;
            }
        }

        string nameKey = display.ToLowerInvariant();
        string accountKey = accountId.ToLowerInvariant();

        if (_state.Names.TryGetValue(nameKey, out string? owner) &&
            !string.Equals(owner, accountKey, StringComparison.Ordinal))
        {
            error = $"The name '{display}' is already taken on this server.";
            return false;
        }

        if (!_activeNames.Add(display)) // case-insensitive; false if currently online
        {
            error = $"'{display}' is already online.";
            return false;
        }

        _state.Names[nameKey] = accountKey; // durable, server-wide, across both factions
        return true;
    }

    /// <summary>Capture every online character and bank into durable records and save.</summary>
    private void PersistAll()
    {
        foreach (PlayerSession session in _sessions.Values)
        {
            if (session.HandshakeComplete && TryGetEntity(session, out ServerEntity? self))
            {
                AccountRecord account = GetOrCreateAccount(session.AccountId);
                account.Characters[session.Name.ToLowerInvariant()] = CharacterMapper.Capture(self!);
            }
        }

        foreach ((string accountId, Inventory bank) in _banks)
        {
            CharacterMapper.CaptureBank(bank, GetOrCreateAccount(accountId));
        }

        _store.Save(_state);
    }

    private void Send(PeerId peer, ConnectAccepted msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, ConnectRejected msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, Pong msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, SnapshotMessage msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, CombatEventMessage msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, PlayerStatus msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, InventoryState msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, BankState msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, PartyState msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, PartyInviteNotice msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, InstanceResult msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, CorpseContentsMessage msg) => SendWith(peer, msg.Write);

    private void SendWith(PeerId peer, Action<PacketWriter> write)
    {
        _writer.Reset();
        write(_writer);
        _transport.Send(peer, _writer.WrittenSpan);
    }

    private sealed class PlayerSession
    {
        public PlayerSession(World.World startWorld) => CurrentWorld = startWorld;

        public int EntityId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string AccountId { get; set; } = string.Empty;
        public bool HandshakeComplete { get; set; }

        /// <summary>The world this player currently inhabits (open world or an instance).</summary>
        public World.World CurrentWorld { get; set; }
    }
}
