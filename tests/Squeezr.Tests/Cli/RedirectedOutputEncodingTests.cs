using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class RedirectedOutputEncodingTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";

    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly string _workspace;

    public RedirectedOutputEncodingTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = TestDirs.NewTempDir();
        _workspace = TestDirs.NewTempDir();
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousEnv);
        SqueezrHome.Reset();
        Directory.Delete(_home, recursive: true);
        Directory.Delete(_workspace, recursive: true);
    }

    [Fact]
    public async Task Status_PrettyRunOverRedirectedStdout_PreservesEmDash()
    {
        (await CliTestProcess.RunWithHomeAsync(_home, _workspace, "init", "--name", "encws", "--json"))
            .ExitCode.ShouldBe(0);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(_workspace);
        var repoDir = Directory.CreateDirectory(Path.Combine(_workspace, "repo"));
        File.WriteAllText(Path.Combine(repoDir.FullName, ".gitignore"), "secret.txt\n");
        File.WriteAllText(Path.Combine(repoDir.FullName, "secret.txt"), "ignored\n");
        (await CliTestProcess.RunWithHomeAsync(_home, _workspace, "add", "repo", "--name", "repo", "--json"))
            .ExitCode.ShouldBe(0);
        (await CliTestProcess.RunWithHomeAsync(_home, _workspace, "index", "--json")).ExitCode.ShouldBe(0);

        var status = await CliTestProcess.RunWithHomeAsync(_home, _workspace, "status");

        status.ExitCode.ShouldBe(0);
        status.StdOut.ShouldContain("—");
    }
}
