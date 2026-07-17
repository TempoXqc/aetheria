using Aetheria.Shared.Data;

namespace Aetheria.Server.Items;

/// <summary>
/// Moves gold and items between a player's carried inventory and their account bank. The bank is
/// account-scoped and persists across a character's death (see permadeath) — what you deposit here is
/// safe from full-loot corpses. Every operation moves only what actually fits/exists, so a partial
/// transfer never loses items. These are pure functions over two <see cref="Inventory"/> instances,
/// which keeps them trivially testable without any networking.
/// </summary>
public static class BankService
{
    /// <summary>Move up to <paramref name="amount"/> gold from the player into the bank. Returns moved.</summary>
    public static int DepositGold(Inventory player, Inventory bank, int amount)
    {
        int moved = player.RemoveGold(amount);
        bank.AddGold(moved);
        return moved;
    }

    /// <summary>Move up to <paramref name="amount"/> gold from the bank to the player. Returns moved.</summary>
    public static int WithdrawGold(Inventory player, Inventory bank, int amount)
    {
        int moved = bank.RemoveGold(amount);
        player.AddGold(moved);
        return moved;
    }

    /// <summary>Deposit up to <paramref name="quantity"/> of an item into the bank. Returns moved.</summary>
    public static int DepositItem(Inventory player, Inventory bank, byte itemId, int quantity, GameData data)
        => Move(from: player, to: bank, itemId, quantity, data);

    /// <summary>Withdraw up to <paramref name="quantity"/> of an item from the bank. Returns moved.</summary>
    public static int WithdrawItem(Inventory player, Inventory bank, byte itemId, int quantity, GameData data)
        => Move(from: bank, to: player, itemId, quantity, data);

    private static int Move(Inventory from, Inventory to, byte itemId, int quantity, GameData data)
    {
        int available = System.Math.Min(quantity, from.CountOf(itemId));
        if (available <= 0)
        {
            return 0;
        }

        ItemDefinition def = data.GetItem(itemId);
        int leftover = to.TryAdd(itemId, available, def.Stackable, def.MaxStack); // add what fits first
        int moved = available - leftover;
        from.RemoveQuantity(itemId, moved);                                       // then remove exactly that
        return moved;
    }
}
