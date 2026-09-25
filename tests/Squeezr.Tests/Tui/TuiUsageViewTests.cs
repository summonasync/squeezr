using Shouldly;
using Spectre.Console.Testing;
using Squeezr.Cli.Tui;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Tui;

[Collection("SqueezrHomeScope")]
public sealed class TuiUsageViewTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public TuiUsageViewTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = TestDirs.NewTempDir();
        _createdDirs.Add(_home);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
    }

    [Fact]
    public async Task Render_TwoWorkspacesWithSeededTelemetry_ShowsBothStoreKeysAndAgentTotals()
    {
        var alpha = NewWorkspace("usage-alpha");
        var beta = NewWorkspace("usage-beta");
        var now = DateTime.UtcNow;
        QueryTelemetry.Append(alpha, new QueryTelemetryEntry(now.AddMinutes(-10), Guid.NewGuid(), "search", null, 100, false, 2, 40, true, 10, "kimi-code", "1.0"));
        QueryTelemetry.Append(alpha, new QueryTelemetryEntry(now.AddMinutes(-5), Guid.NewGuid(), "expand", null, 300, false, 1, 20, false, 10, "kimi-code", "1.0"));
        QueryTelemetry.Append(beta, new QueryTelemetryEntry(now.AddMinutes(-2), Guid.NewGuid(), "search", null, 80, false, 3, 15, false, 10, "claude-code", "2.0"));
        var alphaKey = WorkspaceStoreKeys.Create("usage-alpha", WorkspaceCanonicalPath.Canonicalize(alpha));
        var betaKey = WorkspaceStoreKeys.Create("usage-beta", WorkspaceCanonicalPath.Canonicalize(beta));
        var console = TuiTestHarness.InteractiveConsole();

        await TuiUsageView.RenderAsync(console, alpha);

        console.Output.ShouldContain(alphaKey);
        console.Output.ShouldContain(betaKey);
        console.Output.ShouldContain("By agent");
        console.Output.ShouldContain("kimi-code");
        console.Output.ShouldContain("claude-code");
        console.Output.ShouldContain("3 records");
    }

    [Fact]
    public async Task Render_EmptyRegistry_PrintsEmptyState()
    {
        var workspace = NewWorkspace("usage-empty");
        var console = TuiTestHarness.InteractiveConsole();

        await TuiUsageView.RenderAsync(console, workspace);

        console.Output.ShouldContain("No usage telemetry yet.");
    }

    [Fact]
    public async Task Render_StaleRootPath_ShowsStaleMarkerWithoutException()
    {
        var alive = NewWorkspace("usage-alive");
        var stale = NewWorkspace("usage-stale");
        QueryTelemetry.Append(stale, new QueryTelemetryEntry(DateTime.UtcNow, Guid.NewGuid(), "search", null, 50, false, 1, 10, false, 10, "cli", null));
        var staleKey = WorkspaceStoreKeys.Create("usage-stale", WorkspaceCanonicalPath.Canonicalize(stale));
        Directory.Delete(stale, recursive: true);
        var console = TuiTestHarness.InteractiveConsole();

        await TuiUsageView.RenderAsync(console, alive);

        console.Output.ShouldContain(staleKey);
        console.Output.ShouldContain("(stale)");
    }

    [Fact]
    public async Task MainMenu_UsageAnalyticsEntry_OpensUsageScreenAndExits()
    {
        var workspace = NewWorkspace("usage-menu");
        QueryTelemetry.Append(workspace, new QueryTelemetryEntry(DateTime.UtcNow, Guid.NewGuid(), "search", null, 10, false, 0, 1, false, null, "cli", null));
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 8);
        TuiTestHarness.PushMenuChoice(console, 9);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("Usage analytics");
        console.Output.ShouldContain("By agent");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousEnv);
        SqueezrHome.Reset();
        foreach (var dir in _createdDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private string NewWorkspace(string name)
    {
        var workspace = TestDirs.NewTempDir();
        _createdDirs.Add(workspace);
        new WorkspaceManager(workspace).Init(name);
        return workspace;
    }
}
