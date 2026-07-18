using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Tests;

public static class CombatTests
{
    // Default content: Human Warrior (atk 12) using Slash (base 10, range 2.5) against an Orc
    // Warrior (def 6-1=5). Damage = max(1, 10 + 12 - 5) = 17. PvP requires opposite factions.

    [Test("A landed ability reduces the target's health by the computed amount.")]
    public static void TryUseAbility_DealsExpectedDamage()
    {
        var world = new World();
        ServerEntity attacker = world.SpawnPlayer(new PeerId(1)); // Warrior/Human
        ServerEntity target = world.SpawnPlayer(new PeerId(2), "T", raceId: 2, classId: 1); // adjacent Orc Warrior (opposite faction)
        int maxHp = target.Stats.MaxHealth;

        bool used = world.TryUseAbility(attacker.Id, attacker.BasicAbilityId, target.Id);

        Assert.True(used, "attack should land at spawn range");
        Assert.Equal(maxHp - 25, target.Health); // Slash 18 at WoW swing pace hits harder per blow
    }

    [Test("An ability out of range does not land.")]
    public static void TryUseAbility_OutOfRange_Fails()
    {
        var world = new World();
        ServerEntity attacker = world.SpawnPlayer(new PeerId(1));
        ServerEntity target = world.SpawnPlayer(new PeerId(2), "T", raceId: 2, classId: 1);
        target.Position = new Vec2(1000, 0); // well beyond Slash's 2.5 range
        int hp = target.Health;

        bool used = world.TryUseAbility(attacker.Id, attacker.BasicAbilityId, target.Id);

        Assert.False(used);
        Assert.Equal(hp, target.Health);
    }

    [Test("An ability on cooldown cannot be used again immediately.")]
    public static void TryUseAbility_Cooldown_BlocksSecondUse()
    {
        var world = new World();
        ServerEntity attacker = world.SpawnPlayer(new PeerId(1));
        ServerEntity target = world.SpawnPlayer(new PeerId(2), "T", raceId: 2, classId: 1);

        Assert.True(world.TryUseAbility(attacker.Id, attacker.BasicAbilityId, target.Id));
        int hpAfterFirst = target.Health;

        // Same tick, still on cooldown.
        Assert.False(world.TryUseAbility(attacker.Id, attacker.BasicAbilityId, target.Id));
        Assert.Equal(hpAfterFirst, target.Health);
    }

    [Test("You cannot target yourself.")]
    public static void TryUseAbility_SelfTarget_Fails()
    {
        var world = new World();
        ServerEntity attacker = world.SpawnPlayer(new PeerId(1));

        Assert.False(world.TryUseAbility(attacker.Id, attacker.BasicAbilityId, attacker.Id));
    }

    [Test("Lethal damage kills the target, removes it from view, then it respawns at full health.")]
    public static void Combat_KillsThenRespawns()
    {
        var world = new World();
        ServerEntity attacker = world.SpawnPlayer(new PeerId(1)); // Warrior
        ServerEntity target = world.SpawnPlayer(new PeerId(2), "T", raceId: 2, classId: 1); // passive Orc target

        bool observedKillEvent = false;
        uint deathTick = 0;

        // Attack whenever ready, stepping the world between attempts, until the target dies.
        for (int i = 0; i < 400 && target.IsAlive; i++)
        {
            if (attacker.IsAbilityReady(attacker.BasicAbilityId, world.Tick))
            {
                world.TryUseAbility(attacker.Id, attacker.BasicAbilityId, target.Id);
            }

            world.Step(SimulationConstants.TickDelta);

            foreach (CombatEventMessage e in world.DrainCombatEvents())
            {
                if (e.TargetId == target.Id && e.TargetKilled)
                {
                    observedKillEvent = true;
                }
            }

            if (target.IsDead && deathTick == 0)
            {
                deathTick = world.Tick;
            }
        }

        Assert.True(target.IsDead, "target should have died");
        Assert.True(observedKillEvent, "a kill combat-event should have been emitted");
        Assert.Equal(0, target.Health);

        // While dead, the target is not in the attacker's area-of-interest snapshot.
        var idsWhileDead = ToIds(world.BuildAreaSnapshot(attacker.Position));
        Assert.DoesNotContain(target.Id, idsWhileDead);

        // Step past the respawn delay; the target should return at full health and be visible again.
        for (int i = 0; i < SimulationConstants.RespawnDelayTicks + 5 && target.IsDead; i++)
        {
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True(target.IsAlive, "target should have respawned");
        Assert.Equal(target.Stats.MaxHealth, target.Health);
        Assert.Contains(target.Id, ToIds(world.BuildAreaSnapshot(target.Position)));
    }

    private static List<int> ToIds(List<EntitySnapshot> snapshot)
    {
        var ids = new List<int>(snapshot.Count);
        foreach (EntitySnapshot e in snapshot)
        {
            ids.Add(e.Id);
        }

        return ids;
    }
}
