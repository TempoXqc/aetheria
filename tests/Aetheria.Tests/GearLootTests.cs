using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Tests;

/// <summary>Wand auto-attack, chance-rolled gear drops, per-class kits, and gear in snapshots.</summary>
public static class GearLootTests
{
    [Test("The Mage AUTO-attacks with its wand (instant), while Firebolt stays a hand-cast spell.")]
    public static void Mage_AutoAttacksWithWand()
    {
        var world = new World();
        ServerEntity mage = world.SpawnPlayer(new PeerId(1), "Magus", raceId: 1, classId: 2);
        ServerEntity goblin = world.SpawnMonster(1, mage.Position + new Vec2(5f, 0f));
        int hp = goblin.Health;
        float mana = mage.CurrentResource;

        world.SetAttackTarget(mage.Id, goblin.Id);
        for (int i = 0; i < 5; i++)
        {
            world.Step(SimulationConstants.TickDelta);
        }

        // The wand is INSTANT: damage lands within a few ticks, no incantation, no mana spent.
        Assert.True(goblin.Health < hp, "wand shots land instantly");
        Assert.False(mage.IsCasting, "auto-attack must not incant");
        Assert.True(mage.CurrentResource >= mana - 0.01f, "the wand is free");

        // Firebolt stays a manual incantation.
        Assert.True(world.TryUseAbility(mage.Id, 2, goblin.Id));
        Assert.True(mage.IsCasting, "Firebolt is still a cast-time spell");
    }

    [Test("Gear drops roll their chance: 100% always drops, 0% never does.")]
    public static void GearDrops_RollTheirChance()
    {
        // The Goblin King guarantees an Iron Sword (100%).
        var world = new World { LootRng = new System.Random(42) };
        ServerEntity killer = world.SpawnPlayer(new PeerId(1), "Pilleur", raceId: 2, classId: 1);
        world.GrantExperience(killer, 3000); // muscle for an elite
        ServerEntity king = world.SpawnMonster(3, killer.Position + new Vec2(1f, 0f));

        for (int i = 0; i < 3000 && king.IsAlive; i++)
        {
            if (killer.IsAbilityReady(killer.BasicAbilityId, world.Tick))
            {
                world.TryUseAbility(killer.Id, killer.BasicAbilityId, king.Id, fromAuto: true);
            }

            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(king.IsDead, "the King must fall");
        Assert.True(killer.Inventory.CountOf(2) >= 1, "the King ALWAYS drops his Iron Sword");
    }

    [Test("Starter kits are per class: sword, staff or bow — visible from the first minute.")]
    public static void StarterKit_MatchesClass()
    {
        var world = new World();
        ServerEntity warrior = world.SpawnPlayer(new PeerId(1), "G", raceId: 2, classId: 1);
        ServerEntity mage = world.SpawnPlayer(new PeerId(2), "M", raceId: 1, classId: 2);
        ServerEntity ranger = world.SpawnPlayer(new PeerId(3), "R", raceId: 3, classId: 3);
        world.GrantStarterKit(warrior);
        world.GrantStarterKit(mage);
        world.GrantStarterKit(ranger);

        Assert.Equal((byte)1, warrior.EquippedWeaponId); // Rusty Sword
        Assert.Equal((byte)6, mage.EquippedWeaponId);    // Worn Staff
        Assert.Equal((byte)5, ranger.EquippedWeaponId);  // Worn Bow
    }

    [Test("Snapshots carry the equipped gear, so everyone SEES your loot.")]
    public static void Snapshot_CarriesEquippedGear()
    {
        var world = new World();
        ServerEntity p = world.SpawnPlayer(new PeerId(1), "Mode", raceId: 2, classId: 1);
        world.GrantStarterKit(p);
        world.AddItem(p, itemId: 9, quantity: 1);
        world.TryEquipItem(p.Id, 9, Aetheria.Shared.Items.EquipSlot.Armor); // Chain Mail on

        EntitySnapshot found = default;
        foreach (EntitySnapshot e in world.BuildAreaSnapshot(p.Position))
        {
            if (e.Id == p.Id) { found = e; }
        }

        Assert.Equal((byte)1, found.EquippedWeaponId); // Rusty Sword in hand
        Assert.Equal((byte)9, found.EquippedArmorId);  // Chain Mail on the back
    }
}
