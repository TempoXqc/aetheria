using Aetheria.Server.Items;
using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Tests;

public static class LootTests
{
    [Test("Inventory stacks items, respects capacity, and reports leftovers.")]
    public static void Inventory_StacksAndCapacity()
    {
        var inv = new Inventory(capacity: 2);

        Assert.Equal(0, inv.TryAdd(10, 5, stackable: true, maxStack: 20));
        Assert.Equal(0, inv.TryAdd(10, 3, stackable: true, maxStack: 20)); // merges into the same stack
        Assert.Equal(8, inv.CountOf(10));

        inv.TryAdd(11, 1, stackable: true, maxStack: 20);            // second (last) slot
        Assert.Equal(1, inv.TryAdd(12, 1, stackable: true, maxStack: 20)); // no slot left → 1 leftover
    }

    [Test("Gold can be added and taken in full.")]
    public static void Inventory_Gold()
    {
        var inv = new Inventory(4);
        inv.AddGold(30);
        Assert.Equal(30, inv.Gold);
        Assert.Equal(30, inv.TakeAllGold());
        Assert.Equal(0, inv.Gold);
    }

    [Test("Removing a quantity reduces the stack and reports how much was removed.")]
    public static void Inventory_RemoveQuantity()
    {
        var inv = new Inventory(4);
        inv.TryAdd(10, 10, stackable: true, maxStack: 20);
        Assert.Equal(3, inv.RemoveQuantity(10, 3));
        Assert.Equal(7, inv.CountOf(10));
    }

    [Test("Equipped gear raises effective stats.")]
    public static void Equipment_BonusAppliesToEffectiveStats()
    {
        var world = new World();
        ServerEntity p = world.SpawnPlayer(new PeerId(1), "P", 1, 1); // Warrior attack 12
        int baseAttack = p.EffectiveAttackPower;

        world.GrantStarterKit(p); // equips Rusty Sword (+3 attack)

        Assert.Equal(baseAttack + 3, p.EffectiveAttackPower);
        Assert.True(p.Inventory.Gold >= SimulationConstants.StartingGold);
    }

    [Test("A PvP death leaves a full-loot corpse; looting transfers gold and gear and despawns it.")]
    public static void PvpDeath_CreatesLootableCorpse_AndLootTransfers()
    {
        var world = new World();
        ServerEntity a = world.SpawnPlayer(new PeerId(1), "A", 1, 1);
        ServerEntity b = world.SpawnPlayer(new PeerId(2), "B", 1, 1);
        world.GrantStarterKit(a);
        world.GrantStarterKit(b);
        int aGoldBefore = a.Inventory.Gold;

        // A kills B (B is a player and does not fight back).
        for (int i = 0; i < 400 && b.IsAlive; i++)
        {
            if (a.IsAbilityReady(a.BasicAbilityId, world.Tick))
            {
                world.TryUseAbility(a.Id, a.BasicAbilityId, b.Id);
            }

            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(b.IsDead, "B should have died");

        // A corpse should now exist holding B's gold + gear.
        int corpseId = -1;
        Inventory? loot = null;
        foreach (var kv in world.Entities)
        {
            if (kv.Value.Kind == EntityKind.Corpse)
            {
                corpseId = kv.Key;
                loot = kv.Value.LootContainer;
            }
        }

        Assert.True(corpseId > 0, "a corpse should have been created");
        Assert.True(loot is not null && !loot.IsEmpty);
        Assert.Equal(SimulationConstants.StartingGold, loot!.Gold); // B's starting gold dropped
        Assert.True(loot.CountOf(1) >= 1, "B's Rusty Sword should be on the corpse");
        // B's original gear went to the corpse; hardcore permadeath then re-kits the reborn
        // character with a fresh starter weapon.
        Assert.Equal(1, b.EquippedWeaponId);

        // The corpse is lootable, not attackable.
        Assert.False(world.TryUseAbility(a.Id, a.BasicAbilityId, corpseId));

        // A loots the corpse (A is adjacent to where B fell).
        Assert.True(world.TryLootCorpse(a.Id, corpseId));
        Assert.Equal(aGoldBefore + SimulationConstants.StartingGold, a.Inventory.Gold);
        Assert.True(a.Inventory.CountOf(1) >= 1, "A should now hold B's sword");
        Assert.False(world.Entities.ContainsKey(corpseId), "an emptied corpse despawns");
    }

    [Test("Looting requires being in range of the corpse.")]
    public static void Loot_RequiresProximity()
    {
        var world = new World();
        ServerEntity a = world.SpawnPlayer(new PeerId(1), "A", 1, 1);
        ServerEntity b = world.SpawnPlayer(new PeerId(2), "B", 1, 1);
        world.GrantStarterKit(b);

        for (int i = 0; i < 400 && b.IsAlive; i++)
        {
            if (a.IsAbilityReady(a.BasicAbilityId, world.Tick))
            {
                world.TryUseAbility(a.Id, a.BasicAbilityId, b.Id);
            }

            world.Step(SimulationConstants.TickDelta);
        }

        int corpseId = -1;
        foreach (var kv in world.Entities)
        {
            if (kv.Value.Kind == EntityKind.Corpse)
            {
                corpseId = kv.Key;
            }
        }

        Assert.True(corpseId > 0);
        a.Position = new Vec2(1000, 1000); // move the looter far away
        Assert.False(world.TryLootCorpse(a.Id, corpseId), "cannot loot from across the map");
    }
}
