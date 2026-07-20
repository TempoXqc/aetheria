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
        Assert.True(reason1.Contains("au moins"));

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

    [Test("Entering an instance NEVER locks abilities: its clock matches the open world.")]
    public static void InstanceClock_MatchesOpenWorld_AbilitiesStayReady()
    {
        var manager = new WorldManager();

        // Age the open world so cooldown stamps carry BIG tick values.
        for (int i = 0; i < 400; i++) { manager.OpenWorld.Step(Aetheria.Shared.SimulationConstants.TickDelta); }

        ServerEntity tank = manager.OpenWorld.SpawnPlayer(new PeerId(1), "Rempart", raceId: 0, classId: 1);
        manager.OpenWorld.Teleport(tank, new Vec2(40f, 0f)); // OUTSIDE the sanctuary
        ServerEntity dummy = manager.OpenWorld.SpawnMonster(1, new Vec2(41.2f, 0f));
        tank.FacingRadians = 0f;
        Assert.True(manager.OpenWorld.TryUseAbility(tank.Id, 1, dummy.Id)); // stamps cooldown + GCD

        World inst = manager.CreateInstance(Dungeon, groupSize: 1);
        Assert.Equal((int)manager.OpenWorld.Tick, (int)inst.Tick); // SAME clock

        WorldManager.TransferPlayer(manager.OpenWorld, inst, tank.Id, new Vec2(0f, 0f));

        // Let the swing cooldown + GCD expire INSIDE the instance, then strike its monster.
        for (int i = 0; i < 80; i++) { inst.Step(Aetheria.Shared.SimulationConstants.TickDelta); }

        ServerEntity mob = inst.Entities.Values.First(e => e.IsMonster);
        inst.Teleport(tank, mob.Position + new Vec2(-1.2f, 0f));
        tank.FacingRadians = 0f;
        Assert.True(inst.TryUseAbility(tank.Id, 1, mob.Id),
            "the transferring player's cooldown stamps must stay meaningful in the instance");
    }

    [Test("Instance monsters respawn SLOWLY (3 min), not on the open world's 5-second treadmill.")]
    public static void InstanceMonsters_RespawnSlowly()
    {
        var manager = new WorldManager();
        World inst = manager.CreateInstance(Dungeon, groupSize: 1);
        ServerEntity mob = inst.Entities.Values.First(e => e.IsMonster);
        ServerEntity tank = manager.OpenWorld.SpawnPlayer(new PeerId(1), "Rempart", raceId: 0, classId: 1);
        WorldManager.TransferPlayer(manager.OpenWorld, inst, tank.Id, new Vec2(0f, 0f));
        inst.Teleport(tank, mob.Position + new Vec2(-1.2f, 0f));
        tank.FacingRadians = 0f;

        for (int i = 0; i < 4000 && mob.IsAlive; i++)
        {
            inst.TryUseAbility(tank.Id, 1, mob.Id);
            inst.Step(Aetheria.Shared.SimulationConstants.TickDelta);
        }

        Assert.True(mob.IsDead, "the pack must fall first");

        // 30 s later (open world would have respawned it at 5 s): STILL down.
        for (int i = 0; i < 600; i++) { inst.Step(Aetheria.Shared.SimulationConstants.TickDelta); }
        Assert.True(mob.IsDead, "3-minute timer: nothing comes back after only 30 s");
    }

    [Test("Instance monsters scale with group size (health and damage multipliers).")]
    public static void CreateInstance_ScalesMonsters()
    {
        var manager = new WorldManager();

        // Solo: 1 + 0.5*(1-1) = x1 → goblin hp 60.
        World solo = manager.CreateInstance(Dungeon, groupSize: 1);
        ServerEntity soloMob = solo.Entities.Values.First(e => e.IsMonster);
        Assert.Equal(130, soloMob.Stats.MaxHealth);

        // Five players: hp x(1 + 0.5*4) = x3 → 180; atk x(1 + 0.25*4) = x2 → 10 (goblin atk 5).
        World five = manager.CreateInstance(Dungeon, groupSize: 5);
        ServerEntity fiveMob = five.Entities.Values.First(e => e.IsMonster);
        Assert.Equal(390, fiveMob.Stats.MaxHealth); // 130 × (1 + 0.5×4)
        Assert.Equal(10, fiveMob.Stats.AttackPower);
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
