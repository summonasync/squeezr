using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class StatsCommandTests
{
    [Fact]
    public async Task Stats_AfterSearchAndExpand_ReportsNonZeroSavingsAsSingleJsonObject()
    {
        var root = await NewIndexedCliWorkspaceAsync();
        (await RunCliAsync(root, "search", "alpha", "--json")).ExitCode.ShouldBe(0);
        (await RunCliAsync(root, "expand", "src/Big.cs", "2")).ExitCode.ShouldBe(0);

        var stats = await RunCliAsync(root, "stats", "--json");
        stats.ExitCode.ShouldBe(0);

        using var document = JsonDocument.Parse(stats.StdOut);
        var body = document.RootElement;
        body.ValueKind.ShouldBe(JsonValueKind.Object);
        body.GetProperty("totalRequests").GetInt32().ShouldBe(2);
        body.GetProperty("byTool").ValueKind.ShouldBe(JsonValueKind.Array);
        var savings = body.GetProperty("savings");
        var actual = savings.GetProperty("actual").GetInt64();
        var counterfactual = savings.GetProperty("counterfactual").GetInt64();
        actual.ShouldBeGreaterThan(0);
        counterfactual.ShouldBeGreaterThan(0);
        savings.GetProperty("saved").GetInt64().ShouldBe(counterfactual - actual);
        savings.GetProperty("ratio").GetDouble().ShouldBeGreaterThan(0);
        savings.GetProperty("unknownFiles").GetInt32().ShouldBe(0);
        savings.GetProperty("baseline").GetString().ShouldBe("whole-file (expanded files)");
        savings.GetProperty("baselineApplies").GetBoolean().ShouldBeTrue();
        var cache = body.GetProperty("cache");
        cache.GetProperty("hitRate").GetDouble().ShouldBe(0);
        cache.GetProperty("invalidations").GetInt32().ShouldBe(0);
        cache.TryGetProperty("lastInvalidation", out _).ShouldBeFalse();
        var period = body.GetProperty("period");
        period.GetProperty("records").GetInt32().ShouldBe(2);
        period.GetProperty("from").ValueKind.ShouldNotBe(JsonValueKind.Null);
        period.GetProperty("to").ValueKind.ShouldNotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Stats_Json_IncludesAgentsAndSessionsSections()
    {
        var root = await NewIndexedCliWorkspaceAsync();
        (await RunCliAsync(root, "search", "alpha", "--json")).ExitCode.ShouldBe(0);
        (await RunCliAsync(root, "expand", "src/Big.cs", "2")).ExitCode.ShouldBe(0);

        var stats = await RunCliAsync(root, "stats", "--json");
        stats.ExitCode.ShouldBe(0);

        using var document = JsonDocument.Parse(stats.StdOut);
        var body = document.RootElement;
        var agents = body.GetProperty("agents");
        agents.ValueKind.ShouldBe(JsonValueKind.Array);
        agents.GetArrayLength().ShouldBe(1);
        var agent = agents[0];
        agent.GetProperty("client").GetString().ShouldBe("cli");
        agent.TryGetProperty("clientVersion", out _).ShouldBeFalse();
        agent.GetProperty("count").GetInt32().ShouldBe(2);
        agent.GetProperty("lastSeen").ValueKind.ShouldBe(JsonValueKind.String);
        var sessions = body.GetProperty("sessions");
        sessions.GetArrayLength().ShouldBe(2);
        foreach (var session in sessions.EnumerateArray())
        {
            session.GetProperty("sessionId").GetString()!.Length.ShouldBe(8);
            session.GetProperty("client").GetString().ShouldBe("cli");
            session.GetProperty("count").GetInt32().ShouldBe(1);
            session.GetProperty("lastSeen").ValueKind.ShouldBe(JsonValueKind.String);
            session.GetProperty("topTools").GetArrayLength().ShouldBe(1);
        }

        (await RunCliAsync(root, "stats", "--reset", "--json")).ExitCode.ShouldBe(0);
        var empty = await RunCliAsync(root, "stats", "--json");
        using var emptyDocument = JsonDocument.Parse(empty.StdOut);
        emptyDocument.RootElement.GetProperty("agents").GetArrayLength().ShouldBe(0);
        emptyDocument.RootElement.GetProperty("sessions").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task StatsReset_TruncatesLogAndRespondsWithRemovedCount()
    {
        var root = await NewIndexedCliWorkspaceAsync();
        (await RunCliAsync(root, "search", "alpha", "--json")).ExitCode.ShouldBe(0);
        (await RunCliAsync(root, "expand", "src/Big.cs", "2")).ExitCode.ShouldBe(0);
        QueryTelemetry.Count(root).ShouldBe(2);

        var reset = await RunCliAsync(root, "stats", "--reset", "--json");
        reset.ExitCode.ShouldBe(0);

        using var document = JsonDocument.Parse(reset.StdOut);
        document.RootElement.GetProperty("reset").GetBoolean().ShouldBeTrue();
        document.RootElement.GetProperty("recordsRemoved").GetInt32().ShouldBeGreaterThan(0);
        new FileInfo(SqueezrPaths.QueriesLogPath(root)).Length.ShouldBe(0);

        var human = await RunCliAsync(root, "stats");
        human.ExitCode.ShouldBe(0);
        human.StdOut.ShouldContain("No query telemetry yet.");

        var json = await RunCliAsync(root, "stats", "--json");
        json.ExitCode.ShouldBe(0);
        using var empty = JsonDocument.Parse(json.StdOut);
        empty.RootElement.GetProperty("totalRequests").GetInt32().ShouldBe(0);
        var emptyPeriod = empty.RootElement.GetProperty("period");
        emptyPeriod.GetProperty("records").GetInt32().ShouldBe(0);
        emptyPeriod.TryGetProperty("from", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task StatsReset_HumanOutput_ReportsClearedRecords()
    {
        var root = await NewIndexedCliWorkspaceAsync();
        (await RunCliAsync(root, "search", "alpha", "--json")).ExitCode.ShouldBe(0);

        var reset = await RunCliAsync(root, "stats", "--reset");

        reset.ExitCode.ShouldBe(0);
        reset.StdOut.ShouldContain("Cleared 1 telemetry record(s).");
        QueryTelemetry.Count(root).ShouldBe(0);
    }

    [Fact]
    public async Task Stats_WorkspaceWithoutQueriesLog_ExitsZeroWithoutTrace()
    {
        var root = await NewIndexedCliWorkspaceAsync();

        var stats = await RunCliAsync(root, "stats");

        stats.ExitCode.ShouldBe(0);
        stats.StdOut.ShouldContain("No query telemetry yet.");
        stats.StdErr.ShouldNotContain("Exception");
    }

    [Fact]
    public async Task Stats_UnreadableQueriesLog_ExitsZeroWithEmptyJson()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = await NewIndexedCliWorkspaceAsync();
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        var logPath = SqueezrPaths.QueriesLogPath(root);
        var line = JsonSerializer.Serialize(new QueryTelemetryEntry(
            new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), Guid.NewGuid(), "search", null, 5, false, 0, 1, false, null));
        File.AppendAllText(logPath, line + '\n');
        var mode = File.GetUnixFileMode(logPath);
        File.SetUnixFileMode(logPath, UnixFileMode.None);
        try
        {
            var restricted = false;
            try
            {
                _ = File.ReadAllText(logPath);
            }
            catch (UnauthorizedAccessException)
            {
                restricted = true;
            }

            if (!restricted)
            {
                return;
            }

            var stats = await RunCliAsync(root, "stats", "--json");

            stats.ExitCode.ShouldBe(0);
            stats.StdErr.ShouldNotContain("Exception");
            using var document = JsonDocument.Parse(stats.StdOut);
            document.RootElement.GetProperty("totalRequests").GetInt32().ShouldBe(0);
            document.RootElement.GetProperty("period").GetProperty("records").GetInt32().ShouldBe(0);
        }
        finally
        {
            File.SetUnixFileMode(logPath, mode);
        }
    }

    [Fact]
    public async Task Stats_MoreThan50kRecords_HumanHintPresent_JsonClean()
    {
        var root = await NewIndexedCliWorkspaceAsync();
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        var line = JsonSerializer.Serialize(new QueryTelemetryEntry(
            new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), Guid.NewGuid(), "search", null, 5, false, 0, 1, false, null));
        File.AppendAllLines(SqueezrPaths.QueriesLogPath(root), Enumerable.Repeat(line, 50_001));

        var human = await RunCliAsync(root, "stats");
        human.ExitCode.ShouldBe(0);
        var flattened = Regex.Replace(human.StdOut, @"\s+", " ");
        flattened.ShouldContain("Telemetry holds 50001 records");
        flattened.ShouldContain("run 'squeezr stats --reset' to clear");

        var json = await RunCliAsync(root, "stats", "--json");
        json.ExitCode.ShouldBe(0);
        json.StdOut.ShouldNotContain("to clear");
        using var document = JsonDocument.Parse(json.StdOut);
        document.RootElement.GetProperty("period").GetProperty("records").GetInt32().ShouldBe(50_001);
    }

    [Fact]
    public async Task Stats_DegradedGuard_HumanWarnsOnStderr_JsonCarriesTheSameSentence()
    {
        var root = await NewIndexedCliWorkspaceAsync();
        (await RunCliAsync(root, "expand", "src/Big.cs", "2")).ExitCode.ShouldBe(0);
        SecretGuardFixture.SetGuardExtra(root, "**/notes.txt");
        SecretGuardFixture.CorruptConfig(root);

        var human = await RunCliAsync(root, "stats");
        human.ExitCode.ShouldBe(0);
        human.StdErr.ShouldContain(
            "warning: could not read indexing config (");
        human.StdErr.ShouldContain("user secret-guard masks are inactive");
        human.StdOut.ShouldNotContain("could not read indexing config");

        var json = await RunCliAsync(root, "stats", "--json");
        json.ExitCode.ShouldBe(0);
        json.StdErr.ShouldNotContain("user secret-guard masks are inactive");
        using var document = JsonDocument.Parse(json.StdOut);
        var warnings = document.RootElement.GetProperty("guardWarnings");
        warnings.GetArrayLength().ShouldBe(1);
        warnings[0].GetString().ShouldNotBeNull().ShouldMatch(
            @"^could not read indexing config \(.*\); user secret-guard masks are inactive$");
    }

    [Fact]
    public async Task Stats_CleanGuard_HumanStaysSilent_JsonShapeIsUnchanged()
    {
        var root = await NewIndexedCliWorkspaceAsync();
        (await RunCliAsync(root, "expand", "src/Big.cs", "2")).ExitCode.ShouldBe(0);
        SecretGuardFixture.SetGuardExtra(root, "**/notes.txt");

        var human = await RunCliAsync(root, "stats");
        human.ExitCode.ShouldBe(0);
        human.StdErr.ShouldNotContain("warning:");
        human.StdOut.ShouldNotContain("secret guard");

        var json = await RunCliAsync(root, "stats", "--json");
        json.ExitCode.ShouldBe(0);
        using var document = JsonDocument.Parse(json.StdOut);
        var body = document.RootElement;
        body.TryGetProperty("guardWarnings", out _).ShouldBeFalse();
        string.Join(",", body.EnumerateObject().Select(p => p.Name))
            .ShouldBe("totalRequests,byTool,savings,cache,period,agents,sessions");
    }

    [Fact]
    public async Task Stats_DeadExtraMask_HumanWarnsOnStderr_JsonCarriesTheSameLine()
    {
        var root = await NewIndexedCliWorkspaceAsync();
        (await RunCliAsync(root, "expand", "src/Big.cs", "2")).ExitCode.ShouldBe(0);
        SecretGuardFixture.SetGuardExtra(root, "**/notes.txt", "**/notes.txt/");

        var human = await RunCliAsync(root, "stats");
        human.ExitCode.ShouldBe(0);
        human.StdErr.ShouldContain("warning: could not compile 1 of the user secret-guard masks");
        human.StdErr.ShouldContain("directory-only form names no file to protect");
        human.StdOut.ShouldNotContain("could not compile");

        var json = await RunCliAsync(root, "stats", "--json");
        json.ExitCode.ShouldBe(0);
        json.StdErr.ShouldNotContain("could not compile");
        using var document = JsonDocument.Parse(json.StdOut);
        var warnings = document.RootElement.GetProperty("guardWarnings");
        warnings.GetArrayLength().ShouldBe(1);
        warnings[0].GetString().ShouldNotBeNull().ShouldMatch(
            @"^could not compile 1 of the user secret-guard masks in indexing\.secretGuardExtra \(directory-only form names no file to protect\); the affected masks protect nothing$");
    }

    [Fact]
    public async Task Stats_UnreadableManifest_HumanWarnsAboutTheBlindFold()
    {
        var root = await NewIndexedCliWorkspaceAsync();
        (await RunCliAsync(root, "expand", "src/Big.cs", "2")).ExitCode.ShouldBe(0);
        SecretGuardFixture.SetGuardExtra(root, "**/notes.txt");
        File.WriteAllText(SqueezrPaths.ManifestPath(root), "null");

        var human = await RunCliAsync(root, "stats");
        human.ExitCode.ShouldBe(0);
        human.StdErr.ShouldContain("warning: could not read workspace manifest (");
        human.StdErr.ShouldContain("qualified and absolute path readings are not folded for the secret guard");

        var json = await RunCliAsync(root, "stats", "--json");
        json.ExitCode.ShouldBe(0);
        using var document = JsonDocument.Parse(json.StdOut);
        document.RootElement.GetProperty("guardWarnings")[0].GetString().ShouldNotBeNull().ShouldMatch(
            @"^could not read workspace manifest \(.*\); qualified and absolute path readings are not folded for the secret guard$");
    }

    [Fact]
    public async Task StatsReset_BusyTelemetryLock_RefusesWithAnErrorEnvelopeAndClearsNothing()
    {
        var root = await NewIndexedCliWorkspaceAsync();
        (await RunCliAsync(root, "search", "alpha", "--json")).ExitCode.ShouldBe(0);

        using (var held = TelemetryWriteLock.TryAcquire(SqueezrPaths.StoreDir(root)))
        {
            held.ShouldNotBeNull();
            var reset = await RunCliAsync(root, "stats", "--reset", "--json");
            reset.ExitCode.ShouldBe(1);
            reset.StdOut.ShouldBeEmpty();
            using var error = JsonDocument.Parse(reset.StdErr.Trim());
            var envelope = error.RootElement.GetProperty("error");
            envelope.GetProperty("code").GetString().ShouldBe(WorkspaceException.DefaultCode);
            envelope.GetProperty("message").GetString()!.ShouldContain("refused");
        }

        QueryTelemetry.Count(root).ShouldBe(1);
        var retry = await RunCliAsync(root, "stats", "--reset", "--json");
        retry.ExitCode.ShouldBe(0);
        QueryTelemetry.Count(root).ShouldBe(0);
    }

    private static async Task<string> NewIndexedCliWorkspaceAsync()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        WriteBigFile(root, "src/Big.cs");
        (await RunCliAsync(root, "add", ".", "--name", "dot", "--json")).ExitCode.ShouldBe(0);
        (await RunCliAsync(root, "index", "--json")).ExitCode.ShouldBe(0);
        return root;
    }

    private static void WriteBigFile(string root, string relativePath)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var padding = new string('x', 70);
        File.WriteAllLines(fullPath, Enumerable.Range(1, 40).Select(i => $"var alpha{i} = \"{padding}\";"));
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);
}
