using System.Diagnostics;
using Shouldly;
using Squeezr.Core.Storage;
using Xunit.Abstractions;

namespace Squeezr.Tests.Cli;

// AC7 of 025/T1, the cost pin review-025-t1-01 MAJOR-1 demanded. Every other fixture in this directory is
// 1-2 files and is structurally blind to the difference between a scoped pass and a repo-wide one, so this
// class builds project-bearing repos at two sizes and measures the door against the same store's full-index
// wall. Ratios, not seconds: the accepted floor is one solution open per invocation (seconds, never
// sub-second), and the thing that has to stay small is the part that grows with the repo. Box load goes into
// the output line with the walls; both arms ride the same load.
[Collection("ProcessGlobals")]
public sealed class HookPostEditCostTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int SmallRepoFiles = 300;
    private const int LargeRepoFiles = 1500;

    // Headspace over the measured shape of this route (load 8-13 on 32 cores): scoped 3.3-4.2 s against a
    // 9.7 s control at 300 files (ratio 0.33-0.43),
    // 4.1-4.6 s against a 28.4 s control at 1500 (0.15-0.16), and 1.1-1.3x of wall for a 5x bigger repo. The
    // pre-fix door measured 0.85-1.11 / ~1.05 / 3.5-4.0x on the same fixtures, so every bound below is a claim
    // that failed loudly before the fix and not a formality now.
    private const double SmallScopedOverFullCeiling = 0.6;
    private const double LargeScopedOverFullCeiling = 0.35;
    private const double LargeOverSmallCeiling = 2.5;

    // the one pad file both timed arms edit — index 7 is inside the pad range of every fixture here
    private const int EditPadIndex = 7;

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
    public async Task AnnouncedPaths_CostAFractionOfAFullIndex_AndDoNotGrowWithTheRepo()
    {
        var small = await NewIndexedFixtureAsync(SmallRepoFiles);
        // a file that exists and is never announced: the walk finds it, the door must not
        WritePad(small, SmallRepoFiles + 100, "UnannouncedThree");

        var smallScoped = await HookWallAsync(small, "CostSmallOne");
        output.WriteLine($"small {small.FileCount} cs: scoped {smallScoped:F2} s | load {LoadAverage()}");

        // the scope is real at the store, not only in the clock: what nobody announced stayed out of the
        // pass until a walk ran — checked before any walk can move it
        (await SearchFindsAsync(small, "UnannouncedThree")).ShouldBeFalse(
            "the door indexes announced paths; sweeping the repo is the walk's job");

        var smallFull = await FullIndexWallAsync(small);
        output.WriteLine($"small: full-index {smallFull:F2} s | ratio {smallScoped / smallFull:F3}");
        (await SearchFindsAsync(small, "UnannouncedThree")).ShouldBeTrue(
            "the same query finds it once a walk has run — the zero above is scope, not a dead door");

        var large = await NewIndexedFixtureAsync(LargeRepoFiles);
        var largeScoped = await HookWallAsync(large, "CostLargeOne");
        var largeFull = await FullIndexWallAsync(large);
        output.WriteLine($"large {large.FileCount} cs: scoped {largeScoped:F2} s | full-index {largeFull:F2} s " +
                         $"| ratio {largeScoped / largeFull:F3}");

        var smallRatio = smallScoped / smallFull;
        var largeRatio = largeScoped / largeFull;
        var sizeGrowth = largeScoped / smallScoped;
        output.WriteLine(
            $"cost table (seconds) | small {small.FileCount} cs: scoped {smallScoped:F2} " +
            $"full {smallFull:F2} ratio {smallRatio:F3} | large {large.FileCount} cs: " +
            $"scoped {largeScoped:F2} full {largeFull:F2} ratio {largeRatio:F3} | " +
            $"scoped growth {(double)large.FileCount / small.FileCount:F1}x repo = {sizeGrowth:F2}x wall");

        smallScoped.ShouldNotBe(0, "a wall of zero measured nothing");
        smallFull.ShouldNotBe(0);
        largeFull.ShouldNotBe(0);
        smallRatio.ShouldBeLessThanOrEqualTo(SmallScopedOverFullCeiling);
        largeRatio.ShouldBeLessThanOrEqualTo(LargeScopedOverFullCeiling);
        sizeGrowth.ShouldBeLessThanOrEqualTo(LargeOverSmallCeiling,
            "the scoped wall is the solution open plus the announced file, so a 5x repo must not move it 5x");
    }

    private sealed record Fixture(string Home, string Root, string Repo, int FileCount);

    private async Task<Fixture> NewIndexedFixtureAsync(int padFiles)
    {
        var home = SqueezrHome.Resolve();
        var root = TestDirs.NewNonHomeScratchDir();
        _fixtureRoots.Add(root);
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        File.WriteAllText(Path.Combine(repo, "Mini.csproj"), ProjectFileText);
        File.WriteAllText(
            Path.Combine(repo, "src", "Hub.cs"),
            "namespace Mini;\n\npublic static class Hub\n{\n    public static int Run(int value) => value * 2;\n}\n");
        for (var i = 0; i < padFiles; i++)
        {
            WritePadAt(Path.Combine(repo, "src", $"Pad{i:D5}.cs"), i, null);
        }

        await RestoreAsync(Path.Combine(repo, "Mini.csproj"));
        (await CliTestProcess.RunAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        (await CliTestProcess.RunWithHomeAsync(home, root, "add", repo, "--name", "demo", "--json"))
            .ExitCode.ShouldBe(0);
        // rows plus a current-format trigram index, without buying the semantic parse this test is about
        (await CliTestProcess.RunWithHomeAsync(home, root, "index", "--no-semantic", "--json"))
            .ExitCode.ShouldBe(0);
        return new Fixture(home, root, repo, padFiles + 1);
    }

    private static string WritePad(Fixture fixture, int index, string marker) =>
        WritePadAt(Path.Combine(fixture.Repo, "src", $"Pad{index:D5}.cs"), index, marker);

    private static string WritePadAt(string path, int index, string? marker)
    {
        File.WriteAllText(
            path,
            marker is null
                ? $"namespace Mini;\n\npublic static class Pad{index:D5}\n{{\n    public static int Value() => Hub.Run({index});\n}}\n"
                : $"namespace Mini;\n\npublic static class Pad{index:D5}\n{{\n    public static int Value() => Hub.Run({index});\n}}\n\npublic static class {marker}\n{{\n    public static int Probe() => Hub.Run({index});\n}}\n");
        return path;
    }

    // Edits one pad file, announces exactly that path through the real door, and returns the wall of the
    // invocation. The search that follows is what keeps the timing honest: a door that indexed nothing would
    // be the fastest thing on the box.
    private async Task<double> HookWallAsync(Fixture fixture, string marker)
    {
        var index = EditPadIndex;
        var path = Path.Combine(fixture.Repo, "src", $"Pad{index:D5}.cs");
        File.AppendAllText(
            path,
            $"\npublic static class {marker}\n{{\n    public static int Probe() => Hub.Run({index});\n}}\n");

        var watch = Stopwatch.StartNew();
        var run = await CliTestProcess.RunPipedAsync(
            fixture.Home,
            "{\"tool_name\":\"edit\",\"tool_input\":{\"file_path\":\"" + path + "\"}}",
            fixture.Root,
            "hook", "post-edit");
        watch.Stop();

        run.ExitCode.ShouldBe(0);
        run.StdOut.ShouldBeEmpty();
        run.StdErr.ShouldBeEmpty();
        (await SearchFindsAsync(fixture, marker)).ShouldBeTrue(
            $"the timed arm has to have indexed the file it announced ({marker})");
        return watch.Elapsed.TotalSeconds;
    }

    private async Task<double> FullIndexWallAsync(Fixture fixture)
    {
        var watch = Stopwatch.StartNew();
        var run = await CliTestProcess.RunWithHomeAsync(fixture.Home, fixture.Root, "index", "--json");
        watch.Stop();
        run.ExitCode.ShouldBe(0);
        return watch.Elapsed.TotalSeconds;
    }

    private static async Task<bool> SearchFindsAsync(Fixture fixture, string term)
    {
        var search = await CliTestProcess.RunWithHomeAsync(fixture.Home, fixture.Root, "search", term, "--json");
        search.ExitCode.ShouldBe(0);
        foreach (var line in search.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("matches", out var matches)
                && doc.RootElement.GetProperty("type").GetString() == "summary")
            {
                return matches.GetInt32() > 0;
            }
        }

        throw new InvalidOperationException($"search answered without a summary line: {search.StdOut}");
    }

    private static async Task RestoreAsync(string projectPath)
    {
        var startInfo = new ProcessStartInfo("dotnet", $"restore \"{projectPath}\" --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        process.ExitCode.ShouldBe(0, $"restore failed for the fixture project:\n{stdout}\n{stderr}");
    }

    private static string LoadAverage()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows: no /proc/loadavg";
        }

        try
        {
            return string.Join(' ', File.ReadAllLines("/proc/loadavg")[0].Split(' ')[..3]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or IndexOutOfRangeException)
        {
            return $"load unreadable ({ex.GetType().Name})";
        }
    }

    private const string ProjectFileText = """
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>

        </Project>
        """;
}
