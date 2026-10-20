using System.Text.Json;
using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

[Collection("CliProcessGlobals")]
public sealed class CliChildStoreGuardTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string WorkspaceName = "cli-child-store-guard";

    private readonly string? _previousEnv;
    private readonly string _root;
    private readonly string _decoyHome;

    public CliChildStoreGuardTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _root = TestDirs.NewTempDir();
        _decoyHome = TestDirs.NewTempDir();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousEnv);
        SqueezrHome.Reset();
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_decoyHome, recursive: true);
    }

    [Fact]
    public async Task CliChild_ListsTheRunHomeRegistry_EvenWhenAmbientEnvPointsAtAStubHome()
    {
        var runHome = SqueezrHome.Resolve();
        (await CliTestProcess.RunAsync(_root, "init", "--name", WorkspaceName, "--json")).ExitCode.ShouldBe(0);

        // CliTestProcess hands SQUEEZR_HOME to the child explicitly; a stray ambient value must not
        // win. The stub home would swallow the registration if the pin ever regressed to env inheritance.
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _decoyHome);

        var listing = await CliTestProcess.RunAsync(_root, "workspace", "list", "--json");

        listing.ExitCode.ShouldBe(0);
        var workspaces = listing.StdOut.Trim()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();
        workspaces.ShouldContain(entry => entry.GetProperty("name").GetString() == WorkspaceName);
        File.Exists(Path.Combine(_decoyHome, SqueezrPaths.RegistryFileName)).ShouldBeFalse();
        SqueezrHome.Resolve().ShouldBe(runHome);
    }
}
