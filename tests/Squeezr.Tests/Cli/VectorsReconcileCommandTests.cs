using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

[Collection("CliProcessGlobals")]
public sealed class VectorsReconcileCommandTests
{
    [Fact]
    public async Task Reconcile_StoreWithoutIndex_FailsWithIndexNotBuiltCode()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        File.Delete(SqueezrPaths.MetaDbPath(root));

        var result = await RunCliAsync(root, "vectors", "reconcile", "--json");

        result.ExitCode.ShouldBe(1);
        result.StdOut.ShouldBeEmpty();
        var lines = result.StdErr.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var json = JsonDocument.Parse(lines[^1]);
        json.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("index_not_built");
    }

    [Fact]
    public async Task Reconcile_QdrantDown_FailsWithClearError()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);

        var result = await RunCliAsync(root, "vectors", "reconcile");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("Qdrant is unavailable");
        result.StdErr.ShouldContain("squeezr services up");
    }

    [Fact]
    public async Task Reconcile_DryRunAndApplyTogether_AreRejected()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);

        var result = await RunCliAsync(root, "vectors", "reconcile", "--dry-run", "--apply");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("mutually exclusive");
    }

    [Fact]
    public async Task Reconcile_Help_StatesThatNothingIsDeletedWithoutApply()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);

        var help = await RunCliAsync(root, "vectors", "reconcile", "--help");

        help.ExitCode.ShouldBe(0);
        var text = Regex.Replace(help.StdOut, "\\s+", " ");
        text.ShouldContain("--apply");
        text.ShouldContain("--dry-run");
        text.ShouldContain("dry run unless --apply");
    }

    [Fact]
    public void Reconcile_NoFlags_ResolveToDryRun()
    {
        var settings = new VectorsReconcileCommand.Settings();

        settings.Apply.ShouldBeFalse();
        settings.DryRun.ShouldBeFalse();
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);
}
