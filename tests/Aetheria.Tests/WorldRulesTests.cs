using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Items;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Tests;

/// <summary>Leash/evade AI, monster remains, movement facing, and the equip flow (v0.17.0).</summary>
public static class WorldRulesTests
{
    [Test("A monster dragged beyond the leash radius drops aggro, returns home, and heals.")]
    public static void Leash_DropsAggro_ReturnsHome_Heals()
    {
        var world = new World();
        ServerEntity player = world.SpawnPlayer(new PeerId(1), "Kite", raceId: 2, classId: 1);
        ServerEntity wolf = world.SpawnMonster(2, new Vec2(10f, 0f));
        Vec2 home = wolf.Position;

        // Provoke: hit it once so it targets us, and hurt it.
        world.Teleport(player, new Vec2(9f, 0f));
        world.TryUseAbility(player.Id, player.BasicAbilityId, wolf.Id);
        world.Step(SimulationConstants.TickDelta);
        Assert.Equal(player.Id, wolf.AiTargetId);
        Assert.True(wolf.Health < wolf.EffectiveMaxHealth);

        // Teleport the "kiting" player far beyond the leash range and let the AI notice.
        world.Teleport(player, new Vec2(200f, 0f));
        wolf.Position = new Vec2(10f + SimulationConstants.MonsterLeashRadius + 5f, 0f); // dragged out
        world.Step(SimulationConstants.TickDelta);

        Assert.True(wolf.AiTargetId is null, "leash must drop aggro");
        Assert.True(wolf.IsEvading, "the wolf runs home");

        // Let it walk all the way back: it heals to full and stands down.
        for (int i = 0; i < 400 && wolf.IsEvading; i++)
        {
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.False(wolf.IsEvading, "the wolf must reach home");
        Assert.True(Vec2.DistanceSquared(wolf.Position, home) < 4f);
        Assert.Equal(wolf.EffectiveMaxHealth, wolf.Health);
    }

    [Test("A chasing monster faces its prey (no more moon-walking wolves).")]
    public static void MonsterAi_FacesItsTarget()
    {
        var world = new World();
        ServerEntity player = world.SpawnPlayer(new PeerId(1), "Bait", raceId: 2, classId: 1);
        world.Teleport(player, new Vec2(16f, 0f));
        ServerEntity wolf = world.SpawnMonster(2, new Vec2(10f, 0f)); // 6u away: inside aggro (9)

        world.Step(SimulationConstants.TickDelta);

        // Prey is due east (+X): facing must be ~0 radians.
        Assert.Equal(player.Id, wolf.AiTargetId);
        Assert.Close(0f, wolf.FacingRadians);
    }

    [Test("A slain monster leaves remains on the ground, which despawn on their timer.")]
    public static void MonsterDeath_LeavesTimedRemains()
    {
        var world = new World();
        ServerEntity killer = world.SpawnPlayer(new PeerId(1), "Chasseur", raceId: 2, classId: 1);
        ServerEntity goblin = world.SpawnMonster(1, killer.Position + new Vec2(1f, 0f));

        for (int i = 0; i < 600 && goblin.IsAlive; i++)
        {
            if (killer.IsAbilityReady(killer.BasicAbilityId, world.Tick))
            {
                world.TryUseAbility(killer.Id, killer.BasicAbilityId, goblin.Id);
            }

            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(goblin.IsDead);

        ServerEntity? remains = null;
        foreach (ServerEntity e in world.Entities.Values)
        {
            if (e.Kind == EntityKind.MonsterCorpse) { remains = e; }
        }

        Assert.True(remains is not null, "remains must lie where the goblin fell");
        Assert.Equal((byte)1, remains!.MonsterId);
        Assert.True(remains.DespawnAtTick > world.Tick);

        // Fast-forward past the despawn timer (90 s for lootable bodies): the remains are gone.
        for (int i = 0; i < (SimulationConstants.TickRate * 91) + 5; i++)
        {
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.False(world.Entities.ContainsKey(remains.Id), "remains must despawn");
    }

    [Test("Equipping from the bags swaps the old piece back; unequipping frees the slot.")]
    public static void EquipItem_SwapAndUnequip()
    {
        var world = new World();
        ServerEntity p = world.SpawnPlayer(new PeerId(1), "Forgeron", raceId: 2, classId: 1);
        world.GrantStarterKit(p); // Rusty Sword (1) equipped
        world.AddItem(p, itemId: 2, quantity: 1); // Iron Sword in the bags
        Assert.Equal((byte)1, p.EquippedWeaponId);

        // Swap to the Iron Sword: the Rusty Sword must fall back into the bags.
        Assert.True(world.TryEquipItem(p.Id, 2, EquipSlot.Weapon));
        Assert.Equal((byte)2, p.EquippedWeaponId);
        Assert.Equal(1, p.Inventory.CountOf(1));
        Assert.Equal(0, p.Inventory.CountOf(2));

        // Unequip entirely.
        Assert.True(world.TryEquipItem(p.Id, 0, EquipSlot.Weapon));
        Assert.Equal((byte)0, p.EquippedWeaponId);
        Assert.Equal(1, p.Inventory.CountOf(2));

        // A non-equippable material refuses.
        world.AddItem(p, itemId: 30, quantity: 1); // Goblin Head
        Assert.False(world.TryEquipItem(p.Id, 30, EquipSlot.Weapon));
    }

    [Test("NPCs (the bank chest) cannot be attacked.")]
    public static void Npc_IsInvulnerable()
    {
        var world = new World();
        ServerEntity p = world.SpawnPlayer(new PeerId(1), "Voyou", raceId: 2, classId: 1);
        ServerEntity chest = world.SpawnNpc("Coffre de banque", p.Position + new Vec2(1f, 0f));

        Assert.False(world.TryUseAbility(p.Id, p.BasicAbilityId, chest.Id));
    }

    [Test("Chat messages round-trip byte-exactly.")]
    public static void Chat_RoundTrips()
    {
        var w = new PacketWriter();
        new ChatSend("Salut le monde !").Write(w);
        var r = new PacketReader(w.WrittenSpan);
        Assert.Equal(MessageType.ChatSend, (MessageType)r.ReadByte());
        Assert.Equal("Salut le monde !", ChatSend.Read(ref r).Text);

        var w2 = new PacketWriter();
        new ChatMessage("Thorin", "on farm les loups ?").Write(w2);
        var r2 = new PacketReader(w2.WrittenSpan);
        Assert.Equal(MessageType.ChatMessage, (MessageType)r2.ReadByte());
        ChatMessage msg = ChatMessage.Read(ref r2);
        Assert.Equal("Thorin", msg.From);
        Assert.Equal("on farm les loups ?", msg.Text);
    }
}
