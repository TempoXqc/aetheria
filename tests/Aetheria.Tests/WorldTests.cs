using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Tests;

public static class WorldTests
{
    [Test("A spawned player exists and appears in a snapshot taken at its position.")]
    public static void SpawnPlayer_IsVisibleAtItsPosition()
    {
        var world = new World();
        ServerEntity player = world.SpawnPlayer(new PeerId(1));

        List<EntitySnapshot> snap = world.BuildAreaSnapshot(player.Position);

        Assert.Contains(player.Id, ToIds(snap));
    }

    [Test("Authoritative movement advances the entity by speed * dt in the input direction.")]
    public static void Step_MovesEntityBySpeedTimesDelta()
    {
        var world = new World();
        ServerEntity player = world.SpawnPlayer(new PeerId(1));
        Vec2 start = player.Position;

        world.ApplyInput(player.Id, sequence: 1, new Vec2(1, 0)); // move +X
        world.Step(SimulationConstants.TickDelta);

        float expectedDx = SimulationConstants.PlayerMoveSpeed * SimulationConstants.TickDelta;
        Assert.Close(start.X + expectedDx, player.Position.X);
        Assert.Close(start.Y, player.Position.Y);
    }

    [Test("An oversized input vector cannot make a player move faster than the speed cap.")]
    public static void ApplyInput_ClampsToUnitLength()
    {
        var world = new World();
        ServerEntity player = world.SpawnPlayer(new PeerId(1));
        Vec2 start = player.Position;

        world.ApplyInput(player.Id, 1, new Vec2(1000, 0)); // absurdly large
        world.Step(SimulationConstants.TickDelta);

        float maxDx = SimulationConstants.PlayerMoveSpeed * SimulationConstants.TickDelta;
        Assert.Close(start.X + maxDx, player.Position.X);
    }

    [Test("Stale (out-of-order) inputs are ignored.")]
    public static void ApplyInput_IgnoresStaleSequence()
    {
        var world = new World();
        ServerEntity player = world.SpawnPlayer(new PeerId(1));
        Vec2 start = player.Position;

        world.ApplyInput(player.Id, sequence: 5, new Vec2(1, 0));
        world.ApplyInput(player.Id, sequence: 3, new Vec2(0, 1)); // older, must be dropped
        world.Step(SimulationConstants.TickDelta);

        // Movement should still be along +X (from seq 5), not +Y — measured against the spawn point.
        Assert.True(player.Position.X > start.X, "expected +X movement from the newer input");
        Assert.Close(start.Y, player.Position.Y);
    }

    [Test("Two nearby players each appear in the other's area-of-interest snapshot.")]
    public static void BuildAreaSnapshot_NearbyPlayersSeeEachOther()
    {
        var world = new World();
        ServerEntity a = world.SpawnPlayer(new PeerId(1));
        ServerEntity b = world.SpawnPlayer(new PeerId(2));

        // Spawns are placed near the origin, comfortably within the AoI radius.
        Assert.True(Vec2.DistanceSquared(a.Position, b.Position)
            < SimulationConstants.AreaOfInterestRadius * SimulationConstants.AreaOfInterestRadius);

        Assert.Contains(b.Id, ToIds(world.BuildAreaSnapshot(a.Position)));
        Assert.Contains(a.Id, ToIds(world.BuildAreaSnapshot(b.Position)));
    }

    [Test("A player far outside the AoI radius is culled from the snapshot.")]
    public static void BuildAreaSnapshot_FarPlayerIsCulled()
    {
        var world = new World();
        ServerEntity a = world.SpawnPlayer(new PeerId(1));
        ServerEntity b = world.SpawnPlayer(new PeerId(2));

        // Drive b far away along +X for a couple of seconds of simulated time.
        world.ApplyInput(b.Id, 1, new Vec2(1, 0));
        int stepsToLeaveAoi = (int)(
            (SimulationConstants.AreaOfInterestRadius + 50f)
            / (SimulationConstants.PlayerMoveSpeed * SimulationConstants.TickDelta));

        for (int i = 0; i < stepsToLeaveAoi; i++)
        {
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.DoesNotContain(b.Id, ToIds(world.BuildAreaSnapshot(a.Position)));
        // a still sees itself.
        Assert.Contains(a.Id, ToIds(world.BuildAreaSnapshot(a.Position)));
    }

    [Test("Despawn removes the entity from the world.")]
    public static void Despawn_RemovesEntity()
    {
        var world = new World();
        ServerEntity player = world.SpawnPlayer(new PeerId(1));
        world.Despawn(player.Id);

        Assert.DoesNotContain(player.Id, ToIds(world.BuildAreaSnapshot(player.Position)));
        Assert.False(world.Entities.ContainsKey(player.Id));
    }

    private static List<int> ToIds(List<EntitySnapshot> snapshot)
    {
        var ids = new List<int>(snapshot.Count);
        foreach (EntitySnapshot e in snapshot)
        {
            ids.Add(e.Id);
        }

        return ids;
    }
}
