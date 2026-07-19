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

        killer.FacingRadians = System.MathF.Atan2(victim.Position.Y - killer.Position.Y,
            victim.Position.X - killer.Position.X); // un tueur regarde sa proie

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

        // Bags: [Rusty Sword, Wolf Pelt, Steel Sword]; wear the rusty one first —
        // its cell becomes a HOLE that the next pickup (Goblin Head) fills.
        world.AddItem(p, 1, 1);
        world.AddItem(p, 10, 1);
        world.AddItem(p, 4, 1);
        Assert.True(world.TryEquipItem(p.Id, 1, EquipSlot.None));
        world.AddItem(p, 30, 1); // fills the hole left at cell 0
        Assert.Equal((byte)30, p.Inventory.Stacks[0].ItemId);

        // Equipping the Steel Sword (cell 2) must put the Rusty Sword back at CELL 2.
        Assert.True(world.TryEquipItem(p.Id, 4, EquipSlot.None));

        Assert.Equal((byte)4, p.GetEquipped(EquipSlot.Weapon));
        Assert.Equal((byte)30, p.Inventory.Stacks[0].ItemId); // head untouched
        Assert.Equal((byte)10, p.Inventory.Stacks[1].ItemId); // pelt untouched
        Assert.Equal((byte)1, p.Inventory.Stacks[2].ItemId);  // rusty sword took the steel sword's cell
    }

    [Test("MoveSlot: swap two cells, and a FAR empty cell really keeps the item there (holes pad in).")]
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

        // Drop the first stack onto FAR cell 7: it must sit exactly there, holes in between.
        Assert.True(world.TryMoveItem(p.Id, 0, 7));
        Assert.Equal((byte)0, p.Inventory.Stacks[0].ItemId);  // hole where it left
        Assert.Equal((byte)10, p.Inventory.Stacks[1].ItemId); // untouched
        Assert.Equal((byte)20, p.Inventory.Stacks[7].ItemId); // parked where the player chose

        // New loot fills the first hole instead of stacking at the end.
        world.AddItem(p, 30, 1);
        Assert.Equal((byte)30, p.Inventory.Stacks[0].ItemId);

        Assert.False(world.TryMoveItem(p.Id, 3, 0), "dragging a hole is refused");
        Assert.False(world.TryMoveItem(p.Id, 1, 1), "same-cell move refused");
        Assert.False(world.TryMoveItem(p.Id, 1, 200), "beyond the bag refused");
    }

    [Test("Unequip can target a CHOSEN bag cell (dragging a piece off the character sheet).")]
    public static void UnequipTo_LandsInTheChosenCell()
    {
        var world = new World();
        ServerEntity p = world.SpawnPlayer(new PeerId(11), "Vestiaire", raceId: 1, classId: 1);
        world.AddItem(p, 1, 1);
        Assert.True(world.TryEquipItem(p.Id, 1, EquipSlot.None));

        Assert.True(world.TryEquipItem(p.Id, 0, EquipSlot.Weapon, bagIndex: 6));
        Assert.Equal((byte)0, p.GetEquipped(EquipSlot.Weapon));
        Assert.Equal((byte)1, p.Inventory.Stacks[6].ItemId); // parked exactly where dropped
    }

    [Test("Merchant: buying costs full value near the NPC; selling pays back a quarter.")]
    public static void Vendor_BuysAndSells()
    {
        var world = new World();
        world.SpawnNpc("Mira la Marchande", new Vec2(2f, 2f), npcType: 4);
        ServerEntity p = world.SpawnPlayer(new PeerId(12), "Client", raceId: 1, classId: 1);
        world.Teleport(p, new Vec2(2.5f, 2f));
        p.Inventory.AddGold(100);
        int gold = p.Inventory.Gold;

        // Buy a Minor Healing Potion (id 20, value 5).
        Assert.True(world.TryVendorAction(p.Id, sell: false, itemId: 20, quantity: 1));
        Assert.Equal(gold - 5, p.Inventory.Gold);
        Assert.Equal(1, p.Inventory.CountOf(20));

        // Sell it back: a quarter of its value (5/4 → 1).
        Assert.True(world.TryVendorAction(p.Id, sell: true, itemId: 20, quantity: 1));
        Assert.Equal(gold - 5 + 1, p.Inventory.Gold);
        Assert.Equal(0, p.Inventory.CountOf(20));

        // Sell a WHOLE STACK in one action (the client's right-click at a merchant).
        p.Inventory.TryAdd(20, 6, stackable: true, maxStack: 20);
        int before = p.Inventory.Gold;
        Assert.True(world.TryVendorAction(p.Id, sell: true, itemId: 20, quantity: 6));
        Assert.Equal(before + 6, p.Inventory.Gold);   // 6 × (5/4 → 1)
        Assert.Equal(0, p.Inventory.CountOf(20));

        // Selling MORE than owned is refused outright.
        p.Inventory.TryAdd(20, 2, stackable: true, maxStack: 20);
        Assert.False(world.TryVendorAction(p.Id, sell: true, itemId: 20, quantity: 3), "only 2 owned");
        Assert.Equal(2, p.Inventory.CountOf(20));

        // Not in stock / too far / too poor: refused.
        Assert.False(world.TryVendorAction(p.Id, sell: false, itemId: 14, quantity: 1), "Iron Helm not stocked");
        world.Teleport(p, new Vec2(50f, 50f));
        Assert.False(world.TryVendorAction(p.Id, sell: false, itemId: 20, quantity: 1), "too far away");
    }

    [Test("Druid shapeshift: forms swap the basic attack and the stat profile; others are refused.")]
    public static void Druid_ShapeShiftsThroughForms()
    {
        var world = new World();
        ServerEntity druid = world.SpawnPlayer(new PeerId(20), "Sylvara", raceId: 1, classId: 4);
        int baseAttack = druid.EffectiveAttackPower;
        int baseDefense = druid.EffectiveDefense;
        int baseHealth = druid.EffectiveMaxHealth;
        Assert.Equal((byte)30, druid.BasicAbilityId); // humanoid casts Wrath

        Assert.True(world.TryShapeShift(druid.Id, 1)); // BEAR
        Assert.Equal((byte)1, druid.FormId);
        Assert.Equal((byte)31, druid.BasicAbilityId); // Maul
        Assert.True(druid.EffectiveDefense > baseDefense, "bear tanks harder");
        Assert.True(druid.EffectiveMaxHealth > baseHealth, "bear has a bigger pool");

        Assert.True(world.TryShapeShift(druid.Id, 2)); // OWL
        Assert.Equal((byte)30, druid.BasicAbilityId); // ranged Wrath again
        Assert.True(druid.EffectiveAttackPower > baseAttack, "owl empowers spells");
        Assert.True(druid.Health <= druid.EffectiveMaxHealth, "health clamped out of bear form");

        Assert.True(world.TryShapeShift(druid.Id, 3)); // CAT
        Assert.Equal((byte)32, druid.BasicAbilityId); // Shred
        Assert.True(world.TryShapeShift(druid.Id, 0)); // back to humanoid
        Assert.Equal((byte)0, druid.FormId);
        Assert.Equal(baseDefense, druid.EffectiveDefense);

        // Only druids shapeshift; garbage forms are refused.
        ServerEntity warrior = world.SpawnPlayer(new PeerId(21), "Grunt", raceId: 1, classId: 1);
        Assert.False(world.TryShapeShift(warrior.Id, 1), "warriors stay warriors");
        Assert.False(world.TryShapeShift(druid.Id, 9), "unknown form refused");
    }

    [Test("Log back in where you logged out: position and bag layout survive save/restore.")]
    public static void PositionAndLayout_SurviveRestore()
    {
        var world = new World();
        ServerEntity p = world.SpawnPlayer(new PeerId(9), "Voyageur", raceId: 1, classId: 1);
        world.AddItem(p, 1, 1);
        world.AddItem(p, 10, 1);
        Assert.True(world.TryMoveItem(p.Id, 0, 5)); // park the sword far away
        world.Teleport(p, new Aetheria.Shared.Math.Vec2(42.5f, -17.25f));

        CharacterRecord record = CharacterMapper.Capture(p);
        var world2 = new World();
        ServerEntity back = world2.SpawnPlayer(new PeerId(10), "Voyageur", raceId: 1, classId: 1);
        CharacterMapper.Restore(world2, back, record);

        Assert.True(System.Math.Abs(back.Position.X - 42.5f) < 0.01f, "X restored");
        Assert.True(System.Math.Abs(back.Position.Y - -17.25f) < 0.01f, "Y restored");
        Assert.Equal((byte)10, back.Inventory.Stacks[1].ItemId);
        Assert.Equal((byte)1, back.Inventory.Stacks[5].ItemId); // the parked cell came back
    }
}
