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

    /// <summary>Default server display name (servers are named, not numbered; override with --name).</summary>
    public const string DefaultServerName = "Aetheria";

    /// <summary>
    /// Default player capacity (override with --max-players). A FULL server still lets existing
    /// characters play; it only refuses creating NEW characters.
    /// </summary>
    public const int DefaultMaxPlayers = 100;

    /// <summary>
    /// A monster dragged farther than this from its spawn point drops aggro, runs home and heals
    /// (classic leash/evade — no dragging wolves across the map).
    /// </summary>
    public const float MonsterLeashRadius = 30f;

    /// <summary>Where the sanctuary's bank chest stands (inside the safe zone).</summary>
    public const float BankChestX = 8f;
    public const float BankChestY = 6f;

    /// <summary>How close a player must stand to the bank chest to move goods in or out.</summary>
    public const float BankInteractRange = 6f;

    /// <summary>
    /// Maximum obstacle height a mid-air jump clears: fences, small rocks and the bank chest can
    /// be vaulted; menhirs, trees and tall walls cannot.
    /// </summary>
    public const float JumpClearance = 1.0f;

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

    /// <summary>How close a player must stand to the quest giver to accept or turn in.</summary>
    public const float QuestGiverRange = 5f;

    /// <summary>Gold a new character starts with.</summary>
    public const int StartingGold = 50;

    /// <summary>Number of item slots in an account bank.</summary>
    public const int BankCapacity = 200;

    /// <summary>Maximum party size (raid cap).</summary>
    public const int MaxPartySize = 40;

    /// <summary>Protocol version — bump on any wire-format change; handshake rejects mismatches.</summary>
    public const byte ProtocolVersion = 22;

    /// <summary>How close two players must be to trade (world units).</summary>
    public const float TradeRange = 6f;

    /// <summary>
    /// GLOBAL COOLDOWN (WoW-style): after a player uses any manual ability, every other manual
    /// ability is locked for this many ticks (1.5s). Server-driven auto-attacks bypass it.
    /// </summary>
    public const int GlobalCooldownTicks = 30;

    /// <summary>
    /// The open world's SANCTUARY: a circle around the spawn point where neither PvP nor PvE can
    /// touch you — no player can attack or be attacked, and monsters never aggro. New characters
    /// spawn inside it. Instances have no sanctuary.
    /// </summary>
    public const float SafeZoneRadius = 18f;
    public const float SafeZoneCenterX = 0f;
    public const float SafeZoneCenterY = 0f;

    /// <summary>
    /// Human-readable build version, bumped at every delivery. Shown on the client login screen,
    /// in the in-game HUD, and in the server startup log, so "am I up to date?" has a one-glance
    /// answer on both sides.
    /// </summary>
    public const string GameVersion = "0.37.0";
}
