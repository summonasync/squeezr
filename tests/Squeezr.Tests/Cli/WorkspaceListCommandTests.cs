using System.Text.Json;
using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class WorkspaceListCommandTests
{
    [Fact]
    public async Task Json_ListsRegisteredWorkspaces_FromAnyDirectory()
    {
        var first = await NewInitializedWorkspaceAsync("listed-first");
        var second = await NewInitializedWorkspaceAsync("listed-second");
        var outside = TestDirs.NewTempDir();

        var result = await RunCliAsync(outside, "workspace", "list", "--json");

        result.ExitCode.ShouldBe(0);
        var workspaces = result.StdOut.Trim()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

        var firstEntry = workspaces.Single(w => w.GetProperty("name").GetString() == "listed-first");
        firstEntry.GetProperty("rootPath").GetString().ShouldBe(Path.GetFullPath(first));
        firstEntry.GetProperty("stale").GetBoolean().ShouldBeFalse();
        firstEntry.GetProperty("storeKey").GetString().ShouldNotBeNullOrWhiteSpace();

        var secondEntry = workspaces.Single(w => w.GetProperty("name").GetString() == "listed-second");
        secondEntry.GetProperty("rootPath").GetString().ShouldBe(Path.GetFullPath(second));
        secondEntry.GetProperty("stale").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Json_MissingRootPath_IsMarkedStale()
    {
        await NewInitializedWorkspaceAsync("stale-alive");
        var gone = await NewInitializedWorkspaceAsync("stale-gone");
        Directory.Delete(gone, recursive: true);
        var outside = TestDirs.NewTempDir();

        var result = await RunCliAsync(outside, "workspace", "list", "--json");

        result.ExitCode.ShouldBe(0);
        var workspaces = result.StdOut.Trim()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

        workspaces.Single(w => w.GetProperty("name").GetString() == "stale-alive")
            .GetProperty("stale").GetBoolean().ShouldBeFalse();
        var staleEntry = workspaces.Single(w => w.GetProperty("name").GetString() == "stale-gone");
        staleEntry.GetProperty("stale").GetBoolean().ShouldBeTrue();
        staleEntry.GetProperty("rootPath").GetString().ShouldBe(Path.GetFullPath(gone));

        var human = await RunCliAsync(outside, "workspace", "list");
        human.ExitCode.ShouldBe(0);
        human.StdOut.ShouldContain("stale");
        human.StdErr.ShouldContain("stale");
        human.StdErr.ShouldContain("squeezr drop --store-key");
        human.StdErr.ShouldContain(Path.GetFullPath(gone));
    }

    [Fact]
    public async Task Json_RegistryBusy_ReportsBusyCodeInErrorEnvelope()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        (await CliTestProcess.RunWithHomeAsync(home, root, "init", "--json")).ExitCode.ShouldBe(0);

        using (new FileStream(
            Path.Combine(home, SqueezrPaths.RegistryFileName),
            FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await CliTestProcess.RunWithHomeAsync(home, root, "workspace", "list", "--json");

            result.ExitCode.ShouldBe(1);
            var envelopeLine = result.StdErr.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1];
            using var envelope = JsonDocument.Parse(envelopeLine);
            envelope.RootElement.GetProperty("error").GetProperty("code").GetString()
                .ShouldBe("workspace_registry_busy");
        }
    }

    private static async Task<string> NewInitializedWorkspaceAsync(string name)
    {
        var root = TestDirs.NewTempDir();
        var init = await RunCliAsync(root, "init", "--name", name, "--json");
        init.ExitCode.ShouldBe(0);
        return root;
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);
}
