using Aetheria.Server.Items;
using Aetheria.Server.Persistence;
using Aetheria.Server.World;
using Aetheria.Shared.Data;
using Aetheria.Shared.Net;

namespace Aetheria.Tests;

public static class PersistenceTests
{
    [Test("A character round-trips through capture and restore with progression, gold, items, gear, and skills.")]
    public static void Character_CaptureRestore_RoundTrips()
    {
        var world = new World();
        ServerEntity original = world.SpawnPlayer(new PeerId(1), "Veteran", raceId: 2, classId: 1);
        world.GrantStarterKit(original);
        world.GrantExperience(original, 450);
        original.AddSkill(1, 33, 100);
        world.AddItem(original, 10, 7); // wolf pelts
        original.Inventory.AddGold(25); // 50 starter + 25

        CharacterRecord record = CharacterMapper.Capture(original);

        // Restore onto a brand-new spawn in a brand-new world (as after a server restart).
        var world2 = new World();
        ServerEntity revived = world2.SpawnPlayer(new PeerId(9), "Veteran", raceId: 2, classId: 1);
        CharacterMapper.Restore(world2, revived, record);

        Assert.Equal(450, revived.TotalXp);
        Assert.Equal(original.Level, revived.Level);
        Assert.Equal(75, revived.Inventory.Gold);
        Assert.Equal(7, revived.Inventory.CountOf(10));
        Assert.Equal(original.EquippedWeaponId, revived.EquippedWeaponId);
        Assert.Equal(33, revived.GetSkill(1));
        Assert.Equal(revived.EffectiveMaxHealth, revived.Health); // back at full effective health
    }

    [Test("The JSON file store saves atomically and loads back an identical state.")]
    public static void JsonFileStore_SaveLoad_RoundTrips()
    {
        string path = Path.Combine(Path.GetTempPath(), $"aetheria-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonFilePersistenceStore(path);
            var state = new ServerState();
            var account = new AccountRecord { AccountId = "acc1", SecretHash = "HASH", BankGold = 123 };
            account.BankItems.Add(new ItemStackRecord { ItemId = 10, Quantity = 4 });
            account.Characters["hero"] = new CharacterRecord { Name = "Hero", RaceId = 2, ClassId = 1, TotalXp = 300 };
            state.Accounts["acc1"] = account;
            state.Names["hero"] = "acc1";

            store.Save(state);

            ServerState loaded = new JsonFilePersistenceStore(path).Load();
            Assert.Equal(1, loaded.Accounts.Count);
            Assert.Equal("HASH", loaded.Accounts["acc1"].SecretHash);
            Assert.Equal(123, loaded.Accounts["acc1"].BankGold);
            Assert.Equal(4, loaded.Accounts["acc1"].BankItems[0].Quantity);
            Assert.Equal(300, loaded.Accounts["acc1"].Characters["hero"].TotalXp);
            Assert.Equal("acc1", loaded.Names["hero"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test("A missing or corrupt state file loads as a fresh empty state instead of crashing.")]
    public static void JsonFileStore_MissingOrCorrupt_LoadsEmpty()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"aetheria-none-{Guid.NewGuid():N}.json");
        Assert.Equal(0, new JsonFilePersistenceStore(missing).Load().Accounts.Count);

        string corrupt = Path.Combine(Path.GetTempPath(), $"aetheria-bad-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(corrupt, "{not json!!");
            Assert.Equal(0, new JsonFilePersistenceStore(corrupt).Load().Accounts.Count);
        }
        finally
        {
            File.Delete(corrupt);
        }
    }

    [Test("Bank capture/restore round-trips gold and items.")]
    public static void Bank_CaptureRestore_RoundTrips()
    {
        GameData data = GameData.CreateDefault();
        var bank = new Inventory(200);
        bank.AddGold(77);
        bank.TryAdd(10, 12, stackable: true, maxStack: 20);

        var account = new AccountRecord { AccountId = "a" };
        CharacterMapper.CaptureBank(bank, account);

        Inventory restored = CharacterMapper.RestoreBank(account, 200, data);
        Assert.Equal(77, restored.Gold);
        Assert.Equal(12, restored.CountOf(10));
    }
}
