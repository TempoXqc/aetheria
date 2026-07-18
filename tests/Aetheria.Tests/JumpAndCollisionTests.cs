using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Data;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Tests;

public static class JumpAndCollisionTests
{
    [Test("InputCommand carries the jump flag through a round trip.")]
    public static void InputCommand_CarriesJump()
    {
        var w = new PacketWriter();
        new InputCommand(7, new Vec2(1f, 0f), 0.5f, jump: true).Write(w);
        var r = new PacketReader(w.WrittenSpan);
        Assert.Equal(MessageType.InputCommand, (MessageType)r.ReadByte());
        InputCommand decoded = InputCommand.Read(ref r);
        Assert.True(decoded.Jump);
        Assert.Equal(7u, decoded.Sequence);
    }

    [Test("A jump shows in snapshots for its 12 ticks, then clears.")]
    public static void Jump_FlagsInSnapshot_ThenLands()
    {
        var world = new World();
        ServerEntity p = world.SpawnPlayer(new PeerId(1), "Sauteur", 2, 1);

        world.ApplyInput(p.Id, 1, Vec2.Zero, 0f, jump: true);
        world.Step(SimulationConstants.TickDelta);

        EntitySnapshot snap = FindSelf(world, p);
        Assert.True(snap.IsJumping, "the hop must be visible to everyone");

        for (int i = 0; i < ServerEntity.JumpDurationTicks + 2; i++)
        {
            world.Step(SimulationConstants.TickDelta);
        }

        snap = FindSelf(world, p);
        Assert.False(snap.IsJumping, "landed: the flag clears on its own");
    }

    [Test("A tree blocks you: walking straight into it leaves you outside its trunk circle.")]
    public static void Obstacle_BlocksMovement()
    {
        var world = new World
        {
            Obstacles = new[] { new WorldLayout.Obstacle(5f, 0f, 0.6f) },
        };
        ServerEntity p = world.SpawnPlayer(new PeerId(1), "Fonceur", 2, 1);
        world.Teleport(p, new Vec2(0f, 0f));

        // Sprint due east through the tree for 3 seconds.
        for (int i = 0; i < 60; i++)
        {
            world.ApplyInput(p.Id, (uint)(i + 1), new Vec2(1f, 0f));
            world.Step(SimulationConstants.TickDelta);
        }

        float dist = MathF.Sqrt(Vec2.DistanceSquared(p.Position, new Vec2(5f, 0f)));
        Assert.True(dist >= 0.6f + 0.44f, $"the body must stay outside the trunk (dist={dist:0.00})");
    }

    [Test("Sliding: hitting a tree at an angle carries you around it, not into a dead stop.")]
    public static void Obstacle_AllowsSliding()
    {
        var world = new World
        {
            Obstacles = new[] { new WorldLayout.Obstacle(5f, 0.3f, 0.6f) }, // slightly off-path
        };
        ServerEntity p = world.SpawnPlayer(new PeerId(1), "Glisseur", 2, 1);
        world.Teleport(p, new Vec2(0f, 0f));

        for (int i = 0; i < 80; i++)
        {
            world.ApplyInput(p.Id, (uint)(i + 1), new Vec2(1f, 0f));
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(p.Position.X > 8f, $"the walker should get PAST the tree (x={p.Position.X:0.00})");
    }

    [Test("Instances have no obstacles: the same walk goes straight through.")]
    public static void Instances_HaveNoObstacles()
    {
        var world = new World(); // instance-like: Obstacles defaults to empty
        ServerEntity p = world.SpawnPlayer(new PeerId(1), "Libre", 2, 1);
        world.Teleport(p, new Vec2(0f, 0f));

        for (int i = 0; i < 40; i++)
        {
            world.ApplyInput(p.Id, (uint)(i + 1), new Vec2(1f, 0f));
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(p.Position.X > 8f);
    }

    private static EntitySnapshot FindSelf(World world, ServerEntity p)
    {
        foreach (EntitySnapshot e in world.BuildAreaSnapshot(p.Position))
        {
            if (e.Id == p.Id) { return e; }
        }

        throw new InvalidOperationException("self not found in snapshot");
    }
}
