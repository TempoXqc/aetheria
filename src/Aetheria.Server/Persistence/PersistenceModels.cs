namespace Aetheria.Server.Persistence;

/// <summary>
/// Durable state records — plain serializable classes (public get/set) so any store can persist
/// them: the JSON file store today, Postgres tomorrow. Keys are lowercase for case-insensitive
/// account ids and names.
/// </summary>
public sealed class ServerState
{
    /// <summary>Accounts by lowercase account id.</summary>
    public Dictionary<string, AccountRecord> Accounts { get; set; } = new();

    /// <summary>Character-name registry: lowercase name → lowercase owning account id. Durable and server-wide.</summary>
    public Dictionary<string, string> Names { get; set; } = new();
}

public sealed class AccountRecord
{
    /// <summary>Friends by CHARACTER NAME (lowercase keys) — realm-local, account-scoped.</summary>
    public List<string> Friends { get; set; } = new();

    public string AccountId { get; set; } = string.Empty;

    /// <summary>SHA-256 hex of the account secret, set on first connect and verified after.</summary>
    public string SecretHash { get; set; } = string.Empty;

    // The account bank — survives character permadeath by design (ADR-0010).
    public int BankGold { get; set; }
    public List<ItemStackRecord> BankItems { get; set; } = new();

    /// <summary>Characters by lowercase character name.</summary>
    public Dictionary<string, CharacterRecord> Characters { get; set; } = new();
}

public sealed class CharacterRecord
{
    public string Name { get; set; } = string.Empty;
    public byte RaceId { get; set; }
    public byte ClassId { get; set; }
    public byte Gender { get; set; }

    // Cosmetic customisation (palette/style indexes; see Aetheria.Shared.Combat.Appearance).
    public byte SkinTone { get; set; }
    public byte Face { get; set; }
    public byte HairStyle { get; set; }
    public byte HairColor { get; set; }
    public byte BeardStyle { get; set; }
    public byte BeardColor { get; set; }

    public int TotalXp { get; set; }
    public int Gold { get; set; }

    // Last known position in the open world: log back in where you logged out.
    public bool HasPosition { get; set; }
    public float PosX { get; set; }
    public float PosY { get; set; }

    /// <summary>Hearthstone home (bound inn). (0,0) = the sanctuary.</summary>
    public float HomeX { get; set; }
    public float HomeY { get; set; }

    // Quest chain progress (linear): active quest, its kill counter, highest completed.
    public byte ActiveQuestId { get; set; }
    public int QuestKills { get; set; }
    public byte QuestCompletedUpTo { get; set; }

    // Legacy two-slot fields (kept so pre-0.24 state files still restore).
    public byte EquippedWeaponId { get; set; }
    public byte EquippedArmorId { get; set; }

    /// <summary>Full WoW-style loadout: slot index (as string, JSON-friendly) → item id.</summary>
    public Dictionary<string, byte> Equipment { get; set; } = new();
    public List<ItemStackRecord> Items { get; set; } = new();

    /// <summary>Weapon/spell proficiency per skill line (key: line id as string, for JSON friendliness).</summary>
    public Dictionary<string, int> Skills { get; set; } = new();

    /// <summary>Unix seconds of the last time this character was seen online.</summary>
    public long LastSeenUnix { get; set; }

    // PvP ledger: honor trophies, per-camp standing, and the bandit switch.
    public int Honor { get; set; }
    public int RepAlliance { get; set; }
    public int RepHorde { get; set; }
    public bool Bandit { get; set; }
}

public sealed class ItemStackRecord
{
    public byte ItemId { get; set; }
    public int Quantity { get; set; }
}
