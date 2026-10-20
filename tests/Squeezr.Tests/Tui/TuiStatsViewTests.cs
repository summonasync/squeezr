using Shouldly;
using Spectre.Console.Testing;
using Squeezr.Cli.Tui;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing;

namespace Squeezr.Tests.Tui;

public class TuiStatsViewTests
{
    [Fact]
    public async Task Render_SeededTelemetryWithClientsAndLegacy_ShowsAllSections()
    {
        var workspace = NewWorkspace();
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        var now = DateTime.UtcNow;
        QueryTelemetry.Append(workspace, new QueryTelemetryEntry(now.AddMinutes(-30), sessionA, "search", null, 100, false, 2, 40, true, 10, "kimi-code", "1.0"));
        QueryTelemetry.Append(workspace, new QueryTelemetryEntry(now.AddMinutes(-20), sessionA, "expand", null, 300, false, 1, 20, false, 10, "kimi-code", "1.0"));
        QueryTelemetry.Append(workspace, new QueryTelemetryEntry(now.AddMinutes(-10), sessionB, "search", null, 50, true, 0, 5, false, 10, "claude-code", "2.0"));
        QueryTelemetry.Append(workspace, new QueryTelemetryEntry(now.AddMinutes(-5), sessionB, "map", null, 80, false, 3, 15, false, 10, null, null));
        var console = TuiTestHarness.InteractiveConsole();

        await TuiStatsView.RenderAsync(console, workspace);

        console.Output.ShouldContain("Savings:");
        console.Output.ShouldContain("Cache hit-rate:");
        console.Output.ShouldContain("By agent");
        console.Output.ShouldContain("By session");
        console.Output.ShouldContain("Period:");
        console.Output.ShouldContain("4 records");
        console.Output.ShouldContain("kimi-code");
        console.Output.ShouldContain("claude-code");
        console.Output.ShouldContain("unknown");
    }

    [Fact]
    public async Task Render_EmptyTelemetry_PrintsEmptyState()
    {
        var workspace = NewWorkspace();
        var console = TuiTestHarness.InteractiveConsole();

        await TuiStatsView.RenderAsync(console, workspace);

        console.Output.ShouldContain("No query telemetry yet.");
    }

    [Fact]
    public async Task Render_UninitializedWorkspace_PrintsInitHint()
    {
        var console = TuiTestHarness.InteractiveConsole();

        await TuiStatsView.RenderAsync(console, TestDirs.NewTempDir());

        console.Output.ShouldContain("Workspace not initialized");
    }

    [Fact]
    public async Task Render_DegradedGuard_ShowsTheProducerWarningLine()
    {
        var workspace = NewWorkspace();
        QueryTelemetry.Append(workspace, new QueryTelemetryEntry(
            DateTime.UtcNow, Guid.NewGuid(), "search", null, 10, false, 0, 1, false, null, "cli", null));
        SecretGuardFixture.SetGuardExtra(workspace, "**/notes.txt", "**/notes.txt/");
        var console = WideInteractiveConsole();

        await TuiStatsView.RenderAsync(console, workspace);

        console.Output.ShouldContain("warning: could not compile 1 of the user secret-guard masks");
        console.Output.ShouldContain("directory-only form names no file to protect");
        console.Output.ShouldContain("Savings:");
    }

    [Fact]
    public async Task Render_CleanGuard_RendersNoWarningLine()
    {
        var workspace = NewWorkspace();
        QueryTelemetry.Append(workspace, new QueryTelemetryEntry(
            DateTime.UtcNow, Guid.NewGuid(), "search", null, 10, false, 0, 1, false, null, "cli", null));
        SecretGuardFixture.SetGuardExtra(workspace, "**/notes.txt");
        var console = WideInteractiveConsole();

        await TuiStatsView.RenderAsync(console, workspace);

        console.Output.ShouldNotContain("warning:");
        console.Output.ShouldContain("Savings:");
    }

    [Fact]
    public async Task MainMenu_TokenStatsEntry_OpensStatsScreenAndExits()
    {
        var workspace = NewWorkspace();
        QueryTelemetry.Append(workspace, new QueryTelemetryEntry(
            DateTime.UtcNow, Guid.NewGuid(), "search", null, 10, false, 0, 1, false, null, "cli", null));
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 7);
        TuiTestHarness.PushMenuChoice(console, 9);

        var code = await TuiApp.RunAsync(console, workspace);

        code.ShouldBe(0);
        console.Output.ShouldContain("Token stats");
        console.Output.ShouldContain("Savings:");
    }

    // the producer sentence is longer than the default 80-column test console, and a wrapped warning would
    // make the substring assertions here about line breaks instead of about the guard
    private static TestConsole WideInteractiveConsole()
    {
        var console = TuiTestHarness.InteractiveConsole();
        console.Width(240);
        return console;
    }

    private static string NewWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        new WorkspaceManager(workspace).Init("stats-tui");
        return workspace;
    }
}
