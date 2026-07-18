using System.Security.Cryptography;
using System.Text;
using Aetheria.Server.Items;
using Aetheria.Server.Persistence;
using Aetheria.Server.Social;
using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Combat;
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

    // Pending duel challenges: challenged peer -> (challenger peer, to-death?).
    private readonly Dictionary<PeerId, (PeerId challenger, bool toDeath)> _pendingDuels = new();

    // Pending trade proposals: proposed-to peer -> proposer peer.
    private readonly Dictionary<PeerId, PeerId> _pendingTrades = new();

    // Active trades, indexed by BOTH participants' peers.
    private readonly Dictionary<PeerId, TradeSession> _trades = new();

    // Live account banks, keyed by account id, hydrated from durable state at boot.
    private readonly Dictionary<string, Inventory> _banks = new(StringComparer.OrdinalIgnoreCase);

    // Durable state (accounts, secrets, banks, characters, the server-wide name registry).
    private readonly IPersistenceStore _store;
    private readonly ServerState _state;
    private const int SaveIntervalTicks = SimulationConstants.TickRate * 5; // flush every ~5s

    /// <summary>This server's display name (servers are named, not numbered).</summary>
    public string ServerName { get; }

    /// <summary>Player capacity. Full = no NEW characters; existing characters still play.</summary>
    public int MaxPlayers { get; }

    public GameServer(
        IServerTransport transport, GameData? gameData = null, Action<string>? log = null,
        IPersistenceStore? store = null,
        string serverName = SimulationConstants.DefaultServerName,
        int maxPlayers = SimulationConstants.DefaultMaxPlayers)
    {
        _transport = transport;
        _worlds = new WorldManager(gameData);
        _log = log ?? (_ => { });
        _store = store ?? new InMemoryPersistenceStore();
        _state = _store.Load();
        ServerName = string.IsNullOrWhiteSpace(serverName) ? SimulationConstants.DefaultServerName : serverName;
        MaxPlayers = maxPlayers > 0 ? maxPlayers : SimulationConstants.DefaultMaxPlayers;

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

            BroadcastDuelEndings(world);
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

        // The sanctuary's BANK: a chest players stand next to to move goods/gold in and out.
        open.SpawnNpc("Coffre de banque",
            new Vec2(SimulationConstants.BankChestX, SimulationConstants.BankChestY));

        // ZONE DE DÉPART — ISOLATED goblins east of the sanctuary: every early fight is a duel,
        // one against one. Packs are endgame content (the dungeon camp below stays grouped).
        open.SpawnMonster(monsterId: 1, new Vec2(24f, 10f));
        open.SpawnMonster(monsterId: 1, new Vec2(34f, -4f));
        open.SpawnMonster(monsterId: 1, new Vec2(20f, 22f));

        // CHAMP DES LOUPS — solitary wolves spread across the field, each leashed to its spot.
        open.SpawnMonster(monsterId: 2, new Vec2(-40f, 2f));
        open.SpawnMonster(monsterId: 2, new Vec2(-52f, 10f));
        open.SpawnMonster(monsterId: 2, new Vec2(-46f, -8f));
        open.SpawnMonster(monsterId: 2, new Vec2(-58f, 0f));
        open.SpawnMonster(monsterId: 2, new Vec2(-44f, 16f));
        open.SpawnMonster(monsterId: 2, new Vec2(-64f, -10f));

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

            // Phase gate 1: before login, only Login and the unauthenticated info query are valid.
            if (session.Phase == SessionPhase.AwaitingLogin)
            {
                if (type == MessageType.Login)
                {
                    HandleLogin(peer, session, ref reader);
                }
                else if (type == MessageType.ServerInfoRequest)
                {
                    HandleServerInfo(peer, ref reader);
                }

                return;
            }

            // Phase gate 2: logged in but not yet in the world — character selection screen.
            if (session.Phase == SessionPhase.LoggedIn)
            {
                switch (type)
                {
                    case MessageType.CreateCharacter:
                        CreateCharacter create = CreateCharacter.Read(ref reader);
                        HandleCreateCharacter(peer, session, create);
                        break;

                    case MessageType.EnterWorld:
                        _ = EnterWorld.Read(ref reader);
                        HandleEnterWorld(peer, session);
                        break;

                    case MessageType.Ping:
                        Ping lobbyPing = Ping.Read(ref reader);
                        Send(peer, new Pong(lobbyPing.ClientTimeMs, Environment.TickCount64));
                        break;

                    case MessageType.Disconnect:
                        _transport.Kick(peer);
                        break;

                    default:
                        break;
                }

                return;
            }

            World.World world = session.CurrentWorld;

            switch (type)
            {
                case MessageType.InputCommand:
                    InputCommand input = InputCommand.Read(ref reader);
                    world.ApplyInput(session.EntityId, input.Sequence, input.MoveDirection,
                        input.FacingRadians, input.Jump);
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

                case MessageType.EquipItem:
                    EquipItem equip = EquipItem.Read(ref reader);
                    if (world.TryEquipItem(session.EntityId, equip.ItemId, (EquipSlot)equip.Slot))
                    {
                        SendSelfState(peer, session);
                    }

                    break;

                case MessageType.ChatSend:
                    ChatSend say = ChatSend.Read(ref reader);
                    HandleChat(session, say.Text);
                    break;

                case MessageType.AttackTarget:
                    AttackTarget intent = AttackTarget.Read(ref reader);
                    world.SetAttackTarget(session.EntityId, intent.TargetEntityId);
                    break;

                case MessageType.Inspect:
                    Inspect inspect = Inspect.Read(ref reader);
                    HandleInspect(peer, session, inspect.TargetEntityId);
                    break;

                case MessageType.DuelRequest:
                    DuelRequest duelReq = DuelRequest.Read(ref reader);
                    HandleDuelRequest(peer, session, duelReq);
                    break;

                case MessageType.DuelRespond:
                    DuelRespond duelResp = DuelRespond.Read(ref reader);
                    HandleDuelRespond(peer, session, duelResp.Accept);
                    break;

                case MessageType.TradeRequest:
                    TradeRequest tradeReq = TradeRequest.Read(ref reader);
                    HandleTradeRequest(peer, session, tradeReq.TargetEntityId);
                    break;

                case MessageType.TradeRespond:
                    TradeRespond tradeResp = TradeRespond.Read(ref reader);
                    HandleTradeRespond(peer, session, tradeResp.Accept);
                    break;

                case MessageType.TradeSetOffer:
                    TradeSetOffer offer = TradeSetOffer.Read(ref reader);
                    HandleTradeSetOffer(peer, offer);
                    break;

                case MessageType.TradeAccept:
                    _ = TradeAccept.Read(ref reader);
                    HandleTradeAccept(peer);
                    break;

                case MessageType.TradeCancel:
                    _ = TradeCancel.Read(ref reader);
                    CancelTrade(peer, "Échange annulé.");
                    break;

                case MessageType.DropItem:
                    DropItem drop = DropItem.Read(ref reader);
                    if (session.CurrentWorld.TryDropItem(session.EntityId, drop.ItemId, drop.Quantity))
                    {
                        SendSelfState(peer, session);
                    }

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

    /// <summary>Step 1: authenticate the ACCOUNT and report its (single) character on this server.</summary>
    private void HandleLogin(PeerId peer, PlayerSession session, ref PacketReader reader)
    {
        Login request = Login.Read(ref reader);

        if (request.ProtocolVersion != SimulationConstants.ProtocolVersion)
        {
            Send(peer, LoginResult.Failure(
                $"Version incompatible : serveur v{SimulationConstants.ProtocolVersion}, client v{request.ProtocolVersion}."));
            _transport.Kick(peer);
            return;
        }

        string accountId = (request.AccountId ?? string.Empty).Trim();
        if (accountId.Length is < 1 or > 32)
        {
            Send(peer, LoginResult.Failure("Identifiant de compte requis (1-32 caractères)."));
            return;
        }

        // Explicit account creation vs sign-in — no silent auto-creation anymore.
        bool exists = _state.Accounts.ContainsKey(accountId.ToLowerInvariant());
        if (request.CreateAccount && exists)
        {
            Send(peer, LoginResult.Failure("Ce compte existe déjà sur ce serveur — connecte-toi."));
            return;
        }

        if (!request.CreateAccount && !exists)
        {
            Send(peer, LoginResult.Failure("Compte inconnu sur ce serveur — utilise « Créer un compte »."));
            return;
        }

        if (request.CreateAccount && string.IsNullOrWhiteSpace(request.AccountSecret))
        {
            Send(peer, LoginResult.Failure("Choisis un secret de compte (il protège ton compte)."));
            return;
        }

        AccountRecord account = GetOrCreateAccount(accountId);
        string secretHash = HashSecret(request.AccountSecret);
        if (string.IsNullOrEmpty(account.SecretHash))
        {
            account.SecretHash = secretHash; // fresh account: this login sets the secret
        }
        else if (!string.Equals(account.SecretHash, secretHash, StringComparison.Ordinal))
        {
            Send(peer, LoginResult.Failure("Mauvais secret de compte."));
            return;
        }

        session.AccountId = accountId;
        session.Phase = SessionPhase.LoggedIn;

        // One character per account per server: report it if it exists.
        CharacterRecord? existing = account.Characters.Values.FirstOrDefault();
        if (existing is null)
        {
            Send(peer, new LoginResult(true, "", false, string.Empty, 0, 0, Gender.Male, 1));
        }
        else
        {
            byte level = (byte)System.Math.Clamp(
                _worlds.GameData.Progression.LevelForXp(existing.TotalXp), 1, 255);
            Send(peer, new LoginResult(true, "", true, existing.Name,
                existing.RaceId, existing.ClassId, (Gender)existing.Gender, level,
                new Appearance(existing.SkinTone, existing.Face, existing.HairStyle,
                    existing.HairColor, existing.BeardStyle, existing.BeardColor)));
        }

        _log($"Account '{accountId}' logged in ({peer}); character: {(existing?.Name ?? "none")}.");
    }

    /// <summary>Relay a player's chat line to everyone in the same world. Chat carries ONLY player words.</summary>
    private void HandleChat(PlayerSession sender, string text)
    {
        text = (text ?? string.Empty).Replace("\n", " ").Replace("\r", " ").Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (text.Length > 200)
        {
            text = text.Substring(0, 200);
        }

        var msg = new ChatMessage(sender.Name, text);
        foreach ((PeerId peer, PlayerSession session) in _sessions)
        {
            if (session.HandshakeComplete && ReferenceEquals(session.CurrentWorld, sender.CurrentWorld))
            {
                SendWith(peer, msg.Write);
            }
        }
    }

    /// <summary>Answer the unauthenticated server-browser query: name, population, your character here.</summary>
    private void HandleServerInfo(PeerId peer, ref PacketReader reader)
    {
        ServerInfoRequest request = ServerInfoRequest.Read(ref reader);

        string key = (request.AccountId ?? string.Empty).Trim().ToLowerInvariant();
        AccountRecord? account = null;
        if (key.Length > 0)
        {
            _state.Accounts.TryGetValue(key, out account);
        }

        CharacterRecord? character = account?.Characters.Values.FirstOrDefault();
        byte level = 1;
        if (character is not null)
        {
            level = (byte)System.Math.Clamp(
                _worlds.GameData.Progression.LevelForXp(character.TotalXp), 1, 255);
        }

        Send(peer, new ServerInfo(
            ServerName, PlayerCount, MaxPlayers,
            acceptsNewCharacters: PlayerCount < MaxPlayers,
            hasAccount: account is not null,
            hasCharacter: character is not null,
            characterName: character?.Name ?? string.Empty,
            characterLevel: level));
    }

    /// <summary>Create this server's one character for the account, then enter the world.</summary>
    private void HandleCreateCharacter(PeerId peer, PlayerSession session, CreateCharacter request)
    {
        GameData data = _worlds.GameData;
        AccountRecord account = GetOrCreateAccount(session.AccountId);

        if (account.Characters.Count > 0)
        {
            Send(peer, LoginResult.Failure("Ce compte a déjà un personnage sur ce serveur."));
            return;
        }

        // A full server refuses NEW characters (existing characters may still enter and play).
        if (PlayerCount >= MaxPlayers)
        {
            Send(peer, LoginResult.Failure(
                $"Serveur complet ({PlayerCount}/{MaxPlayers}) — impossible d'y créer un nouveau personnage."));
            return;
        }

        if (!data.IsClassAllowedForRace(request.RaceId, request.ClassId))
        {
            Send(peer, LoginResult.Failure(
                $"{data.GetRace(request.RaceId).Name} ne peut pas jouer {data.GetClass(request.ClassId).Name}."));
            return;
        }

        if (!TryClaimName(request.Name, session.AccountId, out string name, out string nameError))
        {
            Send(peer, LoginResult.Failure(nameError));
            return;
        }

        ServerEntity entity = _worlds.OpenWorld.SpawnPlayer(
            peer, name, request.RaceId, request.ClassId, request.Gender, request.Appearance);
        _worlds.OpenWorld.GrantStarterKit(entity);
        account.Characters[name.ToLowerInvariant()] = CharacterMapper.Capture(entity);

        EnterWorldWith(peer, session, entity, isNew: true);
    }

    /// <summary>Enter the world with the account's existing character.</summary>
    private void HandleEnterWorld(PeerId peer, PlayerSession session)
    {
        AccountRecord account = GetOrCreateAccount(session.AccountId);
        CharacterRecord? saved = account.Characters.Values.FirstOrDefault();
        if (saved is null)
        {
            Send(peer, LoginResult.Failure("Aucun personnage sur ce serveur — crée-le d'abord."));
            return;
        }

        // Blocks a second simultaneous login of the same character (name already online).
        if (!TryClaimName(saved.Name, session.AccountId, out string name, out string nameError))
        {
            Send(peer, LoginResult.Failure(nameError));
            return;
        }

        ServerEntity entity = _worlds.OpenWorld.SpawnPlayer(
            peer, name, saved.RaceId, saved.ClassId, (Gender)saved.Gender,
            new Appearance(saved.SkinTone, saved.Face, saved.HairStyle, saved.HairColor, saved.BeardStyle, saved.BeardColor));
        CharacterMapper.Restore(_worlds.OpenWorld, entity, saved);

        EnterWorldWith(peer, session, entity, isNew: false);
    }

    private void EnterWorldWith(PeerId peer, PlayerSession session, ServerEntity entity, bool isNew)
    {
        GameData data = _worlds.GameData;
        session.EntityId = entity.Id;
        session.Name = entity.Name;
        session.CurrentWorld = _worlds.OpenWorld;
        session.Phase = SessionPhase.InWorld;

        Send(peer, new ConnectAccepted(entity.Id, (byte)SimulationConstants.TickRate));
        SendBankState(peer, session.AccountId);

        _log($"'{session.Name}' entered as {entity.Faction} {data.GetRace(entity.RaceId).Name} " +
             $"{data.GetClass(entity.ClassId).Name} ({entity.Gender}, entity {entity.Id}, {peer}, " +
             $"{(isNew ? "new" : "restored")}). Players online: {PlayerCount}.");
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
        if (!_sessions.Remove(peer, out PlayerSession? session))
        {
            return;
        }

        if (!session.HandshakeComplete)
        {
            return; // never entered the world — nothing to clean up
        }

        Party? left = _parties.Leave(peer.Value);
        if (left is not null)
        {
            BroadcastPartyState(left);
        }

        // Social cleanup: a fleeing duelist forfeits; an open trade is cancelled.
        session.CurrentWorld.ForfeitDuel(session.EntityId);
        CancelTrade(peer, "L'autre joueur s'est déconnecté.");
        _pendingDuels.Remove(peer);
        _pendingTrades.Remove(peer);

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

    // ---------------------------------------------------------------- Social

    private void HandleInspect(PeerId peer, PlayerSession session, int targetEntityId)
    {
        (PeerId targetPeer, PlayerSession targetSession)? found = FindSessionByEntity(targetEntityId);
        if (found is null ||
            !TryGetEntity(session, out ServerEntity? self) ||
            !TryGetEntity(found.Value.targetSession, out ServerEntity? target) ||
            self!.Faction != target!.Faction) // inspection is a same-camp courtesy
        {
            return;
        }

        Send(peer, new InspectResult(target.Name, (byte)System.Math.Clamp(target.Level, 1, 255),
            target.RaceId, target.ClassId, target.EffectiveMaxHealth,
            target.EffectiveAttackPower, target.EffectiveDefense,
            target.EquippedWeaponId, target.EquippedArmorId, target.TotalXp));
    }

    private void HandleDuelRequest(PeerId peer, PlayerSession session, DuelRequest request)
    {
        (PeerId targetPeer, PlayerSession targetSession)? found = FindSessionByEntity(request.TargetEntityId);
        if (found is null ||
            !ReferenceEquals(found.Value.targetSession.CurrentWorld, session.CurrentWorld) ||
            session.CurrentWorld.IsDueling(session.EntityId) ||
            session.CurrentWorld.IsDueling(request.TargetEntityId) ||
            _pendingDuels.ContainsKey(found.Value.targetPeer))
        {
            return;
        }

        _pendingDuels[found.Value.targetPeer] = (peer, request.ToDeath);
        Send(found.Value.targetPeer, new DuelNotice(session.Name, request.ToDeath));
    }

    private void HandleDuelRespond(PeerId peer, PlayerSession session, bool accept)
    {
        if (!_pendingDuels.Remove(peer, out (PeerId challenger, bool toDeath) pending))
        {
            return;
        }

        if (!accept ||
            !_sessions.TryGetValue(pending.challenger, out PlayerSession? challengerSession) ||
            !ReferenceEquals(challengerSession.CurrentWorld, session.CurrentWorld))
        {
            return;
        }

        if (session.CurrentWorld.StartDuel(challengerSession.EntityId, session.EntityId, pending.toDeath))
        {
            string kind = pending.toDeath ? "DUEL À MORT" : "duel amical";
            Send(pending.challenger, new DuelState(true, session.EntityId, pending.toDeath,
                kind + " contre " + session.Name + " — battez-vous !"));
            Send(peer, new DuelState(true, challengerSession.EntityId, pending.toDeath,
                kind + " contre " + challengerSession.Name + " — battez-vous !"));
            _log($"Duel {(pending.toDeath ? "TO THE DEATH" : "(friendly)")}: " +
                 $"'{challengerSession.Name}' vs '{session.Name}'.");
        }
    }

    /// <summary>Broadcast the endings of any duels that resolved this tick.</summary>
    private void BroadcastDuelEndings(World.World world)
    {
        foreach ((int winnerId, int loserId, bool toDeath) in world.DrainDuelEndings())
        {
            (PeerId, PlayerSession)? winner = FindSessionByEntity(winnerId);
            (PeerId, PlayerSession)? loser = FindSessionByEntity(loserId);
            string winnerName = winner?.Item2.Name ?? "?";
            string loserName = loser?.Item2.Name ?? "?";
            string message = toDeath
                ? winnerName + " a tué " + loserName + " en duel à mort !"
                : winnerName + " remporte le duel contre " + loserName + ".";

            if (winner is not null)
            {
                Send(winner.Value.Item1, new DuelState(false, 0, toDeath, message));
            }

            if (loser is not null)
            {
                Send(loser.Value.Item1, new DuelState(false, 0, toDeath, message));
            }

            _log($"Duel ended: {message}");
        }
    }

    private void HandleTradeRequest(PeerId peer, PlayerSession session, int targetEntityId)
    {
        (PeerId targetPeer, PlayerSession targetSession)? found = FindSessionByEntity(targetEntityId);
        if (found is null ||
            _trades.ContainsKey(peer) || _trades.ContainsKey(found.Value.targetPeer) ||
            _pendingTrades.ContainsKey(found.Value.targetPeer) ||
            !ReferenceEquals(found.Value.targetSession.CurrentWorld, session.CurrentWorld) ||
            !TryGetEntity(session, out ServerEntity? self) ||
            !TryGetEntity(found.Value.targetSession, out ServerEntity? target) ||
            self!.Faction != target!.Faction ||
            Vec2.DistanceSquared(self.Position, target.Position)
                > SimulationConstants.TradeRange * SimulationConstants.TradeRange)
        {
            return;
        }

        _pendingTrades[found.Value.targetPeer] = peer;
        Send(found.Value.targetPeer, new TradeNotice(session.Name));
    }

    private void HandleTradeRespond(PeerId peer, PlayerSession session, bool accept)
    {
        if (!_pendingTrades.Remove(peer, out PeerId proposer))
        {
            return;
        }

        if (!accept || !_sessions.TryGetValue(proposer, out PlayerSession? proposerSession) ||
            _trades.ContainsKey(peer) || _trades.ContainsKey(proposer))
        {
            return;
        }

        var trade = new TradeSession(proposer, peer);
        _trades[proposer] = trade;
        _trades[peer] = trade;
        BroadcastTradeState(trade, "");
        _log($"Trade opened: '{proposerSession.Name}' <-> '{session.Name}'.");
    }

    private void HandleTradeSetOffer(PeerId peer, TradeSetOffer offer)
    {
        if (!_trades.TryGetValue(peer, out TradeSession? trade))
        {
            return;
        }

        trade.OfferOf(peer).Set(offer.Gold, offer.Items);
        trade.AcceptedA = false;
        trade.AcceptedB = false;
        BroadcastTradeState(trade, "");
    }

    private void HandleTradeAccept(PeerId peer)
    {
        if (!_trades.TryGetValue(peer, out TradeSession? trade))
        {
            return;
        }

        trade.SetAccepted(peer);
        if (!trade.AcceptedA || !trade.AcceptedB)
        {
            BroadcastTradeState(trade, "");
            return;
        }

        // Both locked in: validate everything and swap atomically.
        if (!_sessions.TryGetValue(trade.PeerA, out PlayerSession? sa) ||
            !_sessions.TryGetValue(trade.PeerB, out PlayerSession? sb) ||
            !TryGetEntity(sa, out ServerEntity? ea) || !TryGetEntity(sb, out ServerEntity? eb))
        {
            CancelTrade(peer, "Échange interrompu.");
            return;
        }

        if (Vec2.DistanceSquared(ea!.Position, eb!.Position)
            > SimulationConstants.TradeRange * SimulationConstants.TradeRange)
        {
            trade.AcceptedA = false;
            trade.AcceptedB = false;
            BroadcastTradeState(trade, "Trop loin l'un de l'autre !");
            return;
        }

        if (TradeLogic.TryExecute(ea.Inventory, trade.OfferA, eb.Inventory, trade.OfferB,
                _worlds.GameData, out string error))
        {
            CloseTrade(trade, "Échange conclu !");
            SendSelfState(trade.PeerA, sa);
            SendSelfState(trade.PeerB, sb);
            _log($"Trade completed: '{sa.Name}' <-> '{sb.Name}'.");
        }
        else
        {
            trade.AcceptedA = false;
            trade.AcceptedB = false;
            BroadcastTradeState(trade, error);
        }
    }

    private void CancelTrade(PeerId peer, string reason)
    {
        if (_trades.TryGetValue(peer, out TradeSession? trade))
        {
            CloseTrade(trade, reason);
        }
    }

    private void CloseTrade(TradeSession trade, string message)
    {
        _trades.Remove(trade.PeerA);
        _trades.Remove(trade.PeerB);
        SendTradeStateTo(trade.PeerA, trade, active: false, message);
        SendTradeStateTo(trade.PeerB, trade, active: false, message);
    }

    private void BroadcastTradeState(TradeSession trade, string message)
    {
        SendTradeStateTo(trade.PeerA, trade, active: true, message);
        SendTradeStateTo(trade.PeerB, trade, active: true, message);
    }

    private void SendTradeStateTo(PeerId peer, TradeSession trade, bool active, string message)
    {
        if (!_sessions.ContainsKey(peer))
        {
            return;
        }

        PeerId other = trade.OtherOf(peer);
        string partnerName = _sessions.TryGetValue(other, out PlayerSession? os) ? os.Name : "?";
        TradeOffer mine = trade.OfferOf(peer);
        TradeOffer theirs = trade.OfferOf(other);
        bool myAccepted = trade.IsAccepted(peer);
        bool theirAccepted = trade.IsAccepted(other);

        Send(peer, new TradeState(active, partnerName, mine.Gold, mine.Items,
            theirs.Gold, theirs.Items, myAccepted, theirAccepted, message));
    }

    /// <summary>An in-progress trade between two peers.</summary>
    private sealed class TradeSession
    {
        public TradeSession(PeerId a, PeerId b)
        {
            PeerA = a;
            PeerB = b;
        }

        public PeerId PeerA { get; }
        public PeerId PeerB { get; }
        public TradeOffer OfferA { get; } = new();
        public TradeOffer OfferB { get; } = new();
        public bool AcceptedA { get; set; }
        public bool AcceptedB { get; set; }

        public TradeOffer OfferOf(PeerId peer) => peer.Equals(PeerA) ? OfferA : OfferB;

        public PeerId OtherOf(PeerId peer) => peer.Equals(PeerA) ? PeerB : PeerA;

        public bool IsAccepted(PeerId peer) => peer.Equals(PeerA) ? AcceptedA : AcceptedB;

        public void SetAccepted(PeerId peer)
        {
            if (peer.Equals(PeerA)) { AcceptedA = true; }
            else { AcceptedB = true; }
        }
    }

    // ------------------------------------------------------------------ Bank

    private void HandleBankTransaction(PeerId peer, PlayerSession session, BankTransaction tx)
    {
        if (!TryGetEntity(session, out ServerEntity? self) || self!.IsDead)
        {
            return;
        }

        // The bank is a PLACE now: you must stand at the sanctuary's chest (open world only).
        var chest = new Vec2(SimulationConstants.BankChestX, SimulationConstants.BankChestY);
        if (!ReferenceEquals(session.CurrentWorld, _worlds.OpenWorld) ||
            Vec2.DistanceSquared(self.Position, chest) >
                SimulationConstants.BankInteractRange * SimulationConstants.BankInteractRange)
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
    private void Send(PeerId peer, InspectResult msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, DuelNotice msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, DuelState msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, TradeNotice msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, TradeState msg) => SendWith(peer, msg.Write);
    private void Send(PeerId peer, LoginResult msg) => SendWith(peer, msg.Write);

    private void Send(PeerId peer, ServerInfo msg) => SendWith(peer, msg.Write);

    private void SendWith(PeerId peer, Action<PacketWriter> write)
    {
        _writer.Reset();
        write(_writer);
        _transport.Send(peer, _writer.WrittenSpan);
    }

    private enum SessionPhase
    {
        AwaitingLogin,
        LoggedIn,
        InWorld,
    }

    private sealed class PlayerSession
    {
        public PlayerSession(World.World startWorld) => CurrentWorld = startWorld;

        public int EntityId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string AccountId { get; set; } = string.Empty;
        public SessionPhase Phase { get; set; } = SessionPhase.AwaitingLogin;

        /// <summary>Kept for the broadcast/persist paths: "fully in the world".</summary>
        public bool HandshakeComplete => Phase == SessionPhase.InWorld;

        /// <summary>The world this player currently inhabits (open world or an instance).</summary>
        public World.World CurrentWorld { get; set; }
    }
}
