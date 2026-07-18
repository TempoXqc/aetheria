using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Tests;

/// <summary>WoW-style incantations: cast bar, movement cancels, costs paid on completion.</summary>
public static class CastingTests
{
    private static (World world, ServerEntity mage, ServerEntity goblin) Arrange()
    {
        var world = new World();
        ServerEntity mage = world.SpawnPlayer(new PeerId(1), "Magus", raceId: 1, classId: 2); // Human Mage
        ServerEntity goblin = world.SpawnMonster(1, mage.Position + new Vec2(5f, 0f));
        return (world, mage, goblin);
    }

    [Test("Firebolt starts an incantation: no damage yet, resource untouched, snapshot shows the bar.")]
    public static void Firebolt_StartsCast_NoInstantDamage()
    {
        (World world, ServerEntity mage, ServerEntity goblin) = Arrange();
        int hp = goblin.Health;
        float mana = mage.CurrentResource;

        Assert.True(world.TryUseAbility(mage.Id, 2, goblin.Id)); // Firebolt: 30-tick cast
        Assert.True(mage.IsCasting);
        Assert.Equal(hp, goblin.Health);                          // nothing lands yet
        Assert.Equal((int)mana, (int)mage.CurrentResource);       // mana paid at completion only

        world.Step(SimulationConstants.TickDelta);
        EntitySnapshot snap = default;
        foreach (EntitySnapshot e in world.BuildAreaSnapshot(mage.Position))
        {
            if (e.Id == mage.Id) { snap = e; }
        }

        Assert.Equal((byte)2, snap.CastAbilityId); // the cast bar is visible to everyone
    }

    [Test("The incantation completes after its cast time: damage lands, mana and cooldown are paid.")]
    public static void Firebolt_Completes_DamageAndCosts()
    {
        (World world, ServerEntity mage, ServerEntity goblin) = Arrange();
        int hp = goblin.Health;
        float mana = mage.CurrentResource;

        world.TryUseAbility(mage.Id, 2, goblin.Id);
        for (int i = 0; i <= 31; i++)
        {
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.False(mage.IsCasting);
        Assert.True(goblin.Health < hp, "the bolt must land at the end of the cast");
        Assert.True(mage.CurrentResource < mana, "mana is spent on completion");
        Assert.False(mage.IsAbilityReady(2, world.Tick), "cooldown starts on completion");
    }

    [Test("MOVING breaks the incantation: no damage, no mana spent.")]
    public static void Moving_CancelsCast()
    {
        (World world, ServerEntity mage, ServerEntity goblin) = Arrange();
        int hp = goblin.Health;
        float mana = mage.CurrentResource;

        world.TryUseAbility(mage.Id, 2, goblin.Id);
        world.Step(SimulationConstants.TickDelta);
        world.ApplyInput(mage.Id, 1, new Vec2(1f, 0f)); // a step: the spell fizzles

        Assert.False(mage.IsCasting);
        for (int i = 0; i < 40; i++)
        {
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.Equal(hp, goblin.Health);
        Assert.True(mage.CurrentResource >= mana - 0.01f, "no mana lost on a cancelled cast");
    }

    [Test("While incanting, further casts are refused (one spell at a time).")]
    public static void Casting_BlocksOtherCasts()
    {
        (World world, ServerEntity mage, ServerEntity goblin) = Arrange();
        world.TryUseAbility(mage.Id, 2, goblin.Id);
        Assert.False(world.TryUseAbility(mage.Id, 2, goblin.Id));
    }

    [Test("Warrior Slash stays INSTANT: no cast bar involved.")]
    public static void Slash_IsInstant()
    {
        var world = new World();
        ServerEntity warrior = world.SpawnPlayer(new PeerId(1), "Grom", raceId: 2, classId: 1);
        ServerEntity goblin = world.SpawnMonster(1, warrior.Position + new Vec2(1f, 0f));
        int hp = goblin.Health;

        Assert.True(world.TryUseAbility(warrior.Id, 1, goblin.Id));
        Assert.False(warrior.IsCasting);
        Assert.True(goblin.Health < hp, "Slash lands immediately");
    }

    [Test("If the target dies mid-cast, the spell fizzles without cost.")]
    public static void TargetDeath_FizzlesCast()
    {
        (World world, ServerEntity mage, ServerEntity goblin) = Arrange();
        float mana = mage.CurrentResource;

        world.TryUseAbility(mage.Id, 2, goblin.Id);
        world.Despawn(goblin.Id); // target vanishes mid-incantation

        for (int i = 0; i <= 31; i++)
        {
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.False(mage.IsCasting);
        Assert.True(mage.CurrentResource >= mana - 0.01f);
    }
}
