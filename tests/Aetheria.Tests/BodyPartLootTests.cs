using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Tests;

/// <summary>
/// Guaranteed skinning loot: every monster kill yields its body parts — no RNG — so collection
/// quests ("bring 10 goblin heads") are deterministic. Parts are distinct per creature type.
/// </summary>
public static class BodyPartLootTests
{
    private static void KillMonster(World world, ServerEntity killer, ServerEntity monster)
    {
        for (int i = 0; i < 600 && monster.IsAlive; i++)
        {
            if (killer.IsAbilityReady(killer.BasicAbilityId, world.Tick))
            {
                world.TryUseAbility(killer.Id, killer.BasicAbilityId, monster.Id);
            }

            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(monster.IsDead, "the monster must die for loot to drop");
    }

    [Test("Killing a goblin ALWAYS yields every goblin body part, in the defined quantities.")]
    public static void GoblinKill_YieldsAllGoblinParts()
    {
        var world = new World();
        ServerEntity killer = world.SpawnPlayer(new PeerId(1), "Chasseur", raceId: 2, classId: 1);
        ServerEntity goblin = world.SpawnMonster(1, killer.Position + new Vec2(1f, 0f));

        KillMonster(world, killer, goblin);

        Assert.Equal(1, killer.Inventory.CountOf(30)); // Goblin Head
        Assert.Equal(1, killer.Inventory.CountOf(31)); // Goblin Skin
        Assert.Equal(2, killer.Inventory.CountOf(11)); // Goblin Ears
        Assert.Equal(2, killer.Inventory.CountOf(32)); // Goblin Eyes
        Assert.Equal(1, killer.Inventory.CountOf(33)); // Goblin Finger
        Assert.Equal(1, killer.Inventory.CountOf(34)); // Goblin Tongue
        Assert.Equal(1, killer.Inventory.CountOf(35)); // Goblin Foot
    }

    [Test("Wolf parts are distinct from goblin parts (paws/tail/organs, no goblin bits).")]
    public static void WolfKill_YieldsWolfParts_NotGoblinParts()
    {
        var world = new World();
        ServerEntity killer = world.SpawnPlayer(new PeerId(1), "Traqueur", raceId: 2, classId: 1);
        world.GrantExperience(killer, 500); // some muscle: a Dire Wolf hits harder than a goblin
        ServerEntity wolf = world.SpawnMonster(2, killer.Position + new Vec2(1f, 0f));

        KillMonster(world, killer, wolf);

        Assert.Equal(1, killer.Inventory.CountOf(10)); // Wolf Pelt
        Assert.Equal(4, killer.Inventory.CountOf(40)); // Wolf Paws
        Assert.Equal(1, killer.Inventory.CountOf(41)); // Wolf Tail
        Assert.Equal(2, killer.Inventory.CountOf(42)); // Wolf Fangs
        Assert.Equal(1, killer.Inventory.CountOf(43)); // Wolf Heart
        Assert.Equal(1, killer.Inventory.CountOf(44)); // Wolf Liver
        Assert.Equal(2, killer.Inventory.CountOf(45)); // Wolf Bones
        Assert.Equal(0, killer.Inventory.CountOf(30)); // no goblin head from a wolf
        Assert.Equal(0, killer.Inventory.CountOf(11)); // no goblin ears either
    }

    [Test("Two goblin kills mean exactly two goblin heads — deterministic collection quests.")]
    public static void TwoKills_TwoHeads_NoRng()
    {
        var world = new World();
        ServerEntity killer = world.SpawnPlayer(new PeerId(1), "Collecteur", raceId: 2, classId: 1);

        KillMonster(world, killer, world.SpawnMonster(1, killer.Position + new Vec2(1f, 0f)));
        KillMonster(world, killer, world.SpawnMonster(1, killer.Position + new Vec2(1f, 0f)));

        Assert.Equal(2, killer.Inventory.CountOf(30)); // exactly 2 heads for 2 kills
        Assert.Equal(4, killer.Inventory.CountOf(11)); // and 2 ears each
    }

    [Test("Parts that don't fit in full bags drop at the kill site as a lootable sack (never lost).")]
    public static void FullBags_OverflowDropsAsGroundSack()
    {
        var world = new World();
        ServerEntity killer = world.SpawnPlayer(new PeerId(1), "Porteur", raceId: 2, classId: 1);

        // Jam the bags: 40 unstackable swords = all inventory slots taken.
        for (int i = 0; i < SimulationConstants.PlayerInventoryCapacity; i++)
        {
            killer.Inventory.TryAdd(1, 1, stackable: false, maxStack: 1);
        }

        ServerEntity goblin = world.SpawnMonster(1, killer.Position + new Vec2(1f, 0f));
        Vec2 killSite = goblin.Position;
        KillMonster(world, killer, goblin);

        Assert.Equal(0, killer.Inventory.CountOf(30)); // no room: the head is NOT in the bags…

        ServerEntity? sack = null;
        foreach (ServerEntity e in world.Entities.Values)
        {
            if (e.Kind == EntityKind.Corpse && e.LootContainer is not null && e.LootContainer.CountOf(30) > 0)
            {
                sack = e;
            }
        }

        Assert.True(sack is not null, "…so a loot sack must exist at the kill site");
        Assert.Equal(1, sack!.LootContainer!.CountOf(30));
        Assert.Equal(2, sack.LootContainer.CountOf(11));
        Assert.True(Vec2.DistanceSquared(sack.Position, killSite) < 1f, "the sack drops where the monster died");
    }
}
