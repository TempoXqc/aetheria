using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Tests;

/// <summary>Server-driven auto-attack, the global cooldown, and the lethal-cast crash regression.</summary>
public static class AutoAttackTests
{
    [Test("Declare the attack intent ONCE: the server swings until the monster dies, then stands down.")]
    public static void AttackIntent_ServerSwingsToTheKill()
    {
        var world = new World();
        ServerEntity warrior = world.SpawnPlayer(new PeerId(1), "Grom", raceId: 2, classId: 1);
        ServerEntity goblin = world.SpawnMonster(1, warrior.Position + new Vec2(1f, 0f));

        world.SetAttackTarget(warrior.Id, goblin.Id); // one message, no button mashing

        for (int i = 0; i < 600 && goblin.IsAlive; i++)
        {
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(goblin.IsDead, "the server must drive the swings to the kill");
        world.Step(SimulationConstants.TickDelta);   // next tick notices the dead target…
        Assert.Equal(0, warrior.AutoAttackTargetId); // …and clears the intent

        // No auto-loot: the head waits inside the corpse's loot window.
        Assert.Equal(0, warrior.Inventory.CountOf(30));
        foreach (ServerEntity e in world.Entities.Values)
        {
            if (e.Kind == EntityKind.MonsterCorpse && e.LootContainer is not null)
            {
                Assert.Equal(1, e.LootContainer.CountOf(30));
            }
        }
    }

    [Test("A mage's attack intent auto-RECASTS its incantation (turret mode while standing).")]
    public static void AttackIntent_MageAutoRecasts()
    {
        var world = new World();
        ServerEntity mage = world.SpawnPlayer(new PeerId(1), "Magus", raceId: 1, classId: 2);
        ServerEntity goblin = world.SpawnMonster(1, mage.Position + new Vec2(5f, 0f));
        int hp = goblin.Health;

        world.SetAttackTarget(mage.Id, goblin.Id);

        // Two full casts' worth of time: at least two bolts must have landed.
        for (int i = 0; i < 80; i++)
        {
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(goblin.Health <= hp - 2 * 10, $"repeated incantations must land (hp {goblin.Health}/{hp})");
    }

    [Test("The GLOBAL COOLDOWN gates manual abilities, but not the server's auto swings.")]
    public static void Gcd_GatesManualCasts()
    {
        var world = new World();
        ServerEntity warrior = world.SpawnPlayer(new PeerId(1), "Grom", raceId: 2, classId: 1);
        ServerEntity target = world.SpawnPlayer(new PeerId(2), "Cible", raceId: 1, classId: 1);
        world.GrantExperience(warrior, 300); // level 3: Whirlwind unlocked
        warrior.GainResource(100);           // rage for Whirlwind

        // First manual cast lands and arms the GCD…
        Assert.True(world.TryUseAbility(warrior.Id, 1, target.Id));
        // …so a DIFFERENT ability (own cooldown clear) is still refused within 1.5s.
        Assert.False(world.TryUseAbility(warrior.Id, 20, target.Id));

        for (int i = 0; i < SimulationConstants.GlobalCooldownTicks + 1; i++)
        {
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(world.TryUseAbility(warrior.Id, 20, target.Id), "after the GCD it fires");
    }

    [Test("REGRESSION: a lethal incantation completing must not crash the tick (corpse spawn mid-loop).")]
    public static void LethalCast_DoesNotCrashProcessCasts()
    {
        var world = new World();
        ServerEntity mage = world.SpawnPlayer(new PeerId(1), "Magus", raceId: 1, classId: 2);
        world.GrantExperience(mage, 2000); // enough punch to finish the goblin
        ServerEntity goblin = world.SpawnMonster(1, mage.Position + new Vec2(5f, 0f));

        // Wear the goblin down to a sliver with server swings, then finish it with a CAST.
        world.SetAttackTarget(mage.Id, goblin.Id);
        for (int i = 0; i < 2000 && goblin.IsAlive; i++)
        {
            world.Step(SimulationConstants.TickDelta); // the killing blow lands INSIDE ProcessCasts
        }

        Assert.True(goblin.IsDead, "the lethal cast must resolve");

        bool remains = false;
        foreach (ServerEntity e in world.Entities.Values)
        {
            if (e.Kind == EntityKind.MonsterCorpse) { remains = true; }
        }

        Assert.True(remains, "the kill still leaves remains (spawned safely mid-resolution)");
    }
}
