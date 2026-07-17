using Aetheria.Shared.Combat;

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
}

public sealed class RaceDefinition
{
    public byte Id { get; init; }
    public string Name { get; init; } = "Race";
    public int HealthBonus { get; init; }
    public int AttackBonus { get; init; }
    public int DefenseBonus { get; init; }
    public float MoveSpeedMultiplier { get; init; } = 1f;

    public RaceModifiers ToModifiers()
        => new(HealthBonus, AttackBonus, DefenseBonus, MoveSpeedMultiplier);
}

public sealed class ClassDefinition
{
    public byte Id { get; init; }
    public string Name { get; init; } = "Class";
    public int MaxHealth { get; init; } = 100;
    public float MoveSpeed { get; init; } = SimulationConstants.PlayerMoveSpeed;
    public int AttackPower { get; init; }
    public int Defense { get; init; }

    /// <summary>The ability used by this class's basic attack.</summary>
    public byte BasicAbilityId { get; init; }

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

    public StatBlock ToStats()
        => new(MaxHealth, MoveSpeed, AttackPower, Defense, AggroRadius);
}
