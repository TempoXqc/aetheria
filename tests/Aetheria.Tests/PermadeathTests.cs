using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Tests;

public static class PermadeathTests
{
    [Test("On death a character resets to a fresh level-1 (XP, level, and skills wiped).")]
    public static void Death_ResetsCharacterProgression()
    {
        var world = new World();
        ServerEntity killer = world.SpawnPlayer(new PeerId(1), "Killer", 1, 1);
        ServerEntity victim = world.SpawnPlayer(new PeerId(2), "Victim", 1, 1);
        world.GrantStarterKit(victim);

        world.GrantExperience(victim, 300); // level 3 with stat bonuses
        victim.AddSkill(1, 50, 100);        // trained sword skill
        Assert.Equal(3, victim.Level);
        Assert.Equal(300, victim.TotalXp);
        Assert.Equal(50, victim.GetSkill(1));

        // Killer beats the victim to death.
        for (int i = 0; i < 400 && victim.IsAlive; i++)
        {
            if (killer.IsAbilityReady(killer.BasicAbilityId, world.Tick))
            {
                world.TryUseAbility(killer.Id, killer.BasicAbilityId, victim.Id);
            }

            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(victim.IsDead);

        // Hardcore permadeath: progression is wiped back to a fresh character.
        Assert.Equal(0, victim.TotalXp);
        Assert.Equal(1, victim.Level);
        Assert.Equal(0, victim.GetSkill(1));
        Assert.Equal(0, victim.ProgressionAttackBonus);
    }

    [Test("The dead character's carried gold and gear drop to a lootable corpse (not lost, not kept).")]
    public static void Death_DropsCarriedGoldToCorpse()
    {
        var world = new World();
        ServerEntity killer = world.SpawnPlayer(new PeerId(1), "Reaper", 1, 1);
        ServerEntity victim = world.SpawnPlayer(new PeerId(2), "Prey", 1, 1);
        world.GrantStarterKit(victim); // 50 gold + Rusty Sword

        for (int i = 0; i < 400 && victim.IsAlive; i++)
        {
            if (killer.IsAbilityReady(killer.BasicAbilityId, world.Tick))
            {
                world.TryUseAbility(killer.Id, killer.BasicAbilityId, victim.Id);
            }

            world.Step(SimulationConstants.TickDelta);
        }

        // The victim's OLD gold is on the corpse; the reborn character has a fresh starter kit only.
        int corpseGold = -1;
        foreach (var kv in world.Entities)
        {
            if (kv.Value.Kind == EntityKind.Corpse && kv.Value.LootContainer is not null)
            {
                corpseGold = kv.Value.LootContainer.Gold;
            }
        }

        Assert.Equal(SimulationConstants.StartingGold, corpseGold);
    }
}
