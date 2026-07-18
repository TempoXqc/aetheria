using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Tests;

/// <summary>
/// Guaranteed skinning loot, WoW-style WINDOWED: every monster kill leaves its body parts in the
/// corpse on the ground — no RNG, nothing auto-looted — so collection quests ("bring 10 goblin
/// heads") are deterministic and looting is a deliberate act. Parts are distinct per creature type.
/// </summary>
public static class BodyPartLootTests
{
    private static void KillMonster(World world, ServerEntity killer, ServerEntity monster)
    {
        for (int i = 0; i < 600 && monster.IsAlive; i++)
        {
            if (killer.IsAbilityReady(killer.BasicAbilityId, world.Tick))
            {
                // Swing as the SERVER auto-attack does (bypasses the manual-cast global cooldown).
                world.TryUseAbility(killer.Id, killer.BasicAbilityId, monster.Id, fromAuto: true);
            }

            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(monster.IsDead, "the monster must die for loot to drop");
    }

    /// <summary>Find the freshest monster corpse that still holds loot.</summary>
    private static ServerEntity FindLootableCorpse(World world)
    {
        ServerEntity? corpse = null;
        foreach (ServerEntity e in world.Entities.Values)
        {
            if (e.Kind == EntityKind.MonsterCorpse && e.LootContainer is not null)
            {
                corpse = e;
            }
        }

        Assert.True(corpse is not null, "a lootable monster corpse must remain on the ground");
        return corpse!;
    }

    private static void KillAndLoot(World world, ServerEntity killer, ServerEntity monster)
    {
        KillMonster(world, killer, monster);
        Assert.True(world.TryLootCorpse(killer.Id, FindLootableCorpse(world).Id), "looting must succeed");
    }

    [Test("NOTHING is auto-looted: the parts wait inside the corpse until the window takes them.")]
    public static void Kill_PutsPartsInTheCorpse_NotTheBags()
    {
        var world = new World();
        ServerEntity killer = world.SpawnPlayer(new PeerId(1), "Fouilleur", raceId: 2, classId: 1);
        ServerEntity goblin = world.SpawnMonster(1, killer.Position + new Vec2(1f, 0f));

        KillMonster(world, killer, goblin);

        // Before opening the corpse: bags empty of parts, gold untouched.
        Assert.Equal(0, killer.Inventory.CountOf(30));
        Assert.Equal(0, killer.Inventory.Gold);

        ServerEntity corpse = FindLootableCorpse(world);
        Assert.Equal(1, corpse.LootContainer!.CountOf(30)); // the head is in the corpse
        Assert.Equal(5, corpse.LootContainer.Gold);         // and so is the goblin's gold

        // The corpse's loot window works through the same validated plumbing as player corpses.
        Assert.True(world.TryLootCorpse(killer.Id, corpse.Id));
        Assert.Equal(1, killer.Inventory.CountOf(30));
        Assert.Equal(5, killer.Inventory.Gold);

        // Emptied: the BODY stays on the ground (WoW-style), just nothing left inside.
        Assert.True(world.Entities.ContainsKey(corpse.Id), "the body stays until its timer");
        Assert.True(corpse.LootContainer is null, "but its loot is spent");
    }

    [Test("Killing a goblin ALWAYS yields every goblin body part, in the defined quantities.")]
    public static void GoblinKill_YieldsAllGoblinParts()
    {
        var world = new World();
        ServerEntity killer = world.SpawnPlayer(new PeerId(1), "Chasseur", raceId: 2, classId: 1);
        ServerEntity goblin = world.SpawnMonster(1, killer.Position + new Vec2(1f, 0f));

        KillAndLoot(world, killer, goblin);

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

        KillAndLoot(world, killer, wolf);

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

    [Test("Two goblin kills, two looted corpses: exactly two heads — deterministic collection quests.")]
    public static void TwoKills_TwoHeads_NoRng()
    {
        var world = new World();
        ServerEntity killer = world.SpawnPlayer(new PeerId(1), "Collecteur", raceId: 2, classId: 1);

        KillAndLoot(world, killer, world.SpawnMonster(1, killer.Position + new Vec2(1f, 0f)));
        KillAndLoot(world, killer, world.SpawnMonster(1, killer.Position + new Vec2(1f, 0f)));

        Assert.Equal(2, killer.Inventory.CountOf(30)); // exactly 2 heads for 2 kills
        Assert.Equal(4, killer.Inventory.CountOf(11)); // and 2 ears each
    }

    [Test("Parts that don't fit in full bags STAY in the corpse — never lost, loot again later.")]
    public static void FullBags_LeftoversStayInTheCorpse()
    {
        var world = new World();
        ServerEntity killer = world.SpawnPlayer(new PeerId(1), "Porteur", raceId: 2, classId: 1);

        // Jam the bags: 40 unstackable swords = all inventory slots taken.
        for (int i = 0; i < SimulationConstants.PlayerInventoryCapacity; i++)
        {
            killer.Inventory.TryAdd(1, 1, stackable: false, maxStack: 1);
        }

        ServerEntity goblin = world.SpawnMonster(1, killer.Position + new Vec2(1f, 0f));
        KillMonster(world, killer, goblin);

        ServerEntity corpse = FindLootableCorpse(world);
        world.TryLootCorpse(killer.Id, corpse.Id); // gold fits; no bag slot for any part

        Assert.Equal(0, killer.Inventory.CountOf(30));       // no room: the head is NOT in the bags…
        Assert.True(corpse.LootContainer is not null, "…so the corpse still holds it");
        Assert.Equal(1, corpse.LootContainer!.CountOf(30));
        Assert.Equal(2, corpse.LootContainer.CountOf(11));
    }
}
