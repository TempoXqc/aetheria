using Aetheria.Server.Persistence;
using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Combat;
using Aetheria.Shared.Items;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Tests;

/// <summary>The WoW-style multi-slot loadout: ten pieces worn at once, stats stacking,
/// full-loot corpses, wire round-trips, and durable persistence.</summary>
public static class EquipmentTests
{
    /// <summary>One equippable item id per wearable slot (matches the default GameData).</summary>
    private static readonly (byte ItemId, EquipSlot Slot)[] FullKit =
    [
        (13, EquipSlot.Head),      // Leather Cap
        (16, EquipSlot.Shoulders), // Wolf-fur Shoulders
        (18, EquipSlot.Back),      // Traveler's Cloak
        (9,  EquipSlot.Chest),     // Chain Mail
        (19, EquipSlot.Hands),     // Leather Gloves
        (21, EquipSlot.Waist),     // Sturdy Belt
        (17, EquipSlot.Legs),      // Leather Pants
        (15, EquipSlot.Feet),      // Leather Boots
        (4,  EquipSlot.Weapon),    // Steel Sword
        (22, EquipSlot.OffHand),   // Wooden Shield
    ];

    private static ServerEntity WearEverything(World world)
    {
        ServerEntity p = world.SpawnPlayer(new PeerId(1), "Bastion", raceId: 2, classId: 1);
        foreach ((byte itemId, EquipSlot _) in FullKit)
        {
            world.AddItem(p, itemId, 1);
            Assert.True(world.TryEquipItem(p.Id, itemId, EquipSlot.None), "equip item " + itemId);
        }

        return p;
    }

    [Test("All ten WoW slots can be worn at once, each piece landing in ITS slot.")]
    public static void FullLoadout_FillsEverySlot()
    {
        var world = new World();
        ServerEntity p = WearEverything(world);

        foreach ((byte itemId, EquipSlot slot) in FullKit)
        {
            Assert.Equal(itemId, p.GetEquipped(slot));
        }

        Assert.Equal(0, p.Inventory.Stacks.Count); // every piece left the bags
    }

    [Test("Equipment bonuses STACK across all worn pieces (defense, attack, health).")]
    public static void FullLoadout_StacksAllBonuses()
    {
        var world = new World();
        ServerEntity naked = world.SpawnPlayer(new PeerId(2), "Nu", raceId: 2, classId: 1);
        ServerEntity geared = WearEverything(world);

        int expectedDef = 0, expectedAtk = 0, expectedHp = 0;
        foreach ((byte itemId, EquipSlot _) in FullKit)
        {
            var def = world.GameData.GetItem(itemId);
            expectedDef += def.DefenseBonus;
            expectedAtk += def.AttackBonus;
            expectedHp += def.HealthBonus;
        }

        Assert.Equal(naked.EquipmentDefenseBonus + expectedDef, geared.EquipmentDefenseBonus);
        Assert.Equal(naked.EquipmentAttackBonus + expectedAtk, geared.EquipmentAttackBonus);
        Assert.Equal(naked.EquipmentHealthBonus + expectedHp, geared.EquipmentHealthBonus);
    }

    [Test("Equipping a piece into an occupied slot swaps the old piece back into the bags.")]
    public static void Equip_SwapsIntoTheBags()
    {
        var world = new World();
        ServerEntity p = world.SpawnPlayer(new PeerId(1), "Coiffe", raceId: 2, classId: 1);
        world.AddItem(p, 13, 1); // Leather Cap
        world.AddItem(p, 14, 1); // Iron Helm

        Assert.True(world.TryEquipItem(p.Id, 13, EquipSlot.None));
        Assert.True(world.TryEquipItem(p.Id, 14, EquipSlot.None));

        Assert.Equal((byte)14, p.GetEquipped(EquipSlot.Head)); // the helm is on
        Assert.Equal(1, p.Inventory.CountOf(13));              // the cap fell back into the bags
    }

    [Test("Hardcore full loot: on death EVERY worn piece drops to the corpse.")]
    public static void Death_DropsEverySlotToTheCorpse()
    {
        var world = new World();
        ServerEntity victim = WearEverything(world);
        ServerEntity killer = world.SpawnPlayer(new PeerId(9), "Faucheur", raceId: 1, classId: 1);
        world.GrantExperience(killer, 3000); // enough muscle to cut through the armor

        for (int i = 0; i < 2000 && victim.IsAlive; i++)
        {
            if (killer.IsAbilityReady(killer.BasicAbilityId, world.Tick))
            {
                world.TryUseAbility(killer.Id, killer.BasicAbilityId, victim.Id, fromAuto: true);
            }

            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(victim.IsDead, "the victim must fall");

        Aetheria.Server.Items.Inventory? loot = null;
        foreach (var kv in world.Entities)
        {
            if (kv.Value.Kind == EntityKind.Corpse && kv.Value.LootContainer is not null)
            {
                loot = kv.Value.LootContainer;
            }
        }

        Assert.True(loot is not null, "a lootable corpse must remain");
        foreach ((byte itemId, EquipSlot _) in FullKit)
        {
            Assert.True(loot!.CountOf(itemId) >= 1, "corpse holds item " + itemId);
        }
    }

    [Test("InventoryState carries the whole 11-byte loadout across the wire.")]
    public static void InventoryState_RoundTripsTheLoadout()
    {
        var equipment = new byte[EquipSlots.Count];
        foreach ((byte itemId, EquipSlot slot) in FullKit)
        {
            equipment[(int)slot] = itemId;
        }

        var writer = new PacketWriter();
        new InventoryState(equipment, new[] { new ItemStack(20, 2) }).Write(writer);

        var reader = new PacketReader(writer.WrittenSpan);
        Assert.Equal(MessageType.InventoryState, (MessageType)reader.ReadByte());
        InventoryState decoded = InventoryState.Read(ref reader);

        foreach ((byte itemId, EquipSlot slot) in FullKit)
        {
            Assert.Equal(itemId, decoded.Equipment[(int)slot]);
        }

        Assert.Equal((byte)4, decoded.EquippedWeaponId);
        Assert.Equal((byte)9, decoded.EquippedArmorId);
        Assert.Equal(1, decoded.Items.Count);
        Assert.Equal((byte)20, decoded.Items[0].ItemId);
    }

    [Test("EntitySnapshot carries per-slot gear, so everyone SEES the whole outfit.")]
    public static void EntitySnapshot_RoundTripsTheLoadout()
    {
        var equipment = new byte[EquipSlots.Count];
        foreach ((byte itemId, EquipSlot slot) in FullKit)
        {
            equipment[(int)slot] = itemId;
        }

        var entities = new[]
        {
            new EntitySnapshot(7, EntityKind.Player, Faction.Alliance, new Vec2(1f, 2f),
                health: 90, maxHealth: 90, resource: 10, maxResource: 100, facingRadians: 0f,
                level: 2, name: "Mode", raceId: 1, classId: 1, equipment: equipment),
        };

        var writer = new PacketWriter();
        new SnapshotMessage(tick: 1, entities).Write(writer);

        var reader = new PacketReader(writer.WrittenSpan);
        Assert.Equal(MessageType.Snapshot, (MessageType)reader.ReadByte());
        SnapshotMessage decoded = SnapshotMessage.Read(ref reader);

        foreach ((byte itemId, EquipSlot slot) in FullKit)
        {
            Assert.Equal(itemId, decoded.Entities[0].EquippedIn(slot));
        }

        // An entity WITHOUT gear (monsters) reads back as bare, not garbage.
        Assert.Equal((byte)0, new EntitySnapshot(8, EntityKind.Monster, Faction.Neutral, Vec2.Zero,
            1, 1, 0, 0, 0f, 1, "").EquippedIn(EquipSlot.Head));
    }

    [Test("The full loadout survives a save/restore cycle (and legacy 2-slot saves still work).")]
    public static void Persistence_RestoresTheLoadout()
    {
        var world = new World();
        ServerEntity original = WearEverything(world);
        CharacterRecord record = CharacterMapper.Capture(original);

        // A fresh spawn restored from the record wears everything again.
        var world2 = new World();
        ServerEntity restored = world2.SpawnPlayer(new PeerId(5), "Revenant", raceId: 2, classId: 1);
        CharacterMapper.Restore(world2, restored, record);

        foreach ((byte itemId, EquipSlot slot) in FullKit)
        {
            Assert.Equal(itemId, restored.GetEquipped(slot));
        }

        // Legacy pre-0.24 record: only the two old fields set — still restores weapon + chest.
        var legacy = new CharacterRecord { EquippedWeaponId = 1, EquippedArmorId = 3 };
        ServerEntity old = world2.SpawnPlayer(new PeerId(6), "Ancien", raceId: 2, classId: 1);
        CharacterMapper.Restore(world2, old, legacy);
        Assert.Equal((byte)1, old.GetEquipped(EquipSlot.Weapon));
        Assert.Equal((byte)3, old.GetEquipped(EquipSlot.Chest));
    }

    [Test("Equip-swap happens IN PLACE: the replaced piece lands in the clicked bag slot.")]
    public static void EquipSwap_ReplacedPieceKeepsTheBagSlot()
    {
        var world = new World();
        ServerEntity p = world.SpawnPlayer(new PeerId(7), "Rangée", raceId: 1, classId: 1);

        // Bags: [Rusty Sword, Wolf Pelt, Steel Sword]; wear the rusty one first.
        world.AddItem(p, 1, 1);
        world.AddItem(p, 10, 1);
        world.AddItem(p, 4, 1);
        Assert.True(world.TryEquipItem(p.Id, 1, EquipSlot.None));

        // Now bags are [Pelt, Steel]; equipping the Steel Sword (index 1) must put the
        // Rusty Sword back at INDEX 1 — not at the end… which is the same here, so use a
        // richer bag: add two more pelts to make the tail obvious.
        world.AddItem(p, 30, 1); // Goblin Head after the sword
        Assert.True(world.TryEquipItem(p.Id, 4, EquipSlot.None));

        Assert.Equal((byte)4, p.GetEquipped(EquipSlot.Weapon));
        Assert.Equal((byte)10, p.Inventory.Stacks[0].ItemId); // pelt untouched
        Assert.Equal((byte)1, p.Inventory.Stacks[1].ItemId);  // rusty sword took the steel sword's slot
        Assert.Equal((byte)30, p.Inventory.Stacks[2].ItemId); // tail untouched
    }

    [Test("MoveSlot reorders the bag: swap two cells, move onto an empty cell = to the end.")]
    public static void MoveSlot_ReordersTheBag()
    {
        var world = new World();
        ServerEntity p = world.SpawnPlayer(new PeerId(8), "Tidy", raceId: 1, classId: 1);
        world.AddItem(p, 1, 1);
        world.AddItem(p, 10, 1);
        world.AddItem(p, 20, 1);

        Assert.True(world.TryMoveItem(p.Id, 0, 2), "swap first and third");
        Assert.Equal((byte)20, p.Inventory.Stacks[0].ItemId);
        Assert.Equal((byte)1, p.Inventory.Stacks[2].ItemId);

        Assert.True(world.TryMoveItem(p.Id, 0, 7), "onto an empty cell: goes to the end");
        Assert.Equal((byte)10, p.Inventory.Stacks[0].ItemId);
        Assert.Equal((byte)20, p.Inventory.Stacks[2].ItemId);

        Assert.False(world.TryMoveItem(p.Id, 5, 0), "out-of-range source refused");
        Assert.False(world.TryMoveItem(p.Id, 1, 1), "same-cell move refused");
    }
}
