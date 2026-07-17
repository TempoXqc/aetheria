namespace Aetheria.Shared.Data;

/// <summary>
/// Tunable progression rules. Deliberately NOT a WoW-style ever-inflating level treadmill: there is a
/// small, fixed <see cref="MaxLevel"/> whose only role is to unlock abilities and gate access to
/// events. The primary source of character power is continuous <b>stat growth from experience</b>:
/// total XP maps to flat bonuses on attack, defense, and max health. Both tracks are data (below), so
/// designers tune curves without recompiling.
/// </summary>
public sealed class ProgressionConfig
{
    /// <summary>The fixed level ceiling. Levels exist to unlock content, not to inflate power forever.</summary>
    public int MaxLevel { get; init; } = 10;

    /// <summary>
    /// Cumulative total XP required to have reached each level. Index i is the XP needed for level i+1,
    /// so element 0 is 0 (everyone starts at level 1).
    /// </summary>
    public int[] LevelXpThresholds { get; init; } =
        [0, 100, 250, 500, 900, 1500, 2400, 3600, 5200, 7200];

    // Continuous stat growth per point of total XP.
    public float AttackPerXp { get; init; } = 0.02f;
    public float DefensePerXp { get; init; } = 0.01f;
    public float HealthPerXp { get; init; } = 0.10f;

    /// <summary>The level a character with the given total XP has reached (clamped to MaxLevel).</summary>
    public int LevelForXp(int totalXp)
    {
        int level = 1;
        for (int i = 0; i < LevelXpThresholds.Length; i++)
        {
            if (totalXp >= LevelXpThresholds[i])
            {
                level = i + 1;
            }
            else
            {
                break;
            }
        }

        return System.Math.Min(level, MaxLevel);
    }

    /// <summary>Total XP required for the next level, or -1 if already at the cap.</summary>
    public int XpForNextLevel(int totalXp)
    {
        int level = LevelForXp(totalXp);
        if (level >= MaxLevel || level >= LevelXpThresholds.Length)
        {
            return -1;
        }

        return LevelXpThresholds[level]; // threshold for level+1 (index == current level)
    }

    public int AttackBonusForXp(int totalXp) => (int)(totalXp * AttackPerXp);
    public int DefenseBonusForXp(int totalXp) => (int)(totalXp * DefensePerXp);
    public int HealthBonusForXp(int totalXp) => (int)(totalXp * HealthPerXp);
}
