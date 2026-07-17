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

    // --- Server -> Client ---
    ConnectAccepted = 128,
    ConnectRejected = 129,
    Snapshot = 130,
    Pong = 131,
    CombatEvent = 132,
}

/// <summary>Coarse kind of an entity, so the client knows how to represent it. Extended over time.</summary>
public enum EntityKind : byte
{
    Player = 0,
    Npc = 1,
    Monster = 2,
}
