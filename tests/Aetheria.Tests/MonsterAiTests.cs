using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;

namespace Aetheria.Tests;

public static class MonsterAiTests
{
    [Test("A monster aggros a nearby player and attacks it, dealing damage.")]
    public static void Monster_AggrosAndAttacks_NearbyPlayer()
    {
        var world = new World();
        ServerEntity player = world.SpawnPlayer(new PeerId(1));
        // Place a goblin one unit away — inside both aggro and melee range.
        ServerEntity goblin = world.SpawnMonster(monsterId: 1, player.Position + new Vec2(1f, 0f));
        int startHp = player.Health;

        for (int i = 0; i < 40; i++)
        {
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.Equal(player.Id, goblin.AiTargetId ?? -1);
        Assert.True(player.Health < startHp, "the aggroed goblin should have damaged the player");
    }

    [Test("A monster ignores a player outside its aggro radius.")]
    public static void Monster_IgnoresFarPlayer()
    {
        var world = new World();
        ServerEntity player = world.SpawnPlayer(new PeerId(1));
        // Goblin aggro radius is 15; put it far past that.
        ServerEntity goblin = world.SpawnMonster(monsterId: 1, player.Position + new Vec2(80f, 80f));
        int startHp = player.Health;

        for (int i = 0; i < 40; i++)
        {
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(goblin.AiTargetId is null, "far player should not be targeted");
        Assert.Equal(startHp, player.Health);
    }

    [Test("A monster moves toward a player that is in aggro range but out of attack range.")]
    public static void Monster_ChasesPlayerInAggroRange()
    {
        var world = new World();
        ServerEntity player = world.SpawnPlayer(new PeerId(1));
        // 10 units away: within aggro (15), beyond melee (2.5) — so it should close the gap.
        ServerEntity goblin = world.SpawnMonster(monsterId: 1, player.Position + new Vec2(10f, 0f));

        float startDistSq = Vec2.DistanceSquared(goblin.Position, player.Position);

        for (int i = 0; i < 15; i++)
        {
            world.Step(SimulationConstants.TickDelta);
        }

        float endDistSq = Vec2.DistanceSquared(goblin.Position, player.Position);
        Assert.True(endDistSq < startDistSq, "goblin should have moved closer to the player");
    }
}
