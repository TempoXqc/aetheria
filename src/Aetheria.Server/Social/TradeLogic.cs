using Aetheria.Server.Items;
using Aetheria.Shared.Data;
using Aetheria.Shared.Items;

namespace Aetheria.Server.Social;

/// <summary>One side's offer in a trade: gold plus item stacks.</summary>
public sealed class TradeOffer
{
    public int Gold { get; set; }
    public List<ItemStack> Items { get; } = new();

    public void Set(int gold, IReadOnlyList<ItemStack> items)
    {
        Gold = System.Math.Max(0, gold);
        Items.Clear();
        foreach (ItemStack stack in items)
        {
            if (stack.Quantity > 0)
            {
                Items.Add(stack);
            }
        }
    }
}

/// <summary>
/// The trade swap, kept pure (two inventories, two offers) so it is trivially unit-testable.
/// Both offers are validated FIRST (owner really has the gold and items), then the exchange is
/// applied. Items that would not fit the receiver's bag bounce back to the giver, so nothing is
/// ever destroyed by a full bag.
/// </summary>
public static class TradeLogic
{
    /// <summary>Does this inventory actually contain everything the offer promises?</summary>
    public static bool Validate(Inventory owner, TradeOffer offer, out string error)
    {
        error = string.Empty;

        if (owner.Gold < offer.Gold)
        {
            error = "Not enough gold for the offered amount.";
            return false;
        }

        foreach (ItemStack stack in offer.Items)
        {
            if (owner.CountOf(stack.ItemId) < stack.Quantity)
            {
                error = "An offered item is no longer in the bag.";
                return false;
            }
        }

        return true;
    }

    /// <summary>Validate both sides then swap. Returns false (nothing moved) if either side lied.</summary>
    public static bool TryExecute(Inventory a, TradeOffer offerA, Inventory b, TradeOffer offerB,
        GameData data, out string error)
    {
        if (!Validate(a, offerA, out error) || !Validate(b, offerB, out error))
        {
            return false;
        }

        // Gold both ways.
        a.RemoveGold(offerA.Gold);
        b.AddGold(offerA.Gold);
        b.RemoveGold(offerB.Gold);
        a.AddGold(offerB.Gold);

        MoveItems(a, b, offerA.Items, data);
        MoveItems(b, a, offerB.Items, data);
        return true;
    }

    private static void MoveItems(Inventory from, Inventory to, List<ItemStack> items, GameData data)
    {
        foreach (ItemStack stack in items)
        {
            int removed = from.RemoveQuantity(stack.ItemId, stack.Quantity);
            if (removed <= 0)
            {
                continue;
            }

            ItemDefinition def = data.GetItem(stack.ItemId);
            int leftover = to.TryAdd(stack.ItemId, removed, def.Stackable, def.MaxStack);
            if (leftover > 0)
            {
                from.TryAdd(stack.ItemId, leftover, def.Stackable, def.MaxStack); // bag full: bounce back
            }
        }
    }
}
