using Aetheria.Server.Items;
using Aetheria.Server.World;
using Aetheria.Shared.Items;

namespace Aetheria.Server.Persistence;

/// <summary>
/// Translates between the live authoritative entity and its durable record. Pure data mapping —
/// no networking, no side effects beyond the passed objects — so it is trivially unit-testable.
/// </summary>
public static class CharacterMapper
{
    /// <summary>Snapshot a live player entity into a durable record.</summary>
    public static CharacterRecord Capture(ServerEntity entity)
    {
        var record = new CharacterRecord
        {
            Name = entity.Name,
            RaceId = entity.RaceId,
            ClassId = entity.ClassId,
            Gender = (byte)entity.Gender,
            SkinTone = entity.Appearance.SkinTone,
            Face = entity.Appearance.Face,
            HairStyle = entity.Appearance.HairStyle,
            HairColor = entity.Appearance.HairColor,
            BeardStyle = entity.Appearance.BeardStyle,
            BeardColor = entity.Appearance.BeardColor,
            TotalXp = entity.TotalXp,
            Gold = entity.Inventory.Gold,
            HasPosition = true,
            PosX = entity.Position.X,
            PosY = entity.Position.Y,
            HomeX = entity.HomeX,
            HomeY = entity.HomeY,
            ActiveQuestId = entity.ActiveQuestId,
            QuestKills = entity.QuestKills,
            QuestCompletedUpTo = entity.QuestCompletedUpTo,
            EquippedWeaponId = entity.EquippedWeaponId,
            EquippedArmorId = entity.EquippedArmorId,
            Honor = entity.HonorPoints,
            RepAlliance = entity.RepAlliance,
            RepHorde = entity.RepHorde,
            Bandit = entity.IsBandit,
        };

        for (int i = 1; i < EquipSlots.Count; i++)
        {
            if (entity.Equipment[i] != 0)
            {
                record.Equipment[i.ToString()] = entity.Equipment[i];
            }
        }

        foreach (ItemStack stack in entity.Inventory.Stacks)
        {
            record.Items.Add(new ItemStackRecord { ItemId = stack.ItemId, Quantity = stack.Quantity });
        }

        for (byte line = 1; line <= 8; line++) // small fixed range of skill lines today
        {
            int skill = entity.GetSkill(line);
            if (skill > 0)
            {
                record.Skills[line.ToString()] = skill;
            }
        }

        return record;
    }

    /// <summary>Apply a durable record onto a freshly spawned player entity (instead of a starter kit).</summary>
    public static void Restore(World.World world, ServerEntity entity, CharacterRecord record)
    {
        world.GrantExperience(entity, record.TotalXp);
        entity.Inventory.AddGold(record.Gold);
        entity.ActiveQuestId = record.ActiveQuestId;
        entity.QuestKills = record.QuestKills;
        entity.QuestCompletedUpTo = record.QuestCompletedUpTo;

        // EXACT bag layout (holes included): the cells come back where the player left them.
        var cells = new List<ItemStack>(record.Items.Count);
        foreach (ItemStackRecord item in record.Items)
        {
            cells.Add(new ItemStack(item.ItemId, item.Quantity));
        }

        entity.Inventory.RestoreLayout(cells);

        // Log back in exactly where you logged out (records saved before 0.38 have no position).
        if (record.HasPosition)
        {
            world.Teleport(entity, new Aetheria.Shared.Math.Vec2(record.PosX, record.PosY));
        }

        entity.HomeX = record.HomeX;
        entity.HomeY = record.HomeY;
        entity.HonorPoints = record.Honor;
        entity.RepAlliance = record.RepAlliance;
        entity.RepHorde = record.RepHorde;
        entity.IsBandit = record.Bandit;

        if (record.Equipment.Count > 0)
        {
            foreach (KeyValuePair<string, byte> pair in record.Equipment)
            {
                if (int.TryParse(pair.Key, out int slot) && slot > 0 && slot < EquipSlots.Count)
                {
                    entity.SetEquipped((EquipSlot)slot, pair.Value);
                }
            }

            world.Equip(entity, entity.EquippedWeaponId, entity.EquippedArmorId); // recompute
        }
        else
        {
            world.Equip(entity, record.EquippedWeaponId, record.EquippedArmorId); // legacy save
        }

        foreach (KeyValuePair<string, int> pair in record.Skills)
        {
            if (byte.TryParse(pair.Key, out byte line))
            {
                entity.AddSkill(line, pair.Value, world.GameData.Progression.MaxSkill);
            }
        }

        entity.RestoreToFull();
    }

    /// <summary>Snapshot an account bank into its record fields.</summary>
    public static void CaptureBank(Inventory bank, AccountRecord account)
    {
        account.BankGold = bank.Gold;
        account.BankItems.Clear();
        foreach (ItemStack stack in bank.Stacks)
        {
            account.BankItems.Add(new ItemStackRecord { ItemId = stack.ItemId, Quantity = stack.Quantity });
        }
    }

    /// <summary>Rebuild a live bank inventory from its record.</summary>
    public static Inventory RestoreBank(AccountRecord account, int capacity, Shared.Data.GameData data)
    {
        var bank = new Inventory(capacity);
        bank.AddGold(account.BankGold);
        foreach (ItemStackRecord item in account.BankItems)
        {
            Shared.Data.ItemDefinition def = data.GetItem(item.ItemId);
            bank.TryAdd(item.ItemId, item.Quantity, def.Stackable, def.MaxStack);
        }

        return bank;
    }
}
