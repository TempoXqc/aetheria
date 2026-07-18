using Aetheria.Server.World;
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

    [Test("The faction rule does not protect monsters (players can always fight PvE).")]
    public static void Monsters_AreAlwaysAttackable()
    {
        var world = new World();
        ServerEntity orc = world.SpawnPlayer(new PeerId(1), "O", raceId: 2, classId: 1);
        ServerEntity goblin = world.SpawnMonster(1, orc.Position);

        Assert.True(world.TryUseAbility(orc.Id, orc.BasicAbilityId, goblin.Id));
    }
}
