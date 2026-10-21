using System.Text.Json;
using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class DropByStoreKeyTests
{
    [Fact]
    public async Task Drop_ByStoreKey_RemovesStaleEntryAndStoreFromAnyDirectory()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--name", "stale-target", "--json")).ExitCode.ShouldBe(0);
        var storeKey = await ReadStoreKeyAsync("stale-target");
        var storeDir = Path.Combine(SqueezrHome.Resolve(), "workspaces", storeKey);
        Directory.Exists(storeDir).ShouldBeTrue();
        Directory.Delete(root, recursive: true);
        var outside = TestDirs.NewTempDir();

        var drop = await RunCliAsync(outside, "drop", "--store-key", storeKey, "--json");

        drop.ExitCode.ShouldBe(0);
        using (var doc = JsonDocument.Parse(drop.StdOut.Trim()))
        {
            doc.RootElement.GetProperty("removed").GetArrayLength().ShouldBe(1);
            doc.RootElement.GetProperty("removed")[0].GetString().ShouldBe(storeDir);
            doc.RootElement.GetProperty("hint").GetString().ShouldBe("squeezr services down");
        }

        Directory.Exists(storeDir).ShouldBeFalse();
        var listing = await RunCliAsync(outside, "workspace", "list", "--json");
        listing.StdOut.ShouldNotContain("stale-target");
    }

    [Fact]
    public async Task Drop_ByUnknownStoreKey_ReportsErrorWithoutResolution()
    {
        var outside = TestDirs.NewTempDir();

        var drop = await RunCliAsync(outside, "drop", "--store-key", "no-such-key");

        drop.ExitCode.ShouldBe(1);
        drop.StdErr.ShouldContain("no-such-key");
        drop.StdErr.ShouldNotContain("not initialized");
    }

    private static async Task<string> ReadStoreKeyAsync(string name)
    {
        var outside = TestDirs.NewTempDir();
        var listing = await RunCliAsync(outside, "workspace", "list", "--json");
        var entry = listing.StdOut.Trim()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Single(element => element.GetProperty("name").GetString() == name);
        return entry.GetProperty("storeKey").GetString()!;
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);
}
