using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;

namespace Aetheria.Tests;

public static class SafeZoneTests
{
    private static World OpenWorld() => new() { HasSafeZone = true };

    [Test("Inside the sanctuary, cross-faction players cannot attack each other.")]
    public static void SafeZone_BlocksPvp()
    {
        World world = OpenWorld();
        ServerEntity human = world.SpawnPlayer(new PeerId(1), "H", 1, 1); // spawns near origin: inside
        ServerEntity orc = world.SpawnPlayer(new PeerId(2), "O", 2, 1);

        Assert.True(world.IsSafePosition(human.Position));
        Assert.False(world.TryUseAbility(human.Id, human.BasicAbilityId, orc.Id));
        Assert.False(world.TryUseAbility(orc.Id, orc.BasicAbilityId, human.Id));

        // Step both OUTSIDE the sanctuary: PvP works again.
        human.Position = new Vec2(30f, 0f);
        orc.Position = new Vec2(31f, 0f);
        Assert.True(world.TryUseAbility(human.Id, human.BasicAbilityId, orc.Id));
    }

    [Test("Inside the sanctuary, monsters never aggro a player.")]
    public static void SafeZone_BlocksMonsterAggro()
    {
        World world = OpenWorld();
        ServerEntity player = world.SpawnPlayer(new PeerId(1), "P", 1, 1); // inside sanctuary
        ServerEntity goblin = world.SpawnMonster(1, player.Position + new Vec2(2f, 0f));
        int hp = player.Health;

        for (int i = 0; i < 60; i++)
        {
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(goblin.AiTargetId is null, "monster must not aggro inside the sanctuary");
        Assert.Equal(hp, player.Health);
    }

    [Test("A player inside the sanctuary cannot cheese monsters outside it either.")]
    public static void SafeZone_BlocksAttackingOut()
    {
        World world = OpenWorld();
        ServerEntity player = world.SpawnPlayer(new PeerId(1), "P", 1, 3); // Ranger... invalid: race1 class3
        player = world.SpawnPlayer(new PeerId(2), "P2", 4, 3);             // Dwarf Ranger (ranged, 10u)
        ServerEntity goblin = world.SpawnMonster(1, new Vec2(9f, 0f));     // in Shot range from origin

        Assert.True(world.IsSafePosition(player.Position));
        Assert.False(world.TryUseAbility(player.Id, player.BasicAbilityId, goblin.Id));
    }

    [Test("Instances have no sanctuary — the same position is not safe there.")]
    public static void Instances_HaveNoSafeZone()
    {
        var plain = new World(); // instance-like: HasSafeZone false
        Assert.False(plain.IsSafePosition(Vec2.Zero));
    }
}
