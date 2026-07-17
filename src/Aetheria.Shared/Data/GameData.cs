using System.Text.Json;

namespace Aetheria.Shared.Data;

/// <summary>
/// The content registry: races, classes, abilities, and monsters, looked up by id. Ships with a set
/// of built-in defaults so the game is fully playable with no external files, and can optionally load
/// overrides from JSON on disk (System.Text.Json is in-box, so this stays dependency-free). Unknown
/// ids fall back to a sensible default rather than throwing — a malformed client request must never
/// crash the server.
/// </summary>
public sealed class GameData
{
    private readonly Dictionary<byte, RaceDefinition> _races;
    private readonly Dictionary<byte, ClassDefinition> _classes;
    private readonly Dictionary<byte, AbilityDefinition> _abilities;
    private readonly Dictionary<byte, MonsterDefinition> _monsters;

    public GameData(
        IEnumerable<RaceDefinition> races,
        IEnumerable<ClassDefinition> classes,
        IEnumerable<AbilityDefinition> abilities,
        IEnumerable<MonsterDefinition> monsters)
    {
        _races = races.ToDictionary(r => r.Id);
        _classes = classes.ToDictionary(c => c.Id);
        _abilities = abilities.ToDictionary(a => a.Id);
        _monsters = monsters.ToDictionary(m => m.Id);

        if (_races.Count == 0 || _classes.Count == 0 || _abilities.Count == 0)
        {
            throw new ArgumentException("GameData requires at least one race, class, and ability.");
        }
    }

    public IReadOnlyCollection<RaceDefinition> Races => _races.Values;
    public IReadOnlyCollection<ClassDefinition> Classes => _classes.Values;
    public IReadOnlyCollection<MonsterDefinition> Monsters => _monsters.Values;

    public RaceDefinition GetRace(byte id)
        => _races.TryGetValue(id, out RaceDefinition? r) ? r : _races.Values.First();

    public ClassDefinition GetClass(byte id)
        => _classes.TryGetValue(id, out ClassDefinition? c) ? c : _classes.Values.First();

    public AbilityDefinition GetAbility(byte id)
        => _abilities.TryGetValue(id, out AbilityDefinition? a) ? a : _abilities.Values.First();

    public MonsterDefinition GetMonster(byte id)
        => _monsters.TryGetValue(id, out MonsterDefinition? m) ? m : _monsters.Values.First();

    /// <summary>
    /// Load definitions from a directory of JSON files (races.json, classes.json, abilities.json,
    /// monsters.json). Any file that is missing or unreadable falls back to the built-in defaults for
    /// that category, so the server always starts with a valid, complete data set.
    /// </summary>
    public static GameData LoadFromDirectoryOrDefault(string directory)
    {
        GameData defaults = CreateDefault();

        var races = LoadList<RaceDefinition>(Path.Combine(directory, "races.json")) ?? defaults.Races.ToList();
        var classes = LoadList<ClassDefinition>(Path.Combine(directory, "classes.json")) ?? defaults.Classes.ToList();
        var abilities = LoadList<AbilityDefinition>(Path.Combine(directory, "abilities.json")) ?? defaults._abilities.Values.ToList();
        var monsters = LoadList<MonsterDefinition>(Path.Combine(directory, "monsters.json")) ?? defaults.Monsters.ToList();

        return new GameData(races, classes, abilities, monsters);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static List<T>? LoadList<T>(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<T>>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Bad or unreadable data file: fall back to defaults rather than failing to boot.
            return null;
        }
    }

    /// <summary>The canonical built-in content. Mirror of the shipped JSON under the server's data/.</summary>
    public static GameData CreateDefault() => new(
        races:
        [
            new RaceDefinition { Id = 1, Name = "Human", HealthBonus = 0, AttackBonus = 0, DefenseBonus = 0, MoveSpeedMultiplier = 1.00f },
            new RaceDefinition { Id = 2, Name = "Orc", HealthBonus = 20, AttackBonus = 3, DefenseBonus = -1, MoveSpeedMultiplier = 0.95f },
            new RaceDefinition { Id = 3, Name = "Elf", HealthBonus = -10, AttackBonus = 1, DefenseBonus = 0, MoveSpeedMultiplier = 1.10f },
        ],
        classes:
        [
            new ClassDefinition { Id = 1, Name = "Warrior", MaxHealth = 120, MoveSpeed = 5.0f, AttackPower = 12, Defense = 6, BasicAbilityId = 1 },
            new ClassDefinition { Id = 2, Name = "Mage", MaxHealth = 80, MoveSpeed = 5.0f, AttackPower = 18, Defense = 2, BasicAbilityId = 2 },
            new ClassDefinition { Id = 3, Name = "Ranger", MaxHealth = 95, MoveSpeed = 5.5f, AttackPower = 14, Defense = 3, BasicAbilityId = 3 },
        ],
        abilities:
        [
            new AbilityDefinition { Id = 1, Name = "Slash", BaseDamage = 10, Range = 2.5f, CooldownTicks = 10 },
            new AbilityDefinition { Id = 2, Name = "Firebolt", BaseDamage = 16, Range = 12f, CooldownTicks = 16 },
            new AbilityDefinition { Id = 3, Name = "Shot", BaseDamage = 12, Range = 10f, CooldownTicks = 12 },
            new AbilityDefinition { Id = 4, Name = "Claw", BaseDamage = 6, Range = 2.5f, CooldownTicks = 12 },
        ],
        monsters:
        [
            new MonsterDefinition { Id = 1, Name = "Goblin Grunt", MaxHealth = 60, MoveSpeed = 4.0f, AttackPower = 8, Defense = 2, AggroRadius = 15f, BasicAbilityId = 4 },
            new MonsterDefinition { Id = 2, Name = "Dire Wolf", MaxHealth = 90, MoveSpeed = 6.0f, AttackPower = 12, Defense = 3, AggroRadius = 20f, BasicAbilityId = 4 },
        ]);
}
