namespace Aetheria.Shared;

/// <summary>
/// Tuning constants shared by client and server so both agree on the simulation contract.
/// These are compile-time defaults for the skeleton; they will move into server-side config
/// (and a handshake-negotiated subset for the client) as the project matures.
/// </summary>
public static class SimulationConstants
{
    /// <summary>Authoritative simulation steps per second (fixed timestep).</summary>
    public const int TickRate = 20;

    /// <summary>Duration of a single tick, in seconds.</summary>
    public const float TickDelta = 1f / TickRate;

    /// <summary>Default UDP port the server listens on.</summary>
    public const int DefaultPort = 27015;

    /// <summary>Player movement speed in world units per second.</summary>
    public const float PlayerMoveSpeed = 5f;

    /// <summary>
    /// Side length (world units) of one interest-management grid cell. Should be a bit
    /// larger than the AoI radius divided by a small integer so radius queries touch few cells.
    /// </summary>
    public const float GridCellSize = 16f;

    /// <summary>
    /// Area-of-interest radius (world units). A client is only told about entities within
    /// this distance of its own entity — the mechanism that keeps a seamless world scalable.
    /// </summary>
    public const float AreaOfInterestRadius = 40f;

    /// <summary>Drop a peer that has not sent any packet for this long (seconds).</summary>
    public const float PeerTimeoutSeconds = 10f;

    /// <summary>How long a dead player or monster stays down before respawning (seconds).</summary>
    public const float RespawnDelaySeconds = 5f;

    /// <summary>Respawn delay expressed in ticks.</summary>
    public const int RespawnDelayTicks = (int)(RespawnDelaySeconds * TickRate);

    /// <summary>Number of item slots in a player's carried inventory.</summary>
    public const int PlayerInventoryCapacity = 40;

    /// <summary>Number of item slots a corpse loot pile can hold.</summary>
    public const int CorpseLootCapacity = 64;

    /// <summary>How close a looter must be to a corpse to loot it (world units).</summary>
    public const float LootRange = 5f;

    /// <summary>Gold a new character starts with.</summary>
    public const int StartingGold = 50;

    /// <summary>Number of item slots in an account bank.</summary>
    public const int BankCapacity = 200;

    /// <summary>Maximum party size (raid cap).</summary>
    public const int MaxPartySize = 40;

    /// <summary>Protocol version — bump on any wire-format change; handshake rejects mismatches.</summary>
    public const byte ProtocolVersion = 7;

    /// <summary>
    /// Human-readable build version, bumped at every delivery. Shown on the client login screen,
    /// in the in-game HUD, and in the server startup log, so "am I up to date?" has a one-glance
    /// answer on both sides.
    /// </summary>
    public const string GameVersion = "0.10.0";
}
