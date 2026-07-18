using Aetheria.Shared.Data;

namespace Aetheria.Tests;

public static class XpScalingTests
{
    [Test("Monsters at your level give full XP; above give more; far below give the floor trickle.")]
    public static void XpMultiplier_ScalesWithLevelDifference()
    {
        var p = new ProgressionConfig(); // +15%/level above, -20%/level below, floor 0.10

        Assert.Close(1.00f, p.XpMultiplierForKill(playerLevel: 3, monsterLevel: 3));
        Assert.Close(1.30f, p.XpMultiplierForKill(playerLevel: 3, monsterLevel: 5)); // fighting up
        Assert.Close(0.60f, p.XpMultiplierForKill(playerLevel: 5, monsterLevel: 3)); // farming down
        Assert.Close(0.10f, p.XpMultiplierForKill(playerLevel: 10, monsterLevel: 1)); // grey floor
        Assert.Close(2.00f, p.XpMultiplierForKill(playerLevel: 1, monsterLevel: 10)); // capped bonus
    }
}
