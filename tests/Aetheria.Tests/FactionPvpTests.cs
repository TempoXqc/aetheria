using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Net;

namespace Aetheria.Tests;

public static class FactionPvpTests
{
    [Test("Players cannot attack members of their own faction.")]
    public static void SameFaction_CannotAttack()
    {
        var world = new World();
        ServerEntity human = world.SpawnPlayer(new PeerId(1), "H", raceId: 1, classId: 1);  // Alliance
        ServerEntity dwarf = world.SpawnPlayer(new PeerId(2), "D", raceId: 4, classId: 1);  // Alliance
        int hp = dwarf.Health;

        Assert.False(world.TryUseAbility(human.Id, human.BasicAbilityId, dwarf.Id));
        Assert.Equal(hp, dwarf.Health);
    }

    [Test("Players CAN attack the opposite faction — open-world PvP.")]
    public static void CrossFaction_CanAttack()
    {
        var world = new World();
        ServerEntity human = world.SpawnPlayer(new PeerId(1), "H", raceId: 1, classId: 1); // Alliance
        ServerEntity orc = world.SpawnPlayer(new PeerId(2), "O", raceId: 2, classId: 1);   // Horde

        Assert.True(world.TryUseAbility(human.Id, human.BasicAbilityId, orc.Id));
        Assert.True(orc.Health < orc.Stats.MaxHealth);
    }

    [Test("BANDIT mode breaks the truce BOTH ways: he strikes his camp, his camp strikes him.")]
    public static void Bandit_BreaksTheFactionTruce()
    {
        var world = new World();
        ServerEntity bandit = world.SpawnPlayer(new PeerId(1), "B", raceId: 1, classId: 1); // Alliance
        ServerEntity honest = world.SpawnPlayer(new PeerId(2), "H", raceId: 4, classId: 1); // Alliance
        bandit.IsBandit = true;

        Assert.True(world.TryUseAbility(bandit.Id, bandit.BasicAbilityId, honest.Id),
            "the outlaw may strike his own camp");

        // Turn to FACE the outlaw before striking back (the 180° facing rule applies).
        honest.FacingRadians = System.MathF.Atan2(
            bandit.Position.Y - honest.Position.Y, bandit.Position.X - honest.Position.X);
        Assert.True(world.TryUseAbility(honest.Id, honest.BasicAbilityId, bandit.Id),
            "and his own camp may strike the outlaw back");
    }

    [Test("A PvP kill moves the ledger: enemy kill = +honor +own-camp rep; own-camp kill = −rep.")]
    public static void PlayerKill_MovesHonorAndReputation()
    {
        // Enemy kill first: Alliance warrior fells a Horde one.
        var world = new World();
        ServerEntity killer = world.SpawnPlayer(new PeerId(1), "K", raceId: 1, classId: 1); // Alliance
        ServerEntity orc = world.SpawnPlayer(new PeerId(2), "O", raceId: 2, classId: 1);    // Horde
        for (int i = 0; i < 4000 && orc.IsAlive; i++)
        {
            world.TryUseAbility(killer.Id, killer.BasicAbilityId, orc.Id);
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(orc.IsDead);
        Assert.Equal(10, killer.HonorPoints);
        Assert.Equal(25, killer.RepAlliance);

        // Own-camp kill (bandit work): the same camp's esteem plummets.
        var world2 = new World();
        ServerEntity outlaw = world2.SpawnPlayer(new PeerId(1), "B", raceId: 1, classId: 1); // Alliance
        ServerEntity victim = world2.SpawnPlayer(new PeerId(2), "V", raceId: 4, classId: 1); // Alliance
        outlaw.IsBandit = true;
        for (int i = 0; i < 4000 && victim.IsAlive; i++)
        {
            world2.TryUseAbility(outlaw.Id, outlaw.BasicAbilityId, victim.Id);
            world2.Step(SimulationConstants.TickDelta);
        }

        Assert.True(victim.IsDead);
        Assert.Equal(10, outlaw.HonorPoints);
        Assert.Equal(-50, outlaw.RepAlliance);
    }

    [Test("The faction rule does not protect monsters (players can always fight PvE).")]
    public static void Monsters_AreAlwaysAttackable()
    {
        var world = new World();
        ServerEntity orc = world.SpawnPlayer(new PeerId(1), "O", raceId: 2, classId: 1);
        ServerEntity goblin = world.SpawnMonster(1, orc.Position);

        Assert.True(world.TryUseAbility(orc.Id, orc.BasicAbilityId, goblin.Id));
    }
}
