using Aetheria.Server.Items;
using Aetheria.Server.Social;
using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Data;
using Aetheria.Shared.Items;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Tests;

public static class SocialTests
{
    // ------------------------------------------------------------------ Duels

    [Test("A duel lets same-faction players fight; without one they cannot.")]
    public static void Duel_BypassesFactionRule()
    {
        var world = new World();
        ServerEntity a = world.SpawnPlayer(new PeerId(1), "A", 1, 1); // Alliance
        ServerEntity b = world.SpawnPlayer(new PeerId(2), "B", 4, 1); // Alliance

        Assert.False(world.TryUseAbility(a.Id, a.BasicAbilityId, b.Id)); // same camp: blocked

        Assert.True(world.StartDuel(a.Id, b.Id, toDeath: false));
        Assert.True(world.TryUseAbility(a.Id, a.BasicAbilityId, b.Id)); // dueling: allowed
        Assert.True(b.Health < b.EffectiveMaxHealth);
    }

    [Test("A FRIENDLY duel ends at 1 hp — the loser survives and a winner is recorded.")]
    public static void FriendlyDuel_LoserSurvivesAtOneHp()
    {
        var world = new World();
        ServerEntity a = world.SpawnPlayer(new PeerId(1), "A", 1, 1);
        ServerEntity b = world.SpawnPlayer(new PeerId(2), "B", 4, 1);
        world.StartDuel(a.Id, b.Id, toDeath: false);

        for (int i = 0; i < 600 && world.IsDueling(a.Id); i++)
        {
            if (a.IsAbilityReady(a.BasicAbilityId, world.Tick))
            {
                world.TryUseAbility(a.Id, a.BasicAbilityId, b.Id);
            }

            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(b.IsAlive, "friendly duel must never kill");
        Assert.Equal(1, b.Health);
        Assert.False(world.IsDueling(a.Id), "duel should have ended");

        var endings = world.DrainDuelEndings();
        Assert.Equal(1, endings.Count);
        Assert.Equal(a.Id, endings[0].winnerId);
        Assert.False(endings[0].toDeath);
    }

    [Test("A duel TO THE DEATH kills for real: corpse, permadeath, winner recorded.")]
    public static void DeathDuel_KillsForReal()
    {
        var world = new World();
        ServerEntity a = world.SpawnPlayer(new PeerId(1), "A", 1, 1);
        ServerEntity b = world.SpawnPlayer(new PeerId(2), "B", 4, 1);
        world.GrantStarterKit(b);
        world.StartDuel(a.Id, b.Id, toDeath: true);

        for (int i = 0; i < 600 && b.IsAlive; i++)
        {
            if (a.IsAbilityReady(a.BasicAbilityId, world.Tick))
            {
                world.TryUseAbility(a.Id, a.BasicAbilityId, b.Id);
            }

            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(b.IsDead, "death duel must kill");
        Assert.Equal(0, b.TotalXp); // permadeath applied

        bool corpseExists = false;
        foreach (var kv in world.Entities)
        {
            if (kv.Value.Kind == EntityKind.Corpse) { corpseExists = true; }
        }

        Assert.True(corpseExists, "the loser's corpse should be lootable");

        var endings = world.DrainDuelEndings();
        Assert.Equal(1, endings.Count);
        Assert.Equal(a.Id, endings[0].winnerId);
        Assert.True(endings[0].toDeath);
    }

    [Test("Disconnecting mid-duel forfeits: the opponent wins.")]
    public static void Duel_ForfeitOnDisconnect()
    {
        var world = new World();
        ServerEntity a = world.SpawnPlayer(new PeerId(1), "A", 1, 1);
        ServerEntity b = world.SpawnPlayer(new PeerId(2), "B", 4, 1);
        world.StartDuel(a.Id, b.Id, toDeath: false);

        world.ForfeitDuel(b.Id); // b flees

        var endings = world.DrainDuelEndings();
        Assert.Equal(1, endings.Count);
        Assert.Equal(a.Id, endings[0].winnerId);
    }

    // ------------------------------------------------------------------ Trade

    [Test("A valid trade swaps gold and items atomically.")]
    public static void Trade_SwapsGoldAndItems()
    {
        GameData data = GameData.CreateDefault();
        var a = new Inventory(40);
        var b = new Inventory(40);
        a.AddGold(100);
        a.TryAdd(10, 5, true, 20);   // 5 pelts
        b.AddGold(20);
        b.TryAdd(2, 1, false, 1);    // an Iron Sword

        var offerA = new TradeOffer();
        offerA.Set(30, new[] { new ItemStack(10, 5) });
        var offerB = new TradeOffer();
        offerB.Set(0, new[] { new ItemStack(2, 1) });

        Assert.True(TradeLogic.TryExecute(a, offerA, b, offerB, data, out _));

        Assert.Equal(70, a.Gold);       // 100 - 30
        Assert.Equal(50, b.Gold);       // 20 + 30
        Assert.Equal(0, a.CountOf(10));
        Assert.Equal(5, b.CountOf(10));
        Assert.Equal(1, a.CountOf(2));  // sword crossed over
        Assert.Equal(0, b.CountOf(2));
    }

    [Test("A trade offering more than you own is refused and nothing moves.")]
    public static void Trade_RefusesOverdraw()
    {
        GameData data = GameData.CreateDefault();
        var a = new Inventory(40);
        var b = new Inventory(40);
        a.AddGold(10);

        var offerA = new TradeOffer();
        offerA.Set(500, System.Array.Empty<ItemStack>()); // liar
        var offerB = new TradeOffer();
        offerB.Set(0, System.Array.Empty<ItemStack>());

        Assert.False(TradeLogic.TryExecute(a, offerA, b, offerB, data, out string error));
        Assert.True(error.Length > 0);
        Assert.Equal(10, a.Gold);
        Assert.Equal(0, b.Gold);
    }

    // ------------------------------------------------------------- Drop item

    [Test("Dropping an item creates a lootable ground sack; emptying it makes it vanish.")]
    public static void DropItem_CreatesLootableSack()
    {
        var world = new World();
        ServerEntity p = world.SpawnPlayer(new PeerId(1), "P", 1, 1);
        world.AddItem(p, 10, 6); // 6 pelts

        Assert.True(world.TryDropItem(p.Id, 10, 4));
        Assert.Equal(2, p.Inventory.CountOf(10)); // 4 dropped, 2 kept

        int sackId = -1;
        foreach (var kv in world.Entities)
        {
            if (kv.Value.Kind == EntityKind.Corpse) { sackId = kv.Key; }
        }

        Assert.True(sackId > 0, "a ground sack should exist");

        // Pick it back up through the normal corpse-loot path.
        Assert.True(world.TryLootCorpse(p.Id, sackId));
        Assert.Equal(6, p.Inventory.CountOf(10));
        Assert.False(world.Entities.ContainsKey(sackId), "emptied sack despawns");
    }

    [Test("You cannot drop what you do not have.")]
    public static void DropItem_RequiresOwnership()
    {
        var world = new World();
        ServerEntity p = world.SpawnPlayer(new PeerId(1), "P", 1, 1);
        Assert.False(world.TryDropItem(p.Id, 10, 3));
    }
}
