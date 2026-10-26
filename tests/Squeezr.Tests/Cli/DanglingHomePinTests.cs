using System.Text.Json;
using Shouldly;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class DanglingHomePinTests
{
    private static readonly string DanglingHome =
        Path.Combine(Path.GetTempPath(), "squeezr-tests", "missing-home-082");

    [Fact]
    public async Task WorkspaceList_DanglingHome_FailsWithParseableEnvelope()
    {
        var run = await CliTestProcess.RunWithProfileHomeAsync(
            DanglingHome, "", TestDirs.NewTempDir(), "workspace", "list", "--json");

        run.ExitCode.ShouldBe(1);
        run.StdOut.ShouldNotContain("Error:");
        using var doc = JsonDocument.Parse(run.StdErr.Trim());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("home_unavailable");
    }

    [Fact]
    public async Task Fleet_DanglingHome_FailsWithParseableEnvelope()
    {
        var run = await CliTestProcess.RunWithProfileHomeAsync(
            DanglingHome, "", TestDirs.NewTempDir(), "fleet", "--json");

        run.ExitCode.ShouldBe(1);
        run.StdOut.ShouldNotContain("Error:");
        using var doc = JsonDocument.Parse(run.StdErr.Trim());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("home_unavailable");
    }

    [Fact]
    public async Task WorkspaceRepair_DanglingHome_FailsWithParseableEnvelope()
    {
        var run = await CliTestProcess.RunWithProfileHomeAsync(
            DanglingHome, "", TestDirs.NewTempDir(), "workspace", "repair", "--dry-run", "--json");

        run.ExitCode.ShouldBe(1);
        run.StdOut.ShouldNotContain("Error:");
        using var doc = JsonDocument.Parse(run.StdErr.Trim());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("home_unavailable");
    }

    [Fact]
    public async Task Commands_WithUnsetHome_StayGreen()
    {
        var freshHome = TestDirs.NewTempDir();

        var list = await CliTestProcess.RunWithProfileHomeAsync(
            "", freshHome, TestDirs.NewTempDir(), "workspace", "list", "--json");
        var fleet = await CliTestProcess.RunWithProfileHomeAsync(
            "", freshHome, TestDirs.NewTempDir(), "fleet", "--json");
        var repair = await CliTestProcess.RunWithProfileHomeAsync(
            "", freshHome, TestDirs.NewTempDir(), "workspace", "repair", "--dry-run", "--json");

        list.ExitCode.ShouldBe(0);
        fleet.ExitCode.ShouldBe(0);
        repair.ExitCode.ShouldBe(0);
    }
}
