using Shouldly;
using Squeezr.Core.Infra.Autostart;

namespace Squeezr.Tests.Infra.Autostart;

public sealed class WatchdRestartPolicyTests
{
    private const string Binary = "/opt/squeezr/squeezr";

    [Theory]
    [InlineData(AutostartPlatform.SystemdUser, true)]
    [InlineData(AutostartPlatform.LaunchAgent, true)]
    [InlineData(AutostartPlatform.WindowsScheduledTask, false)]
    public void RestartsOnUnsuccessfulExit_AnswersForEveryPlatformTheGeneratorWrites(
        AutostartPlatform platform,
        bool expected) =>
        WatchdRestartPolicy.RestartsOnUnsuccessfulExit(platform).ShouldBe(expected);

    [Fact]
    public void RestartsOnUnsuccessfulExit_RefusesAPlatformItDoesNotKnow() =>
        WatchdRestartPolicy.RestartsOnUnsuccessfulExit((AutostartPlatform)42).ShouldBeFalse();

    // The policy is only honest while the entries still carry the directives it reads them from: a
    // template edit that drops Restart=on-failure would leave the supervisor retiring into a unit
    // that never starts again, and nothing but this pairing would notice.
    [Fact]
    public void TheDirectivesThePolicyIsBuiltOn_AreStillWhatTheGeneratorEmits()
    {
        WatchdUnitGenerator.Generate(AutostartPlatform.SystemdUser, Binary)
            .ShouldContain("Restart=on-failure");
        WatchdUnitGenerator.Generate(AutostartPlatform.LaunchAgent, Binary)
            .ShouldContain("<key>KeepAlive</key>\n    <dict>\n      <key>SuccessfulExit</key>\n      <false/>");
        WatchdUnitGenerator.Generate(AutostartPlatform.WindowsScheduledTask, Binary)
            .ShouldContain("/SC ONLOGON");
    }
}
