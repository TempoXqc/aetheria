using Aetheria.Shared.Combat;
using Aetheria.Shared.Items;

namespace Aetheria.Shared.Data;

/// <summary>
/// Data definitions for the game's content. These are plain DTOs with init properties so they can be
/// authored as JSON and deserialized (see <see cref="GameData"/>). Stat fields are flattened rather
/// than nesting a <see cref="StatBlock"/> so the JSON stays simple and human-editable; each type
/// exposes a helper that builds the runtime value object.
/// </summary>
public sealed class AbilityDefinition
{
    public byte Id { get; init; }
    public string Name { get; init; } = "Ability";

    /// <summary>Base damage before the attacker's power and the target's defense are applied.</summary>
    public int BaseDamage { get; init; }

    /// <summary>Maximum distance (world units) between attacker and target for the ability to land.</summary>
    public float Range { get; init; } = 2.5f;

    /// <summary>Cooldown in simulation ticks between uses.</summary>
    public int CooldownTicks { get; init; } = 10;

    /// <summary>
    /// Incantation time in ticks (0 = instant). Cast-time spells start a visible cast bar; MOVING
    /// cancels the cast; resource and cooldown are only paid when the cast completes — like WoW.
    /// </summary>
    public int CastTimeTicks { get; init; }

    /// <summary>Resource spent to use the ability (rage/mana/energy). 0 for free abilities.</summary>
    public int ResourceCost { get; init; }

    /// <summary>Non-damage effect applied on use (heal, buff, resource restore). None for pure attacks.</summary>
    public EffectType Effect { get; init; } = EffectType.None;

    /// <summary>Effect magnitude: a fraction for heals/restores, a multiplier bonus for buffs.</summary>
    public float EffectMagnitude { get; init; }

    /// <summary>How long a buff effect lasts, in ticks. 0 for instant effects.</summary>
    public int EffectDurationTicks { get; init; }

    /// <summary>Character level required before a player may use this ability. 1 = available immediately.</summary>
    public int UnlockLevel { get; init; } = 1;

    /// <summary>
    /// The proficiency line this ability trains (e.g. Swords, Fire, Marksmanship). Using the ability
    /// raises that skill, which raises the ability's damage. 0 = no line (racials, monster attacks).
    /// </summary>
    public byte SkillLineId { get; init; }
}

public sealed class RaceDefinition
{
    public byte Id { get; init; }
    public string Name { get; init; } = "Race";
    public Faction Faction { get; init; } = Faction.Neutral;
    public int HealthBonus { get; init; }
    public int AttackBonus { get; init; }
    public int DefenseBonus { get; init; }
    public float MoveSpeedMultiplier { get; init; } = 1f;

    /// <summary>Class ids this race is allowed to play (the balance matrix).</summary>
    public byte[] AllowedClassIds { get; init; } = [];

    /// <summary>The race's unique racial ability id.</summary>
    public byte RacialAbilityId { get; init; }

    public RaceModifiers ToModifiers()
        => new(HealthBonus, AttackBonus, DefenseBonus, MoveSpeedMultiplier);

    public bool AllowsClass(byte classId) => Array.IndexOf(AllowedClassIds, classId) >= 0;
}

public sealed class ClassDefinition
{
    public byte Id { get; init; }
    public string Name { get; init; } = "Class";
    public int MaxHealth { get; init; } = 100;
    public float MoveSpeed { get; init; } = SimulationConstants.PlayerMoveSpeed;
    public int AttackPower { get; init; }
    public int Defense { get; init; }

    /// <summary>The ability used by this class's basic attack (available from level 1).</summary>
    public byte BasicAbilityId { get; init; }

    /// <summary>
    /// The ability the server AUTO-ATTACKS with (0 = same as BasicAbilityId). Lets the Mage
    /// auto-attack with its WAND while Firebolt stays a hand-cast incantation, WoW-style.
    /// </summary>
    public byte AutoAttackAbilityId { get; init; }

    /// <summary>The resolved auto-attack ability.</summary>
    public byte EffectiveAutoAttackId => AutoAttackAbilityId != 0 ? AutoAttackAbilityId : BasicAbilityId;

    /// <summary>Every ability this class can use (basic + advanced). Advanced ones gate on level.</summary>
    public byte[] AbilityIds { get; init; } = [];

    /// <summary>True if the ability belongs to this class's kit.</summary>
    public bool HasAbility(byte abilityId)
        => abilityId == BasicAbilityId || abilityId == EffectiveAutoAttackId ||
           Array.IndexOf(AbilityIds, abilityId) >= 0;

    /// <summary>The resource this class spends on abilities.</summary>
    public ResourceType Resource { get; init; } = ResourceType.Mana;

    /// <summary>Maximum resource pool.</summary>
    public int MaxResource { get; init; } = 100;

    /// <summary>Passive resource regenerated per second (0 for rage, which builds in combat).</summary>
    public float ResourceRegenPerSec { get; init; }

    public StatBlock ToBaseStats()
        => new(MaxHealth, MoveSpeed, AttackPower, Defense, aggroRadius: 0f);
}

public sealed class MonsterDefinition
{
    public byte Id { get; init; }
    public string Name { get; init; } = "Monster";
    public int MaxHealth { get; init; } = 50;
    public float MoveSpeed { get; init; } = 4f;
    public int AttackPower { get; init; }
    public int Defense { get; init; }
    public float AggroRadius { get; init; } = 15f;
    public byte BasicAbilityId { get; init; }

    /// <summary>Creature level, used for nameplates and the level-difference XP multiplier.</summary>
    public int Level { get; init; } = 1;

    /// <summary>Experience granted to the player who lands the killing blow (before level scaling).</summary>
    public int XpReward { get; init; }

    /// <summary>Gold granted to the killer.</summary>
    public int GoldReward { get; init; }

    /// <summary>
    /// GUARANTEED body-part drops, distinct per creature type: every kill yields exactly these
    /// items (no RNG), so "bring me 10 goblin heads" quests always make sense — kill 10, get 10.
    /// </summary>
    public IReadOnlyList<LootEntry> BodyParts { get; init; } = [];

    /// <summary>
    /// EQUIPMENT drops: rolled once per kill with the given percent chance (100 = guaranteed).
    /// Body parts are deterministic; gear is where the dice live.
    /// </summary>
    public IReadOnlyList<GearDrop> GearDrops { get; init; } = [];

    public StatBlock ToStats()
        => new(MaxHealth, MoveSpeed, AttackPower, Defense, AggroRadius);
}

/// <summary>One guaranteed drop: an item id and the quantity every kill yields.</summary>
public sealed class LootEntry
{
    public byte ItemId { get; init; }
    public int Quantity { get; init; } = 1;
}

/// <summary>One chance-based equipment drop.</summary>
public sealed class GearDrop
{
    public byte ItemId { get; init; }

    /// <summary>Drop chance in percent (100 = always).</summary>
    public int ChancePercent { get; init; } = 100;
}

/// <summary>One monster placement inside an instance template.</summary>
public sealed class InstanceSpawn
{
    public byte MonsterId { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
}

/// <summary>
/// A data-driven instanced zone template. Instances (and raids) are private copies created per
/// group; their monsters SCALE with the entering group's size. Raids additionally require a minimum
/// party size. (Open-world dungeons are deliberately NOT defined here — they are just monster camps
/// in the shared world, where PvP is possible.)
/// </summary>
public sealed class InstanceDefinition
{
    public byte Id { get; init; }
    public string Name { get; init; } = "Instance";

    /// <summary>Raids require a minimum group size and allow a much larger one.</summary>
    public bool IsRaid { get; init; }

    public int MinPlayers { get; init; } = 1;
    public int MaxPlayers { get; init; } = 5;

    /// <summary>Extra monster health per player beyond the first (0.5 = +50% each).</summary>
    public float HealthScalingPerExtraPlayer { get; init; } = 0.5f;

    /// <summary>Extra monster damage per player beyond the first.</summary>
    public float DamageScalingPerExtraPlayer { get; init; } = 0.25f;

    public InstanceSpawn[] Spawns { get; init; } = [];
}

/// <summary>A data-driven item: equippable gear (with stat bonuses) or stackable materials/consumables.</summary>
public sealed class ItemDefinition
{
    public byte Id { get; init; }
    public string Name { get; init; } = "Item";
    public ItemType Type { get; init; } = ItemType.Material;
    public EquipSlot Slot { get; init; } = EquipSlot.None;

    public int AttackBonus { get; init; }
    public int DefenseBonus { get; init; }
    public int HealthBonus { get; init; }

    public bool Stackable { get; init; }
    public int MaxStack { get; init; } = 1;

    /// <summary>Worth in gold (for future vendors / banking valuation).</summary>
    public int GoldValue { get; init; }

    public bool IsEquippable => Slot != EquipSlot.None;
}
