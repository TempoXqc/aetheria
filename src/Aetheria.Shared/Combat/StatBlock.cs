namespace Aetheria.Shared.Combat;

/// <summary>
/// The derived combat attributes an entity fights with. For players this is computed from their
/// class (base) plus their race (modifiers); for monsters it comes straight from the monster
/// definition. Kept as an immutable value — the mutable part (current health) lives on the entity.
/// </summary>
public readonly struct StatBlock
{
    public StatBlock(
        int maxHealth,
        float moveSpeed,
        int attackPower,
        int defense,
        float aggroRadius)
    {
        MaxHealth = maxHealth;
        MoveSpeed = moveSpeed;
        AttackPower = attackPower;
        Defense = defense;
        AggroRadius = aggroRadius;
    }

    /// <summary>Maximum hit points.</summary>
    public int MaxHealth { get; }

    /// <summary>Movement speed in world units per second.</summary>
    public float MoveSpeed { get; }

    /// <summary>Added to an ability's base damage before the target's defense is subtracted.</summary>
    public int AttackPower { get; }

    /// <summary>Subtracted from incoming damage (a floor of 1 damage always applies).</summary>
    public int Defense { get; }

    /// <summary>How far (world units) a monster will notice and aggro onto a player. 0 for players.</summary>
    public float AggroRadius { get; }

    /// <summary>Combine a class's base stats with a race's modifiers into a final stat block.</summary>
    public static StatBlock Combine(StatBlock classBase, RaceModifiers race) => new(
        maxHealth: System.Math.Max(1, classBase.MaxHealth + race.HealthBonus),
        moveSpeed: System.Math.Max(0.1f, classBase.MoveSpeed * race.MoveSpeedMultiplier),
        attackPower: System.Math.Max(0, classBase.AttackPower + race.AttackBonus),
        defense: System.Math.Max(0, classBase.Defense + race.DefenseBonus),
        aggroRadius: classBase.AggroRadius);
}

/// <summary>Additive/multiplicative tweaks a race applies on top of a class's base stats.</summary>
public readonly struct RaceModifiers
{
    public RaceModifiers(int healthBonus, int attackBonus, int defenseBonus, float moveSpeedMultiplier)
    {
        HealthBonus = healthBonus;
        AttackBonus = attackBonus;
        DefenseBonus = defenseBonus;
        MoveSpeedMultiplier = moveSpeedMultiplier;
    }

    public int HealthBonus { get; }
    public int AttackBonus { get; }
    public int DefenseBonus { get; }
    public float MoveSpeedMultiplier { get; }

    public static RaceModifiers None => new(0, 0, 0, 1f);
}
