using Aetheria.Server.Persistence;
using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Tests;

/// <summary>The linear kill-quest chain: accept at the giver, count kills, turn in, unlock next.</summary>
public static class QuestTests
{
    private static (World world, ServerEntity player) SetupAtGiver()
    {
        var world = new World();
        ServerEntity giver = world.SpawnNpc("Aldric le Guetteur", new Vec2(3.5f, 3.5f), npcType: 2);
        ServerEntity player = world.SpawnPlayer(new PeerId(1), "Héros", raceId: 1, classId: 1);
        world.Teleport(player, giver.Position + new Vec2(1f, 0f)); // stand at the giver
        return (world, player);
    }

    private static void KillGoblin(World world, ServerEntity killer)
    {
        ServerEntity goblin = world.SpawnMonster(1, killer.Position + new Vec2(1f, 0f));
        for (int i = 0; i < 600 && goblin.IsAlive; i++)
        {
            if (killer.IsAbilityReady(killer.BasicAbilityId, world.Tick))
            {
                world.TryUseAbility(killer.Id, killer.BasicAbilityId, goblin.Id, fromAuto: true);
            }

            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(goblin.IsDead, "the goblin must fall");
        killer.RestoreToFull(); // patched up between fights — this is a quest test, not a survival one
    }

    [Test("Accepting a quest requires standing at the quest giver — and follows the chain.")]
    public static void Accept_RequiresGiverAndChainOrder()
    {
        (World world, ServerEntity player) = SetupAtGiver();

        Assert.False(world.TryQuestAction(player.Id, 2, turnIn: false), "quest 2 is locked before 1");
        Assert.True(world.TryQuestAction(player.Id, 1, turnIn: false), "quest 1 accepts at the giver");
        Assert.Equal((byte)1, player.ActiveQuestId);

        // Far from the giver: no accepting (a fresh player elsewhere).
        var world2 = new World();
        world2.SpawnNpc("Aldric", new Vec2(0f, 0f), npcType: 2);
        ServerEntity far = world2.SpawnPlayer(new PeerId(2), "Loin", raceId: 1, classId: 1);
        world2.Teleport(far, new Vec2(50f, 50f));
        Assert.False(world2.TryQuestAction(far.Id, 1, turnIn: false), "too far from the giver");
    }

    [Test("Only the quest's TARGET monster advances the counter, capped at the requirement.")]
    public static void Kills_CountTowardTheObjective()
    {
        (World world, ServerEntity player) = SetupAtGiver();
        Assert.True(world.TryQuestAction(player.Id, 1, turnIn: false));

        KillGoblin(world, player);
        Assert.Equal(1, player.QuestKills);

        // A wolf (not the target) doesn't count.
        world.GrantExperience(player, 500);
        ServerEntity wolf = world.SpawnMonster(2, player.Position + new Vec2(1f, 0f));
        for (int i = 0; i < 900 && wolf.IsAlive; i++)
        {
            if (player.IsAbilityReady(player.BasicAbilityId, world.Tick))
            {
                world.TryUseAbility(player.Id, player.BasicAbilityId, wolf.Id, fromAuto: true);
            }

            world.Step(SimulationConstants.TickDelta);
        }

        Assert.Equal(1, player.QuestKills);
    }

    [Test("Turn-in pays XP + gold, completes the quest, and unlocks the next link (the King).")]
    public static void TurnIn_PaysAndUnlocksTheChain()
    {
        (World world, ServerEntity player) = SetupAtGiver();
        world.GrantExperience(player, 3000); // muscle: killed goblins RESPAWN and gang up over 10 fights
        Assert.True(world.TryQuestAction(player.Id, 1, turnIn: false));

        Assert.False(world.TryQuestAction(player.Id, 1, turnIn: true), "objective not complete yet");

        for (int i = 0; i < 10; i++) { KillGoblin(world, player); }
        Assert.Equal(10, player.QuestKills);

        int xp = player.TotalXp;
        int gold = player.Inventory.Gold;
        Assert.True(world.TryQuestAction(player.Id, 1, turnIn: true), "complete objective turns in");
        Assert.Equal(xp + 150, player.TotalXp);
        Assert.Equal(gold + 5000, player.Inventory.Gold);
        Assert.Equal((byte)1, player.QuestCompletedUpTo);
        Assert.Equal((byte)0, player.ActiveQuestId);

        // The chain continues: quest 2 now accepts; quest 1 can never be re-taken.
        Assert.False(world.TryQuestAction(player.Id, 1, turnIn: false), "no repeating quest 1");
        Assert.True(world.TryQuestAction(player.Id, 2, turnIn: false), "the King quest unlocks");
    }

    [Test("Quest kill progress is pushed to the dirty list for the server to broadcast.")]
    public static void KillProgress_MarksTheDirtyList()
    {
        (World world, ServerEntity player) = SetupAtGiver();
        Assert.True(world.TryQuestAction(player.Id, 1, turnIn: false));
        world.DrainQuestDirty(); // clear anything pending

        KillGoblin(world, player);
        System.Collections.Generic.IReadOnlyList<int> dirty = world.DrainQuestDirty();
        Assert.Equal(1, dirty.Count);
        Assert.Equal(player.Id, dirty[0]);
        Assert.Equal(0, world.DrainQuestDirty().Count); // drained
    }

    [Test("Quest progress survives a save/restore cycle.")]
    public static void Persistence_KeepsQuestProgress()
    {
        (World world, ServerEntity player) = SetupAtGiver();
        Assert.True(world.TryQuestAction(player.Id, 1, turnIn: false));
        KillGoblin(world, player);

        CharacterRecord record = CharacterMapper.Capture(player);
        var world2 = new World();
        ServerEntity restored = world2.SpawnPlayer(new PeerId(5), "Revenant", raceId: 1, classId: 1);
        CharacterMapper.Restore(world2, restored, record);

        Assert.Equal((byte)1, restored.ActiveQuestId);
        Assert.Equal(1, restored.QuestKills);
        Assert.Equal((byte)0, restored.QuestCompletedUpTo);
    }
}
