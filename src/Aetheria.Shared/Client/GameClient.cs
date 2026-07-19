using Aetheria.Shared;
using Aetheria.Shared.Combat;
using Aetheria.Shared.Items;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Shared.Client;

/// <summary>
/// A headless client connection state machine used to exercise the server end-to-end without a
/// rendering engine. The Unity client will grow its own richer version (prediction, interpolation,
/// rendering), but the wire handling and message flow modelled here are exactly what it must do.
/// </summary>
public sealed class GameClient
{
    private readonly IClientTransport _transport;
    private readonly PacketWriter _writer = new();
    private uint _inputSequence;

    public GameClient(IClientTransport transport) => _transport = transport;

    /// <summary>Our own entity id once the handshake succeeds; null until then.</summary>
    public int? EntityId { get; private set; }

    public bool WasRejected { get; private set; }
    public string? RejectReason { get; private set; }

    /// <summary>Most recent server tick we have received a snapshot for.</summary>
    public uint LastTick { get; private set; }

    /// <summary>Entities inside our area of interest as of the last snapshot.</summary>
    public IReadOnlyList<EntitySnapshot> Visible { get; private set; } = [];

    /// <summary>Last measured round-trip time in milliseconds, or -1 if unknown.</summary>
    public long LastRttMs { get; private set; } = -1;

    /// <summary>The most recent combat event we received, if any (for status printing).</summary>
    public CombatEventMessage? LastCombat { get; private set; }

    /// <summary>How many combat events we've been dealt, in total (attacker or target = us).</summary>
    public int CombatEventsSeen { get; private set; }

    /// <summary>How many kills we've landed (a combat event where we are the attacker and the target died).</summary>
    public int KillsByMe { get; private set; }

    /// <summary>
    /// Combat events accumulated since the last <see cref="DrainCombatFeed"/> call — every event in
    /// our area of interest, not just our own. Drives attack animations and floating damage text.
    /// </summary>
    private readonly List<CombatEventMessage> _combatFeed = [];

    /// <summary>Return the combat events received since the last drain, then clear the feed.</summary>
    public IReadOnlyList<CombatEventMessage> DrainCombatFeed()
    {
        if (_combatFeed.Count == 0)
        {
            return [];
        }

        var copy = _combatFeed.ToArray();
        _combatFeed.Clear();
        return copy;
    }

    // --- Self status (from PlayerStatus / InventoryState) ---
    public int Level { get; private set; } = 1;
    public int TotalXp { get; private set; }
    public int XpForNextLevel { get; private set; } = -1;
    public int Gold { get; private set; }
    /// <summary>Item id per equipment slot (index = (int)EquipSlot).</summary>
    public IReadOnlyList<byte> EquipmentSlots { get; private set; } = new byte[EquipSlots.Count];

    public byte EquippedWeaponId => EquipmentSlots[(int)EquipSlot.Weapon];
    public byte EquippedArmorId => EquipmentSlots[(int)EquipSlot.Chest];
    public int InventoryStackCount { get; private set; }
    public IReadOnlyList<ItemStack> InventoryItems { get; private set; } = [];
    public int EffectiveAttack { get; private set; }
    public int EffectiveDefense { get; private set; }

    // --- Account bank (from BankState) ---
    public int BankGold { get; private set; }
    public IReadOnlyList<ItemStack> BankItems { get; private set; } = [];
    public int BankStackCount { get; private set; }

    // --- Party & instance state ---
    public string PartyLeader { get; private set; } = string.Empty;
    public int PartySize { get; private set; }
    public string? PendingInviteFrom { get; private set; }

    /// <summary>The party roster with LIVE vitals and buffs (party frames feed on this).</summary>
    public IReadOnlyList<PartyMemberInfo> PartyMembers { get; private set; } = [];

    /// <summary>Forget the pending invite (the player answered the dialog).</summary>
    public void ClearPendingInvite() => PendingInviteFrom = null;
    public string LastInstanceMessage { get; private set; } = string.Empty;
    public bool InInstance { get; private set; }

    // --- Login flow (account first, then the server's single character) ---

    /// <summary>True once the account is authenticated (the character screen can show).</summary>
    public bool LoggedIn { get; private set; }

    /// <summary>Login/creation error to display, cleared on the next attempt.</summary>
    public string LoginError { get; private set; } = string.Empty;

    /// <summary>The account's character on this server, if any (valid once LoggedIn).</summary>
    public bool HasCharacter { get; private set; }
    public string CharacterName { get; private set; } = string.Empty;
    public byte CharacterRaceId { get; private set; }
    public byte CharacterClassId { get; private set; }
    public Gender CharacterGender { get; private set; }
    public byte CharacterLevel { get; private set; } = 1;
    public Appearance CharacterAppearance { get; private set; }

    /// <summary>Item id per equip slot for the account's character (lobby preview loadout).</summary>
    public byte[] CharacterEquipment { get; private set; } = System.Array.Empty<byte>();

    /// <summary>Player chat lines received since the last drain (world chat, players only).</summary>
    private readonly List<ChatMessage> _chatFeed = [];

    /// <summary>Return the chat lines received since the last call, then clear the feed.</summary>
    public IReadOnlyList<ChatMessage> DrainChatFeed()
    {
        if (_chatFeed.Count == 0)
        {
            return [];
        }

        var copy = _chatFeed.ToArray();
        _chatFeed.Clear();
        return copy;
    }

    /// <summary>Open the socket toward the server and authenticate the account.</summary>
    public void Connect(string host, int port, string accountId, string accountSecret,
        bool createAccount = false)
    {
        _transport.Connect(host, port);
        LoginError = string.Empty;
        Send(new Login(SimulationConstants.ProtocolVersion, accountId, accountSecret, createAccount));
    }

    /// <summary>Create this server's one character for the account, then enter the world.</summary>
    public void SendCreateCharacter(string name, byte raceId, byte classId, Gender gender,
        Appearance appearance = default)
    {
        LoginError = string.Empty;
        Send(new CreateCharacter(name, raceId, classId, gender, appearance));
    }

    /// <summary>Enter the world with the existing character.</summary>
    public void SendEnterWorld()
    {
        LoginError = string.Empty;
        Send(new EnterWorld());
    }

    /// <summary>Teleport home with the hearthstone (15 min cooldown, server-enforced).</summary>
    public void SendHearthstone() => Send(new Hearthstone());

    /// <summary>Bind the hearthstone to the inn you're standing at.</summary>
    public void SendSetHome() => Send(new SetHome());

    /// <summary>Permanently delete this server's character (character screen only).</summary>
    public void SendDeleteCharacter()
    {
        LoginError = string.Empty;
        Send(new DeleteCharacter());
    }

    public void SendBank(BankOp op, byte itemId, int amount) => Send(new BankTransaction(op, itemId, amount));

    /// <summary>Equip a weapon/armor from the bags (or unequip a slot with itemId 0).</summary>
    public void SendEquipItem(byte itemId, byte slot) => Send(new EquipItem(itemId, slot));

    /// <summary>Unequip a slot INTO a chosen bag cell (drag from the character sheet).</summary>
    public void SendUnequipTo(byte slot, byte bagIndex) => Send(new EquipItem(0, slot, bagIndex));

    public void SendMoveItem(byte fromIndex, byte toIndex) => Send(new MoveItem(fromIndex, toIndex));

    public void SendVendor(bool sell, byte itemId, byte quantity) => Send(new VendorAction(sell, itemId, quantity));

    /// <summary>Druid shapeshift: 0 humanoid, 1 bear, 2 owl, 3 cat.</summary>
    public void SendShapeShift(byte formId) => Send(new ShapeShift(formId));

    /// <summary>Say something in the world chat.</summary>
    public void SendChat(string text) => Send(new ChatSend(text ?? string.Empty));

    /// <summary>Fight this target (the server auto-swings); 0 stops attacking.</summary>
    public void SendAttackTarget(int targetEntityId) => Send(new AttackTarget(targetEntityId));

    /// <summary>Accept (or turn in) a quest at the quest giver. The server validates everything.</summary>
    public void SendQuestAction(byte questId, bool turnIn) => Send(new QuestAction(questId, turnIn));

    /// <summary>The quest currently pursued (0 = none), its kill counter, and chain position.</summary>
    public byte ActiveQuestId { get; private set; }
    public int QuestKills { get; private set; }
    public byte QuestCompletedUpTo { get; private set; }

    /// <summary>The server's quest catalogue (sent at login); null until received.</summary>
    public Aetheria.Shared.Data.QuestDefinition[]? QuestCatalog { get; private set; }

    /// <summary>Bumped each time a catalogue arrives, so the UI can re-apply it once.</summary>
    public int QuestCatalogVersion { get; private set; }

    public void SendPartyInvite(int targetEntityId) => Send(new PartyInvite(targetEntityId));

    public void SendPartyRespond(bool accept) => Send(new PartyRespond(accept));

    public void SendPartyLeave() => Send(new PartyLeave());

    public void SendPartyKick(int targetEntityId) => Send(new PartyKick(targetEntityId));

    public void SendEnterInstance(byte instanceDefId) => Send(new EnterInstance(instanceDefId));

    public void SendLeaveInstance() => Send(new LeaveInstance());

    public void SendInput(Vec2 direction, float facingRadians = 0f, bool jump = false)
    {
        _inputSequence++;
        Send(new InputCommand(_inputSequence, direction, facingRadians, jump));
    }

    public void SendUseAbility(byte abilityId, int targetEntityId)
        => Send(new UseAbility(abilityId, targetEntityId));

    public void SendUseRacial() => Send(new UseRacial());

    public void SendLootCorpse(int corpseEntityId) => Send(new LootCorpse(corpseEntityId));

    /// <summary>Ask the server for a corpse's contents (opens the loot window).</summary>
    public void SendOpenCorpse(int corpseEntityId) => Send(new OpenCorpse(corpseEntityId));

    /// <summary>Take all of one item from an open corpse — or its gold when <paramref name="itemId"/> is 0.</summary>
    public void SendLootItem(int corpseEntityId, byte itemId) => Send(new LootItem(corpseEntityId, itemId));

    // --- Open corpse (loot window) state ---
    public int OpenCorpseId { get; private set; } = -1;
    public int OpenCorpseGold { get; private set; }
    public IReadOnlyList<ItemStack> OpenCorpseItems { get; private set; } = [];

    /// <summary>Forget the open corpse (client closed the window).</summary>
    public void CloseCorpse()
    {
        OpenCorpseId = -1;
        OpenCorpseGold = 0;
        OpenCorpseItems = [];
    }

    // --- Social: inspect / duel / trade / drop ---

    public void SendInspect(int targetEntityId) => Send(new Inspect(targetEntityId));

    public void SendDuelRequest(int targetEntityId, bool toDeath) => Send(new DuelRequest(targetEntityId, toDeath));

    public void SendDuelRespond(bool accept) => Send(new DuelRespond(accept));

    public void SendTradeRequest(int targetEntityId) => Send(new TradeRequest(targetEntityId));

    public void SendTradeRespond(bool accept) => Send(new TradeRespond(accept));

    public void SendTradeSetOffer(int gold, IReadOnlyList<ItemStack> items) => Send(new TradeSetOffer(gold, items));

    public void SendTradeAccept() => Send(new TradeAccept());

    public void SendTradeCancel() => Send(new TradeCancel());

    public void SendDropItem(byte itemId, int quantity) => Send(new DropItem(itemId, quantity));

    /// <summary>Last inspection result received, if any (cleared by the UI).</summary>
    public InspectResult? LastInspect { get; private set; }

    public void ClearInspect() => LastInspect = null;

    /// <summary>Pending duel challenge: challenger name + stakes; null when none.</summary>
    public string? PendingDuelFrom { get; private set; }
    public bool PendingDuelToDeath { get; private set; }

    public void ClearPendingDuel() => PendingDuelFrom = null;

    /// <summary>Active duel opponent (entity id), or -1. Message carries start/end text.</summary>
    public int DuelOpponentId { get; private set; } = -1;
    public bool DuelToDeath { get; private set; }
    public string DuelMessage { get; private set; } = string.Empty;

    /// <summary>Pending trade proposal (proposer name), or null.</summary>
    public string? PendingTradeFrom { get; private set; }

    public void ClearPendingTrade() => PendingTradeFrom = null;

    /// <summary>Live trade window state; TradeActive false means no trade (window closed).</summary>
    public bool TradeActive { get; private set; }
    public string TradePartner { get; private set; } = string.Empty;
    public int TradeMyGold { get; private set; }
    public IReadOnlyList<ItemStack> TradeMyItems { get; private set; } = [];
    public int TradeTheirGold { get; private set; }
    public IReadOnlyList<ItemStack> TradeTheirItems { get; private set; } = [];
    public bool TradeMyAccepted { get; private set; }
    public bool TradeTheirAccepted { get; private set; }
    public string TradeMessage { get; private set; } = string.Empty;

    /// <summary>Find the nearest visible corpse, or -1 if none are in view.</summary>
    public int FindNearestCorpse()
    {
        if (!TryGetSelf(out EntitySnapshot self))
        {
            return -1;
        }

        int nearest = -1;
        float bestDistSq = float.MaxValue;
        foreach (EntitySnapshot e in Visible)
        {
            if (e.Kind != EntityKind.Corpse)
            {
                continue;
            }

            float d = Vec2.DistanceSquared(self.Position, e.Position);
            if (d < bestDistSq)
            {
                bestDistSq = d;
                nearest = e.Id;
            }
        }

        return nearest;
    }

    public void SendPing() => Send(new Ping(SharedClock.NowMs));

    public void SendDisconnect() => Send(new Disconnect());

    /// <summary>Find the nearest visible monster, or -1 if none are in view.</summary>
    public int FindNearestMonster()
    {
        if (!TryGetSelf(out EntitySnapshot self))
        {
            return -1;
        }

        int nearest = -1;
        float bestDistSq = float.MaxValue;
        foreach (EntitySnapshot e in Visible)
        {
            if (e.Kind != EntityKind.Monster)
            {
                continue;
            }

            float d = Vec2.DistanceSquared(self.Position, e.Position);
            if (d < bestDistSq)
            {
                bestDistSq = d;
                nearest = e.Id;
            }
        }

        return nearest;
    }

    /// <summary>Process every datagram currently waiting from the server.</summary>
    public void Pump()
    {
        while (_transport.Poll(out byte[] payload))
        {
            Handle(payload);
        }
    }

    private void Handle(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return;
        }

        try
        {
            var reader = new PacketReader(payload);
            var type = (MessageType)reader.ReadByte();

            switch (type)
            {
                case MessageType.ConnectAccepted:
                    ConnectAccepted accepted = ConnectAccepted.Read(ref reader);
                    EntityId = accepted.EntityId;
                    break;

                case MessageType.ConnectRejected:
                    ConnectRejected rejected = ConnectRejected.Read(ref reader);
                    WasRejected = true;
                    RejectReason = rejected.Reason;
                    break;

                case MessageType.Snapshot:
                    SnapshotMessage snapshot = SnapshotMessage.Read(ref reader);
                    LastTick = snapshot.Tick;
                    Visible = snapshot.Entities;
                    break;

                case MessageType.Pong:
                    Pong pong = Pong.Read(ref reader);
                    LastRttMs = SharedClock.NowMs - pong.ClientTimeMs;
                    break;

                case MessageType.PlayerStatus:
                    PlayerStatus status = PlayerStatus.Read(ref reader);
                    Level = status.Level;
                    TotalXp = status.TotalXp;
                    XpForNextLevel = status.XpForNextLevel;
                    Gold = status.Gold;
                    EffectiveAttack = status.EffectiveAttack;
                    EffectiveDefense = status.EffectiveDefense;
                    break;

                case MessageType.InventoryState:
                    InventoryState inv = InventoryState.Read(ref reader);
                    EquipmentSlots = inv.Equipment;
                    InventoryItems = inv.Items;
                    InventoryStackCount = inv.Items.Count;
                    break;

                case MessageType.BankState:
                    BankState bankState = BankState.Read(ref reader);
                    BankGold = bankState.Gold;
                    BankItems = bankState.Items;
                    BankStackCount = bankState.Items.Count;
                    break;

                case MessageType.PartyState:
                    PartyState partyState = PartyState.Read(ref reader);
                    PartyLeader = partyState.LeaderName;
                    PartyMembers = partyState.Members;
                    PartySize = partyState.Members.Count;
                    break;

                case MessageType.PartyInviteNotice:
                    PartyInviteNotice notice = PartyInviteNotice.Read(ref reader);
                    PendingInviteFrom = notice.InviterName;
                    break;

                case MessageType.InstanceResult:
                    InstanceResult result = InstanceResult.Read(ref reader);
                    LastInstanceMessage = result.Message;
                    if (result.Ok)
                    {
                        InInstance = result.InstanceDefId != 0;
                    }

                    break;

                case MessageType.LoginResult:
                    LoginResult login = LoginResult.Read(ref reader);
                    if (login.Ok)
                    {
                        LoggedIn = true;
                        HasCharacter = login.HasCharacter;
                        CharacterName = login.CharacterName;
                        CharacterRaceId = login.RaceId;
                        CharacterClassId = login.ClassId;
                        CharacterGender = login.Gender;
                        CharacterLevel = login.Level;
                        CharacterAppearance = login.Appearance;
                        CharacterEquipment = login.Equipment;
                    }
                    else
                    {
                        LoginError = login.Message;
                    }

                    break;

                case MessageType.ChatMessage:
                    ChatMessage chat = ChatMessage.Read(ref reader);
                    if (_chatFeed.Count < 128)
                    {
                        _chatFeed.Add(chat);
                    }

                    break;

                case MessageType.InspectResult:
                    LastInspect = InspectResult.Read(ref reader);
                    break;

                case MessageType.QuestState:
                    QuestStateMessage quest = QuestStateMessage.Read(ref reader);
                    ActiveQuestId = quest.ActiveQuestId;
                    QuestKills = quest.Kills;
                    QuestCompletedUpTo = quest.CompletedUpTo;
                    break;

                case MessageType.QuestCatalog:
                    QuestCatalogMessage catalog = QuestCatalogMessage.Read(ref reader);
                    QuestCatalog = catalog.Quests;
                    QuestCatalogVersion++;
                    break;

                case MessageType.DuelNotice:
                    DuelNotice duelNotice = DuelNotice.Read(ref reader);
                    PendingDuelFrom = duelNotice.ChallengerName;
                    PendingDuelToDeath = duelNotice.ToDeath;
                    break;

                case MessageType.DuelState:
                    DuelState duelState = DuelState.Read(ref reader);
                    DuelOpponentId = duelState.Active ? duelState.OpponentEntityId : -1;
                    DuelToDeath = duelState.ToDeath;
                    DuelMessage = duelState.Message;
                    break;

                case MessageType.TradeNotice:
                    TradeNotice tradeNotice = TradeNotice.Read(ref reader);
                    PendingTradeFrom = tradeNotice.FromName;
                    break;

                case MessageType.TradeState:
                    TradeState tradeState = TradeState.Read(ref reader);
                    TradeActive = tradeState.Active;
                    TradePartner = tradeState.PartnerName;
                    TradeMyGold = tradeState.MyGold;
                    TradeMyItems = tradeState.MyItems;
                    TradeTheirGold = tradeState.TheirGold;
                    TradeTheirItems = tradeState.TheirItems;
                    TradeMyAccepted = tradeState.MyAccepted;
                    TradeTheirAccepted = tradeState.TheirAccepted;
                    TradeMessage = tradeState.Message;
                    break;

                case MessageType.CorpseContents:
                    CorpseContentsMessage contents = CorpseContentsMessage.Read(ref reader);
                    if (contents.Gold == 0 && contents.Items.Count == 0)
                    {
                        CloseCorpse(); // spent (or unreachable) — the window closes
                    }
                    else
                    {
                        OpenCorpseId = contents.CorpseEntityId;
                        OpenCorpseGold = contents.Gold;
                        OpenCorpseItems = contents.Items;
                    }

                    break;

                case MessageType.CombatEvent:
                    CombatEventMessage combat = CombatEventMessage.Read(ref reader);
                    LastCombat = combat;
                    if (_combatFeed.Count < 256)
                    {
                        _combatFeed.Add(combat);
                    }

                    if (combat.AttackerId == EntityId || combat.TargetId == EntityId)
                    {
                        CombatEventsSeen++;
                    }

                    if (combat.AttackerId == EntityId && combat.TargetKilled)
                    {
                        KillsByMe++;
                    }

                    break;

                default:
                    break;
            }
        }
        catch (MalformedPacketException)
        {
            // Ignore corrupt datagrams.
        }
    }

    /// <summary>Find a visible entity by id in the latest snapshot.</summary>
    public bool TryGetEntity(int id, out EntitySnapshot entity)
    {
        foreach (EntitySnapshot e in Visible)
        {
            if (e.Id == id)
            {
                entity = e;
                return true;
            }
        }

        entity = default;
        return false;
    }

    /// <summary>Find our own entity in the latest snapshot, if present.</summary>
    public bool TryGetSelf(out EntitySnapshot self)
    {
        if (EntityId is int id)
        {
            foreach (EntitySnapshot e in Visible)
            {
                if (e.Id == id)
                {
                    self = e;
                    return true;
                }
            }
        }

        self = default;
        return false;
    }

    private void Send(Login msg) => SendWith(msg.Write);
    private void Send(QuestAction msg) => SendWith(msg.Write);
    private void Send(CreateCharacter msg) => SendWith(msg.Write);
    private void Send(EnterWorld msg) => SendWith(msg.Write);
    private void Send(DeleteCharacter msg) => SendWith(msg.Write);
    private void Send(Hearthstone msg) => SendWith(msg.Write);
    private void Send(SetHome msg) => SendWith(msg.Write);
    private void Send(InputCommand msg) => SendWith(msg.Write);
    private void Send(UseAbility msg) => SendWith(msg.Write);
    private void Send(UseRacial msg) => SendWith(msg.Write);
    private void Send(LootCorpse msg) => SendWith(msg.Write);
    private void Send(OpenCorpse msg) => SendWith(msg.Write);
    private void Send(LootItem msg) => SendWith(msg.Write);
    private void Send(Inspect msg) => SendWith(msg.Write);
    private void Send(DuelRequest msg) => SendWith(msg.Write);
    private void Send(DuelRespond msg) => SendWith(msg.Write);
    private void Send(TradeRequest msg) => SendWith(msg.Write);
    private void Send(TradeRespond msg) => SendWith(msg.Write);
    private void Send(TradeSetOffer msg) => SendWith(msg.Write);
    private void Send(TradeAccept msg) => SendWith(msg.Write);
    private void Send(TradeCancel msg) => SendWith(msg.Write);
    private void Send(DropItem msg) => SendWith(msg.Write);
    private void Send(BankTransaction msg) => SendWith(msg.Write);
    private void Send(EquipItem msg) => SendWith(msg.Write);

    private void Send(MoveItem msg) => SendWith(msg.Write);

    private void Send(VendorAction msg) => SendWith(msg.Write);

    private void Send(ShapeShift msg) => SendWith(msg.Write);
    private void Send(ChatSend msg) => SendWith(msg.Write);
    private void Send(AttackTarget msg) => SendWith(msg.Write);
    private void Send(PartyInvite msg) => SendWith(msg.Write);
    private void Send(PartyRespond msg) => SendWith(msg.Write);
    private void Send(PartyLeave msg) => SendWith(msg.Write);

    private void Send(PartyKick msg) => SendWith(msg.Write);
    private void Send(EnterInstance msg) => SendWith(msg.Write);
    private void Send(LeaveInstance msg) => SendWith(msg.Write);
    private void Send(Ping msg) => SendWith(msg.Write);
    private void Send(Disconnect msg) => SendWith(msg.Write);

    private void SendWith(Action<PacketWriter> write)
    {
        _writer.Reset();
        write(_writer);
        _transport.Send(_writer.WrittenSpan);
    }
}
