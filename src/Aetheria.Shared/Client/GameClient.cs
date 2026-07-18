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

    // --- Self status (from PlayerStatus / InventoryState) ---
    public int Level { get; private set; } = 1;
    public int TotalXp { get; private set; }
    public int XpForNextLevel { get; private set; } = -1;
    public int Gold { get; private set; }
    public byte EquippedWeaponId { get; private set; }
    public byte EquippedArmorId { get; private set; }
    public int InventoryStackCount { get; private set; }
    public IReadOnlyList<ItemStack> InventoryItems { get; private set; } = [];
    public int EffectiveAttack { get; private set; }
    public int EffectiveDefense { get; private set; }

    // --- Account bank (from BankState) ---
    public int BankGold { get; private set; }
    public int BankStackCount { get; private set; }

    // --- Party & instance state ---
    public string PartyLeader { get; private set; } = string.Empty;
    public int PartySize { get; private set; }
    public string? PendingInviteFrom { get; private set; }
    public string LastInstanceMessage { get; private set; } = string.Empty;
    public bool InInstance { get; private set; }

    public void Connect(
        string host, int port, string name, byte raceId, byte classId, Gender gender,
        string accountId, string accountSecret = "")
    {
        _transport.Connect(host, port);
        Send(new ConnectRequest(
            SimulationConstants.ProtocolVersion, name, raceId, classId, gender, accountId, accountSecret));
    }

    public void SendBank(BankOp op, byte itemId, int amount) => Send(new BankTransaction(op, itemId, amount));

    public void SendPartyInvite(int targetEntityId) => Send(new PartyInvite(targetEntityId));

    public void SendPartyRespond(bool accept) => Send(new PartyRespond(accept));

    public void SendPartyLeave() => Send(new PartyLeave());

    public void SendEnterInstance(byte instanceDefId) => Send(new EnterInstance(instanceDefId));

    public void SendLeaveInstance() => Send(new LeaveInstance());

    public void SendInput(Vec2 direction, float facingRadians = 0f)
    {
        _inputSequence++;
        Send(new InputCommand(_inputSequence, direction, facingRadians));
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
                    EquippedWeaponId = inv.EquippedWeaponId;
                    EquippedArmorId = inv.EquippedArmorId;
                    InventoryItems = inv.Items;
                    InventoryStackCount = inv.Items.Count;
                    break;

                case MessageType.BankState:
                    BankState bankState = BankState.Read(ref reader);
                    BankGold = bankState.Gold;
                    BankStackCount = bankState.Items.Count;
                    break;

                case MessageType.PartyState:
                    PartyState partyState = PartyState.Read(ref reader);
                    PartyLeader = partyState.LeaderName;
                    PartySize = partyState.MemberNames.Count;
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

                case MessageType.InspectResult:
                    LastInspect = InspectResult.Read(ref reader);
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

    private void Send(ConnectRequest msg) => SendWith(msg.Write);
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
    private void Send(PartyInvite msg) => SendWith(msg.Write);
    private void Send(PartyRespond msg) => SendWith(msg.Write);
    private void Send(PartyLeave msg) => SendWith(msg.Write);
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
