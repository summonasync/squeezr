using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Squeezr.Cli.Commands;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class UsageCommandTests
{
    [Fact]
    public async Task Json_TwoWorkspaces_RollupBlocksTotalsAndPeriod_CamelCase()
    {
        var home = TestDirs.NewTempDir();
        var registry = new List<WorkspaceRegistryEntry>();
        var tKimi1 = DateTime.UtcNow.AddHours(-1);
        var tKimi2 = DateTime.UtcNow.AddMinutes(-30);
        var tCli = DateTime.UtcNow.AddMinutes(-5);
        var tClaude = DateTime.UtcNow.AddHours(-2);
        var alpha = AddWorkspace(registry, home, "usage-alpha",
        [
            Entry(tKimi1, "kimi-code", "1.0"),
            Entry(tKimi2, "kimi-code", "1.0"),
            Entry(tCli, "cli", null)
        ]);
        var beta = AddWorkspace(registry, home, "usage-beta", [Entry(tClaude, "claude-code", "2.0")]);
        WriteRegistry(home, registry);

        var result = await RunCliAsync(home, "usage", "--json");

        result.ExitCode.ShouldBe(0);
        result.StdErr.ShouldBe(string.Empty);
        using var document = JsonDocument.Parse(result.StdOut);
        var body = document.RootElement;
        var workspaces = body.GetProperty("workspaces");
        workspaces.GetArrayLength().ShouldBe(2);

        var alphaBlock = workspaces.EnumerateArray().Single(w => w.GetProperty("storeKey").GetString() == alpha.StoreKey);
        alphaBlock.GetProperty("rootPath").GetString().ShouldBe(alpha.RootPath);
        alphaBlock.GetProperty("records").GetInt32().ShouldBe(3);
        alphaBlock.GetProperty("stale").GetBoolean().ShouldBeFalse();
        var alphaAgents = alphaBlock.GetProperty("agents");
        var kimi = alphaAgents.EnumerateArray().Single(a => a.GetProperty("client").GetString() == "kimi-code");
        kimi.GetProperty("clientVersion").GetString().ShouldBe("1.0");
        kimi.GetProperty("count").GetInt32().ShouldBe(2);
        kimi.GetProperty("lastSeen").GetDateTime().ShouldBe(tKimi2);
        alphaAgents.EnumerateArray().Single(a => a.GetProperty("client").GetString() == "cli").GetProperty("count").GetInt32().ShouldBe(1);

        var betaBlock = workspaces.EnumerateArray().Single(w => w.GetProperty("storeKey").GetString() == beta.StoreKey);
        betaBlock.GetProperty("records").GetInt32().ShouldBe(1);

        var totals = body.GetProperty("totals");
        totals.GetProperty("records").GetInt32().ShouldBe(4);
        var totalAgents = totals.GetProperty("agents");
        totalAgents.GetArrayLength().ShouldBe(3);
        totalAgents.EnumerateArray().Single(a => a.GetProperty("client").GetString() == "kimi-code").GetProperty("count").GetInt32().ShouldBe(2);
        totalAgents.EnumerateArray().Single(a => a.GetProperty("client").GetString() == "claude-code").GetProperty("clientVersion").GetString().ShouldBe("2.0");

        var period = body.GetProperty("period");
        period.GetProperty("from").GetDateTime().ShouldBe(tClaude);
        period.GetProperty("to").GetDateTime().ShouldBe(tCli);
    }

    [Fact]
    public async Task Human_TwoWorkspaces_PerWorkspaceBlocksAndByAgentTable()
    {
        var home = TestDirs.NewTempDir();
        var registry = new List<WorkspaceRegistryEntry>();
        var alpha = AddWorkspace(registry, home, "usage-human-a",
        [
            Entry(DateTime.UtcNow.AddMinutes(-10), "kimi-code", "1.0"),
            Entry(DateTime.UtcNow, "cli", null)
        ]);
        AddWorkspace(registry, home, "usage-human-b", [Entry(DateTime.UtcNow, "cli", null)]);
        WriteRegistry(home, registry);

        var result = await RunCliAsync(home, "usage");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldContain(alpha.StoreKey);
        result.StdOut.ShouldContain(alpha.RootPath);
        result.StdOut.ShouldContain("2 record(s)");
        result.StdOut.ShouldContain("By agent");
        result.StdOut.ShouldContain("kimi-code");
        result.StdOut.ShouldContain("Period:");
    }

    [Fact]
    public async Task Filter_Agent_IsCaseInsensitiveExact()
    {
        var home = TestDirs.NewTempDir();
        var registry = new List<WorkspaceRegistryEntry>();
        AddWorkspace(registry, home, "usage-agent",
        [
            Entry(DateTime.UtcNow.AddMinutes(-10), "kimi-code", "1.0"),
            Entry(DateTime.UtcNow, "cli", null)
        ]);
        WriteRegistry(home, registry);

        var result = await RunCliAsync(home, "usage", "--agent", "KIMI-CODE", "--json");

        result.ExitCode.ShouldBe(0);
        using var document = JsonDocument.Parse(result.StdOut);
        var body = document.RootElement;
        body.GetProperty("totals").GetProperty("records").GetInt32().ShouldBe(1);
        var agents = body.GetProperty("totals").GetProperty("agents");
        agents.GetArrayLength().ShouldBe(1);
        agents[0].GetProperty("client").GetString().ShouldBe("kimi-code");
    }

    [Fact]
    public async Task Filter_Workspace_ByStoreKey()
    {
        var home = TestDirs.NewTempDir();
        var registry = new List<WorkspaceRegistryEntry>();
        var alpha = AddWorkspace(registry, home, "usage-ws-a", [Entry(DateTime.UtcNow, "cli", null)]);
        var beta = AddWorkspace(registry, home, "usage-ws-b", [Entry(DateTime.UtcNow, "cli", null)]);
        WriteRegistry(home, registry);

        var result = await RunCliAsync(home, "usage", "--workspace", beta.StoreKey, "--json");

        result.ExitCode.ShouldBe(0);
        using var document = JsonDocument.Parse(result.StdOut);
        var workspaces = document.RootElement.GetProperty("workspaces");
        workspaces.GetArrayLength().ShouldBe(1);
        workspaces[0].GetProperty("storeKey").GetString().ShouldBe(beta.StoreKey);
        workspaces[0].GetProperty("storeKey").GetString().ShouldNotBe(alpha.StoreKey);
    }

    [Fact]
    public async Task Filter_Since7d_DropsOlderRecords()
    {
        var home = TestDirs.NewTempDir();
        var registry = new List<WorkspaceRegistryEntry>();
        AddWorkspace(registry, home, "usage-since",
        [
            Entry(DateTime.UtcNow.AddDays(-30), "cli", null),
            Entry(DateTime.UtcNow.AddHours(-2), "cli", null)
        ]);
        WriteRegistry(home, registry);

        var result = await RunCliAsync(home, "usage", "--since", "7d", "--json");

        result.ExitCode.ShouldBe(0);
        using var document = JsonDocument.Parse(result.StdOut);
        var body = document.RootElement;
        body.GetProperty("totals").GetProperty("records").GetInt32().ShouldBe(1);
        body.GetProperty("workspaces")[0].GetProperty("records").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Filters_AgentAndSince_CombineWithAnd()
    {
        var home = TestDirs.NewTempDir();
        var registry = new List<WorkspaceRegistryEntry>();
        AddWorkspace(registry, home, "usage-and",
        [
            Entry(DateTime.UtcNow.AddDays(-30), "cli", null),
            Entry(DateTime.UtcNow.AddHours(-2), "cli", null),
            Entry(DateTime.UtcNow.AddMinutes(-5), "kimi-code", "1.0")
        ]);
        WriteRegistry(home, registry);

        var result = await RunCliAsync(home, "usage", "--agent", "CLI", "--since", "7d", "--json");

        result.ExitCode.ShouldBe(0);
        using var document = JsonDocument.Parse(result.StdOut);
        var body = document.RootElement;
        body.GetProperty("totals").GetProperty("records").GetInt32().ShouldBe(1);
        var agents = body.GetProperty("totals").GetProperty("agents");
        agents.GetArrayLength().ShouldBe(1);
        agents[0].GetProperty("client").GetString().ShouldBe("cli");
    }

    [Theory]
    [InlineData("неделю")]
    [InlineData("7x")]
    [InlineData("m")]
    [InlineData("999999999d")]
    [InlineData("999999999h")]
    [InlineData("10000000d")]
    public async Task Since_InvalidPeriod_ReportsErrorContract(string since)
    {
        var home = TestDirs.NewTempDir();

        var result = await RunCliAsync(home, "usage", "--since", since, "--json");

        result.ExitCode.ShouldBe(1);
        result.StdOut.ShouldBe(string.Empty);
        using var document = JsonDocument.Parse(result.StdErr.Trim());
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("invalid_usage");
        error.GetProperty("message").GetString()!.ShouldContain("--since");
    }

    [Fact]
    public async Task Since_EmptyString_ReportsErrorContract()
    {
        var home = TestDirs.NewTempDir();

        var result = await RunCliAsync(home, "usage", "--since", "\"\"", "--json");

        result.ExitCode.ShouldBe(1);
        result.StdOut.ShouldBe(string.Empty);
        using var document = JsonDocument.Parse(result.StdErr.Trim());
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("invalid_usage");
        error.GetProperty("message").GetString()!.ShouldContain("--since");
    }

    [Fact]
    public async Task Since_InvalidPeriod_HumanOutput_PrintsErrorWithoutJson()
    {
        var home = TestDirs.NewTempDir();

        var result = await RunCliAsync(home, "usage", "--since", "7x");

        result.ExitCode.ShouldBe(1);
        result.StdOut.ShouldBe(string.Empty);
        result.StdErr.ShouldContain("error:");
        result.StdErr.ShouldContain("--since");
    }

    [Fact]
    public async Task StaleRootPath_MarkedStale_CommandSucceeds()
    {
        var home = TestDirs.NewTempDir();
        var registry = new List<WorkspaceRegistryEntry>();
        var alpha = AddWorkspace(registry, home, "usage-stale", [Entry(DateTime.UtcNow.AddMinutes(-3), "cli", null)]);
        WriteRegistry(home, registry);
        Directory.Delete(alpha.Root, recursive: true);

        var json = await RunCliAsync(home, "usage", "--json");

        json.ExitCode.ShouldBe(0);
        json.StdErr.ShouldBe(string.Empty);
        using var document = JsonDocument.Parse(json.StdOut);
        var block = document.RootElement.GetProperty("workspaces").EnumerateArray()
            .Single(w => w.GetProperty("storeKey").GetString() == alpha.StoreKey);
        block.GetProperty("stale").GetBoolean().ShouldBeTrue();
        block.GetProperty("records").GetInt32().ShouldBe(1);

        var human = await RunCliAsync(home, "usage");

        human.ExitCode.ShouldBe(0);
        human.StdOut.ShouldContain("(stale)");
        human.StdOut.ShouldNotContain("Exception");
    }

    [Fact]
    public async Task EmptyRegistry_HumanFriendlyMessage_EmptyJsonObject()
    {
        var home = TestDirs.NewTempDir();

        var human = await RunCliAsync(home, "usage");

        human.ExitCode.ShouldBe(0);
        human.StdOut.ShouldContain("No usage telemetry yet.");

        var json = await RunCliAsync(home, "usage", "--json");

        json.ExitCode.ShouldBe(0);
        using var document = JsonDocument.Parse(json.StdOut);
        var body = document.RootElement;
        body.GetProperty("workspaces").GetArrayLength().ShouldBe(0);
        body.GetProperty("totals").GetProperty("records").GetInt32().ShouldBe(0);
        body.GetProperty("totals").GetProperty("agents").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task WorkspaceWithoutTelemetry_RollsUpAsEmpty()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        var canonical = WorkspaceCanonicalPath.Canonicalize(root);
        var registry = new List<WorkspaceRegistryEntry>
        {
            new("silent", canonical, WorkspaceStoreKeys.Create("silent", canonical), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        };
        WriteRegistry(home, registry);

        var result = await RunCliAsync(home, "usage");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldContain("No usage telemetry yet.");
    }

    [Fact]
    public async Task Human_MoreThan50kRecords_RetentionHintPresent()
    {
        var home = TestDirs.NewTempDir();
        var registry = new List<WorkspaceRegistryEntry>();
        AddWorkspace(registry, home, "usage-big", [Entry(DateTime.UtcNow, "cli", null)], repeat: 50_001);
        WriteRegistry(home, registry);

        var result = await RunCliAsync(home, "usage");

        result.ExitCode.ShouldBe(0);
        var flattened = Regex.Replace(result.StdOut, @"\s+", " ");
        flattened.ShouldContain("Telemetry holds 50001 records");
        flattened.ShouldContain("squeezr usage --clear --yes");
    }

    [Fact]
    public async Task Watch_WithJson_ReportsErrorContract()
    {
        var home = TestDirs.NewTempDir();

        var result = await RunCliAsync(home, "usage", "--watch", "--json");

        result.ExitCode.ShouldBe(1);
        result.StdOut.ShouldBe(string.Empty);
        using var document = JsonDocument.Parse(result.StdErr.Trim());
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("invalid_usage");
        error.GetProperty("message").GetString()!.ShouldContain("--watch");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    public async Task Watch_InvalidInterval_ReportsErrorContract(string interval)
    {
        var home = TestDirs.NewTempDir();

        var result = await RunCliAsync(home, "usage", "--interval", interval, "--json");

        result.ExitCode.ShouldBe(1);
        result.StdOut.ShouldBe(string.Empty);
        using var document = JsonDocument.Parse(result.StdErr.Trim());
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("invalid_usage");
        error.GetProperty("message").GetString()!.ShouldContain("--interval");
    }

    [Fact]
    public async Task Watch_NonTty_AppliesFiltersAndPrintsSingleSnapshot()
    {
        var home = TestDirs.NewTempDir();
        var registry = new List<WorkspaceRegistryEntry>();
        AddWorkspace(registry, home, "usage-watch",
        [
            Entry(DateTime.UtcNow.AddMinutes(-10), "kimi-code", "1.0"),
            Entry(DateTime.UtcNow, "claude-code", "2.0")
        ]);
        WriteRegistry(home, registry);

        var result = await RunCliAsync(home, "usage", "--watch", "--agent", "KIMI-CODE");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldContain("kimi-code");
        result.StdOut.ShouldNotContain("claude-code");
        result.StdErr.ShouldContain("warning:");
        result.StdErr.ShouldContain("--watch");
    }

    [Fact]
    public async Task Watch_InteractiveConsole_RendersLiveView_ExitsGracefullyOnCancellation()
    {
        var home = TestDirs.NewTempDir();
        var registry = new List<WorkspaceRegistryEntry>();
        AddWorkspace(registry, home, "usage-watch-live", [Entry(DateTime.UtcNow.AddMinutes(-10), "kimi-code", "1.0")]);
        WriteRegistry(home, registry);

        var previousConsole = AnsiConsole.Console;
        var previousHome = Environment.GetEnvironmentVariable("SQUEEZR_HOME");
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        AnsiConsole.Console = console;
        Environment.SetEnvironmentVariable("SQUEEZR_HOME", home);
        SqueezrHome.Reset();
        try
        {
            var app = new CommandApp();
            app.Configure(config => config.AddCommand<UsageCommand>("usage"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            var code = await app.RunAsync(["usage", "--watch"], cts.Token);

            code.ShouldBe(0);
            console.Output.ShouldContain("By agent");
            console.Output.ShouldContain("kimi-code");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SQUEEZR_HOME", previousHome);
            SqueezrHome.Reset();
            AnsiConsole.Console = previousConsole;
        }
    }

    private sealed record FixtureWorkspace(string Root, string RootPath, string StoreKey);

    private static FixtureWorkspace AddWorkspace(
        List<WorkspaceRegistryEntry> registry, string home, string name,
        IReadOnlyList<QueryTelemetryEntry> entries, int repeat = 1)
    {
        var root = TestDirs.NewTempDir();
        var canonical = WorkspaceCanonicalPath.Canonicalize(root);
        var storeKey = WorkspaceStoreKeys.Create(name, canonical);
        var storeDir = Path.Combine(home, SqueezrPaths.WorkspacesDirName, storeKey);
        Directory.CreateDirectory(storeDir);
        var lines = Enumerable.Repeat(entries.Select(e => JsonSerializer.Serialize(e)), repeat)
            .SelectMany(serialized => serialized);
        File.WriteAllLines(Path.Combine(storeDir, SqueezrPaths.QueriesLogFileName), lines);
        registry.Add(new WorkspaceRegistryEntry(name, canonical, storeKey, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        return new FixtureWorkspace(root, canonical, storeKey);
    }

    private static void WriteRegistry(string home, IReadOnlyList<WorkspaceRegistryEntry> registry) =>
        File.WriteAllText(
            Path.Combine(home, SqueezrPaths.RegistryFileName),
            JsonSerializer.Serialize(registry, SqueezrJson.FileOptions));

    private static QueryTelemetryEntry Entry(DateTime ts, string? client, string? clientVersion) =>
        new(ts, Guid.NewGuid(), "search", null, 10, false, 0, 1, false, null, client, clientVersion);

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string squeezrHome, params string[] args) =>
        CliTestProcess.RunWithHomeAsync(squeezrHome, TestDirs.NewTempDir(), args);
}
