using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Tests;

public static class InteractiveLootTests
{
    private static (World world, ServerEntity looter, int corpseId) KillAndFindCorpse()
    {
        var world = new World();
        ServerEntity killer = world.SpawnPlayer(new PeerId(1), "Reaper", 1, 1);
        ServerEntity victim = world.SpawnPlayer(new PeerId(2), "Prey", raceId: 2, classId: 1);
        world.GrantStarterKit(victim); // 50 gold + Rusty Sword + 2 potions

        for (int i = 0; i < 400 && victim.IsAlive; i++)
        {
            if (killer.IsAbilityReady(killer.BasicAbilityId, world.Tick))
            {
                world.TryUseAbility(killer.Id, killer.BasicAbilityId, victim.Id);
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

        return (world, killer, corpseId);
    }

    [Test("Opening a corpse in range reveals its gold and items without taking anything.")]
    public static void OpenCorpse_RevealsContents()
    {
        (World world, ServerEntity looter, int corpseId) = KillAndFindCorpse();

        Assert.True(world.TryOpenCorpse(looter.Id, corpseId, out int gold, out var items));
        Assert.Equal(SimulationConstants.StartingGold, gold);
        Assert.True(items.Count >= 2, "sword + potions expected");

        // Nothing was taken by looking.
        Assert.True(world.TryOpenCorpse(looter.Id, corpseId, out int goldAgain, out _));
        Assert.Equal(gold, goldAgain);
    }

    [Test("Opening a corpse from across the map is refused.")]
    public static void OpenCorpse_OutOfRange_Fails()
    {
        (World world, ServerEntity looter, int corpseId) = KillAndFindCorpse();
        looter.Position = new Vec2(500, 500);

        Assert.False(world.TryOpenCorpse(looter.Id, corpseId, out _, out _));
    }

    [Test("LootItem takes one item type; LootItem(0) takes the gold; the emptied corpse despawns.")]
    public static void LootItem_TakesPiecemeal_ThenCorpseDespawns()
    {
        (World world, ServerEntity looter, int corpseId) = KillAndFindCorpse();
        int myGoldBefore = looter.Inventory.Gold;

        // Take the sword (item 1) only.
        Assert.True(world.TryLootItem(looter.Id, corpseId, itemId: 1));
        Assert.True(looter.Inventory.CountOf(1) >= 1);
        Assert.True(world.Entities.ContainsKey(corpseId), "corpse remains while loot is left");

        // Take the gold.
        Assert.True(world.TryLootItem(looter.Id, corpseId, itemId: 0));
        Assert.Equal(myGoldBefore + SimulationConstants.StartingGold, looter.Inventory.Gold);

        // Take the potions (item 20) — the last thing — and the corpse must vanish.
        Assert.True(world.TryLootItem(looter.Id, corpseId, itemId: 20));
        Assert.False(world.Entities.ContainsKey(corpseId), "emptied corpse despawns");
    }

    [Test("Looting an item the corpse does not hold takes nothing.")]
    public static void LootItem_MissingItem_TakesNothing()
    {
        (World world, ServerEntity looter, int corpseId) = KillAndFindCorpse();
        Assert.False(world.TryLootItem(looter.Id, corpseId, itemId: 99));
    }

    [Test("OpenCorpse and LootItem messages round-trip; CorpseContents carries gold and stacks.")]
    public static void Protocol_RoundTrips()
    {
        var w1 = new PacketWriter();
        new OpenCorpse(42).Write(w1);
        var r1 = new PacketReader(w1.WrittenSpan);
        Assert.Equal(MessageType.OpenCorpse, (MessageType)r1.ReadByte());
        Assert.Equal(42, OpenCorpse.Read(ref r1).CorpseEntityId);

        var w2 = new PacketWriter();
        new LootItem(42, 7).Write(w2);
        var r2 = new PacketReader(w2.WrittenSpan);
        Assert.Equal(MessageType.LootItem, (MessageType)r2.ReadByte());
        LootItem li = LootItem.Read(ref r2);
        Assert.Equal(42, li.CorpseEntityId);
        Assert.Equal((byte)7, li.ItemId);

        var w3 = new PacketWriter();
        new CorpseContentsMessage(42, 30, new[]
        {
            new Aetheria.Shared.Items.ItemStack(1, 1),
            new Aetheria.Shared.Items.ItemStack(20, 2),
        }).Write(w3);
        var r3 = new PacketReader(w3.WrittenSpan);
        Assert.Equal(MessageType.CorpseContents, (MessageType)r3.ReadByte());
        CorpseContentsMessage cc = CorpseContentsMessage.Read(ref r3);
        Assert.Equal(42, cc.CorpseEntityId);
        Assert.Equal(30, cc.Gold);
        Assert.Equal(2, cc.Items.Count);
        Assert.Equal(2, cc.Items[1].Quantity);
    }
}
