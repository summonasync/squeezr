using System.Runtime.Versioning;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

// One removal has two phases: the store dir and the registry row. When they disagree the report has to
// say which one moved — a store that disappeared while its row stayed is not "nothing happened", and a
// refusal that deleted nothing is not "removed" either.
[Collection("CliProcessGlobals")]
public sealed class WorkspaceStoreRemovalPhaseTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private static readonly UnixFileMode HomeWritable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly string _workspaces;

    public WorkspaceStoreRemovalPhaseTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = TestDirs.NewTempDir();
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
        _workspaces = Path.Combine(_home, SqueezrPaths.WorkspacesDirName);
        Directory.CreateDirectory(_workspaces);
        File.WriteAllText(Path.Combine(_home, "canary.txt"), "keep me");
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void DropByStoreKey_RegistryUnwritableAfterStoreRemoval_KeepsThePathAndSaysUnverified()
    {
        const string StoreKey = "gone-1a2b3c4d";
        WriteRegistry(Row("gone", StoreKey));
        var storeDir = WriteStoreDir(StoreKey);
        File.SetUnixFileMode(_home, HomeWritable & ~UnixFileMode.UserWrite);
        try
        {
            var error = Should.Throw<WorkspaceStoreRemovalUnverifiedException>(
                () => WorkspaceStore.DropByStoreKey(StoreKey));

            error.RemovedStoreDirs.ShouldHaveSingleItem().ShouldBe(storeDir);
            error.Message.ShouldContain("removed: unverified");
            error.Message.ShouldContain(storeDir);
            error.Message.ShouldNotContain("Could not remove the store dir");
            Directory.Exists(storeDir).ShouldBeFalse();
            WorkspaceRegistry.FindByStoreKey(StoreKey).ShouldNotBeNull();
        }
        finally
        {
            File.SetUnixFileMode(_home, HomeWritable);
        }

        Directory.Exists(_workspaces).ShouldBeTrue();
        File.ReadAllText(Path.Combine(_home, "canary.txt")).ShouldBe("keep me");
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void DropByStoreKey_RegistryUnwritableWithoutStoreDir_DoesNotClaimAnUnverifiedRemoval()
    {
        const string StoreKey = "row-only-1a2b3c4d";
        WriteRegistry(Row("row-only", StoreKey));
        File.SetUnixFileMode(_home, HomeWritable & ~UnixFileMode.UserWrite);
        try
        {
            var error = Should.Throw<UnauthorizedAccessException>(
                () => WorkspaceStore.DropByStoreKey(StoreKey));

            error.ShouldNotBeOfType<WorkspaceStoreRemovalUnverifiedException>();
            error.Message.ShouldNotContain("removed: unverified");
        }
        finally
        {
            File.SetUnixFileMode(_home, HomeWritable);
        }

        WorkspaceRegistry.FindByStoreKey(StoreKey).ShouldNotBeNull();
        File.ReadAllText(Path.Combine(_home, "canary.txt")).ShouldBe("keep me");
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Drop_RegistryUnwritableAfterStoreRemoval_KeepsEveryPathItDeleted()
    {
        var root = NewProject("cwd-route");
        new WorkspaceManager(root).Init("cwd-route");
        var storeKey = WorkspaceRegistry.FindByRootPath(root)!.StoreKey;
        var storeDir = Path.Combine(_workspaces, storeKey);
        File.SetUnixFileMode(_home, HomeWritable & ~UnixFileMode.UserWrite);
        try
        {
            var error = Should.Throw<WorkspaceStoreRemovalUnverifiedException>(
                () => new WorkspaceManager(root).Drop());

            error.RemovedStoreDirs.ShouldHaveSingleItem().ShouldBe(storeDir);
            error.Message.ShouldContain("removed: unverified");
            Directory.Exists(storeDir).ShouldBeFalse();
            WorkspaceRegistry.FindByRootPath(root).ShouldNotBeNull();
        }
        finally
        {
            File.SetUnixFileMode(_home, HomeWritable);
        }

        Directory.Exists(root).ShouldBeTrue();
        Directory.Exists(storeDir).ShouldBeFalse();
        Directory.Exists(_workspaces).ShouldBeTrue();
        File.ReadAllText(Path.Combine(_home, "canary.txt")).ShouldBe("keep me");
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Drop_CurrentWorkspace_StoreDirDeleteFails_SaysTheStorePhaseWithoutInventingAStoreKey()
    {
        var root = NewProject("cwd-store-phase");
        new WorkspaceManager(root).Init("cwd-store-phase");
        var storeKey = WorkspaceRegistry.FindByRootPath(root)!.StoreKey;
        var storeDir = Path.Combine(_workspaces, storeKey);
        File.SetUnixFileMode(_workspaces, HomeWritable & ~UnixFileMode.UserWrite);
        try
        {
            var error = Should.Throw<WorkspaceStoreRemovalException>(
                () => new WorkspaceManager(root).Drop());

            error.StoreKey.ShouldBeNull();
            error.Message.ShouldContain("Could not remove the workspace store dir");
            error.Message.ShouldContain(storeDir);
            error.Message.ShouldNotContain("store key");
            error.InnerException.ShouldNotBeNull();
            Directory.Exists(storeDir).ShouldBeTrue();
            WorkspaceRegistry.FindByRootPath(root).ShouldNotBeNull();
        }
        finally
        {
            File.SetUnixFileMode(_workspaces, HomeWritable);
        }

        WorkspaceRegistry.FindByStoreKey(storeKey).ShouldNotBeNull();
        File.ReadAllText(Path.Combine(_home, "canary.txt")).ShouldBe("keep me");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousEnv);
        SqueezrHome.Reset();
        if (Directory.Exists(_home))
        {
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(_home, HomeWritable);
            }

            Directory.Delete(_home, recursive: true);
        }
    }

    private string NewProject(string leaf)
    {
        var dir = Path.GetFullPath(Path.Combine(_home, $"project-{leaf}"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string WriteStoreDir(string storeKey)
    {
        var storeDir = Path.Combine(_workspaces, storeKey);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.MetaDbFileName), new string('x', 64));
        return storeDir;
    }

    private void WriteRegistry(params WorkspaceRegistryEntry[] entries) =>
        File.WriteAllText(
            Path.Combine(_home, SqueezrPaths.RegistryFileName),
            JsonSerializer.Serialize(entries, SqueezrJson.FileOptions));

    private WorkspaceRegistryEntry Row(string name, string storeKey) =>
        new(name, Canonical(Path.Combine(_home, "gone", name)), storeKey, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static string Canonical(string path) => WorkspaceCanonicalPath.Canonicalize(path);
}
