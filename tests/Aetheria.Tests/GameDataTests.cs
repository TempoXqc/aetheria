using Aetheria.Shared.Combat;
using Aetheria.Shared.Data;

namespace Aetheria.Tests;

public static class GameDataTests
{
    [Test("The built-in defaults contain the expected content.")]
    public static void CreateDefault_HasContent()
    {
        GameData data = GameData.CreateDefault();

        Assert.Equal(4, data.Races.Count); // Human, Dwarf, Orc, Elf
        Assert.Equal(3, data.Classes.Count);
        Assert.Equal(4, data.Monsters.Count); // Grunt, Wolf, Goblin King, Ashmaw
        Assert.Equal("Warrior", data.GetClass(1).Name);
        Assert.Equal("Goblin Grunt", data.GetMonster(1).Name);
    }

    [Test("Unknown ids fall back to a valid default instead of throwing.")]
    public static void Lookups_FallBackOnUnknownId()
    {
        GameData data = GameData.CreateDefault();

        // id 200 does not exist; must return *some* valid definition, not null / not a throw.
        Assert.True(data.GetClass(200) is not null);
        Assert.True(data.GetRace(200) is not null);
        Assert.True(data.GetAbility(200) is not null);
        Assert.True(data.GetMonster(200) is not null);
    }

    [Test("Race modifiers combine with class base stats as expected.")]
    public static void StatBlock_Combine_AppliesRaceModifiers()
    {
        GameData data = GameData.CreateDefault();
        ClassDefinition warrior = data.GetClass(1); // hp 120, atk 12, def 6, speed 5.0
        RaceDefinition orc = data.GetRace(2);       // +20 hp, +3 atk, -1 def, x0.95 speed

        StatBlock combined = StatBlock.Combine(warrior.ToBaseStats(), orc.ToModifiers());

        Assert.Equal(140, combined.MaxHealth);
        Assert.Equal(15, combined.AttackPower);
        Assert.Equal(5, combined.Defense);
        Assert.Close(5.0f * 0.95f, combined.MoveSpeed);
    }
}
