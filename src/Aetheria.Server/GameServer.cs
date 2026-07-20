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

    /// <summary>Offline party members still holding their seat: dead peer key → character name.</summary>
    private readonly Dictionary<int, string> _partyGhosts = new();
    private readonly Dictionary<PeerId, PlayerSession> _sessions = new();
    private readonly PacketWriter _writer = new();
    private readonly Action<string> _log;

    // Character names are unique server-wide (across both factions). Case-insensitive.
    // NOTE: this is uniqueness among currently-active characters; durable uniqueness arrives with
    // persistence (M4), where names are reserved in the database.
    private readonly HashSet<string> _activeNames = new(StringComparer.OrdinalIgnoreCase);

    // Instance GATES in the open world: (instance def, portal position, meeting-stone position).
    private readonly List<(byte defId, Vec2 gate, Vec2 stone)> _portals = new();

    // The running instance of each party (keyed by leader peer), so members who walk into the
    // portal AFTER the group entered land in the SAME instance.
    private readonly Dictionary<int, World.World> _partyInstances = new();

    // Anti-spam: last tick a refusal message was sent per player / a stone fired per party.
    private readonly Dictionary<int, long> _portalMsgAt = new();
    private readonly Dictionary<int, long> _stoneFiredAt = new();

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
        _worlds.OpenWorld.PartySiblingsOf = PartySiblingEntityIds;
    }

    /// <summary>The OTHER party members' entity ids for a player entity (kill sharing).</summary>
    private List<int> PartySiblingEntityIds(int entityId)
    {
        (PeerId peer, PlayerSession _)? owner = FindSessionByEntity(entityId);
        if (owner is null)
        {
            return [];
        }

        Party? party = _parties.GetParty(owner.Value.peer.Value);
        if (party is null)
        {
            return [];
        }

        var result = new List<int>(party.Count - 1);
        foreach (int member in party.Members)
        {
            var memberPeer = new PeerId(member);
            if (memberPeer.Value != owner.Value.peer.Value &&
                _sessions.TryGetValue(memberPeer, out PlayerSession? ms) && ms.HandshakeComplete)
            {
                result.Add(ms.EntityId);
            }
        }

        return result;
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

        // Death INSIDE an instance sends you home: the full-loot corpse stays where you fell,
        // but the reborn character wakes at the SANCTUARY — never back inside the dungeon.
        foreach ((PeerId _, PlayerSession session) in _sessions)
        {
            if (session.HandshakeComplete &&
                !ReferenceEquals(session.CurrentWorld, _worlds.OpenWorld) &&
                TryGetEntity(session, out ServerEntity? fallen) && fallen is not null && fallen.IsDead)
            {
                World.World from = session.CurrentWorld;
                if (WorldManager.TransferPlayer(from, _worlds.OpenWorld, session.EntityId, Vec2.Zero))
                {
                    session.CurrentWorld = _worlds.OpenWorld;
                    _worlds.DestroyInstanceIfEmpty(from);
                }
            }
        }

        TickHearthstones(); // finished 5-second channels teleport home

        // PRESENCE sidecar (~every 5 s): who is online, for the launcher's live friends list
        // (the patch server reads and serves it over HTTP).
        if (_worlds.OpenWorld.Tick % 100 == 0)
        {
            WritePresence();
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

            // Quest kill-counters that moved this tick: push fresh state to those players.
            foreach (int playerId in world.DrainQuestDirty())
            {
                (PeerId qPeer, PlayerSession qSession)? qFound = FindSessionByEntity(playerId);
                if (qFound is not null)
                {
                    SendQuestState(qFound.Value.qPeer, qFound.Value.qSession);
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
            BroadcastPartyVitals(); // live HP/mana for the party frames
            TickPortalsAndStones(); // walk-in instance gates + meeting stones
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

        // The sanctuary's PEOPLE: the quest giver by the plaza, and flavour villagers at their
        // stalls — the safe zone should feel inhabited, not just paved.
        open.SpawnNpc("Aldric le Guetteur", new Vec2(3.5f, 3.5f), npcType: 2);
        open.SpawnNpc("Mira la Marchande", new Vec2(-3.2f, 5.6f), npcType: 4); // 4 = merchant
        open.SpawnNpc("Brom le Forgeron", new Vec2(-6.0f, -3.8f), npcType: 8); // 8 = smith-merchant
        open.SpawnNpc("Aubergiste Marla", new Vec2(6.2f, -2.5f), npcType: 7);  // 7 = innkeeper

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

        // INSTANCE GATES: physical portals in the world (no key, no menu — you WALK in), each
        // with its meeting stone. The stone summons missing party members when ≥2 stand by it.
        foreach (InstanceDefinition def in _worlds.GameData.Instances)
        {
            Vec2 gate = def.IsRaid ? new Vec2(74f, 66f) : new Vec2(34f, 26f);
            var stone = new Vec2(gate.X - 3f, gate.Y - 1.5f);
            open.SpawnNpc("Portail : " + def.Name, gate, npcType: 5);
            open.SpawnNpc("Pierre de téléportation", stone, npcType: 6);
            _portals.Add((def.Id, gate, stone));
        }
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

                    case MessageType.DeleteCharacter:
                        _ = DeleteCharacter.Read(ref reader);
                        HandleDeleteCharacter(peer, session);
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
                    world.TryLootAllNearby(session.EntityId, loot.CorpseEntityId); // area loot
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
                    if (world.TryEquipItem(session.EntityId, equip.ItemId, (EquipSlot)equip.Slot,
                            equip.BagIndex == 255 ? -1 : equip.BagIndex))
                    {
                        SendSelfState(peer, session);
                    }

                    break;

                case MessageType.MoveItem:
                    MoveItem move = MoveItem.Read(ref reader);
                    if (world.TryMoveItem(session.EntityId, move.FromIndex, move.ToIndex))
                    {
                        SendSelfState(peer, session); // the bag order is part of self state
                    }

                    break;

                case MessageType.VendorAction:
                    VendorAction vendor = VendorAction.Read(ref reader);
                    if (world.TryVendorAction(session.EntityId, vendor.Sell, vendor.ItemId, vendor.Quantity))
                    {
                        SendSelfState(peer, session); // gold and bags changed
                    }

                    break;

                case MessageType.PartyKick:
                    HandlePartyKick(peer, PartyKick.Read(ref reader));
                    break;

                case MessageType.ShapeShift:
                    ShapeShift shift = ShapeShift.Read(ref reader);
                    if (world.TryShapeShift(session.EntityId, shift.FormId))
                    {
                        SendSelfState(peer, session); // stats changed with the form
                    }

                    break;

                case MessageType.Hearthstone:
                    _ = Hearthstone.Read(ref reader);
                    HandleHearthstone(peer, session);
                    break;

                case MessageType.UseItem:
                    UseItem use = UseItem.Read(ref reader);
                    if (world.TryUseItem(session.EntityId, use.ItemId, out string refusal))
                    {
                        SendSelfState(peer, session); // the bags changed
                    }
                    else if (refusal.Length > 0)
                    {
                        SendWith(peer, new ChatMessage(ChatChannel.System, "", "", refusal).Write);
                    }

                    break;

                case MessageType.FriendAction:
                    FriendAction friendAct = FriendAction.Read(ref reader);
                    HandleFriendAction(peer, session, friendAct);
                    break;

                case MessageType.SetBandit:
                    SetBanditMode bandit = SetBanditMode.Read(ref reader);
                    if (TryGetEntity(session, out ServerEntity? outlaw) && outlaw is not null && outlaw.IsAlive)
                    {
                        // The switch only flips INSIDE a sanctuary: no turning outlaw (or
                        // repenting) in the middle of a fight.
                        if (!ReferenceEquals(world, _worlds.OpenWorld) || !world.IsSafePosition(outlaw.Position))
                        {
                            SendWith(peer, new ChatMessage(ChatChannel.System, "", "",
                                "Tu dois être dans un sanctuaire pour changer de statut de bandit.").Write);
                            break;
                        }

                        outlaw.IsBandit = bandit.Enabled;
                        SendSelfState(peer, session);
                        SendWith(peer, new ChatMessage(ChatChannel.System, "", "", bandit.Enabled
                            ? "Mode BANDIT activé : tu peux frapper ta propre faction — et elle le sait."
                            : "Mode bandit désactivé.").Write);
                    }

                    break;

                case MessageType.SetHome:
                    _ = SetHome.Read(ref reader);
                    HandleSetHome(peer, session);
                    break;

                case MessageType.ChatSend:
                    ChatSend say = ChatSend.Read(ref reader);
                    HandleChat(peer, session, say.Channel, say.Target, say.Text);
                    break;

                case MessageType.QuestAction:
                    QuestAction qa = QuestAction.Read(ref reader);
                    if (session.CurrentWorld.TryQuestAction(session.EntityId, qa.QuestId, qa.TurnIn))
                    {
                        SendSelfState(peer, session); // rewards may have landed
                    }

                    SendQuestState(peer, session);
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

            // Ship the loadout too: the lobby preview shows the character EXACTLY as in game.
            var equipment = new byte[Aetheria.Shared.Items.EquipSlots.Count];
            foreach (System.Collections.Generic.KeyValuePair<string, byte> pair in existing.Equipment)
            {
                if (int.TryParse(pair.Key, out int slot) && slot >= 0 && slot < equipment.Length)
                {
                    equipment[slot] = pair.Value;
                }
            }

            Send(peer, new LoginResult(true, "", true, existing.Name,
                existing.RaceId, existing.ClassId, (Gender)existing.Gender, level,
                new Appearance(existing.SkinTone, existing.Face, existing.HairStyle,
                    existing.HairColor, existing.BeardStyle, existing.BeardColor), equipment));
        }

        _log($"Account '{accountId}' logged in ({peer}); character: {(existing?.Name ?? "none")}.");
    }

    /// <summary>Relay a player's chat line to everyone in the same world. Chat carries ONLY player words.</summary>
    /// <summary>Route a chat line by CHANNEL: say is short-range, party/raid go to the group,
    /// trade/world reach everyone, whispers find their target by name.</summary>
    private void HandleChat(PeerId senderPeer, PlayerSession sender, ChatChannel channel,
        string target, string text)
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

        switch (channel)
        {
            case ChatChannel.Say:
                // Short range (30 units), same world — like leaning over to talk.
                if (!TryGetEntity(sender, out ServerEntity? speaker) || speaker is null)
                {
                    return;
                }

                var sayMsg = new ChatMessage(ChatChannel.Say, sender.Name, "", text);
                foreach ((PeerId peer, PlayerSession session) in _sessions)
                {
                    if (session.HandshakeComplete &&
                        ReferenceEquals(session.CurrentWorld, sender.CurrentWorld) &&
                        TryGetEntity(session, out ServerEntity? hearer) && hearer is not null &&
                        Vec2.DistanceSquared(speaker.Position, hearer.Position) <= 30f * 30f)
                    {
                        SendWith(peer, sayMsg.Write);
                    }
                }

                break;

            case ChatChannel.Party:
            case ChatChannel.Raid:
                Party? party = _parties.GetParty(senderPeer.Value);
                if (party is null)
                {
                    SendWith(senderPeer, new ChatMessage(ChatChannel.System, "", "",
                        "Tu n'es dans aucun groupe.").Write);
                    return;
                }

                var partyMsg = new ChatMessage(channel, sender.Name, "", text);
                foreach (int member in party.Members)
                {
                    var memberPeer = new PeerId(member);
                    if (_sessions.TryGetValue(memberPeer, out PlayerSession? ms) && ms.HandshakeComplete)
                    {
                        SendWith(memberPeer, partyMsg.Write);
                    }
                }

                break;

            case ChatChannel.Guild:
                SendWith(senderPeer, new ChatMessage(ChatChannel.System, "", "",
                    "Les guildes arrivent bientôt — ce canal s'ouvrira avec elles.").Write);
                break;

            case ChatChannel.Trade:
            case ChatChannel.World:
                var wideMsg = new ChatMessage(channel, sender.Name, "", text);
                foreach ((PeerId peer, PlayerSession session) in _sessions)
                {
                    if (session.HandshakeComplete)
                    {
                        SendWith(peer, wideMsg.Write);
                    }
                }

                break;

            case ChatChannel.Whisper:
                string wanted = (target ?? "").Trim();
                foreach ((PeerId peer, PlayerSession session) in _sessions)
                {
                    if (session.HandshakeComplete &&
                        string.Equals(session.Name, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        SendWith(peer, new ChatMessage(ChatChannel.Whisper, sender.Name, "", text).Write);
                        // Echo to the sender: « À X : … ».
                        SendWith(senderPeer, new ChatMessage(ChatChannel.Whisper, "", session.Name, text).Write);
                        return;
                    }
                }

                SendWith(senderPeer, new ChatMessage(ChatChannel.System, "", "",
                    $"Aucun joueur nommé « {wanted} » en ligne.").Write);
                break;
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

    /// <summary>
    /// Permanently delete the account's character on this server (character screen only — the
    /// phase gate guarantees the player is NOT in the world). Frees the reserved name, then
    /// resends a LoginResult with HasCharacter = false so the client flips to creation.
    /// </summary>
    private void HandleDeleteCharacter(PeerId peer, PlayerSession session)
    {
        AccountRecord account = GetOrCreateAccount(session.AccountId);
        CharacterRecord? saved = account.Characters.Values.FirstOrDefault();
        if (saved is null)
        {
            Send(peer, LoginResult.Failure("Aucun personnage à supprimer sur ce serveur."));
            return;
        }

        string key = saved.Name.ToLowerInvariant();
        account.Characters.Remove(key);
        _state.Names.Remove(key);
        _log($"Account '{session.AccountId}' DELETED character '{saved.Name}'.");

        Send(peer, new LoginResult(true, "", false, string.Empty, 0, 0, Gender.Male, 1));
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
        Send(peer, new QuestCatalogMessage(QuestsWithZones(data)));
        SendQuestState(peer, session);

        // RECONNECTION: if a party still holds this character's seat (he shows greyed
        // « déco » to the others), hand it back under the new peer id.
        foreach (KeyValuePair<int, string> ghost in _partyGhosts)
        {
            if (string.Equals(ghost.Value, entity.Name, StringComparison.OrdinalIgnoreCase))
            {
                Party? rejoined = _parties.ReplaceMember(ghost.Key, peer.Value);
                _partyGhosts.Remove(ghost.Key);
                if (rejoined is not null)
                {
                    BroadcastPartyState(rejoined);
                }

                break;
            }
        }

        // FRIENDS: your list on arrival — and everyone who lists YOU sees the lamp turn
        // green, with a system-chat note (the "friend is online" notification).
        SendFriends(peer, session);
        NotifyFriendWatchers(session.Name, online: true);

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

        // A disconnected party member KEEPS HIS SEAT: the others see him greyed « (déco) »
        // instead of the group silently shrinking. He takes the seat back on reconnection.
        Party? party = _parties.GetParty(peer.Value);
        if (party is not null)
        {
            _partyGhosts[peer.Value] = session.Name;
            bool anyOnline = party.Members.Any(m => _sessions.ContainsKey(new PeerId(m)));
            if (!anyOnline)
            {
                // Everyone is gone: the party dissolves for real.
                foreach (int m in party.Members.ToArray())
                {
                    _parties.Leave(m);
                    _partyGhosts.Remove(m);
                }
            }
            else
            {
                BroadcastPartyState(party);
            }
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
        NotifyFriendWatchers(session.Name, online: false);
        session.CurrentWorld.Despawn(session.EntityId);
        _worlds.DestroyInstanceIfEmpty(session.CurrentWorld);
        PersistAll();
        _log($"'{session.Name}' left (entity {session.EntityId}). Players online: {PlayerCount}.");
    }

    // ---------------------------------------------------------------- Friends

    /// <summary>Write the online roster beside the state file (launcher live presence).</summary>
    private void WritePresence()
    {
        if (_store is not JsonFilePersistenceStore fileStore)
        {
            return;
        }

        try
        {
            var players = new List<object>();
            foreach (PlayerSession s in _sessions.Values)
            {
                if (s.HandshakeComplete && TryGetEntity(s, out ServerEntity? body) && body is not null)
                {
                    players.Add(new { name = s.Name, level = body.Level, classId = (int)body.ClassId });
                }
            }

            string json = System.Text.Json.JsonSerializer.Serialize(new { server = ServerName, players });
            File.WriteAllText(fileStore.FilePath + ".presence.json", json);
        }
        catch (IOException) { /* transient lock: next tick writes again */ }
    }


    private void HandleFriendAction(PeerId peer, PlayerSession session, FriendAction action)
    {
        if (!session.HandshakeComplete)
        {
            return;
        }

        AccountRecord account = GetOrCreateAccount(session.AccountId);
        string key = action.Name.Trim().ToLowerInvariant();
        switch ((FriendOp)action.Op)
        {
            case FriendOp.Add:
                if (key.Length == 0) { break; }
                if (string.Equals(key, session.Name, StringComparison.OrdinalIgnoreCase))
                {
                    SendSystemLine(peer, "Tu ne peux pas t'ajouter toi-même.");
                    break;
                }

                if (!_state.Names.ContainsKey(key))
                {
                    SendSystemLine(peer, $"Aucun personnage nommé « {action.Name.Trim()} » sur ce royaume.");
                    break;
                }

                if (!account.Friends.Contains(key))
                {
                    account.Friends.Add(key);
                    PersistAll();
                    SendSystemLine(peer, $"« {DisplayNameOf(key)} » ajouté à tes amis.");
                }

                break;

            case FriendOp.Remove:
                if (account.Friends.Remove(key))
                {
                    PersistAll();
                }

                break;

            case FriendOp.Invite:
                foreach ((PeerId otherPeer, PlayerSession other) in _sessions)
                {
                    if (other.HandshakeComplete &&
                        string.Equals(other.Name, action.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        HandlePartyInvite(peer, session, other.EntityId);
                        return; // no roster push needed
                    }
                }

                SendSystemLine(peer, $"« {action.Name.Trim()} » n'est pas en ligne.");
                return;
        }

        SendFriends(peer, session);
    }

    /// <summary>Build and push the caller's friends list (live presence, level, last-seen).</summary>
    private void SendFriends(PeerId peer, PlayerSession session)
    {
        AccountRecord account = GetOrCreateAccount(session.AccountId);
        var list = new List<FriendInfo>(account.Friends.Count);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (string key in account.Friends)
        {
            var info = new FriendInfo { Name = DisplayNameOf(key), Server = ServerName };

            // Online? Level and class come from the LIVE entity.
            bool online = false;
            foreach (PlayerSession other in _sessions.Values)
            {
                if (other.HandshakeComplete &&
                    string.Equals(other.Name, key, StringComparison.OrdinalIgnoreCase) &&
                    TryGetEntity(other, out ServerEntity? body) && body is not null)
                {
                    info.Online = true;
                    info.Level = (byte)Math.Clamp(body.Level, 1, 255);
                    info.ClassId = body.ClassId;
                    online = true;
                    break;
                }
            }

            if (!online && _state.Names.TryGetValue(key, out string? ownerAccount) &&
                _state.Accounts.TryGetValue(ownerAccount, out AccountRecord? owner) &&
                owner.Characters.TryGetValue(key, out CharacterRecord? record))
            {
                info.Level = (byte)Math.Clamp(
                    _worlds.GameData.Progression.LevelForXp(record.TotalXp), 1, 255);
                info.ClassId = record.ClassId;
                info.MinutesSinceSeen = record.LastSeenUnix > 0
                    ? (int)Math.Max(0, (now - record.LastSeenUnix) / 60)
                    : -1;
            }

            list.Add(info);
        }

        Send(peer, new FriendsState(list));
    }

    /// <summary>Everyone who lists this character as a friend gets a note + a fresh roster.</summary>
    private void NotifyFriendWatchers(string characterName, bool online)
    {
        string key = characterName.ToLowerInvariant();
        foreach ((PeerId otherPeer, PlayerSession other) in _sessions.ToArray())
        {
            if (!other.HandshakeComplete)
            {
                continue;
            }

            AccountRecord account = GetOrCreateAccount(other.AccountId);
            if (account.Friends.Contains(key))
            {
                SendSystemLine(otherPeer, online
                    ? $"Ton ami « {characterName} » vient de se connecter."
                    : $"Ton ami « {characterName} » s'est déconnecté.");
                SendFriends(otherPeer, other);
            }
        }
    }

    /// <summary>The canonical display casing of a known character name (falls back to the key).</summary>
    private string DisplayNameOf(string nameKey)
    {
        if (_state.Names.TryGetValue(nameKey, out string? ownerAccount) &&
            _state.Accounts.TryGetValue(ownerAccount, out AccountRecord? owner) &&
            owner.Characters.TryGetValue(nameKey, out CharacterRecord? record))
        {
            return record.Name;
        }

        return nameKey;
    }

    private void SendSystemLine(PeerId peer, string text)
        => SendWith(peer, new ChatMessage(ChatChannel.System, "", "", text).Write);

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
        // Snapshot the roster BEFORE leaving: a 2-man party DISBANDS on leave, and the
        // (then empty) remainder left the OTHER member with a stale party icon — same
        // trap as the kick. And ALWAYS answer the caller, even without a party: a stale
        // client asking to leave heals itself with the empty roster.
        Party? party = _parties.GetParty(peer.Value);
        int[] everyone = party is not null ? party.Members.ToArray() : [];
        _parties.Leave(peer.Value);
        foreach (int member in everyone)
        {
            SendPartyStateTo(new PeerId(member)); // dissolved → empty roster for each
        }

        SendPartyStateTo(peer);

        // Seats whose party dissolved release their offline ghosts.
        List<int>? stale = null;
        foreach (int key in _partyGhosts.Keys)
        {
            if (_parties.GetParty(key) is null)
            {
                (stale ??= new List<int>()).Add(key);
            }
        }

        if (stale is not null)
        {
            foreach (int key in stale)
            {
                _partyGhosts.Remove(key);
            }
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
            ? ls.Name
            : _partyGhosts.TryGetValue(party.Leader, out string? ghostLeader) ? ghostLeader : "?";
        uint tick = _worlds.OpenWorld.Tick;
        var members = new List<PartyMemberInfo>(party.Count);
        foreach (int member in party.Members)
        {
            if (!_sessions.TryGetValue(new PeerId(member), out PlayerSession? ms) ||
                !TryGetEntity(ms, out ServerEntity? body))
            {
                // A DISCONNECTED member: keep his row, greyed — the frame shows « (déco) ».
                if (_partyGhosts.TryGetValue(member, out string? ghostName))
                {
                    members.Add(new PartyMemberInfo
                    {
                        Name = ghostName, EntityId = -1, Online = false,
                    });
                }

                continue;
            }

            var info = new PartyMemberInfo
            {
                Name = ms.Name,
                EntityId = body!.Id,
                ClassId = body.ClassId,
                Level = (byte)System.Math.Clamp(body.Level, 1, 255),
                Health = body.Health,
                MaxHealth = body.EffectiveMaxHealth,
                Resource = (int)body.CurrentResource,
                MaxResource = body.EffectiveMaxResource,
                X = body.Position.X,
                Y = body.Position.Y,
            };

            // Timed buffs, with their remaining seconds — the frames draw them as icons.
            var effects = new List<(byte, float)>();
            foreach (ActiveEffect e in body.ActiveEffects)
            {
                if (e.ExpiresAtTick > tick)
                {
                    effects.Add(((byte)e.Type, (e.ExpiresAtTick - tick) * SimulationConstants.TickDelta));
                }
            }

            info.Effects = effects.ToArray();
            members.Add(info);
        }

        Send(peer, new PartyState(leaderName, members));
    }

    /// <summary>Live vitals for the party frames: re-send the roster a few times per second.</summary>
    private void BroadcastPartyVitals()
    {
        foreach (PeerId peer in _sessions.Keys)
        {
            if (_parties.GetParty(peer.Value) is not null)
            {
                SendPartyStateTo(peer);
            }
        }
    }

    private void HandlePartyKick(PeerId peer, PartyKick kick)
    {
        Party? party = _parties.GetParty(peer.Value);
        if (party is null || party.Leader != peer.Value)
        {
            return; // only the leader throws people out
        }

        // Find the member session owning that entity.
        foreach (int member in party.Members)
        {
            var memberPeer = new PeerId(member);
            if (member != peer.Value &&
                _sessions.TryGetValue(memberPeer, out PlayerSession? ms) && ms.EntityId == kick.TargetEntityId)
            {
                // Snapshot the roster BEFORE the kick: a 2-man party DISBANDS on kick, and
                // iterating the (then empty) remainder left the LEADER with a stale party icon.
                int[] everyone = party.Members.ToArray();
                _parties.Leave(member);
                Send(memberPeer, new PartyState(string.Empty, []));
                foreach (int rest in everyone)
                {
                    if (rest != member)
                    {
                        SendPartyStateTo(new PeerId(rest)); // no party left → empty roster
                    }
                }

                return;
            }
        }
    }

    // -------------------------------------------------------------- Instances

    /// <summary>Bind the hearthstone HERE — only while standing near an innkeeper (npcType 7).</summary>
    private void HandleSetHome(PeerId peer, PlayerSession session)
    {
        if (!TryGetEntity(session, out ServerEntity? self) || self is null || self.IsDead)
        {
            return;
        }

        foreach (ServerEntity e in session.CurrentWorld.Entities.Values)
        {
            if (e.Kind == EntityKind.Npc && e.RaceId == 7 &&
                Vec2.DistanceSquared(self.Position, e.Position) <=
                SimulationConstants.InnkeeperRange * SimulationConstants.InnkeeperRange)
            {
                self.HomeX = e.Position.X;
                self.HomeY = e.Position.Y + 1.5f; // land beside the innkeeper, not inside her
                Send(peer, new InstanceResult(true, 0,
                    $"Cette auberge est désormais ton foyer ({e.Name})."));
                _log($"'{self.Name}' bound their hearthstone at {e.Name}.");
                return;
            }
        }

        Send(peer, new InstanceResult(false, 0, "Il faut être auprès d'un aubergiste."));
    }

    /// <summary>The HEARTHSTONE: teleport home, 15-minute cooldown, instance-safe.</summary>
    private void HandleHearthstone(PeerId peer, PlayerSession session)
    {
        if (!TryGetEntity(session, out ServerEntity? self) || self is null || self.IsDead)
        {
            return;
        }

        uint now = _worlds.OpenWorld.Tick;
        if (self.HearthReadyTick > now)
        {
            int minutes = (int)System.Math.Ceiling(
                (self.HearthReadyTick - now) * SimulationConstants.TickDelta / 60f);
            Send(peer, new InstanceResult(false, 0,
                $"La pierre de foyer se recharge encore ({minutes} min)."));
            return;
        }

        if (self.IsCasting)
        {
            return; // already channelling something (maybe the stone itself)
        }

        // A 5-second CHANNEL, WoW-style: the cast bar shows, moving or getting hit cancels.
        // The GameServer resolves it in Tick (the combat pipeline skips ids ≥ 200).
        self.BeginCast(SimulationConstants.HearthstoneCastId, self.Id,
            session.CurrentWorld.Tick, SimulationConstants.HearthstoneCastTicks);
    }

    /// <summary>Resolve finished hearthstone channels: teleport home, start the cooldown.</summary>
    private void TickHearthstones()
    {
        foreach (KeyValuePair<PeerId, PlayerSession> pair in _sessions)
        {
            PlayerSession session = pair.Value;
            if (!session.HandshakeComplete ||
                !TryGetEntity(session, out ServerEntity? self) || self is null ||
                self.CastAbilityId != SimulationConstants.HearthstoneCastId ||
                session.CurrentWorld.Tick < self.CastEndTick)
            {
                continue;
            }

            self.CancelCast();

            // From an instance, the stone walks you OUT first, then carries you home.
            if (!ReferenceEquals(session.CurrentWorld, _worlds.OpenWorld))
            {
                HandleLeaveInstance(pair.Key, session, notify: false);
            }

            if (!TryGetEntity(session, out ServerEntity? home) || home is null)
            {
                continue;
            }

            _worlds.OpenWorld.Teleport(home, new Vec2(home.HomeX, home.HomeY));
            home.HearthReadyTick = _worlds.OpenWorld.Tick + SimulationConstants.HearthstoneCooldownTicks;
            Send(pair.Key, new InstanceResult(true, 0, "La pierre de foyer te ramène chez toi."));
        }
    }

    /// <summary>
    /// The WALK-IN gates: a player standing on a portal enters their party's instance (min 2 —
    /// solo adventurers are told to find a group); standing on the exit portal inside an
    /// instance walks back out. The meeting stone summons the missing party members when at
    /// least two of them stand beside it. Runs every 10 ticks.
    /// </summary>
    private void TickPortalsAndStones()
    {
        const float GateRangeSq = 2.2f * 2.2f;
        const float StoneRangeSq = 3f * 3f;
        long now = _worlds.OpenWorld.Tick;

        // Drop instance registrations whose world has emptied and been disposed.
        List<int>? dead = null;
        foreach (KeyValuePair<int, World.World> entry in _partyInstances)
        {
            if (!_worlds.AllWorlds.Contains(entry.Value))
            {
                (dead ??= new List<int>()).Add(entry.Key);
            }
        }

        if (dead is not null)
        {
            foreach (int key in dead) { _partyInstances.Remove(key); }
        }

        foreach (KeyValuePair<PeerId, PlayerSession> pair in _sessions)
        {
            PlayerSession session = pair.Value;
            if (!session.HandshakeComplete)
            {
                continue;
            }

            if (!TryGetEntity(session, out ServerEntity? self) || self is null || self.IsDead)
            {
                continue;
            }

            if (ReferenceEquals(session.CurrentWorld, _worlds.OpenWorld))
            {
                foreach ((byte defId, Vec2 gate, Vec2 _) in _portals)
                {
                    if (Vec2.DistanceSquared(self!.Position, gate) <= GateRangeSq)
                    {
                        TryPortalEnter(pair.Key, session, defId);
                        break;
                    }
                }
            }
            else
            {
                // Inside an instance: the exit portal stands at (-4,-4).
                if (Vec2.DistanceSquared(self!.Position, new Vec2(-4f, -4f)) <= GateRangeSq)
                {
                    HandleLeaveInstance(pair.Key, session, notify: true);
                }
            }
        }

        // Meeting stones: ≥2 party members beside a stone pull the rest of the party to it.
        foreach ((byte _, Vec2 _, Vec2 stone) in _portals)
        {
            foreach (Party party in _parties.AllParties)
            {
                if (_stoneFiredAt.TryGetValue(party.Leader, out long at) && now - at < 300)
                {
                    continue; // 15s per-party cooldown
                }

                var present = new List<int>();
                var absent = new List<(PeerId peer, PlayerSession session, ServerEntity entity)>();
                foreach (int member in party.Members)
                {
                    var memberPeer = new PeerId(member);
                    if (!_sessions.TryGetValue(memberPeer, out PlayerSession? ms) || !ms.HandshakeComplete ||
                        !ReferenceEquals(ms.CurrentWorld, _worlds.OpenWorld) ||
                        !TryGetEntity(ms, out ServerEntity? me) || me is null || me.IsDead)
                    {
                        continue;
                    }

                    if (Vec2.DistanceSquared(me!.Position, stone) <= StoneRangeSq)
                    {
                        present.Add(member);
                    }
                    else
                    {
                        absent.Add((memberPeer, ms, me!));
                    }
                }

                if (present.Count >= 2 && absent.Count > 0)
                {
                    _stoneFiredAt[party.Leader] = now;
                    int slot = 0;
                    foreach ((PeerId memberPeer, PlayerSession _, ServerEntity entity) in absent)
                    {
                        _worlds.OpenWorld.Teleport(entity, new Vec2(stone.X + 1f + slot, stone.Y + 1.2f));
                        slot++;
                        Send(memberPeer, new InstanceResult(true, 0,
                            "La pierre de téléportation t'a invoqué auprès de ton groupe."));
                    }

                    _log($"Meeting stone summoned {absent.Count} member(s) of party {party.Id}.");
                }
            }
        }
    }

    /// <summary>A player walked into a gate: join the party's running instance, or open it.</summary>
    private void TryPortalEnter(PeerId peer, PlayerSession session, byte instanceDefId)
    {
        if (!_worlds.GameData.TryGetInstance(instanceDefId, out InstanceDefinition def))
        {
            return;
        }

        long now = _worlds.OpenWorld.Tick;
        Party? party = _parties.GetParty(peer.Value);

        // Refusals are throttled (the player is STANDING on the portal, every 10 ticks…).
        bool canComplain = !_portalMsgAt.TryGetValue(peer.Value, out long at) || now - at >= 100;

        if (party is null || !WorldManager.CanEnter(def, party.Count, out string reason0))
        {
            string reason = party is null
                ? $"{def.Name} demande un groupe d'au moins {def.MinPlayers} joueurs."
                : ReasonOf(def, party.Count);
            if (canComplain)
            {
                _portalMsgAt[peer.Value] = now;
                Send(peer, new InstanceResult(false, instanceDefId, reason));
            }

            return;
        }

        // The party's instance: reuse the running one, or open it scaled to the FULL party.
        if (!_partyInstances.TryGetValue(party.Leader, out World.World? instance) ||
            !_worlds.AllWorlds.Contains(instance))
        {
            instance = _worlds.CreateInstance(def, party.Count);
            instance.PartySiblingsOf = PartySiblingEntityIds;
            _partyInstances[party.Leader] = instance;
            _log($"Instance '{def.Name}' opened for party {party.Id} ({party.Count} player(s)).");
        }

        if (WorldManager.TransferPlayer(session.CurrentWorld, instance, session.EntityId, Vec2.Zero))
        {
            session.CurrentWorld = instance;
            Send(peer, new InstanceResult(true, instanceDefId, $"Tu entres dans {def.Name}."));
        }

        static string ReasonOf(InstanceDefinition def, int count)
        {
            WorldManager.CanEnter(def, count, out string reason);
            return reason;
        }
    }

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
        instance.PartySiblingsOf = PartySiblingEntityIds;

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

        // Come out WHERE YOU WENT IN: a step in front of this dungeon's own gate (clear of
        // its 2.2 u trigger so you don't instantly walk back in) — not at the sanctuary.
        Vec2 exitAt = Vec2.Zero;
        InstanceDefinition? leavingDef = _worlds.DefinitionOf(instance);
        if (leavingDef is not null)
        {
            foreach ((byte defId, Vec2 gate, Vec2 _) in _portals)
            {
                if (defId == leavingDef.Id)
                {
                    float len = MathF.Sqrt((gate.X * gate.X) + (gate.Y * gate.Y));
                    float ox = len > 0.01f ? -gate.X / len : 0f;
                    float oy = len > 0.01f ? -gate.Y / len : -1f;
                    exitAt = new Vec2(gate.X + (ox * 4f), gate.Y + (oy * 4f));
                    break;
                }
            }
        }

        if (WorldManager.TransferPlayer(instance, _worlds.OpenWorld, session.EntityId, exitAt))
        {
            session.CurrentWorld = _worlds.OpenWorld;
            _worlds.DestroyInstanceIfEmpty(instance);
            if (notify)
            {
                Send(peer, new InstanceResult(true, 0, "Te revoilà devant l'entrée."));
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
            target.EquippedWeaponId, target.EquippedArmorId, target.TotalXp,
            target.CopyEquipment()));
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
        Send(peer, new InventoryState(self.CopyEquipment(), self.Inventory.Stacks, self.Inventory.Capacity));
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

    /// <summary>Push a player's quest progress (active quest, kills, chain position).</summary>
    private void SendQuestState(PeerId peer, PlayerSession session)
    {
        if (TryGetEntity(session, out ServerEntity? self))
        {
            Send(peer, new QuestStateMessage(self!.ActiveQuestId, self.QuestKills, self.QuestCompletedUpTo));
        }
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
            self.EffectiveAttackPower, self.EffectiveDefense,
            self.HonorPoints, self.RepAlliance, self.RepHorde, self.IsBandit));
        Send(peer, new InventoryState(self.CopyEquipment(), self.Inventory.Stacks, self.Inventory.Capacity));
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
                self.EffectiveAttackPower, self.EffectiveDefense,
                self.HonorPoints, self.RepAlliance, self.RepHorde, self.IsBandit));
            Send(peer, new InventoryState(self.CopyEquipment(), self.Inventory.Stacks, self.Inventory.Capacity));
        }
    }

    // --------------------------------------------------------------- Helpers

    private bool TryGetEntity(PlayerSession session, out ServerEntity? entity)
        => session.CurrentWorld.Entities.TryGetValue(session.EntityId, out entity);

    private QuestDefinition[]? _questsWithZones;

    /// <summary>
    /// The quest catalogue with HUNTING ZONES filled in: each quest's zone is the circle around
    /// the open-world monsters it targets — computed once from the real spawns, so the map hint
    /// always points at the actual hunting grounds (Studio quests included).
    /// </summary>
    private QuestDefinition[] QuestsWithZones(GameData data)
    {
        if (_questsWithZones is not null)
        {
            return _questsWithZones;
        }

        var result = new List<QuestDefinition>();
        foreach (QuestDefinition q in data.Quests)
        {
            float sumX = 0f, sumY = 0f;
            int n = 0;
            foreach (ServerEntity e in _worlds.OpenWorld.Entities.Values)
            {
                if (e.Kind == EntityKind.Monster && e.RaceId == q.TargetMonsterId)
                {
                    sumX += e.Position.X;
                    sumY += e.Position.Y;
                    n++;
                }
            }

            if (n == 0)
            {
                result.Add(q);
                continue;
            }

            var center = new Vec2(sumX / n, sumY / n);
            float radius = 6f;
            foreach (ServerEntity e in _worlds.OpenWorld.Entities.Values)
            {
                if (e.Kind == EntityKind.Monster && e.RaceId == q.TargetMonsterId)
                {
                    float d = MathF.Sqrt(Vec2.DistanceSquared(center, e.Position)) + 5f;
                    if (d > radius) { radius = d; }
                }
            }

            result.Add(new QuestDefinition
            {
                Id = q.Id, Name = q.Name, Description = q.Description, TurnInText = q.TurnInText,
                TargetMonsterId = q.TargetMonsterId, RequiredKills = q.RequiredKills,
                RewardXp = q.RewardXp, RewardGold = q.RewardGold, RewardItemId = q.RewardItemId,
                NextQuestId = q.NextQuestId,
                ZoneX = center.X, ZoneY = center.Y, ZoneRadius = radius,
            });
        }

        _questsWithZones = result.ToArray();
        return _questsWithZones;
    }

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
            error = "Le nom doit faire entre 2 et 16 caractères.";
            return false;
        }

        foreach (char c in display)
        {
            if (!char.IsLetterOrDigit(c))
            {
                error = "Le nom ne peut contenir que des lettres et des chiffres.";
                return false;
            }
        }

        string nameKey = display.ToLowerInvariant();
        string accountKey = accountId.ToLowerInvariant();

        if (_state.Names.TryGetValue(nameKey, out string? owner) &&
            !string.Equals(owner, accountKey, StringComparison.Ordinal))
        {
            error = $"Le nom « {display} » est déjà pris sur ce serveur.";
            return false;
        }

        if (!_activeNames.Add(display)) // case-insensitive; false if currently online
        {
            error = $"« {display} » est déjà en ligne.";
            return false;
        }

        _state.Names[nameKey] = accountKey; // durable, server-wide, across both factions
        return true;
    }

    /// <summary>Flush everything to disk NOW — called by the host on graceful shutdown.</summary>
    public void SaveNow() => PersistAll();

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
    private void Send(PeerId peer, QuestStateMessage msg) => SendWith(peer, msg.Write);

    private void Send(PeerId peer, QuestCatalogMessage msg) => SendWith(peer, msg.Write);
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
    private void Send(PeerId peer, FriendsState msg) => SendWith(peer, msg.Write);

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
