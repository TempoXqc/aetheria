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

        // Firebolt is an INCANTATION now: the mana is paid when the cast completes.
        for (int i = 0; i <= 31 && mage.IsCasting; i++)
        {
            world.Step(SimulationConstants.TickDelta);
        }

        Assert.True((int)mage.CurrentResource < startMana, "mana must be spent at cast completion");

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

        attacker.FacingRadians = System.MathF.Atan2(human.Position.Y - attacker.Position.Y, human.Position.X - attacker.Position.X); // face la cible
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

    [Test("The Cleric heals an ALLY: same faction mends, the enemy is refused.")]
    public static void Cleric_HealsAllies_NotEnemies()
    {
        var world = new World();
        ServerEntity cleric = world.SpawnPlayer(new PeerId(1), "C", raceId: 1, classId: 5); // Human Cleric
        ServerEntity ally = world.SpawnPlayer(new PeerId(2), "A", raceId: 1, classId: 1);   // Human Warrior
        ServerEntity enemy = world.SpawnPlayer(new PeerId(3), "E", raceId: 2, classId: 1);  // Orc

        // Hurt the ally first.
        enemy.FacingRadians = System.MathF.Atan2(ally.Position.Y - enemy.Position.Y,
            ally.Position.X - enemy.Position.X);
        world.TryUseAbility(enemy.Id, enemy.BasicAbilityId, ally.Id);
        int hurt = ally.Health;
        Assert.True(hurt < ally.EffectiveMaxHealth);

        // The heal (id 51) has a cast time: start it, then step until it lands.
        Assert.True(world.TryUseAbility(cleric.Id, 51, ally.Id), "heal cast must start");
        for (int i = 0; i < 40; i++) { world.Step(SimulationConstants.TickDelta); }
        Assert.True(ally.Health > hurt, "the ally must be healed");

        // Healing the ENEMY is refused outright.
        Assert.False(world.TryUseAbility(cleric.Id, 51, enemy.Id), "no heals across factions");
    }

    [Test("Consumables: potions restore instantly (shared cooldown); food refuses combat.")]
    public static void Consumables_PotionsAndFood()
    {
        var world = new World();
        ServerEntity p = world.SpawnPlayer(new PeerId(1), "Soiffard", raceId: 1, classId: 2); // Mage (mana)
        ServerEntity foe = world.SpawnPlayer(new PeerId(2), "F", raceId: 2, classId: 1);
        world.Step(SimulationConstants.TickDelta); // tick 0 means « never fought » — move past it

        // Drain some mana, then drink the mana potion.
        p.SpendResource(60);
        world.AddItem(p, 26, 2);
        double before = p.CurrentResource;
        Assert.True(world.TryUseItem(p.Id, 26, out _));
        Assert.True(p.CurrentResource > before, "mana potion must restore");
        Assert.Equal(1, p.Inventory.CountOf(26));

        // Shared potion cooldown: a second one right away is refused.
        Assert.False(world.TryUseItem(p.Id, 26, out string cd));
        Assert.True(cd.Contains("pas encore"), "cooldown message expected");

        // Food out of combat: fine. In combat: refused.
        world.AddItem(p, 27, 2);
        Assert.True(world.TryUseItem(p.Id, 27, out _), "eating in peace is fine");

        world.Teleport(foe, p.Position + new Aetheria.Shared.Math.Vec2(1.5f, 0f)); // step into melee range
        foe.FacingRadians = System.MathF.Atan2(p.Position.Y - foe.Position.Y,
            p.Position.X - foe.Position.X);
        Assert.True(world.TryUseAbility(foe.Id, foe.BasicAbilityId, p.Id), "the blow must land");
        Assert.False(world.TryUseItem(p.Id, 27, out string busy));
        Assert.True(busy.Contains("combat"), "food must refuse combat");
    }
}

