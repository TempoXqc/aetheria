using Aetheria.Server.World;
using Aetheria.Shared.Data;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Tests;

public static class InstanceTests
{
    private static InstanceDefinition Dungeon => new()
    {
        Id = 1, Name = "Test Depths", IsRaid = false, MinPlayers = 1, MaxPlayers = 5,
        HealthScalingPerExtraPlayer = 0.5f, DamageScalingPerExtraPlayer = 0.25f,
        Spawns = [new InstanceSpawn { MonsterId = 1, X = 10f, Y = 5f }], // Goblin: hp 60, atk 8
    };

    private static InstanceDefinition Raid => new()
    {
        Id = 2, Name = "Test Sanctum", IsRaid = true, MinPlayers = 6, MaxPlayers = 40,
        Spawns = [new InstanceSpawn { MonsterId = 2, X = 10f, Y = 5f }],
    };

    [Test("Raid gating: fewer than the minimum is refused; 6..40 allowed; beyond the cap refused.")]
    public static void RaidGate_EnforcesGroupSize()
    {
        Assert.False(WorldManager.CanEnter(Raid, 1, out string reason1));
        Assert.True(reason1.Contains("at least"));

        Assert.False(WorldManager.CanEnter(Raid, 5, out _));
        Assert.True(WorldManager.CanEnter(Raid, 6, out _));
        Assert.True(WorldManager.CanEnter(Raid, 40, out _));
        Assert.False(WorldManager.CanEnter(Raid, 41, out _));
    }

    [Test("A non-raid instance allows solo entry up to its max.")]
    public static void Dungeon_AllowsSoloToMax()
    {
        Assert.True(WorldManager.CanEnter(Dungeon, 1, out _));
        Assert.True(WorldManager.CanEnter(Dungeon, 5, out _));
        Assert.False(WorldManager.CanEnter(Dungeon, 6, out _));
    }

    [Test("Instance monsters scale with group size (health and damage multipliers).")]
    public static void CreateInstance_ScalesMonsters()
    {
        var manager = new WorldManager();

        // Solo: 1 + 0.5*(1-1) = x1 → goblin hp 60.
        World solo = manager.CreateInstance(Dungeon, groupSize: 1);
        ServerEntity soloMob = solo.Entities.Values.First(e => e.IsMonster);
        Assert.Equal(60, soloMob.Stats.MaxHealth);

        // Five players: hp x(1 + 0.5*4) = x3 → 180; atk x(1 + 0.25*4) = x2 → 16.
        World five = manager.CreateInstance(Dungeon, groupSize: 5);
        ServerEntity fiveMob = five.Entities.Values.First(e => e.IsMonster);
        Assert.Equal(180, fiveMob.Stats.MaxHealth);
        Assert.Equal(16, fiveMob.Stats.AttackPower);
    }

    [Test("A player transfers into an instance (same id) and out again; the empty instance is destroyed.")]
    public static void Transfer_InAndOut_PreservesIdentity()
    {
        var manager = new WorldManager();
        World open = manager.OpenWorld;
        ServerEntity player = open.SpawnPlayer(new PeerId(1), "Runner", 1, 1);
        int id = player.Id;

        World instance = manager.CreateInstance(Dungeon, 1);
        Assert.Equal(1, manager.InstanceCount);

        Assert.True(WorldManager.TransferPlayer(open, instance, id, new Vec2(0, 0)));
        Assert.False(open.Entities.ContainsKey(id));
        Assert.True(instance.Entities.ContainsKey(id));
        Assert.Equal(id, instance.Entities[id].Id); // identity preserved across worlds

        // The instance's monsters are visible to the player inside it, not from the open world.
        var visibleInside = instance.BuildAreaSnapshot(instance.Entities[id].Position);
        Assert.True(visibleInside.Any(e => e.Kind == EntityKind.Monster));

        Assert.True(WorldManager.TransferPlayer(instance, open, id, Vec2.Zero));
        manager.DestroyInstanceIfEmpty(instance);
        Assert.Equal(0, manager.InstanceCount); // emptied instance is torn down
        Assert.True(open.Entities.ContainsKey(id));
    }

    [Test("Worlds are isolated: combat in an instance does not leak into the open world.")]
    public static void Worlds_AreIsolated()
    {
        var manager = new WorldManager();
        World open = manager.OpenWorld;
        World instance = manager.CreateInstance(Dungeon, 1);

        ServerEntity insidePlayer = open.SpawnPlayer(new PeerId(1), "Inside", 1, 1);
        WorldManager.TransferPlayer(open, instance, insidePlayer.Id, new Vec2(10f, 5f));

        // Fight the instance goblin.
        ServerEntity mob = instance.Entities.Values.First(e => e.IsMonster);
        Assert.True(instance.TryUseAbility(insidePlayer.Id, insidePlayer.BasicAbilityId, mob.Id));

        Assert.True(instance.DrainCombatEvents().Count > 0);
        Assert.Equal(0, open.DrainCombatEvents().Count); // nothing leaked into the open world
    }
}
