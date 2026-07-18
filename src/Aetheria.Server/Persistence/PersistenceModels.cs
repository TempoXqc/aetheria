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

    public int TotalXp { get; set; }
    public int Gold { get; set; }
    public byte EquippedWeaponId { get; set; }
    public byte EquippedArmorId { get; set; }
    public List<ItemStackRecord> Items { get; set; } = new();

    /// <summary>Weapon/spell proficiency per skill line (key: line id as string, for JSON friendliness).</summary>
    public Dictionary<string, int> Skills { get; set; } = new();
}

public sealed class ItemStackRecord
{
    public byte ItemId { get; set; }
    public int Quantity { get; set; }
}
