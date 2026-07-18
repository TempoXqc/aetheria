using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Net;

namespace Aetheria.Tests;

public static class RegenSpellTests
{
    [Test("Renew (self-cast) restores health gradually over its duration.")]
    public static void Renew_HealsOverTime()
    {
        var world = new World();
        ServerEntity human = world.SpawnPlayer(new PeerId(1), "H", 1, 1);   // Human Warrior
        ServerEntity orc = world.SpawnPlayer(new PeerId(2), "O", 2, 1);     // hits him first

        world.TryUseAbility(orc.Id, orc.BasicAbilityId, human.Id);
        int damagedHp = human.Health;
        Assert.True(damagedHp < human.EffectiveMaxHealth);

        // Self-cast Renew: the target id is ignored for range-0 abilities.
        Assert.True(world.TryUseAbility(human.Id, abilityId: 5, human.Id));

        for (int i = 0; i < 60; i++) // 3 seconds of the 10s effect
        {
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(human.Health > damagedHp, "Renew should have healed over time");
    }

    [Test("Renew also refills mana over time for mana users, but leaves rage untouched.")]
    public static void Renew_RestoresManaOnly()
    {
        var world = new World();
        ServerEntity mage = world.SpawnPlayer(new PeerId(1), "M", 1, 2);    // Mana
        ServerEntity warrior = world.SpawnPlayer(new PeerId(2), "W", 2, 1); // Rage (starts 0)

        mage.SpendResource(80);
        int mageManaBefore = (int)mage.CurrentResource;

        Assert.True(world.TryUseAbility(mage.Id, 5, mage.Id));
        Assert.True(world.TryUseAbility(warrior.Id, 5, warrior.Id));

        for (int i = 0; i < 40; i++)
        {
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True((int)mage.CurrentResource > mageManaBefore + 5, "mana should regenerate faster with Renew");
        Assert.Equal(0, (int)warrior.CurrentResource); // rage untouched (no combat happening)
    }

    [Test("Renew respects its cooldown.")]
    public static void Renew_Cooldown()
    {
        var world = new World();
        ServerEntity p = world.SpawnPlayer(new PeerId(1), "P", 1, 1);

        Assert.True(world.TryUseAbility(p.Id, 5, p.Id));
        Assert.False(world.TryUseAbility(p.Id, 5, p.Id)); // 30s cooldown
    }
}
