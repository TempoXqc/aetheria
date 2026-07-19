namespace Aetheria.Shared.Protocol;

/// <summary>
/// Discriminator written as the first byte of every packet. Client→server and server→client
/// messages share one enum for simplicity; the receiving side only handles the directions it
/// expects and rejects the rest.
/// </summary>
public enum MessageType : byte
{
    // --- Client -> Server ---
    ConnectRequest = 1,
    InputCommand = 2,
    Ping = 3,
    Disconnect = 4,
    UseAbility = 5,
    UseRacial = 6,
    LootCorpse = 7,
    BankTransaction = 8,
    PartyInvite = 9,
    PartyRespond = 10,
    PartyLeave = 11,
    EnterInstance = 12,
    LeaveInstance = 13,
    OpenCorpse = 14,
    LootItem = 15,
    Inspect = 16,
    DuelRequest = 17,
    DuelRespond = 18,
    TradeRequest = 19,
    TradeRespond = 20,
    TradeSetOffer = 21,
    TradeAccept = 22,
    TradeCancel = 23,
    DropItem = 24,
    Login = 25,
    CreateCharacter = 26,
    EnterWorld = 27,
    ServerInfoRequest = 28,
    EquipItem = 29,
    ChatSend = 30,
    AttackTarget = 31,
    QuestAction = 32,
    MoveItem = 33,
    VendorAction = 34,
    PartyKick = 35,
    ShapeShift = 36,
    DeleteCharacter = 37,

    // --- Server -> Client ---
    ConnectAccepted = 128,
    ConnectRejected = 129,
    Snapshot = 130,
    Pong = 131,
    CombatEvent = 132,
    PlayerStatus = 133,
    InventoryState = 134,
    BankState = 135,
    PartyState = 136,
    InstanceResult = 137,
    PartyInviteNotice = 138,
    CorpseContents = 139,
    InspectResult = 140,
    DuelNotice = 141,
    DuelState = 142,
    TradeNotice = 143,
    TradeState = 144,
    LoginResult = 145,
    ServerInfo = 146,
    ChatMessage = 147,
    QuestState = 148,
    QuestCatalog = 149,
}

/// <summary>Coarse kind of an entity, so the client knows how to represent it. Extended over time.</summary>
public enum EntityKind : byte
{
    Player = 0,

    /// <summary>Friendly interactive object/person (e.g. the bank chest). Invulnerable.</summary>
    Npc = 1,
    Monster = 2,

    /// <summary>A dead PLAYER's lootable corpse (full-loot rules).</summary>
    Corpse = 3,

    /// <summary>A slain creature's cosmetic remains; despawns on a timer, cannot be looted.</summary>
    MonsterCorpse = 4,
}
