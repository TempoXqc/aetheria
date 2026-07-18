namespace Aetheria.Shared.Combat;

/// <summary>The two warring factions, plus Neutral for monsters and world objects.</summary>
public enum Faction : byte
{
    Neutral = 0,
    Alliance = 1,
    Horde = 2,
}

/// <summary>Character gender. Cosmetic only — it has no effect on stats or gameplay.</summary>
public enum Gender : byte
{
    Male = 0,
    Female = 1,
}

/// <summary>
/// The resource a class spends to use abilities. Each behaves differently over time (see the
/// server's resource handling): Mana and Energy regenerate passively; Rage is built in combat and
/// decays out of it.
/// </summary>
public enum ResourceType : byte
{
    Mana = 0,
    Rage = 1,
    Energy = 2,
}

/// <summary>
/// The kind of effect an ability applies to its target (or, for racials, the caster). Damage is
/// handled separately via an ability's base damage; these are the non-damage outcomes.
/// </summary>
public enum EffectType : byte
{
    None = 0,
    Heal = 1,            // instant: restore a fraction of max health
    RestoreResource = 2, // instant: restore a fraction of max resource
    BuffAttack = 3,      // timed: multiply attack power
    BuffDefense = 4,     // timed: multiply defense
    BuffMoveSpeed = 5,   // timed: multiply move speed
    Regen = 6,           // timed: restore a fraction of max health (and max mana) per second
}

/// <summary>
/// A temporary stat modifier currently affecting an entity (e.g. an Orc's Blood Fury). Expires at a
/// fixed tick; while active it contributes its magnitude to the relevant effective stat.
/// </summary>
public readonly struct ActiveEffect
{
    public readonly EffectType Type;

    /// <summary>Multiplier bonus for buffs (0.4 = +40%). Not used by instant effects.</summary>
    public readonly float Magnitude;

    /// <summary>Simulation tick at which this effect ends.</summary>
    public readonly uint ExpiresAtTick;

    public ActiveEffect(EffectType type, float magnitude, uint expiresAtTick)
    {
        Type = type;
        Magnitude = magnitude;
        ExpiresAtTick = expiresAtTick;
    }
}
