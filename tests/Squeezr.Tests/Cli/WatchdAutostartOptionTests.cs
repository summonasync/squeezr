using System.Reflection;
using Shouldly;
using Spectre.Console.Cli;
using Squeezr.Cli.Commands;
using Squeezr.Core.Infra.Autostart;

namespace Squeezr.Tests.Cli;

// Spectre.Console.Cli 0.55 hands an unknown option to an empty CommandSettings without a word — the
// review measured `watchd --bogus-option` behaving exactly like `watchd --autostart`. So the marker
// only means something because it is declared, and it has to be declared as the very string the
// generator writes into the unit: tolerance cannot be the thing an installed entry relies on.
public sealed class WatchdAutostartOptionTests
{
    [Fact]
    public void Autostart_IsDeclaredOnTheSettingsTypeTheParserBinds()
    {
        var property = typeof(WatchdCommand.Settings).GetProperty("Autostart");

        property.ShouldNotBeNull();
        property.PropertyType.ShouldBe(typeof(bool));
        property.GetCustomAttribute<CommandOptionAttribute>().ShouldNotBeNull();
    }

    [Fact]
    public void TheDeclaredOptionIsTheFlagTheGeneratedUnitsCarry()
    {
        var option = typeof(WatchdCommand.Settings)
            .GetProperty("Autostart")!
            .GetCustomAttribute<CommandOptionAttribute>()!;

        option.LongNames.ShouldHaveSingleItem().ShouldBe(WatchdUnitGenerator.AutostartFlag.TrimStart('-'));
        option.ShortNames.ShouldBeEmpty();
    }

    [Fact]
    public void EveryGeneratedUnitCarriesThatFlagOnTheWatchdVerb()
    {
        foreach (var platform in new[]
                 {
                     AutostartPlatform.SystemdUser,
                     AutostartPlatform.LaunchAgent,
                     AutostartPlatform.WindowsScheduledTask,
                 })
        {
            WatchdUnitGenerator.Generate(platform, "/opt/squeezr/squeezr")
                .ShouldContain(WatchdUnitGenerator.AutostartFlag);
        }
    }
}
