using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

public class UsageAdoptionTests
{
    private static readonly DateTime Stamp = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    private static QueryTelemetryEntry Entry(
        Guid session, string tool, string client, int minutesOffset = 0) =>
        new(Stamp.AddMinutes(minutesOffset), session, tool, null, 0, false, 0, 1, false, null, client, "1.0");

    private static (WorkspaceRegistryEntry Workspace, IReadOnlyList<QueryTelemetryEntry> Entries) Block(
        params QueryTelemetryEntry[] entries) =>
        (new WorkspaceRegistryEntry("ws", "/tmp/ws", "ws-key", Stamp, Stamp), entries);

    [Fact]
    public void Rollup_CountsSessionsPerSide_ExcludingUnattributed()
    {
        var mcpSession = Guid.NewGuid();
        var hookA = Guid.NewGuid();
        var hookB = Guid.NewGuid();
        var entries = new[]
        {
            Entry(mcpSession, "code_search", "cli"),
            Entry(mcpSession, "expand_span", "cli", 1),
            Entry(hookA, "pre-read", "hook"),
            Entry(hookA, "pre-read", "hook", 1),
            Entry(hookB, "pre-read", "hook"),
            Entry(Guid.Empty, "search", "cli"),
        };

        var rollup = UsageAnalytics.Rollup([Block(entries)], new HashSet<string>(), since: null);

        rollup.Totals.Sessions.ShouldNotBeNull();
        rollup.Totals.Sessions.McpSessions.ShouldBe(1);
        rollup.Totals.Sessions.HookSessions.ShouldBe(2);
        rollup.Totals.Sessions.Ratio.ShouldBe(0.5);
    }

    [Fact]
    public void Rollup_AgentStatsCarryDistinctSessions()
    {
        var entries = new[]
        {
            Entry(Guid.NewGuid(), "code_search", "cli"),
            Entry(Guid.NewGuid(), "code_search", "cli"),
            Entry(Guid.NewGuid(), "code_search", "cli"),
            Entry(Guid.Empty, "search", "cli"),
        };

        var rollup = UsageAnalytics.Rollup([Block(entries)], new HashSet<string>(), since: null);

        var cli = rollup.Totals.Agents.Single(a => a.Client == "cli");
        cli.Count.ShouldBe(4);
        cli.Sessions.ShouldBe(3);
    }

    [Fact]
    public void AdoptionLine_SpeaksEveryZeroSideExplicitly()
    {
        UsageCommand.AdoptionLine(new SessionAdoption(0, 0))
            .ShouldContain("no session-attributed usage");
        UsageCommand.AdoptionLine(new SessionAdoption(3, 0))
            .ShouldContain("read side unknown");
        UsageCommand.AdoptionLine(new SessionAdoption(3, 0))
            .ShouldContain("install the pre-read hook");
        UsageCommand.AdoptionLine(new SessionAdoption(0, 4))
            .ShouldContain("0 sessions used MCP");
        UsageCommand.AdoptionLine(new SessionAdoption(2, 4))
            .ShouldContain("2 of 4 reading session(s)");
        UsageCommand.AdoptionLine(new SessionAdoption(2, 4))
            .ShouldContain("ratio 0.5");
    }

    [Fact]
    public void Ratio_IsNullWhenNoHookSideExists()
    {
        new SessionAdoption(5, 0).Ratio.ShouldBeNull();
        new SessionAdoption(2, 4).Ratio.ShouldBe(0.5);
    }
}
