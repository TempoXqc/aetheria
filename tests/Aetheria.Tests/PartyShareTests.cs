using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;

namespace Aetheria.Tests;

/// <summary>
/// Group life: a kill feeds the whole party — the XP is split equally between nearby members
/// and the quest kill counts for everyone hunting that monster, not just the killing blow.
/// </summary>
public static class PartyShareTests
{
    [Test("A party kill SPLITS the XP equally: the healer levels from the tank's killing blow.")]
    public static void PartyKill_SplitsXpWithNearbyMembers()
    {
        var world = new World();
        ServerEntity tank = world.SpawnPlayer(new PeerId(1), "Rempart", raceId: 0, classId: 1);
        ServerEntity healer = world.SpawnPlayer(new PeerId(2), "Soigneuse", raceId: 0, classId: 5);
        world.Teleport(healer, tank.Position + new Vec2(3f, 0f));

        // Wire the two as one party (the GameServer normally injects this resolver).
        world.PartySiblingsOf = id => id == tank.Id ? new[] { healer.Id } : new[] { tank.Id };

        ServerEntity goblin = world.SpawnMonster(1, tank.Position + new Vec2(1.2f, 0f));
        tank.FacingRadians = 0f; // the goblin stands east: face it

        for (int i = 0; i < 2000 && goblin.IsAlive; i++)
        {
            world.TryUseAbility(tank.Id, 1, goblin.Id);
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(goblin.IsDead, "the goblin must fall for the payout to happen");
        Assert.True(tank.TotalXp > 0, "the killer earns his share");
        Assert.True(healer.TotalXp > 0, "the nearby party member earns hers too");
        Assert.Equal(tank.TotalXp, healer.TotalXp); // same level, same split
    }

    [Test("A member FAR AWAY (>60 u) shares nothing: you must be part of the fight.")]
    public static void PartyKill_FarMemberGetsNothing()
    {
        var world = new World();
        ServerEntity tank = world.SpawnPlayer(new PeerId(1), "Rempart", raceId: 0, classId: 1);
        ServerEntity afk = world.SpawnPlayer(new PeerId(2), "Absent", raceId: 0, classId: 2);
        world.Teleport(afk, tank.Position + new Vec2(80f, 0f)); // way out of the 60 u share range

        world.PartySiblingsOf = id => id == tank.Id ? new[] { afk.Id } : new[] { tank.Id };

        ServerEntity goblin = world.SpawnMonster(1, tank.Position + new Vec2(1.2f, 0f));
        tank.FacingRadians = 0f;

        for (int i = 0; i < 2000 && goblin.IsAlive; i++)
        {
            world.TryUseAbility(tank.Id, 1, goblin.Id);
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(goblin.IsDead);
        Assert.True(tank.TotalXp > 0);
        Assert.Equal(0, afk.TotalXp); // no free ride from across the map
    }
}
