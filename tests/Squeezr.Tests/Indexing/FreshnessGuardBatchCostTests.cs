using System.Diagnostics;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Indexing.Roslyn;
using Squeezr.Core.Workspace;
using Xunit.Abstractions;

namespace Squeezr.Tests.Indexing;

// The proof feature 032/T5 exists for: healing RefreshBudget.MaxFilesReRead stale candidates through the
// guard costs one IndexScoped batch — one store open, one MSBuild solution open — not one re-read cycle per
// candidate, which is what the pre-T4 seam (ReindexFile, one IndexFile per call) charged. The 025 review
// pinned why the fixture cannot be small: a 1-2-file fixture answers from the walk's cost and cannot tell a
// batch from repeated per-file opens, so each ordering here builds its own 302-file project-bearing fixture
// (restored, two projects joined by a real ProjectReference) and a full-index pass sits beside the arms as
// the floor. The seams are the production delegates of both eras driven through the guard's internal
// overload; the zero clock only keeps the wall budget from refusing slots after the first, so both arms
// heal the same four candidates and the walls stay comparable. A probe session proves per fixture that the
// projects open as a real MSBuild solution (not the ad-hoc fallback) and warms the MSBuild path before
// either arm runs; the order swap then controls what is left — ambient load and cache drift, so a floor
// shift cannot dress up as an effect. Ratios with the box load inline — absolute seconds are not the claim.
[Collection("ProcessGlobals")]
public sealed class FreshnessGuardBatchCostTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int PadFiles = 300;

    // a floor, not a precise figure: the per-file route pays the solution open four times where the batch
    // pays it once, so noise compresses the gap but a fair box cannot close it below this
    private const double PerFileOverBatchFloor = 1.3;

    // the 025 house ceiling at this fixture size: the batch is one announced pass, never a repo walk
    private const double BatchOverFullCeiling = 0.6;

    private static readonly int[] ArmAIndices = [3, 13, 103, 203];
    private static readonly int[] ArmBIndices = [5, 15, 105, 205];

    private readonly List<string> _fixtureRoots = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var root in _fixtureRoots)
        {
            TestDirs.TryRemoveRegisteredLeaf(root);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task HealingTheFullCandidateSet_CostsOneBatch_NotOneReReadPerFile()
    {
        var perFileFirst = await MeasureAsync("one", batchGoesFirst: false);
        var batchFirst = await MeasureAsync("two", batchGoesFirst: true);

        output.WriteLine(
            $"fixture {perFileFirst.FileCount} cs files ({PadFiles} pads + Hub + Lib), 2 projects, " +
            "1 ProjectReference edge, restore proven by obj/project.assets.json plus per-project g.props");

        Report("order per-file-then-batch", perFileFirst);
        Report("order batch-then-per-file", batchFirst);

        foreach (var run in new[] { perFileFirst, batchFirst })
        {
            run.PerFileWall.ShouldNotBe(0, "a wall of zero measured nothing");
            run.BatchWall.ShouldNotBe(0);
            run.FullWall.ShouldNotBe(0);
            run.PerFileOverBatch.ShouldBeGreaterThanOrEqualTo(PerFileOverBatchFloor,
                "healing the same candidates through the per-file seam must cost visibly more than one batch, " +
                "in both orderings — the claim this feature is bought for");
            run.BatchOverFull.ShouldBeLessThanOrEqualTo(BatchOverFullCeiling,
                "one announced pass must stay a fraction of a repo-wide walk");
        }
    }

    private sealed record CostFixture(string Workspace, string Repo, int FileCount);

    private sealed record MeasuredRun(
        int FileCount,
        double PerFileWall,
        double BatchWall,
        double FullWall,
        double PerFileOverBatch,
        double BatchOverFull);

    private async Task<MeasuredRun> MeasureAsync(string tag, bool batchGoesFirst)
    {
        var fixture = await NewProjectFixtureAsync(tag);
        var first = batchGoesFirst
            ? HealArm(fixture, tag, 'b', ArmBIndices, BatchSeam)
            : HealArm(fixture, tag, 'a', ArmAIndices, PerFileSeam);
        var second = batchGoesFirst
            ? HealArm(fixture, tag, 'a', ArmAIndices, PerFileSeam)
            : HealArm(fixture, tag, 'b', ArmBIndices, BatchSeam);

        var watch = Stopwatch.StartNew();
        var walked = new WorkspaceIndexer().Index(fixture.Workspace, full: true);
        watch.Stop();

        var repoRow = walked.Repos.ShouldHaveSingleItem(
            "the fixture is one repository and the full-index control must have walked it");
        repoRow.Files.ShouldBeGreaterThanOrEqualTo(fixture.FileCount,
            "the control's floor claim is only good if the walk really covered every source file of the fixture " +
            "(the walked total also carries the sln and project files, so it only ever exceeds the source count)");
        var fullWall = watch.Elapsed.TotalSeconds;
        output.WriteLine(
            $"{tag}: full-index {fullWall:F2} s ({repoRow.Files} files walked) | load {LoadLine()}");

        var perFileWall = batchGoesFirst ? second.Wall : first.Wall;
        var batchWall = batchGoesFirst ? first.Wall : second.Wall;
        return new MeasuredRun(
            fixture.FileCount,
            perFileWall,
            batchWall,
            fullWall,
            perFileWall / batchWall,
            batchWall / fullWall);
    }

    // Times one guard call healing the full candidate set through the given seam, and — before the wall
    // means anything — proves the arm did real work: every candidate came back refreshed and every marker
    // it wrote is searchable. An arm that healed nothing would also be the fastest thing on the box.
    private (double Wall, string Label) HealArm(
        CostFixture fixture,
        string tag,
        char arm,
        int[] indices,
        ReindexScoped seam)
    {
        var markers = indices.Select(index => (Index: index, Marker: $"Stale{tag}{arm}{index}")).ToList();
        foreach (var (index, marker) in markers)
        {
            File.AppendAllText(
                Path.Combine(fixture.Repo, $"Pad{index:D5}.cs"),
                $"\npublic static class {marker}\n{{\n    public static int Probe() => Hub.Run({index});\n}}\n");
        }

        var candidates = indices.Select(index => ("demo", $"Pad{index:D5}.cs")).ToList();

        var watch = Stopwatch.StartNew();
        var result = FreshnessGuard.CheckAndRefresh(fixture.Workspace, candidates, null, seam, () => TimeSpan.Zero);
        watch.Stop();

        result.Refreshed.Count.ShouldBe(indices.Length,
            $"arm {arm} must have healed all {indices.Length} candidates before its wall is compared to anything");
        foreach (var (index, marker) in markers)
        {
            SearchFinds(fixture.Workspace, marker).ShouldBeTrue(
                $"arm {arm} wrote rows for Pad{index:D5}.cs — a measured wall over nothing is not evidence");
        }

        var label = $"arm {arm} ({indices.Length} candidates)";
        output.WriteLine($"{tag} {label}: {watch.Elapsed.TotalSeconds:F2} s | load {LoadLine()}");
        return (watch.Elapsed.TotalSeconds, label);
    }

    private async Task<CostFixture> NewProjectFixtureAsync(string tag)
    {
        var workspace = TestDirs.NewTempDir();
        _fixtureRoots.Add(workspace);
        var repo = Path.Combine(workspace, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "lib"));

        File.WriteAllText(Path.Combine(repo, "Mini.sln"), SolutionText);
        File.WriteAllText(Path.Combine(repo, "Mini.App.csproj"), AppProjectText);
        File.WriteAllText(Path.Combine(repo, "lib", "Mini.Lib.csproj"), LibProjectText);
        File.WriteAllText(
            Path.Combine(repo, "lib", "Lib.cs"),
            $"namespace Mini.Lib;\n\npublic static class Lib{tag}\n{{\n    public static int Echo(int value) => value + 1;\n}}\n");
        File.WriteAllText(
            Path.Combine(repo, "Hub.cs"),
            "namespace Mini;\n\npublic static class Hub\n{\n    public static int Run(int value) => value * 2;\n}\n");
        for (var i = 0; i < PadFiles; i++)
        {
            File.WriteAllText(
                Path.Combine(repo, $"Pad{i:D5}.cs"),
                $"namespace Mini;\n\npublic static class Pad{i:D5}\n{{\n    public static int Value() => Hub.Run({i});\n}}\n");
        }

        // the lib project nests under lib/ so the two restores cannot share one obj: a shared obj makes the
        // second restore answer "up-to-date" against the first one's fresh assets and the graph stays
        // half-restored — found the hard way, the app's g.props never appeared
        await RestoreAsync(Path.Combine(repo, "Mini.sln"));
        File.Exists(Path.Combine(repo, "obj", "project.assets.json")).ShouldBeTrue(
            "an unrestored project would push RoslynSession into the ad-hoc fallback and blind every arm to the solution open");
        File.Exists(Path.Combine(repo, "obj", "Mini.App.csproj.nuget.g.props")).ShouldBeTrue();
        File.Exists(Path.Combine(repo, "lib", "obj", "Mini.Lib.csproj.nuget.g.props")).ShouldBeTrue();

        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace, noSemantic: true);

        // the regime probe: the guard routes create their sessions the same way, so a fixture that can only
        // answer with the ad-hoc fallback would measure repeated file loads and call them solution opens.
        // Asserting the MSBuild session here keeps every arm honest — and incidentally warms the MSBuild
        // path before either arm runs, so the first arm's JIT cannot dress up as a route difference
        using (var probe = RoslynSession.Create(repo, semantic: true))
        {
            probe.Degraded.ShouldBeFalse(
                "the fixture fell back to the ad-hoc session; every wall below would measure the wrong regime");
            probe.Warnings.ShouldContain(
                warning => warning.StartsWith("semantic mode:", StringComparison.Ordinal),
                "the fixture must open its projects as a real MSBuild solution, not ad-hoc");
        }

        return new CostFixture(workspace, repo, PadFiles + 2);
    }

    private void Report(string label, MeasuredRun run)
    {
        output.WriteLine(
            $"{label}: per-file {run.PerFileWall:F2} s | batch {run.BatchWall:F2} s | full-index {run.FullWall:F2} s " +
            $"| per-file/batch {run.PerFileOverBatch:F2} | batch/full {run.BatchOverFull:F3} | load {LoadLine()}");
    }

    private static async Task RestoreAsync(string projectPath)
    {
        var startInfo = new ProcessStartInfo("dotnet", $"restore \"{projectPath}\" --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(startInfo)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        process.ExitCode.ShouldBe(0, $"restore failed for the fixture project:\n{stdout}\n{stderr}");
    }

    private static bool SearchFinds(string workspace, string term) =>
        new SearchEngine(workspace)
            .Search(new SearchQuery(term, false, null, null, null, null, null, null))
            .Matches.Count > 0;

    private static string LoadLine()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows: no /proc/loadavg";
        }

        try
        {
            var load = File.ReadAllLines("/proc/loadavg")[0].Split(' ')[..3];
            var uptimeSeconds = double.Parse(File.ReadAllLines("/proc/uptime")[0].Split(' ')[0]);
            return $"{string.Join(' ', load)} on {Environment.ProcessorCount} cores, " +
                   $"up {TimeSpan.FromSeconds(uptimeSeconds).TotalDays:F1} days";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or IndexOutOfRangeException or FormatException)
        {
            return $"load unreadable ({ex.GetType().Name})";
        }
    }

    // the route the guard drove before T4: one full IndexFile cycle per candidate, each paying its own
    // lock, store open, manifest read and MSBuild solution open — the price the batch was bought to retire
    private static readonly ReindexScoped PerFileSeam = (root, batch, takeover) =>
    {
        var indexed = 0;
        var removed = 0;
        foreach (var candidate in batch)
        {
            var row = new WorkspaceIndexer().IndexFile(root, candidate.Repo, candidate.Path, onStaleTakeover: takeover);
            if (row is null)
            {
                removed++;
            }
            else
            {
                indexed++;
            }
        }

        return new WorkspaceIndexer.ScopedIndexResult(indexed, removed, []);
    };

    // the route T4 shipped: the guard hands the whole announced set to one IndexScoped pass
    private static readonly ReindexScoped BatchSeam = (root, batch, takeover) =>
        new WorkspaceIndexer().IndexScoped(root, [.. batch.Select(candidate => candidate.AbsolutePath)]);

    private const string LibProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
          </PropertyGroup>

          <ItemGroup>
            <Compile Include="Lib.cs" />
          </ItemGroup>

        </Project>
        """;

    private const string AppProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
          </PropertyGroup>

          <ItemGroup>
            <Compile Include="**/*.cs" Exclude="lib/**/*.cs" />
          </ItemGroup>

          <ItemGroup>
            <ProjectReference Include="lib/Mini.Lib.csproj" />
          </ItemGroup>

        </Project>
        """;

    private const string SolutionText = """
        Microsoft Visual Studio Solution File, Format Version 12.00
        # Visual Studio Version 17
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Mini.App", "Mini.App.csproj", "{11111111-1111-1111-1111-111111111111}"
        EndProject
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Mini.Lib", "lib\Mini.Lib.csproj", "{22222222-2222-2222-2222-222222222222}"
        EndProject
        Global
        	GlobalSection(SolutionConfigurationPlatforms) = preSolution
        		Debug|Any CPU = Debug|Any CPU
        		Release|Any CPU = Release|Any CPU
        	EndGlobalSection
        	GlobalSection(ProjectConfigurationPlatforms) = postSolution
        		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
        		{11111111-1111-1111-1111-111111111111}.Release|Any CPU.ActiveCfg = Release|Any CPU
        		{11111111-1111-1111-1111-111111111111}.Release|Any CPU.Build.0 = Release|Any CPU
        		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.Build.0 = Debug|Any CPU
        		{22222222-2222-2222-2222-222222222222}.Release|Any CPU.ActiveCfg = Release|Any CPU
        		{22222222-2222-2222-2222-222222222222}.Release|Any CPU.Build.0 = Release|Any CPU
        	EndGlobalSection
        EndGlobal
        """;
}
