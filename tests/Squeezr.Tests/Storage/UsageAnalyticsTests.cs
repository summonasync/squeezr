using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Storage;

public class UsageAnalyticsTests
{
    private static readonly DateTime T1 = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 9, 24, 10, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime T3 = new(2026, 9, 24, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Rollup_TwoWorkspaces_PerWorkspaceBlocksAndTotalsByAgent()
    {
        var rootA = TestDirs.NewTempDir();
        var rootB = TestDirs.NewTempDir();
        var workspaces = new List<(WorkspaceRegistryEntry, IReadOnlyList<QueryTelemetryEntry>)>
        {
            (RegistryEntry("alpha", rootA),
            [
                Entry(T1, "kimi-code", "1.0"),
                Entry(T2, "kimi-code", "1.0"),
                Entry(T3, "cli", null)
            ]),
            (RegistryEntry("beta", rootB), [Entry(T2, "claude-code", "2.0")]),
        };

        var rollup = UsageAnalytics.Rollup(workspaces, NoStale());

        rollup.Workspaces.Count.ShouldBe(2);
        var alpha = rollup.Workspaces[0];
        alpha.StoreKey.ShouldBe(WorkspaceStoreKeys.Create("alpha", WorkspaceCanonicalPath.Canonicalize(rootA)));
        alpha.RootPath.ShouldBe(WorkspaceCanonicalPath.Canonicalize(rootA));
        alpha.Records.ShouldBe(3);
        alpha.Stale.ShouldBeFalse();
        alpha.Agents.Count.ShouldBe(2);
        var kimi = alpha.Agents.Single(a => a.Client == "kimi-code");
        kimi.ClientVersion.ShouldBe("1.0");
        kimi.Count.ShouldBe(2);
        kimi.LastSeen.ShouldBe(T2);
        alpha.Agents.Single(a => a.Client == "cli").Count.ShouldBe(1);
        rollup.Workspaces[1].Records.ShouldBe(1);

        rollup.Totals.Records.ShouldBe(4);
        rollup.Totals.Agents.Count.ShouldBe(3);
        rollup.Totals.Agents[0].ShouldBe(kimi);
        rollup.Period.From.ShouldBe(T1);
        rollup.Period.To.ShouldBe(T3);
    }

    [Fact]
    public void Rollup_AgentFilterCaseInsensitiveExactMatch()
    {
        var root = TestDirs.NewTempDir();
        var workspaces = new List<(WorkspaceRegistryEntry, IReadOnlyList<QueryTelemetryEntry>)>
        {
            (RegistryEntry("alpha", root), [Entry(T1, "kimi-code", "1.0"), Entry(T2, "cli", null)]),
        };

        var rollup = UsageAnalytics.Rollup(workspaces, NoStale(), agent: "KIMI-CODE");

        rollup.Totals.Records.ShouldBe(1);
        rollup.Totals.Agents.Single().Client.ShouldBe("kimi-code");

        UsageAnalytics.Rollup(workspaces, NoStale(), agent: "kimi").Totals.Records.ShouldBe(0);
        UsageAnalytics.Rollup(workspaces, NoStale(), agent: "unknown").Totals.Records.ShouldBe(0);
    }

    [Fact]
    public void Rollup_LegacyRecordsWithoutClient_MatchUnknownAgentFilter()
    {
        var root = TestDirs.NewTempDir();
        var workspaces = new List<(WorkspaceRegistryEntry, IReadOnlyList<QueryTelemetryEntry>)>
        {
            (RegistryEntry("alpha", root), [Entry(T1, null, null), Entry(T2, "cli", null)]),
        };

        var rollup = UsageAnalytics.Rollup(workspaces, NoStale(), agent: "UNKNOWN");

        rollup.Totals.Records.ShouldBe(1);
        rollup.Totals.Agents.Single().Client.ShouldBe("unknown");
    }

    [Fact]
    public void Rollup_SinceFilter_KeepsEntriesAtOrAfterCutoff()
    {
        var root = TestDirs.NewTempDir();
        var workspaces = new List<(WorkspaceRegistryEntry, IReadOnlyList<QueryTelemetryEntry>)>
        {
            (RegistryEntry("alpha", root), [Entry(Old, "cli", null), Entry(T1, "cli", null)]),
        };

        var rollup = UsageAnalytics.Rollup(workspaces, NoStale(), since: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

        rollup.Totals.Records.ShouldBe(1);
        rollup.Period.From.ShouldBe(T1);
        rollup.Period.To.ShouldBe(T1);
    }

    [Fact]
    public void Rollup_WorkspaceStoreKeyFilter_CombinesWithAgentFilter()
    {
        var rootA = TestDirs.NewTempDir();
        var rootB = TestDirs.NewTempDir();
        var entryA = RegistryEntry("alpha", rootA);
        var workspaces = new List<(WorkspaceRegistryEntry, IReadOnlyList<QueryTelemetryEntry>)>
        {
            (entryA, [Entry(T1, "kimi-code", "1.0"), Entry(T2, "cli", null)]),
            (RegistryEntry("beta", rootB), [Entry(T2, "kimi-code", "1.0")]),
        };

        var byKey = UsageAnalytics.Rollup(workspaces, NoStale(), workspaceStoreKey: entryA.StoreKey);
        byKey.Workspaces.Single().StoreKey.ShouldBe(entryA.StoreKey);
        byKey.Totals.Records.ShouldBe(2);

        var byKeyAndAgent = UsageAnalytics.Rollup(
            workspaces, NoStale(), agent: "kimi-code", workspaceStoreKey: entryA.StoreKey);
        byKeyAndAgent.Totals.Records.ShouldBe(1);
        byKeyAndAgent.Totals.Agents.Single().Client.ShouldBe("kimi-code");

        UsageAnalytics.Rollup(workspaces, NoStale(), workspaceStoreKey: "missing-key").Totals.Records.ShouldBe(0);
    }

    [Fact]
    public void Rollup_ConsumesTheCoreStaleVerdict_DoesNotProbeTheFilesystem()
    {
        var root = TestDirs.NewTempDir();
        var entry = RegistryEntry("alpha", root);
        var workspaces = new List<(WorkspaceRegistryEntry, IReadOnlyList<QueryTelemetryEntry>)>
        {
            (entry, [Entry(T1, "cli", null)]),
        };

        UsageAnalytics.Rollup(workspaces, StaleKeys(entry.StoreKey)).Workspaces.Single().Stale.ShouldBeTrue();
        UsageAnalytics.Rollup(workspaces, NoStale()).Workspaces.Single().Stale.ShouldBeFalse();
    }

    [Fact]
    public void Rollup_NoMatchingRecords_EmptyRollupWithNullPeriod()
    {
        var root = TestDirs.NewTempDir();
        var workspaces = new List<(WorkspaceRegistryEntry, IReadOnlyList<QueryTelemetryEntry>)>
        {
            (RegistryEntry("alpha", root), [Entry(T1, "cli", null)]),
        };

        var rollup = UsageAnalytics.Rollup(workspaces, NoStale(), agent: "kimi-code");

        rollup.Workspaces.ShouldBeEmpty();
        rollup.Totals.Records.ShouldBe(0);
        rollup.Totals.Agents.ShouldBeEmpty();
        rollup.Period.From.ShouldBeNull();
        rollup.Period.To.ShouldBeNull();
    }

    [Fact]
    public void Rollup_SplitsHookSessionsByVerbatimRecordedTool()
    {
        var root = TestDirs.NewTempDir();
        var claude = Guid.NewGuid();
        var qwen = Guid.NewGuid();
        var workspaces = new List<(WorkspaceRegistryEntry, IReadOnlyList<QueryTelemetryEntry>)>
        {
            (RegistryEntry("alpha", root),
            [
                HookEntry(claude, "Read"),
                HookEntry(qwen, "read_file"),
                HookEntry(qwen, "read_file", minutes: 5),
                Entry(T3, "cli", null),
            ]),
        };

        var sessions = UsageAnalytics.Rollup(workspaces, NoStale()).Totals.Sessions!;

        sessions.HookSessions.ShouldBe(2);
        sessions.McpSessions.ShouldBe(1);
        sessions.HookSessionsByTool!.Select(b => (b.Tool, b.Sessions)).ToArray()
            .ShouldBe([("Read", 1), ("read_file", 1)]);
        sessions.HookSessionsWithoutTool.ShouldBe(0);
        sessions.Ratio.ShouldBe(0.5);
    }

    [Fact]
    public void Rollup_HookRowsWithoutARecordedToolName_JoinOnlyTheUnknownBucket()
    {
        var root = TestDirs.NewTempDir();
        var bare = Guid.NewGuid();
        var fileOnly = Guid.NewGuid();
        var workspaces = new List<(WorkspaceRegistryEntry, IReadOnlyList<QueryTelemetryEntry>)>
        {
            (RegistryEntry("alpha", root),
            [
                new QueryTelemetryEntry(T1, bare, "pre-read", null, 0, false, 0, 0, false, null, "hook", "1.0"),
                HookEntry(fileOnly, null),
                HookEntry(Guid.NewGuid(), "Read"),
            ]),
        };

        var sessions = UsageAnalytics.Rollup(workspaces, NoStale()).Totals.Sessions!;

        sessions.HookSessions.ShouldBe(3);
        sessions.HookSessionsWithoutTool.ShouldBe(2);
        sessions.HookSessionsByTool!.Select(b => b.Tool).ShouldBe(["Read"]);
    }

    [Fact]
    public void Rollup_LegacyHookRows_AreBucketedByTheRecordedNameAndNothingElse()
    {
        var root = TestDirs.NewTempDir();
        var legacy = Guid.NewGuid();
        var workspaces = new List<(WorkspaceRegistryEntry, IReadOnlyList<QueryTelemetryEntry>)>
        {
            (RegistryEntry("alpha", root), [HookEntry(legacy, "Read")]),
        };

        var sessions = UsageAnalytics.Rollup(workspaces, NoStale()).Totals.Sessions!;

        sessions.HookSessionsByTool!.Select(b => (b.Tool, b.Sessions)).ToArray().ShouldBe([("Read", 1)]);
        sessions.HookSessionsWithoutTool.ShouldBe(0);
    }

    [Fact]
    public void Rollup_DoesNotFoldToolNamesThatDifferByCaseOrSpacing()
    {
        var root = TestDirs.NewTempDir();
        var workspaces = new List<(WorkspaceRegistryEntry, IReadOnlyList<QueryTelemetryEntry>)>
        {
            (RegistryEntry("alpha", root),
            [
                HookEntry(Guid.NewGuid(), "Read"),
                HookEntry(Guid.NewGuid(), "READ"),
                HookEntry(Guid.NewGuid(), "Read "),
            ]),
        };

        var sessions = UsageAnalytics.Rollup(workspaces, NoStale()).Totals.Sessions!;

        sessions.HookSessionsByTool!.Select(b => b.Tool).ShouldBe(["READ", "Read", "Read "]);
        sessions.HookSessions.ShouldBe(3);
    }

    private static QueryTelemetryEntry HookEntry(Guid session, string? tool, int minutes = 0) =>
        new(
            T1.AddMinutes(minutes),
            session,
            "pre-read",
            tool is null
                ? new { file = "src/Program.cs" }
                : new { tool, file = "src/Program.cs" },
            0,
            false,
            0,
            0,
            false,
            null,
            "hook",
            "1.0");

    private static WorkspaceRegistryEntry RegistryEntry(string name, string rootPath) =>
        new(name, rootPath, WorkspaceStoreKeys.Create(name, WorkspaceCanonicalPath.Canonicalize(rootPath)), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static IReadOnlySet<string> NoStale() => new HashSet<string>(StringComparer.Ordinal);

    private static IReadOnlySet<string> StaleKeys(params string[] keys) =>
        new HashSet<string>(keys, StringComparer.Ordinal);

    private static QueryTelemetryEntry Entry(DateTime ts, string? client, string? clientVersion) =>
        new(ts, Guid.NewGuid(), "search", null, 10, false, 0, 1, false, null, client, clientVersion);
}
