using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Core.Serialization;
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

    [Fact]
    public void AdoptionLine_NamesEveryRuntimeTheLedgerShows_WithItsOwnZeroSide()
    {
        var rollup = UsageAnalytics.Rollup(
            [Block(
                Entry(Guid.NewGuid(), "expand_span", "cli"),
                HookRow(Guid.NewGuid(), "read_file"),
                HookRow(Guid.NewGuid(), "read_file"))],
            new HashSet<string>(),
            since: null);

        var line = UsageCommand.AdoptionLine(rollup.Totals.Sessions!);

        line.ShouldContain("Qwen Code 2");
        line.ShouldContain("Claude Code 0");
        line.ShouldContain("unrecorded tool name 0");
    }

    [Fact]
    public void AdoptionLine_SpeaksARecordedNameNobodyHasAttributedYet_Verbatim()
    {
        var rollup = UsageAnalytics.Rollup(
            [Block(HookRow(Guid.NewGuid(), "ReadFile"))],
            new HashSet<string>(),
            since: null);

        var line = UsageCommand.AdoptionLine(rollup.Totals.Sessions!);

        line.ShouldContain("ReadFile 1");
        line.ShouldContain("Claude Code 0");
        line.ShouldContain("Qwen Code 0");
    }

    [Fact]
    public void AdoptionLine_SpeaksTheUnrecordedSide_OfAHookLedgerThatNamedNoTool()
    {
        var rollup = UsageAnalytics.Rollup(
            [Block(HookRow(Guid.NewGuid(), null), HookRow(Guid.NewGuid(), null))],
            new HashSet<string>(),
            since: null);

        var line = UsageCommand.AdoptionLine(rollup.Totals.Sessions!);

        line.ShouldContain("unrecorded tool name 2");
        line.ShouldContain("Claude Code 0");
    }

    [Fact]
    public void AdoptionLine_KeepsTheLegacyShape_WhenTheHookSideIsEmpty()
    {
        UsageCommand.AdoptionLine(new SessionAdoption(3, 0, [], 0))
            .ShouldContain("read side unknown");
        UsageCommand.AdoptionLine(new SessionAdoption(3, 0, [], 0))
            .ShouldNotContain("reads:");
    }

    [Fact]
    public void Ratio_DenominatorStaysTotalHookSessions_AcrossEveryRuntime()
    {
        var oneSessionBothRuntimes = Guid.NewGuid();
        var rollup = UsageAnalytics.Rollup(
            [Block(
                Entry(Guid.NewGuid(), "code_search", "cli"),
                Entry(Guid.NewGuid(), "code_search", "cli"),
                HookRow(oneSessionBothRuntimes, "Read"),
                HookRow(oneSessionBothRuntimes, "read_file"))],
            new HashSet<string>(),
            since: null);

        var adoption = rollup.Totals.Sessions!;

        adoption.HookSessions.ShouldBe(1);
        adoption.McpSessions.ShouldBe(2);
        adoption.Ratio.ShouldBe(2.0);
        adoption.HookSessionsByTool!.Sum(b => b.Sessions).ShouldBe(2);
    }

    [Fact]
    public void AdoptionJson_LegacyKeysKeepTheirMeaning_TheSplitRidesAsAdditiveKeys()
    {
        var mcp = Guid.NewGuid();
        var claude = Guid.NewGuid();
        var qwen = Guid.NewGuid();
        var rollup = UsageAnalytics.Rollup(
            [Block(
                Entry(mcp, "code_search", "cli"),
                HookRow(claude, "Read"),
                HookRow(qwen, "read_file"),
                HookRow(Guid.NewGuid(), null))],
            new HashSet<string>(),
            since: null);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(rollup, SqueezrJson.OutputOptions));
        var sessions = document.RootElement.GetProperty("totals").GetProperty("sessions");

        sessions.GetProperty("mcpSessions").GetInt32().ShouldBe(1);
        sessions.GetProperty("hookSessions").GetInt32().ShouldBe(3);
        sessions.GetProperty("ratio").GetDouble().ShouldBe(0.33);
        sessions.GetProperty("hookSessionsWithoutTool").GetInt32().ShouldBe(1);
        var byTool = sessions.GetProperty("hookSessionsByTool");
        byTool.EnumerateArray().Single(b => b.GetProperty("tool").GetString() == "Read")
            .GetProperty("sessions").GetInt32().ShouldBe(1);
        byTool.EnumerateArray().Single(b => b.GetProperty("tool").GetString() == "read_file")
            .GetProperty("sessions").GetInt32().ShouldBe(1);

        sessions.EnumerateObject().Select(p => p.Name)
            .ShouldBe(["mcpSessions", "hookSessions", "hookSessionsByTool", "hookSessionsWithoutTool", "ratio"]);
    }

    [Fact]
    public void AdoptionJson_MixedRuntimeLedger_SpeaksTwoNamedSidesInTextAndJson()
    {
        var entries = new[]
        {
            Entry(Guid.NewGuid(), "symbol_lookup", "cli"),
            HookRow(Guid.NewGuid(), "Read"),
            HookRow(Guid.NewGuid(), "read_file"),
        };
        var rollup = UsageAnalytics.Rollup([Block(entries)], new HashSet<string>(), since: null);

        var json = JsonSerializer.Serialize(rollup.Totals.Sessions, SqueezrJson.OutputOptions);
        var line = UsageCommand.AdoptionLine(rollup.Totals.Sessions!);

        json.ShouldContain("\"tool\":\"Read\"");
        json.ShouldContain("\"tool\":\"read_file\"");
        line.ShouldContain("Claude Code 1");
        line.ShouldContain("Qwen Code 1");
    }

    [Fact]
    public void AdoptionLine_ReadsAPreSplitLedgerAtFaceValue_NoRuntimeGainsSessionsItNeverRecorded()
    {
        var rollup = UsageAnalytics.Rollup(
            [Block(HookRow(Guid.NewGuid(), "Read"), HookRow(Guid.NewGuid(), "Read"))],
            new HashSet<string>(),
            since: null);

        var line = UsageCommand.AdoptionLine(rollup.Totals.Sessions!);
        var json = JsonSerializer.Serialize(rollup.Totals.Sessions, SqueezrJson.OutputOptions);

        line.ShouldContain("Claude Code 2");
        line.ShouldContain("Qwen Code 0");
        line.ShouldNotContain("ReadFile");
        json.ShouldNotContain("read_file");
    }

    private static QueryTelemetryEntry HookRow(Guid session, string? recordedTool) =>
        new(
            Stamp,
            session,
            "pre-read",
            recordedTool is null
                ? new { file = "src/Squeezr.Cli/Program.cs" }
                : new { tool = recordedTool, file = "src/Squeezr.Cli/Program.cs" },
            0,
            false,
            0,
            0,
            false,
            null,
            "hook",
            "1.0");
}
