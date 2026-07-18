using System.Text.Json;
using Aetheria.Shared.Combat;
using Aetheria.Shared.Items;

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
    private readonly Dictionary<byte, ItemDefinition> _items;
    private readonly Dictionary<byte, InstanceDefinition> _instances;

    public GameData(
        IEnumerable<RaceDefinition> races,
        IEnumerable<ClassDefinition> classes,
        IEnumerable<AbilityDefinition> abilities,
        IEnumerable<MonsterDefinition> monsters,
        IEnumerable<ItemDefinition>? items = null,
        ProgressionConfig? progression = null,
        IEnumerable<InstanceDefinition>? instances = null)
    {
        _races = races.ToDictionary(r => r.Id);
        _classes = classes.ToDictionary(c => c.Id);
        _abilities = abilities.ToDictionary(a => a.Id);
        _monsters = monsters.ToDictionary(m => m.Id);
        _items = (items ?? []).ToDictionary(i => i.Id);
        _instances = (instances ?? []).ToDictionary(i => i.Id);
        Progression = progression ?? new ProgressionConfig();

        if (_races.Count == 0 || _classes.Count == 0 || _abilities.Count == 0)
        {
            throw new ArgumentException("GameData requires at least one race, class, and ability.");
        }
    }

    public IReadOnlyCollection<RaceDefinition> Races => _races.Values;
    public IReadOnlyCollection<ClassDefinition> Classes => _classes.Values;
    public IReadOnlyCollection<MonsterDefinition> Monsters => _monsters.Values;
    public IReadOnlyCollection<ItemDefinition> Items => _items.Values;
    public ProgressionConfig Progression { get; }

    public ItemDefinition GetItem(byte id)
        => _items.TryGetValue(id, out ItemDefinition? i) ? i : _items.Values.First();

    public bool HasItem(byte id) => _items.ContainsKey(id);

    public IReadOnlyCollection<InstanceDefinition> Instances => _instances.Values;

    public bool TryGetInstance(byte id, out InstanceDefinition definition)
    {
        if (_instances.TryGetValue(id, out InstanceDefinition? found))
        {
            definition = found;
            return true;
        }

        definition = null!;
        return false;
    }

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
        var items = LoadList<ItemDefinition>(Path.Combine(directory, "items.json")) ?? defaults.Items.ToList();
        var progression = LoadObject<ProgressionConfig>(Path.Combine(directory, "progression.json")) ?? defaults.Progression;
        var instances = LoadList<InstanceDefinition>(Path.Combine(directory, "instances.json")) ?? defaults.Instances.ToList();

        return new GameData(races, classes, abilities, monsters, items, progression, instances);
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

    private static T? LoadObject<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True if the race is allowed to play the class (the balance matrix).</summary>
    public bool IsClassAllowedForRace(byte raceId, byte classId) => GetRace(raceId).AllowsClass(classId);

    /// <summary>The canonical built-in content. Mirror of the shipped JSON under the server's data/.</summary>
    public static GameData CreateDefault() => new(
        races:
        [
            // Alliance
            new RaceDefinition { Id = 1, Name = "Human", Faction = Faction.Alliance, HealthBonus = 0,   AttackBonus = 0, DefenseBonus = 0,  MoveSpeedMultiplier = 1.00f, AllowedClassIds = [1, 2], RacialAbilityId = 10 },
            new RaceDefinition { Id = 4, Name = "Dwarf", Faction = Faction.Alliance, HealthBonus = 15,  AttackBonus = 0, DefenseBonus = 2,  MoveSpeedMultiplier = 0.95f, AllowedClassIds = [1, 3], RacialAbilityId = 11 },
            // Horde
            new RaceDefinition { Id = 2, Name = "Orc",   Faction = Faction.Horde,    HealthBonus = 20,  AttackBonus = 3, DefenseBonus = -1, MoveSpeedMultiplier = 0.95f, AllowedClassIds = [1, 3], RacialAbilityId = 12 },
            new RaceDefinition { Id = 3, Name = "Elf",   Faction = Faction.Horde,    HealthBonus = -10, AttackBonus = 1, DefenseBonus = 0,  MoveSpeedMultiplier = 1.10f, AllowedClassIds = [2, 3], RacialAbilityId = 13 },
        ],
        classes:
        [
            new ClassDefinition { Id = 1, Name = "Warrior", MaxHealth = 120, MoveSpeed = 5.0f, AttackPower = 12, Defense = 6, BasicAbilityId = 1, AbilityIds = [1, 20], Resource = ResourceType.Rage,   MaxResource = 100, ResourceRegenPerSec = 0f },
            new ClassDefinition { Id = 2, Name = "Mage",    MaxHealth = 80,  MoveSpeed = 5.0f, AttackPower = 18, Defense = 2, BasicAbilityId = 2, AbilityIds = [2, 21], Resource = ResourceType.Mana,   MaxResource = 100, ResourceRegenPerSec = 8f },
            new ClassDefinition { Id = 3, Name = "Ranger",  MaxHealth = 95,  MoveSpeed = 5.5f, AttackPower = 14, Defense = 3, BasicAbilityId = 3, AbilityIds = [3, 22], Resource = ResourceType.Energy, MaxResource = 100, ResourceRegenPerSec = 20f },
        ],
        abilities:
        [
            // Basic attacks (level 1). SkillLineId ties each to a proficiency: 1 Swords, 2 Fire, 3 Marksmanship.
            new AbilityDefinition { Id = 1, Name = "Slash",    BaseDamage = 10, Range = 2.5f, CooldownTicks = 10, ResourceCost = 0,  SkillLineId = 1 },
            new AbilityDefinition { Id = 2, Name = "Firebolt", BaseDamage = 16, Range = 12f,  CooldownTicks = 16, ResourceCost = 20, SkillLineId = 2 },
            new AbilityDefinition { Id = 3, Name = "Shot",     BaseDamage = 12, Range = 10f,  CooldownTicks = 12, ResourceCost = 30, SkillLineId = 3 },
            new AbilityDefinition { Id = 4, Name = "Claw",     BaseDamage = 6,  Range = 2.5f, CooldownTicks = 12, ResourceCost = 0 },
            // Advanced abilities (unlock at level 3)
            new AbilityDefinition { Id = 20, Name = "Whirlwind",  BaseDamage = 25, Range = 3f,  CooldownTicks = 40, ResourceCost = 25, UnlockLevel = 3, SkillLineId = 1 },
            new AbilityDefinition { Id = 21, Name = "Frostbolt",  BaseDamage = 24, Range = 12f, CooldownTicks = 24, ResourceCost = 30, UnlockLevel = 3, SkillLineId = 2 },
            new AbilityDefinition { Id = 22, Name = "Aimed Shot", BaseDamage = 22, Range = 12f, CooldownTicks = 30, ResourceCost = 50, UnlockLevel = 3, SkillLineId = 3 },
            // Racials (self-cast, no resource cost, long cooldown)
            new AbilityDefinition { Id = 10, Name = "Second Wind",       Range = 0f, CooldownTicks = 1200, Effect = EffectType.Heal,          EffectMagnitude = 0.25f, EffectDurationTicks = 0 },
            new AbilityDefinition { Id = 11, Name = "Stoneform",         Range = 0f, CooldownTicks = 1200, Effect = EffectType.BuffDefense,   EffectMagnitude = 0.50f, EffectDurationTicks = 160 },
            new AbilityDefinition { Id = 12, Name = "Blood Fury",        Range = 0f, CooldownTicks = 1200, Effect = EffectType.BuffAttack,    EffectMagnitude = 0.40f, EffectDurationTicks = 160 },
            new AbilityDefinition { Id = 13, Name = "Nature's Swiftness", Range = 0f, CooldownTicks = 900,  Effect = EffectType.BuffMoveSpeed, EffectMagnitude = 0.40f, EffectDurationTicks = 120 },
        ],
        monsters:
        [
            new MonsterDefinition { Id = 1, Name = "Goblin Grunt", MaxHealth = 60, MoveSpeed = 4.0f, AttackPower = 8, Defense = 2, AggroRadius = 15f, BasicAbilityId = 4, XpReward = 25, GoldReward = 5 },
            new MonsterDefinition { Id = 2, Name = "Dire Wolf", MaxHealth = 90, MoveSpeed = 6.0f, AttackPower = 12, Defense = 3, AggroRadius = 20f, BasicAbilityId = 4, XpReward = 40, GoldReward = 10 },
            // Elite: rules the open-world "dungeon" camp (non-instanced, so PvP can erupt around it).
            new MonsterDefinition { Id = 3, Name = "Goblin King", MaxHealth = 350, MoveSpeed = 4.5f, AttackPower = 22, Defense = 6, AggroRadius = 18f, BasicAbilityId = 4, XpReward = 200, GoldReward = 80 },
            // World raid boss: raid-difficulty, lives in the OPEN world — never instanced, PvP possible.
            new MonsterDefinition { Id = 4, Name = "Ashmaw the Devourer", MaxHealth = 2500, MoveSpeed = 5.0f, AttackPower = 45, Defense = 12, AggroRadius = 25f, BasicAbilityId = 4, XpReward = 1500, GoldReward = 600 },
        ],
        instances:
        [
            new InstanceDefinition
            {
                Id = 1, Name = "Ragefire Depths", IsRaid = false, MinPlayers = 1, MaxPlayers = 5,
                HealthScalingPerExtraPlayer = 0.5f, DamageScalingPerExtraPlayer = 0.25f,
                Spawns =
                [
                    new InstanceSpawn { MonsterId = 1, X = 8f, Y = 4f },
                    new InstanceSpawn { MonsterId = 1, X = 12f, Y = 8f },
                    new InstanceSpawn { MonsterId = 2, X = 16f, Y = 4f },
                    new InstanceSpawn { MonsterId = 3, X = 24f, Y = 8f }, // end boss
                ],
            },
            new InstanceDefinition
            {
                Id = 2, Name = "Molten Sanctum", IsRaid = true, MinPlayers = 6, MaxPlayers = 40,
                HealthScalingPerExtraPlayer = 0.35f, DamageScalingPerExtraPlayer = 0.15f,
                Spawns =
                [
                    new InstanceSpawn { MonsterId = 2, X = 10f, Y = 6f },
                    new InstanceSpawn { MonsterId = 2, X = 14f, Y = 6f },
                    new InstanceSpawn { MonsterId = 3, X = 20f, Y = 8f },
                    new InstanceSpawn { MonsterId = 4, X = 30f, Y = 10f }, // raid boss (instanced copy)
                ],
            },
        ],
        items:
        [
            new ItemDefinition { Id = 1,  Name = "Rusty Sword",  Type = ItemType.Weapon, Slot = EquipSlot.Weapon, AttackBonus = 3,  GoldValue = 5 },
            new ItemDefinition { Id = 2,  Name = "Iron Sword",   Type = ItemType.Weapon, Slot = EquipSlot.Weapon, AttackBonus = 6,  GoldValue = 20 },
            new ItemDefinition { Id = 3,  Name = "Leather Vest", Type = ItemType.Armor,  Slot = EquipSlot.Armor,  DefenseBonus = 4, HealthBonus = 10, GoldValue = 15 },
            new ItemDefinition { Id = 10, Name = "Wolf Pelt",    Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 3 },
            new ItemDefinition { Id = 11, Name = "Goblin Ear",   Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 2 },
            new ItemDefinition { Id = 20, Name = "Minor Healing Potion", Type = ItemType.Consumable, Stackable = true, MaxStack = 10, GoldValue = 5 },
        ]);
}
