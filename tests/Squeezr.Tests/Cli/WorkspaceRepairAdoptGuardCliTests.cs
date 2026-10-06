using System.Text.Json;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

[Collection("CliProcessGlobals")]
public sealed class WorkspaceRepairAdoptGuardCliTests
{
    private const string LeakedMarker = "leaked";

    [Theory]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("workspaces/../../outside")]
    [InlineData("a/b")]
    [InlineData("..:stream")]
    public async Task Repair_AdoptWithKeyThatLeavesWorkspaces_RefusesBeforeReadingAnyStore(string storeKey)
    {
        var fixture = NewFixture();
        try
        {
            var victimBytes = File.ReadAllBytes(PlannedVictimManifest(fixture.Home));
            var outsideBytes = File.ReadAllBytes(PlannedVictimManifest(fixture.Outside));

            var repair = await RunAsync(fixture.Home, "workspace", "repair",
                "--store-key", storeKey, "--root", Path.Combine(fixture.Root, "any-root"));

            repair.ExitCode.ShouldBe(1);
            repair.StdErr.ShouldContain("not a single path segment");
            repair.StdErr.ShouldNotContain("already records root path");
            repair.StdErr.ShouldNotContain(LeakedMarker);
            repair.StdErr.ShouldNotContain("at Squeezr");
            repair.StdOut.ShouldBeEmpty();
            File.ReadAllBytes(PlannedVictimManifest(fixture.Home)).ShouldBe(victimBytes);
            File.ReadAllBytes(PlannedVictimManifest(fixture.Outside)).ShouldBe(outsideBytes);
            ReadRegistry(fixture.Home).ShouldBeEmpty();
            Directory.GetFileSystemEntries(WorkspacesDir(fixture.Home)).ShouldBeEmpty();
            File.ReadAllText(Path.Combine(fixture.Root, "canary.txt")).ShouldBe("keep me");
        }
        finally
        {
            Clean(fixture);
        }
    }

    [Fact]
    public async Task Repair_AdoptWithTraversalStoreKey_Json_ReportsWorkspaceErrorEnvelope()
    {
        var fixture = NewFixture();
        try
        {
            var repair = await RunAsync(fixture.Home, "workspace", "repair",
                "--store-key", "..", "--root", Path.Combine(fixture.Root, "any-root"), "--json");

            repair.ExitCode.ShouldBe(1);
            repair.StdOut.ShouldBeEmpty();
            using var error = JsonDocument.Parse(Envelope(repair.StdErr));
            var envelope = error.RootElement.GetProperty("error");
            envelope.GetProperty("code").GetString().ShouldBe(WorkspaceException.DefaultCode);
            envelope.GetProperty("message").GetString()!.ShouldContain("not a single path segment");
            Directory.Exists(fixture.Home).ShouldBeTrue();
            ReadRegistry(fixture.Home).ShouldBeEmpty();
        }
        finally
        {
            Clean(fixture);
        }
    }

    [Theory]
    [InlineData("проект")]
    [InlineData("café")]
    public async Task Repair_AdoptWithNonAsciiStoreKey_StillAdopts(string name)
    {
        var fixture = NewFixture();
        var root = Path.Combine(fixture.Root, $"target-{name}");
        Directory.CreateDirectory(root);
        var storeKey = WorkspaceStoreKeys.Create(name, Canonical(root));
        storeKey.ShouldStartWith(name);
        try
        {
            var storeDir = Path.Combine(WorkspacesDir(fixture.Home), storeKey);
            Directory.CreateDirectory(storeDir);
            File.WriteAllText(
                Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName),
                JsonSerializer.Serialize(new WorkspaceManifest { Name = name }, SqueezrJson.FileOptions));

            var repair = await RunAsync(fixture.Home, "workspace", "repair",
                "--store-key", storeKey, "--root", root, "--json");

            repair.ExitCode.ShouldBe(0);
            using var adopted = JsonDocument.Parse(repair.StdOut.Trim());
            adopted.RootElement.GetProperty("storeKey").GetString().ShouldBe(storeKey);
            adopted.RootElement.GetProperty("name").GetString().ShouldBe(name);
            adopted.RootElement.GetProperty("rootPath").GetString().ShouldBe(Canonical(root));
            ReadRegistry(fixture.Home).ShouldHaveSingleItem().StoreKey.ShouldBe(storeKey);
        }
        finally
        {
            Clean(fixture, root);
        }
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string home, params string[] args) =>
        CliTestProcess.RunWithHomeAsync(home, TestDirs.NewTempDir(), args);

    private static Fixture NewFixture()
    {
        var root = TestDirs.NewTempDir();
        var home = Path.Combine(root, "home");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(WorkspacesDir(home));
        Directory.CreateDirectory(outside);
        File.WriteAllText(PlannedVictimManifest(home), Manifest("home-victim", Canonical(Path.Combine(root, LeakedMarker + "-home"))));
        File.WriteAllText(PlannedVictimManifest(outside), Manifest("outside-victim", Canonical(Path.Combine(root, LeakedMarker + "-outside"))));
        File.WriteAllText(Path.Combine(home, SqueezrPaths.RegistryFileName), "[]");
        File.WriteAllText(Path.Combine(root, "canary.txt"), "keep me");
        return new Fixture(root, home, outside);
    }

    private static string Manifest(string name, string rootPath) =>
        JsonSerializer.Serialize(new WorkspaceManifest { Name = name, RootPath = rootPath }, SqueezrJson.FileOptions);

    private static string PlannedVictimManifest(string directory) =>
        Path.Combine(directory, SqueezrPaths.StoreManifestFileName);

    private static string WorkspacesDir(string home) => Path.Combine(home, SqueezrPaths.WorkspacesDirName);

    private static string Canonical(string path) => WorkspaceCanonicalPath.Canonicalize(path);

    private static List<WorkspaceRegistryEntry> ReadRegistry(string home) =>
        File.Exists(Path.Combine(home, SqueezrPaths.RegistryFileName))
            ? JsonSerializer.Deserialize<List<WorkspaceRegistryEntry>>(
                File.ReadAllText(Path.Combine(home, SqueezrPaths.RegistryFileName)), SqueezrJson.FileOptions) ?? []
            : [];

    private static string Envelope(string stdErr) =>
        stdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Last(line => line.TrimStart().StartsWith('{'));

    private static void Clean(Fixture fixture, params string[] extraDirs)
    {
        foreach (var dir in new[] { fixture.Root }.Concat(extraDirs))
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private sealed record Fixture(string Root, string Home, string Outside);
}
