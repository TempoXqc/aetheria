using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Data;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Tests;

public static class ResourceSkillTests
{
    [Test("Mana grows with XP; rage and energy stay fixed at their base pool.")]
    public static void Resources_OnlyManaGrowsWithXp()
    {
        var world = new World();
        ServerEntity mage = world.SpawnPlayer(new PeerId(1), "Manauser", 1, 2);    // Human Mage (Mana)
        ServerEntity warrior = world.SpawnPlayer(new PeerId(2), "Rageuser", 1, 1);  // Human Warrior (Rage)
        ServerEntity ranger = world.SpawnPlayer(new PeerId(3), "Energyuser", 2, 3); // Orc Ranger (Energy)

        int mageBase = mage.EffectiveMaxResource;   // 100
        int warriorBase = warrior.EffectiveMaxResource;
        int rangerBase = ranger.EffectiveMaxResource;

        world.GrantExperience(mage, 500);
        world.GrantExperience(warrior, 500);
        world.GrantExperience(ranger, 500);

        Assert.Equal(mageBase + 25, mage.EffectiveMaxResource); // 500 * 0.05 mana growth
        Assert.Equal(warriorBase, warrior.EffectiveMaxResource); // rage does not grow
        Assert.Equal(rangerBase, ranger.EffectiveMaxResource);   // energy does not grow
    }

    [Test("A skill line's damage multiplier scales with skill and caps at MaxSkill.")]
    public static void SkillDamageMultiplier_ScalesAndCaps()
    {
        var p = new ProgressionConfig(); // 0.004 per point, cap 100
        Assert.Close(1.00f, p.SkillDamageMultiplier(0));
        Assert.Close(1.40f, p.SkillDamageMultiplier(100));
        Assert.Close(1.40f, p.SkillDamageMultiplier(500)); // capped
    }

    [Test("Using an ability trains its skill line, and the trained skill is capped.")]
    public static void UsingAbility_TrainsSkillLine()
    {
        var world = new World();
        ServerEntity w = world.SpawnPlayer(new PeerId(1), "Swinger", 1, 1); // Warrior, Slash → line 1
        ServerEntity t = world.SpawnPlayer(new PeerId(2), "Dummy", raceId: 2, classId: 1);

        Assert.Equal(0, w.GetSkill(1));
        world.TryUseAbility(w.Id, w.BasicAbilityId, t.Id);
        Assert.Equal(1, w.GetSkill(1)); // trained by one use

        w.AddSkill(1, 500, 100);
        Assert.Equal(100, w.GetSkill(1)); // capped
    }

    [Test("Higher weapon skill makes the same ability hit harder.")]
    public static void HigherSkill_DealsMoreDamage()
    {
        var world = new World();
        ServerEntity w = world.SpawnPlayer(new PeerId(1), "Master", 1, 1);
        ServerEntity t = world.SpawnPlayer(new PeerId(2), "Target", raceId: 2, classId: 1);

        world.TryUseAbility(w.Id, w.BasicAbilityId, t.Id); // skill 0 at damage time
        int lowSkillDamage = world.DrainCombatEvents()[0].Damage;

        w.AddSkill(1, 100, 100); // max out the sword line
        for (int i = 0; i < SimulationConstants.GlobalCooldownTicks + 2; i++) // clear cooldown AND the GCD
        {
            world.Step(SimulationConstants.TickDelta);
        }

        world.TryUseAbility(w.Id, w.BasicAbilityId, t.Id);
        int highSkillDamage = world.DrainCombatEvents()[0].Damage;

        Assert.True(highSkillDamage > lowSkillDamage, "maxed weapon skill should deal more damage");
    }
}
