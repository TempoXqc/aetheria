using Aetheria.Server.Items;
using Aetheria.Shared.Data;

namespace Aetheria.Tests;

public static class BankTests
{
    private static readonly GameData Data = GameData.CreateDefault();

    [Test("Depositing gold moves it from the player to the bank.")]
    public static void DepositGold_MovesToBank()
    {
        var player = new Inventory(40);
        var bank = new Inventory(200);
        player.AddGold(100);

        int moved = BankService.DepositGold(player, bank, 70);

        Assert.Equal(70, moved);
        Assert.Equal(30, player.Gold);
        Assert.Equal(70, bank.Gold);
    }

    [Test("Depositing more gold than owned only moves what the player has.")]
    public static void DepositGold_CappedByBalance()
    {
        var player = new Inventory(40);
        var bank = new Inventory(200);
        player.AddGold(20);

        int moved = BankService.DepositGold(player, bank, 1000);

        Assert.Equal(20, moved);
        Assert.Equal(0, player.Gold);
        Assert.Equal(20, bank.Gold);
    }

    [Test("Withdrawing gold moves it from the bank back to the player, capped at the bank balance.")]
    public static void WithdrawGold_MovesToPlayer()
    {
        var player = new Inventory(40);
        var bank = new Inventory(200);
        bank.AddGold(50);

        int moved = BankService.WithdrawGold(player, bank, 1000);

        Assert.Equal(50, moved);
        Assert.Equal(50, player.Gold);
        Assert.Equal(0, bank.Gold);
    }

    [Test("Items can be deposited to and withdrawn from the bank.")]
    public static void Items_DepositAndWithdraw()
    {
        var player = new Inventory(40);
        var bank = new Inventory(200);
        player.TryAdd(10, 5, stackable: true, maxStack: 20); // 5 Wolf Pelts

        Assert.Equal(5, BankService.DepositItem(player, bank, itemId: 10, quantity: 5, Data));
        Assert.Equal(0, player.CountOf(10));
        Assert.Equal(5, bank.CountOf(10));

        Assert.Equal(3, BankService.WithdrawItem(player, bank, itemId: 10, quantity: 3, Data));
        Assert.Equal(3, player.CountOf(10));
        Assert.Equal(2, bank.CountOf(10));
    }
}
