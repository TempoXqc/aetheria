using Aetheria.Server.World;
using Aetheria.Shared;
using Aetheria.Shared.Combat;
using Aetheria.Shared.Data;
using Aetheria.Shared.Net;

namespace Aetheria.Tests;

public static class CharacterSystemTests
{
    // Race ids: 1 Human (Alliance), 4 Dwarf (Alliance), 2 Orc (Horde), 3 Elf (Horde).
    // Class ids: 1 Warrior (Rage), 2 Mage (Mana), 3 Ranger (Energy).

    [Test("The class/race balance matrix allows and forbids the intended combinations.")]
    public static void ClassRaceMatrix_EnforcesAllowedCombinations()
    {
        GameData d = GameData.CreateDefault();

        Assert.True(d.IsClassAllowedForRace(1, 1));  // Human Warrior
        Assert.True(d.IsClassAllowedForRace(1, 2));  // Human Mage
        Assert.False(d.IsClassAllowedForRace(1, 3)); // Human Ranger — not allowed

        Assert.True(d.IsClassAllowedForRace(4, 3));  // Dwarf Ranger
        Assert.False(d.IsClassAllowedForRace(4, 2)); // Dwarf Mage — not allowed

        Assert.True(d.IsClassAllowedForRace(3, 2));  // Elf Mage
        Assert.False(d.IsClassAllowedForRace(3, 1)); // Elf Warrior — not allowed
    }

    [Test("Players are assigned the faction of their race.")]
    public static void SpawnPlayer_AssignsFaction()
    {
        var world = new World();
        Assert.Equal(Faction.Alliance, world.SpawnPlayer(new PeerId(1), "H", raceId: 1, classId: 1).Faction);
        Assert.Equal(Faction.Alliance, world.SpawnPlayer(new PeerId(2), "D", raceId: 4, classId: 1).Faction);
        Assert.Equal(Faction.Horde, world.SpawnPlayer(new PeerId(3), "O", raceId: 2, classId: 1).Faction);
        Assert.Equal(Faction.Horde, world.SpawnPlayer(new PeerId(4), "E", raceId: 3, classId: 2).Faction);
    }

    [Test("Resource pools initialize per class: rage empty, mana/energy full.")]
    public static void Resources_InitializePerClass()
    {
        var world = new World();
        ServerEntity warrior = world.SpawnPlayer(new PeerId(1), "W", 1, 1); // Rage
        ServerEntity mage = world.SpawnPlayer(new PeerId(2), "M", 1, 2);    // Mana
        ServerEntity ranger = world.SpawnPlayer(new PeerId(3), "R", 2, 3);  // Energy

        Assert.Equal(ResourceType.Rage, warrior.ResourceType);
        Assert.Equal(0, (int)warrior.CurrentResource);

        Assert.Equal(ResourceType.Mana, mage.ResourceType);
        Assert.Equal(mage.MaxResource, (int)mage.CurrentResource);

        Assert.Equal(ResourceType.Energy, ranger.ResourceType);
        Assert.Equal(ranger.MaxResource, (int)ranger.CurrentResource);
    }

    [Test("An ability spends its resource cost; an empty pool blocks the ability.")]
    public static void Resources_CostIsSpentAndEnforced()
    {
        var world = new World();
        ServerEntity mage = world.SpawnPlayer(new PeerId(1), "M", 1, 2); // Human Mage, Firebolt cost 20
        ServerEntity target = world.SpawnPlayer(new PeerId(2), "T", raceId: 2, classId: 1); // opposite faction

        int startMana = (int)mage.CurrentResource;
        Assert.True(world.TryUseAbility(mage.Id, mage.BasicAbilityId, target.Id));
        Assert.Equal(startMana - 20, (int)mage.CurrentResource);

        // Drain the pool: the next cast must fail and deal no damage.
        mage.SpendResource(mage.MaxResource);
        int targetHp = target.Health;
        // Advance ticks so the ability is off cooldown, but the pool stays near-empty.
        for (int i = 0; i < 20; i++)
        {
            world.Step(SimulationConstants.TickDelta);
        }

        mage.SpendResource(mage.MaxResource); // ensure empty after any regen
        Assert.False(world.TryUseAbility(mage.Id, mage.BasicAbilityId, target.Id));
        Assert.Equal(targetHp, target.Health);
    }

    [Test("Mana regenerates over time.")]
    public static void Resources_ManaRegenerates()
    {
        var world = new World();
        ServerEntity mage = world.SpawnPlayer(new PeerId(1), "M", 1, 2);
        mage.SpendResource(50); // drop to ~50
        int before = (int)mage.CurrentResource;

        for (int i = 0; i < 40; i++) // ~2s at 20 Hz
        {
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True((int)mage.CurrentResource > before, "mana should regenerate");
    }

    [Test("Warriors build rage by dealing damage.")]
    public static void Resources_WarriorBuildsRage()
    {
        var world = new World();
        ServerEntity warrior = world.SpawnPlayer(new PeerId(1), "W", 1, 1); // Rage starts at 0
        ServerEntity target = world.SpawnPlayer(new PeerId(2), "T", raceId: 2, classId: 1); // opposite faction

        Assert.Equal(0, (int)warrior.CurrentResource);
        world.TryUseAbility(warrior.Id, warrior.BasicAbilityId, target.Id); // Slash, cost 0
        Assert.True((int)warrior.CurrentResource >= 10, "landing a hit should generate rage");
    }

    [Test("The Human racial (Second Wind) heals the caster.")]
    public static void Racial_Heal_RestoresHealth()
    {
        var world = new World();
        ServerEntity human = world.SpawnPlayer(new PeerId(1), "H", 1, 1); // Human Warrior, racial = Second Wind
        ServerEntity attacker = world.SpawnPlayer(new PeerId(2), "A", raceId: 2, classId: 1); // Orc attacker (opposite faction)

        world.TryUseAbility(attacker.Id, attacker.BasicAbilityId, human.Id); // damage the human
        int damagedHp = human.Health;
        Assert.True(damagedHp < human.Stats.MaxHealth);

        Assert.True(world.TryUseRacial(human.Id));
        Assert.True(human.Health > damagedHp, "Second Wind should heal");
    }

    [Test("The Orc racial (Blood Fury) buffs attack power for a duration, then expires.")]
    public static void Racial_Buff_AppliesThenExpires()
    {
        var world = new World();
        ServerEntity orc = world.SpawnPlayer(new PeerId(1), "O", raceId: 2, classId: 1); // Orc Warrior
        int baseAttack = orc.EffectiveAttackPower;

        Assert.True(world.TryUseRacial(orc.Id)); // Blood Fury: +40% attack
        Assert.True(orc.EffectiveAttackPower > baseAttack, "buff should raise attack power");

        // Step past the buff duration (160 ticks) and confirm it expires.
        for (int i = 0; i < 170; i++)
        {
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.Equal(baseAttack, orc.EffectiveAttackPower);
    }

    [Test("A racial on cooldown cannot be recast immediately.")]
    public static void Racial_Cooldown_BlocksImmediateRecast()
    {
        var world = new World();
        ServerEntity dwarf = world.SpawnPlayer(new PeerId(1), "D", raceId: 4, classId: 1); // Dwarf, Stoneform

        Assert.True(world.TryUseRacial(dwarf.Id));
        Assert.False(world.TryUseRacial(dwarf.Id));
    }

    [Test("The Elf racial (Nature's Swiftness) increases effective move speed.")]
    public static void Racial_MoveSpeedBuff_Applies()
    {
        var world = new World();
        ServerEntity elf = world.SpawnPlayer(new PeerId(1), "E", raceId: 3, classId: 3); // Elf Ranger
        float baseSpeed = elf.EffectiveMoveSpeed;

        Assert.True(world.TryUseRacial(elf.Id));
        Assert.True(elf.EffectiveMoveSpeed > baseSpeed, "swiftness should raise move speed");
    }
}
