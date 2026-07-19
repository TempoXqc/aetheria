#if !NETSTANDARD2_1
using System.Text.Json;
#endif
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
    private readonly Dictionary<byte, QuestDefinition> _quests;

    public GameData(
        IEnumerable<RaceDefinition> races,
        IEnumerable<ClassDefinition> classes,
        IEnumerable<AbilityDefinition> abilities,
        IEnumerable<MonsterDefinition> monsters,
        IEnumerable<ItemDefinition>? items = null,
        ProgressionConfig? progression = null,
        IEnumerable<InstanceDefinition>? instances = null,
        IEnumerable<QuestDefinition>? quests = null)
    {
        _races = races.ToDictionary(r => r.Id);
        _classes = classes.ToDictionary(c => c.Id);
        _abilities = abilities.ToDictionary(a => a.Id);
        _monsters = monsters.ToDictionary(m => m.Id);
        _items = (items ?? []).ToDictionary(i => i.Id);
        _instances = (instances ?? []).ToDictionary(i => i.Id);
        _quests = (quests ?? []).ToDictionary(q => q.Id);
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

    public IReadOnlyCollection<QuestDefinition> Quests => _quests.Values;

    public bool HasQuest(byte id) => _quests.ContainsKey(id);

    public QuestDefinition? GetQuest(byte id)
        => _quests.TryGetValue(id, out QuestDefinition? q) ? q : null;

    /// <summary>
    /// Swap in the server's quest catalogue (received at login). The client ships built-in
    /// defaults only; the SERVER owns the real quest data — including quests written in the
    /// Studio — and this keeps every player's texts in sync without a client rebuild.
    /// </summary>
    public void ReplaceQuests(IEnumerable<QuestDefinition> quests)
    {
        _quests.Clear();
        foreach (QuestDefinition q in quests)
        {
            _quests[q.Id] = q;
        }
    }

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
#if NETSTANDARD2_1
        // System.Text.Json is not in-box on netstandard2.1 (Unity). Clients render, they do not
        // author content — the built-in defaults match the server's shipped JSON.
        _ = directory;
        return CreateDefault();
#else
        GameData defaults = CreateDefault();

        var races = LoadList<RaceDefinition>(Path.Combine(directory, "races.json")) ?? defaults.Races.ToList();
        var classes = LoadList<ClassDefinition>(Path.Combine(directory, "classes.json")) ?? defaults.Classes.ToList();
        var abilities = LoadList<AbilityDefinition>(Path.Combine(directory, "abilities.json")) ?? defaults._abilities.Values.ToList();
        var monsters = LoadList<MonsterDefinition>(Path.Combine(directory, "monsters.json")) ?? defaults.Monsters.ToList();
        var items = LoadList<ItemDefinition>(Path.Combine(directory, "items.json")) ?? defaults.Items.ToList();
        var progression = LoadObject<ProgressionConfig>(Path.Combine(directory, "progression.json")) ?? defaults.Progression;
        var instances = LoadList<InstanceDefinition>(Path.Combine(directory, "instances.json")) ?? defaults.Instances.ToList();
        var quests = LoadList<QuestDefinition>(Path.Combine(directory, "quests.json")) ?? defaults.Quests.ToList();

        return new GameData(races, classes, abilities, monsters, items, progression, instances, quests);
#endif
    }

#if !NETSTANDARD2_1
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
#endif

    /// <summary>True if the race is allowed to play the class (the balance matrix).</summary>
    public bool IsClassAllowedForRace(byte raceId, byte classId) => GetRace(raceId).AllowsClass(classId);

    /// <summary>The canonical built-in content. Mirror of the shipped JSON under the server's data/.</summary>
    public static GameData CreateDefault() => new(
        races:
        [
            // Alliance
            new RaceDefinition { Id = 1, Name = "Human", Faction = Faction.Alliance, HealthBonus = 0,   AttackBonus = 0, DefenseBonus = 0,  MoveSpeedMultiplier = 1.00f, AllowedClassIds = [1, 2, 4, 5], RacialAbilityId = 10 },
            new RaceDefinition { Id = 4, Name = "Dwarf", Faction = Faction.Alliance, HealthBonus = 15,  AttackBonus = 0, DefenseBonus = 2,  MoveSpeedMultiplier = 0.95f, AllowedClassIds = [1, 3, 5], RacialAbilityId = 11 },
            // Horde
            new RaceDefinition { Id = 2, Name = "Orc",   Faction = Faction.Horde,    HealthBonus = 20,  AttackBonus = 3, DefenseBonus = -1, MoveSpeedMultiplier = 0.95f, AllowedClassIds = [1, 3, 5], RacialAbilityId = 12 },
            new RaceDefinition { Id = 3, Name = "Elf",   Faction = Faction.Horde,    HealthBonus = -10, AttackBonus = 1, DefenseBonus = 0,  MoveSpeedMultiplier = 1.10f, AllowedClassIds = [2, 3, 4, 5], RacialAbilityId = 13 },
        ],
        classes:
        [
            new ClassDefinition { Id = 1, Name = "Warrior", MaxHealth = 120, MoveSpeed = 5.0f, AttackPower = 12, Defense = 6, BasicAbilityId = 1, AbilityIds = [1, 20, 5], Resource = ResourceType.Rage,   MaxResource = 100, ResourceRegenPerSec = 0f },
            new ClassDefinition { Id = 2, Name = "Mage",    MaxHealth = 80,  MoveSpeed = 5.0f, AttackPower = 18, Defense = 2, BasicAbilityId = 2, AutoAttackAbilityId = 6, AbilityIds = [2, 21, 5, 6], Resource = ResourceType.Mana,   MaxResource = 100, ResourceRegenPerSec = 8f },
            new ClassDefinition { Id = 3, Name = "Ranger",  MaxHealth = 95,  MoveSpeed = 5.5f, AttackPower = 14, Defense = 3, BasicAbilityId = 3, AbilityIds = [3, 22, 5], Resource = ResourceType.Energy, MaxResource = 100, ResourceRegenPerSec = 20f },
            // The DRUID shapeshifts: humanoid/owl cast Wrath (30), bear mauls (31), cat shreds (32).
            new ClassDefinition { Id = 4, Name = "Druid",   MaxHealth = 100, MoveSpeed = 5.0f, AttackPower = 14, Defense = 4, BasicAbilityId = 30, AbilityIds = [30, 31, 32, 33, 5], Resource = ResourceType.Mana, MaxResource = 100, ResourceRegenPerSec = 8f },
            new ClassDefinition { Id = 5, Name = "Cleric",  MaxHealth = 90,  MoveSpeed = 5.0f, AttackPower = 12, Defense = 4, BasicAbilityId = 50, AutoAttackAbilityId = 6, AbilityIds = [50, 51, 5, 6], Resource = ResourceType.Mana, MaxResource = 100, ResourceRegenPerSec = 10f },
        ],
        abilities:
        [
            // Basic attacks (level 1). SkillLineId ties each to a proficiency: 1 Swords, 2 Fire, 3 Marksmanship.
            new AbilityDefinition { Id = 1, Name = "Slash",    BaseDamage = 18, Range = 2.5f, CooldownTicks = 40, ResourceCost = 0,  SkillLineId = 1 },
            new AbilityDefinition { Id = 2, Name = "Firebolt", BaseDamage = 16, Range = 12f,  CooldownTicks = 16, ResourceCost = 20, SkillLineId = 2, CastTimeTicks = 30 },
            new AbilityDefinition { Id = 3, Name = "Shot",     BaseDamage = 20, Range = 10f,  CooldownTicks = 50, ResourceCost = 30, SkillLineId = 3 },
            new AbilityDefinition { Id = 4, Name = "Claw",     BaseDamage = 9,  Range = 2.5f, CooldownTicks = 36, ResourceCost = 0 },
            // Self-cast recovery, all classes: 4%/s of max health (and max mana) for 10s, 30s cooldown.
            new AbilityDefinition { Id = 5, Name = "Renew", Range = 0f, CooldownTicks = 600, ResourceCost = 0, Effect = EffectType.Regen, EffectMagnitude = 0.04f, EffectDurationTicks = 200 },
            // The Mage's WAND: a free, instant auto-attack (Firebolt stays a hand-cast spell).
            new AbilityDefinition { Id = 6, Name = "Wand Shot", BaseDamage = 10, Range = 12f, CooldownTicks = 30, ResourceCost = 0 },
            // Advanced abilities (unlock at level 3)
            new AbilityDefinition { Id = 20, Name = "Whirlwind",  BaseDamage = 25, Range = 3f,  CooldownTicks = 40, ResourceCost = 25, UnlockLevel = 3, SkillLineId = 1 },
            new AbilityDefinition { Id = 21, Name = "Frostbolt",  BaseDamage = 24, Range = 12f, CooldownTicks = 24, ResourceCost = 30, UnlockLevel = 3, SkillLineId = 2, CastTimeTicks = 30 },
            // The Cleric's kit: holy damage at range, and a TARGETED heal (self or ally).
            new AbilityDefinition { Id = 50, Name = "Châtiment", BaseDamage = 15, Range = 12f, CooldownTicks = 16, ResourceCost = 15, SkillLineId = 2, CastTimeTicks = 24 },
            new AbilityDefinition { Id = 51, Name = "Soin", BaseDamage = 0, Range = 12f, CooldownTicks = 24, ResourceCost = 25, Effect = EffectType.Heal, EffectMagnitude = 0.3f, CastTimeTicks = 30 },
            new AbilityDefinition { Id = 22, Name = "Aimed Shot", BaseDamage = 22, Range = 12f, CooldownTicks = 30, ResourceCost = 50, UnlockLevel = 3, SkillLineId = 3, CastTimeTicks = 30 },
            // Druid kit: one basic attack per FORM, plus an instant self-heal at level 3.
            new AbilityDefinition { Id = 30, Name = "Wrath",    BaseDamage = 15, Range = 12f,  CooldownTicks = 30, ResourceCost = 12, SkillLineId = 4, CastTimeTicks = 24 },
            new AbilityDefinition { Id = 31, Name = "Maul",     BaseDamage = 22, Range = 2.5f, CooldownTicks = 44, ResourceCost = 0,  SkillLineId = 4 },
            new AbilityDefinition { Id = 32, Name = "Shred",    BaseDamage = 20, Range = 2.5f, CooldownTicks = 24, ResourceCost = 12, SkillLineId = 4 },
            new AbilityDefinition { Id = 33, Name = "Regrowth", Range = 0f, CooldownTicks = 400, ResourceCost = 30, UnlockLevel = 3, Effect = EffectType.Heal, EffectMagnitude = 0.20f, EffectDurationTicks = 0 },
            // Racials (self-cast, no resource cost, long cooldown)
            new AbilityDefinition { Id = 10, Name = "Second Wind",       Range = 0f, CooldownTicks = 1200, Effect = EffectType.Heal,          EffectMagnitude = 0.25f, EffectDurationTicks = 0 },
            new AbilityDefinition { Id = 11, Name = "Stoneform",         Range = 0f, CooldownTicks = 1200, Effect = EffectType.BuffDefense,   EffectMagnitude = 0.50f, EffectDurationTicks = 160 },
            new AbilityDefinition { Id = 12, Name = "Blood Fury",        Range = 0f, CooldownTicks = 1200, Effect = EffectType.BuffAttack,    EffectMagnitude = 0.40f, EffectDurationTicks = 160 },
            new AbilityDefinition { Id = 13, Name = "Nature's Swiftness", Range = 0f, CooldownTicks = 900,  Effect = EffectType.BuffMoveSpeed, EffectMagnitude = 0.40f, EffectDurationTicks = 120 },
        ],
        monsters:
        [
            // Body parts are GUARANTEED per kill (no RNG), distinct per creature type — so collection
            // quests ("bring 10 goblin heads") are deterministic: kill 10 goblins, own 10 heads.
            new MonsterDefinition
            {
                Id = 1, Name = "Goblin Grunt", Level = 1, MaxHealth = 130, MoveSpeed = 4.0f, AttackPower = 5, Defense = 2, AggroRadius = 8f, BasicAbilityId = 4, XpReward = 35, GoldReward = 5,
                BodyParts =
                [
                    new LootEntry { ItemId = 30 },                // Goblin Head
                    new LootEntry { ItemId = 31 },                // Goblin Skin
                    new LootEntry { ItemId = 11, Quantity = 2 },  // Goblin Ears
                    new LootEntry { ItemId = 32, Quantity = 2 },  // Goblin Eyes
                    new LootEntry { ItemId = 33 },                // Goblin Finger
                    new LootEntry { ItemId = 34 },                // Goblin Tongue
                    new LootEntry { ItemId = 35 },                // Goblin Foot
                ],
                GearDrops =
                [
                    new GearDrop { ItemId = 1, ChancePercent = 8 },  // Rusty Sword
                    new GearDrop { ItemId = 3, ChancePercent = 5 },  // Leather Vest
                    new GearDrop { ItemId = 12, ChancePercent = 4 }, // Padded Robe
                    new GearDrop { ItemId = 13, ChancePercent = 5 }, // Leather Cap
                    new GearDrop { ItemId = 15, ChancePercent = 5 }, // Leather Boots
                    new GearDrop { ItemId = 17, ChancePercent = 4 }, // Leather Pants
                    new GearDrop { ItemId = 19, ChancePercent = 4 }, // Leather Gloves
                    new GearDrop { ItemId = 21, ChancePercent = 3 }, // Sturdy Belt
                ],
            },
            new MonsterDefinition
            {
                Id = 2, Name = "Dire Wolf", Level = 3, MaxHealth = 200, MoveSpeed = 6.0f, AttackPower = 8, Defense = 3, AggroRadius = 9f, BasicAbilityId = 4, XpReward = 60, GoldReward = 10,
                BodyParts =
                [
                    new LootEntry { ItemId = 10 },                // Wolf Pelt
                    new LootEntry { ItemId = 40, Quantity = 4 },  // Wolf Paws
                    new LootEntry { ItemId = 41 },                // Wolf Tail
                    new LootEntry { ItemId = 42, Quantity = 2 },  // Wolf Fangs
                    new LootEntry { ItemId = 43 },                // Wolf Heart
                    new LootEntry { ItemId = 44 },                // Wolf Liver
                    new LootEntry { ItemId = 45, Quantity = 2 },  // Wolf Bones
                ],
                GearDrops =
                [
                    new GearDrop { ItemId = 16, ChancePercent = 10 }, // Wolf-fur Shoulders
                ],
            },
            // Elite: rules the open-world "dungeon" camp (non-instanced, so PvP can erupt around it).
            new MonsterDefinition
            {
                Id = 3, Name = "Goblin King", Level = 6, MaxHealth = 700, MoveSpeed = 4.5f, AttackPower = 16, Defense = 6, AggroRadius = 12f, BasicAbilityId = 4, XpReward = 300, GoldReward = 80,
                BodyParts =
                [
                    new LootEntry { ItemId = 50 },                // Crowned Goblin Head (unique to the King)
                    new LootEntry { ItemId = 31, Quantity = 2 },  // Goblin Skin
                    new LootEntry { ItemId = 11, Quantity = 2 },  // Goblin Ears
                    new LootEntry { ItemId = 32, Quantity = 2 },  // Goblin Eyes
                ],
                GearDrops =
                [
                    new GearDrop { ItemId = 2, ChancePercent = 100 }, // Iron Sword: the King always pays
                    new GearDrop { ItemId = 9, ChancePercent = 35 },  // Chain Mail
                    new GearDrop { ItemId = 7, ChancePercent = 30 },  // Hunting Bow
                    new GearDrop { ItemId = 18, ChancePercent = 40 }, // Traveler's Cloak
                    new GearDrop { ItemId = 22, ChancePercent = 30 }, // Wooden Shield
                ],
            },
            // World raid boss: raid-difficulty, lives in the OPEN world — never instanced, PvP possible.
            new MonsterDefinition
            {
                Id = 4, Name = "Ashmaw the Devourer", Level = 10, MaxHealth = 4500, MoveSpeed = 5.0f, AttackPower = 35, Defense = 12, AggroRadius = 14f, BasicAbilityId = 4, XpReward = 2200, GoldReward = 600,
                BodyParts =
                [
                    new LootEntry { ItemId = 60, Quantity = 2 },  // Ashmaw Horns
                    new LootEntry { ItemId = 61 },                // Devourer Heart
                    new LootEntry { ItemId = 62 },                // Charred Hide
                    new LootEntry { ItemId = 63, Quantity = 4 },  // Ashmaw Claws
                    new LootEntry { ItemId = 64, Quantity = 3 },  // Devourer Bones
                ],
                GearDrops =
                [
                    new GearDrop { ItemId = 4, ChancePercent = 100 }, // Steel Sword
                    new GearDrop { ItemId = 9, ChancePercent = 100 }, // Chain Mail
                    new GearDrop { ItemId = 14, ChancePercent = 100 }, // Iron Helm
                    new GearDrop { ItemId = 8, ChancePercent = 50 },  // Oak Staff
                ],
            },
        ],
        instances:
        [
            new InstanceDefinition
            {
                Id = 1, Name = "Ragefire Depths", IsRaid = false, MinPlayers = 2, MaxPlayers = 5,
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
                Id = 2, Name = "Molten Sanctum", IsRaid = true, MinPlayers = 2, MaxPlayers = 40,
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
        quests:
        [
            new QuestDefinition
            {
                Id = 1, Name = "La menace gobeline",
                Description = "Les gobelins pillent nos champs et nos caravanes. Abats 10 Gobelins " +
                              "du camp à l'est, puis reviens me voir.",
                TurnInText = "Dix de moins ! Mais tant que leur roi respire, ils reviendront…",
                TargetMonsterId = 1, RequiredKills = 10,
                RewardXp = 150, RewardGold = 5000, NextQuestId = 2,
            },
            new QuestDefinition
            {
                Id = 2, Name = "Le Roi des gobelins",
                Description = "Leur roi se terre au fond du camp, gras de nos récoltes volées. " +
                              "Tranche la tête de cette vermine couronnée !",
                TurnInText = "Le roi est mort ! Le sanctuaire te doit une fière chandelle, héros.",
                TargetMonsterId = 3, RequiredKills = 1,
                RewardXp = 500, RewardGold = 20000, RewardItemId = 14, NextQuestId = 0,
            },
        ],
        items:
        [
            new ItemDefinition { Id = 1,  Name = "Rusty Sword",  Type = ItemType.Weapon, Slot = EquipSlot.Weapon, AttackBonus = 3,  GoldValue = 5 },
            new ItemDefinition { Id = 2,  Name = "Iron Sword",   Type = ItemType.Weapon, Slot = EquipSlot.Weapon, AttackBonus = 6,  GoldValue = 20 },
            new ItemDefinition { Id = 3,  Name = "Leather Vest", Type = ItemType.Armor,  Slot = EquipSlot.Armor,  DefenseBonus = 4, HealthBonus = 10, GoldValue = 15 },
            new ItemDefinition { Id = 4,  Name = "Steel Sword",  Type = ItemType.Weapon, Slot = EquipSlot.Weapon, AttackBonus = 9,  GoldValue = 120 },
            new ItemDefinition { Id = 5,  Name = "Worn Bow",     Type = ItemType.Weapon, Slot = EquipSlot.Weapon, AttackBonus = 2,  GoldValue = 4 },
            new ItemDefinition { Id = 6,  Name = "Worn Staff",   Type = ItemType.Weapon, Slot = EquipSlot.Weapon, AttackBonus = 2,  GoldValue = 4 },
            new ItemDefinition { Id = 7,  Name = "Hunting Bow",  Type = ItemType.Weapon, Slot = EquipSlot.Weapon, AttackBonus = 5,  GoldValue = 45 },
            new ItemDefinition { Id = 8,  Name = "Oak Staff",    Type = ItemType.Weapon, Slot = EquipSlot.Weapon, AttackBonus = 6,  GoldValue = 60 },
            new ItemDefinition { Id = 9,  Name = "Chain Mail",   Type = ItemType.Armor,  Slot = EquipSlot.Armor,  DefenseBonus = 7, HealthBonus = 20, GoldValue = 150 },
            new ItemDefinition { Id = 12, Name = "Padded Robe",  Type = ItemType.Armor,  Slot = EquipSlot.Armor,  DefenseBonus = 2, HealthBonus = 15, GoldValue = 35 },
            // Full WoW-style loadout pieces.
            new ItemDefinition { Id = 13, Name = "Leather Cap",       Type = ItemType.Armor, Slot = EquipSlot.Head,      DefenseBonus = 2, GoldValue = 12 },
            new ItemDefinition { Id = 14, Name = "Iron Helm",         Type = ItemType.Armor, Slot = EquipSlot.Head,      DefenseBonus = 4, HealthBonus = 10, GoldValue = 90 },
            new ItemDefinition { Id = 15, Name = "Leather Boots",     Type = ItemType.Armor, Slot = EquipSlot.Feet,      DefenseBonus = 2, GoldValue = 12 },
            new ItemDefinition { Id = 16, Name = "Wolf-fur Shoulders", Type = ItemType.Armor, Slot = EquipSlot.Shoulders, DefenseBonus = 3, HealthBonus = 5, GoldValue = 40 },
            new ItemDefinition { Id = 17, Name = "Leather Pants",     Type = ItemType.Armor, Slot = EquipSlot.Legs,      DefenseBonus = 3, GoldValue = 18 },
            new ItemDefinition { Id = 18, Name = "Traveler's Cloak",  Type = ItemType.Armor, Slot = EquipSlot.Back,      DefenseBonus = 1, HealthBonus = 10, GoldValue = 30 },
            new ItemDefinition { Id = 19, Name = "Leather Gloves",    Type = ItemType.Armor, Slot = EquipSlot.Hands,     DefenseBonus = 2, GoldValue = 12 },
            new ItemDefinition { Id = 21, Name = "Sturdy Belt",       Type = ItemType.Armor, Slot = EquipSlot.Waist,     DefenseBonus = 2, GoldValue = 12 },
            new ItemDefinition { Id = 22, Name = "Wooden Shield",     Type = ItemType.Armor, Slot = EquipSlot.OffHand,   DefenseBonus = 5, GoldValue = 50 },

            // BAGS: worn in the Bag slot, each adds cells to the carried inventory (vendor stock).
            new ItemDefinition { Id = 23, Name = "Sac de toile",          Type = ItemType.Bag, Slot = EquipSlot.Bag, BagCapacity = 8,  GoldValue = 200 },
            new ItemDefinition { Id = 24, Name = "Sac de cuir",           Type = ItemType.Bag, Slot = EquipSlot.Bag, BagCapacity = 16, GoldValue = 800 },
            new ItemDefinition { Id = 25, Name = "Grand sac du voyageur", Type = ItemType.Bag, Slot = EquipSlot.Bag, BagCapacity = 24, GoldValue = 2500 },

            // CONSUMABLES: potions are instant (30 s shared cooldown); food regenerates over time.
            new ItemDefinition { Id = 26, Name = "Potion de mana mineure", Type = ItemType.Consumable, Stackable = true, MaxStack = 20, GoldValue = 25, ConsumeEffect = EffectType.RestoreResource, ConsumeMagnitude = 0.4f },
            new ItemDefinition { Id = 27, Name = "Pain de route", Type = ItemType.Consumable, Stackable = true, MaxStack = 20, GoldValue = 8, ConsumeEffect = EffectType.Regen, ConsumeMagnitude = 0.05f, ConsumeDurationTicks = 360 },
            new ItemDefinition { Id = 10, Name = "Wolf Pelt",    Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 3 },
            new ItemDefinition { Id = 11, Name = "Goblin Ear",   Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 2 },
            new ItemDefinition { Id = 20, Name = "Minor Healing Potion", Type = ItemType.Consumable, Stackable = true, MaxStack = 10, GoldValue = 5, ConsumeEffect = EffectType.Heal, ConsumeMagnitude = 0.4f },
            // Body parts (guaranteed skinning loot; see MonsterDefinition.BodyParts).
            new ItemDefinition { Id = 30, Name = "Goblin Head",         Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 4 },
            new ItemDefinition { Id = 31, Name = "Goblin Skin",         Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 3 },
            new ItemDefinition { Id = 32, Name = "Goblin Eye",          Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 1 },
            new ItemDefinition { Id = 33, Name = "Goblin Finger",       Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 1 },
            new ItemDefinition { Id = 34, Name = "Goblin Tongue",       Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 1 },
            new ItemDefinition { Id = 35, Name = "Goblin Foot",         Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 1 },
            new ItemDefinition { Id = 40, Name = "Wolf Paw",            Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 2 },
            new ItemDefinition { Id = 41, Name = "Wolf Tail",           Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 2 },
            new ItemDefinition { Id = 42, Name = "Wolf Fang",           Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 3 },
            new ItemDefinition { Id = 43, Name = "Wolf Heart",          Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 5 },
            new ItemDefinition { Id = 44, Name = "Wolf Liver",          Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 4 },
            new ItemDefinition { Id = 45, Name = "Wolf Bone",           Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 2 },
            new ItemDefinition { Id = 50, Name = "Crowned Goblin Head", Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 40 },
            new ItemDefinition { Id = 60, Name = "Ashmaw Horn",         Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 100 },
            new ItemDefinition { Id = 61, Name = "Devourer Heart",      Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 250 },
            new ItemDefinition { Id = 62, Name = "Charred Hide",        Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 150 },
            new ItemDefinition { Id = 63, Name = "Ashmaw Claw",         Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 80 },
            new ItemDefinition { Id = 64, Name = "Devourer Bone",       Type = ItemType.Material, Stackable = true, MaxStack = 20, GoldValue = 60 },
        ]);
}
