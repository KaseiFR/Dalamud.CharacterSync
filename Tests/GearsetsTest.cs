using System.Reflection;
using Dalamud.CharacterSync;
using Xunit;
using Xunit.Abstractions;

namespace Tests;

public class GearsetsTest
{
    private readonly ITestOutputHelper testConsole;

    public GearsetsTest(ITestOutputHelper testConsole)
    {
        this.testConsole = testConsole;
    }

    [Fact]
    public void Test1()
    {
        using var file = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Tests.resources.GEARSET_6.5.DAT");
        var gearsets = GearsetUtils.ReadGearsets(file);
        this.testConsole.WriteLine($"Parsed {gearsets.Count} gearsets:");
        foreach (var g in gearsets) this.testConsole.WriteLine($"{g}");

        Assert.Equal(26, gearsets.Count);
        Assert.Equivalent(new GearsetUtils.GearsetInfo(0, 4, "Lancer"), gearsets[0]);
        Assert.Equivalent(new GearsetUtils.GearsetInfo(1, 13, "Weaver"), gearsets[1]);
        Assert.Equivalent(
            // Exotic gearset name, with some private and large codepoints
            new GearsetUtils.GearsetInfo(29, 31, "\u2665\u2573üñîçó\ue074Ｅ\ue04c\u2661タタル\ue03c"),
            gearsets[23]
        );
        Assert.Equivalent(
            // Longest possible name (15 3-byte characters)
            new GearsetUtils.GearsetInfo(30, 31, "タタルタタルタタルタタルタタル"),
            gearsets[24]
        );
        Assert.Equivalent(new GearsetUtils.GearsetInfo(99, 31, "Last"), gearsets[25]);
    }
}