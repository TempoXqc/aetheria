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
}

/// <summary>Coarse kind of an entity, so the client knows how to represent it. Extended over time.</summary>
public enum EntityKind : byte
{
    Player = 0,
    Npc = 1,
    Monster = 2,
    Corpse = 3,
}
