using System.Text.Json;
using Shouldly;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class FleetWatchAdoptionDisplayTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Fleet_Json_AdoptionEvent_SurfacesWhoWhenAndTheDisplacedOwner()
    {
        var name = UniqueName("fleet-adoption-json");
        var root = await NewWorkspaceAsync(name);
        WriteAdoption(SqueezrPaths.StoreDir(root));

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "--json");

        result.ExitCode.ShouldBe(0);
        var entry = SingleEntry(result, name);
        var adoption = entry.GetProperty("adoption");
        adoption.GetProperty("adopterPid").GetInt32().ShouldBe(5200);
        adoption.GetProperty("adopterHost").GetString().ShouldBe(Environment.MachineName);
        adoption.GetProperty("displacedPid").GetInt32().ShouldBe(4242);
        adoption.GetProperty("adoptedAt").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Fleet_Json_NoAdoptionEvent_OmitsTheBlock()
    {
        var name = UniqueName("fleet-adoption-empty");
        await NewWorkspaceAsync(name);

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "--json");

        result.ExitCode.ShouldBe(0);
        SingleEntry(result, name).TryGetProperty("adoption", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Status_Json_AndHuman_ShowTheAdoptionEvent()
    {
        var root = await NewWorkspaceAsync(UniqueName("status-adoption"));
        WriteAdoption(SqueezrPaths.StoreDir(root));

        var json = await RunCliAsync(root, "status", "--json");
        json.ExitCode.ShouldBe(0);
        var report = JsonDocument.Parse(json.StdOut.Trim()).RootElement;
        report.GetProperty("watchAdoption").GetProperty("adopterPid").GetInt32().ShouldBe(5200);
        report.GetProperty("watchAdoption").GetProperty("displacedPid").GetInt32().ShouldBe(4242);

        var human = await RunCliAsync(root, "status");
        human.ExitCode.ShouldBe(0);
        human.StdOut.ShouldContain("Watch claim last adopted by pid 5200");
        human.StdOut.ShouldContain("dead pid 4242");
    }

    private static async Task<string> NewWorkspaceAsync(string name)
    {
        var root = TestDirs.NewTempDir();
        var init = await RunCliAsync(root, "init", "--name", name, "--json");
        init.ExitCode.ShouldBe(0);
        return root;
    }

    private static void WriteAdoption(string storeDir)
    {
        WatchAdoptionEventFile.Write(storeDir, new WatchAdoptionEvent(
            AdopterPid: 5200,
            AdopterHost: Environment.MachineName,
            AdopterKind: DaemonHeartbeat.ServeWatchKind,
            AdoptedAt: new DateTime(2026, 10, 27, 14, 5, 0, DateTimeKind.Utc),
            DisplacedPid: 4242,
            DisplacedHost: Environment.MachineName,
            DisplacedStartedAt: new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc),
            DisplacedLastBeatAt: new DateTime(2026, 10, 27, 13, 59, 0, DateTimeKind.Utc)));
    }

    private static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static JsonElement SingleEntry((int ExitCode, string StdOut, string StdErr) result, string name) =>
        result.StdOut.Trim()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Single(entry => entry.GetProperty("name").GetString() == name);

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);
}
