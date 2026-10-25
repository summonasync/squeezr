using System.Text.Json;
using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class DoctorCommandTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";

    private readonly string _home = TestDirs.NewTempDir();

    public DoctorCommandTests()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
        File.WriteAllText(WorkspaceRegistry.FilePath, "[]");
    }

    public void Dispose()
    {
        SqueezrHome.Reset();
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Doctor_OnEmptyMachine_ReportsServicesFailureAndExitsOne()
    {
        var root = TestDirs.NewTempDir();
        (await CliTestProcess.RunPipedAsync(_home, "", root, "init", "--json")).ExitCode.ShouldBe(0);

        var run = await CliTestProcess.RunPipedAsync(_home, "", root, "doctor", "--json");

        run.ExitCode.ShouldBe(1);
        var doc = JsonDocument.Parse(run.StdOut);
        doc.RootElement.GetProperty("kind").GetString().ShouldBe("doctor");
        doc.RootElement.GetProperty("healthy").GetBoolean().ShouldBeFalse();
        doc.RootElement.GetProperty("checks").EnumerateArray()
            .ShouldContain(c => c.GetProperty("name").GetString() == "services");
    }

    [Fact]
    public async Task Doctor_TextRender_NamesRemedyAndChangesNothing()
    {
        var root = TestDirs.NewTempDir();
        (await CliTestProcess.RunPipedAsync(_home, "", root, "init", "--json")).ExitCode.ShouldBe(0);

        var run = await CliTestProcess.RunPipedAsync(_home, "", root, "doctor");

        run.ExitCode.ShouldBe(1);
        run.StdOut.ShouldContain("fail");
        run.StdOut.ShouldContain(DoctorRemedyText);
        run.StdOut.ShouldContain("nothing was changed");
    }

    private const string DoctorRemedyText = "squeezr services up";
}
