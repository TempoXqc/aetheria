using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Data;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Tests;

public static class ProgressionTests
{
    [Test("Level is derived from cumulative XP thresholds and capped at MaxLevel.")]
    public static void LevelForXp_MatchesThresholds()
    {
        var p = new ProgressionConfig(); // thresholds [0,100,250,500,...], max 10

        Assert.Equal(1, p.LevelForXp(0));
        Assert.Equal(1, p.LevelForXp(99));
        Assert.Equal(2, p.LevelForXp(100));
        Assert.Equal(3, p.LevelForXp(300));
        Assert.Equal(p.MaxLevel, p.LevelForXp(10_000_000)); // capped, never inflates past MaxLevel
    }

    [Test("XpForNextLevel returns the next threshold, or -1 at the cap.")]
    public static void XpForNextLevel_Works()
    {
        var p = new ProgressionConfig();
        Assert.Equal(100, p.XpForNextLevel(0));   // next after level 1
        Assert.Equal(250, p.XpForNextLevel(100)); // next after level 2
        Assert.Equal(-1, p.XpForNextLevel(10_000_000));
    }

    [Test("Stat bonuses grow continuously with total XP.")]
    public static void StatBonuses_ScaleWithXp()
    {
        var p = new ProgressionConfig(); // 0.02 atk, 0.01 def, 0.10 hp per xp
        Assert.Equal(0, p.AttackBonusForXp(0));
        Assert.Equal(10, p.AttackBonusForXp(500));  // 500 * 0.02
        Assert.Equal(5, p.DefenseBonusForXp(500));  // 500 * 0.01
        Assert.Equal(50, p.HealthBonusForXp(500));  // 500 * 0.10
    }

    [Test("Killing a monster grants its XP and gold to the killer.")]
    public static void KillingMonster_GrantsXpAndGold()
    {
        var world = new World();
        ServerEntity hunter = world.SpawnPlayer(new PeerId(1), "H", 1, 1); // Warrior, no starter kit
        ServerEntity goblin = world.SpawnMonster(1, hunter.Position + new Vec2(1f, 0f)); // xp 35, gold 5

        for (int i = 0; i < 200 && goblin.IsAlive; i++)
        {
            if (hunter.IsAbilityReady(hunter.BasicAbilityId, world.Tick))
            {
                world.TryUseAbility(hunter.Id, hunter.BasicAbilityId, goblin.Id);
            }

            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(goblin.IsDead, "hunter should have killed the goblin");
        Assert.Equal(35, hunter.TotalXp); // XP is immediate…
        Assert.Equal(0, hunter.Inventory.Gold); // …but the gold waits in the corpse (no auto-loot)

        foreach (ServerEntity e in world.Entities.Values)
        {
            if (e.Kind == EntityKind.MonsterCorpse && e.LootContainer is not null)
            {
                world.TryLootCorpse(hunter.Id, e.Id);
            }
        }

        Assert.Equal(5, hunter.Inventory.Gold);
        Assert.Equal(1, hunter.Level);
    }

    [Test("Progression bonuses feed into effective stats.")]
    public static void ProgressionBonus_RaisesEffectiveStats()
    {
        var world = new World();
        ServerEntity p = world.SpawnPlayer(new PeerId(1), "P", 1, 1);
        int baseAttack = p.EffectiveAttackPower;

        p.ProgressionAttackBonus = 5;
        Assert.Equal(baseAttack + 5, p.EffectiveAttackPower);
    }

    [Test("Advanced abilities are gated by level: locked at level 1, usable once the level is reached.")]
    public static void AbilityUnlock_GatedByLevel()
    {
        var world = new World();
        ServerEntity warrior = world.SpawnPlayer(new PeerId(1), "W", 1, 1); // kit: Slash(1), Whirlwind(20 @ lvl3)
        ServerEntity target = world.SpawnPlayer(new PeerId(2), "T", raceId: 2, classId: 1); // opposite faction
        warrior.GainResource(100); // ensure rage is not the blocker (Whirlwind costs 25)

        warrior.Level = 1;
        Assert.False(world.TryUseAbility(warrior.Id, abilityId: 20, target.Id), "Whirlwind locked at level 1");

        warrior.Level = 3;
        Assert.True(world.TryUseAbility(warrior.Id, abilityId: 20, target.Id), "Whirlwind unlocked at level 3");
    }
}
