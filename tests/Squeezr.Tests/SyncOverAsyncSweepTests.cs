using Shouldly;
using Xunit.Abstractions;

namespace Squeezr.Tests;

public class SyncOverAsyncSweepTests
{
    private readonly ITestOutputHelper _output;

    public SyncOverAsyncSweepTests(ITestOutputHelper output) => _output = output;

    // Ratchet floor from feature 032 (spec 823f8925 item 4, task T1). Measured 2026-11-24 at main
    // ae4d016: 21 GetAwaiter().GetResult() sites in src/Squeezr.Core — WorktreeReconcile 3,
    // WorkspaceIndexer 7, EmbeddingIndexPhase 6, RoslynSession 2, RoslynProvider 3. The floor is
    // two-sided on purpose: growth fails, and so does a drop until the constant follows the code down.
    private const int CoreBaseline = 21;

    // A broken glob finds almost nothing; real debt reduction shaves a site or two off the baseline.
    // The denominator floor sits far enough below the baseline to tell those apart, so the ratchet
    // message below stays the one an honest drop sees.
    private const int CoreDenominatorFloor = 10;

    private const int CoreFileFloor = 4;

    [Fact]
    public void Core_SyncOverAsyncSites_SitAtTheRatchetedBaseline()
    {
        var srcRoot = CoreSrcRoot();
        var offenders = SyncOverAsyncSweep.Scan(srcRoot, out var scannedFiles);

        _output.WriteLine(
            $"sync-over-async sweep: {scannedFiles} files scanned under {srcRoot}, " +
            $"{offenders.Count} sites against a floor of {CoreBaseline}");
        foreach (var offender in offenders)
            _output.WriteLine($"  {offender.RelativePath}:{offender.Line}");

        scannedFiles.ShouldBeGreaterThan(0, "the sweep read nothing — the src root or the glob is broken");
        offenders.Count.ShouldBeGreaterThanOrEqualTo(CoreDenominatorFloor,
            "this few hits means the scan measured nothing, not that the debt is gone");
        offenders.Select(o => o.RelativePath).Distinct().Count().ShouldBeGreaterThanOrEqualTo(CoreFileFloor,
            "the baseline spans several files; one file found means the glob collapsed");

        offenders.Count.ShouldBe(CoreBaseline, RatchetMessage(offenders.Count, offenders));
    }

    [Fact]
    public void CoreSrcRoot_ResolvesFromTheTestBinaries_RegardlessOfRunnerCwd()
    {
        var srcRoot = CoreSrcRoot();

        Path.GetFileName(srcRoot).ShouldBe("Squeezr.Core");
        Directory.Exists(srcRoot).ShouldBeTrue($"resolved root '{srcRoot}' must exist without a cwd assumption");
    }

    [Fact]
    public void Scan_FileWithSyncOverAsyncCall_NamesThePathAndLine()
    {
        using var scratch = new ScratchDir();
        scratch.Write("Deep/Handler.cs",
            """
            public class Handler
            {
                public string Load() => Task.FromResult("x").GetAwaiter().GetResult();
            }
            """);

        var offender = SyncOverAsyncSweep.Scan(scratch.Root).Single();

        offender.RelativePath.ShouldBe("Deep/Handler.cs");
        offender.Line.ShouldBe(3);
    }

    [Fact]
    public void Scan_BuildOutput_IsSkipped()
    {
        using var scratch = new ScratchDir();
        scratch.Write("bin/Debug/Generated.cs", "var g = t.GetAwaiter().GetResult();");
        scratch.Write("obj/scratch.cs", "var o = t.GetAwaiter().GetResult();");
        scratch.Write("Real.cs", "var r = t.GetAwaiter().GetResult();");

        var offenders = SyncOverAsyncSweep.Scan(scratch.Root);

        offenders.Select(o => o.RelativePath).ShouldBe(["Real.cs"]);
        offenders.Single().Line.ShouldBe(1);
    }

    [Fact]
    public void Scan_TheSweepsOwnFiles_NeverReportThemselves()
    {
        using var scratch = new ScratchDir();
        scratch.Write("SyncOverAsyncSweep.cs", "public static class SyncOverAsyncSweep { }");
        scratch.Write("SyncOverAsyncSweepTests.cs", "var v = t.GetAwaiter().GetResult();");
        scratch.Write("Handler.cs", "var v = t.GetAwaiter().GetResult();");

        var offenders = SyncOverAsyncSweep.Scan(scratch.Root);

        offenders.Select(o => o.RelativePath).ShouldBe(["Handler.cs"]);
    }

    private static string RatchetMessage(int actual, IReadOnlyList<SyncOverAsyncSweep.Offender> offenders) =>
        actual > CoreBaseline
            ? $"sync-over-async debt in Squeezr.Core grew to {actual} sites against the floor of {CoreBaseline}: " +
              $"{Describe(offenders)} — convert the sites back, never raise the floor to absorb a regression"
            : $"sync-over-async debt in Squeezr.Core dropped to {actual} sites against the floor of {CoreBaseline}: " +
              $"ratchet CoreBaseline down to {actual}, the floor only means something while it tracks the code";

    private static string Describe(IReadOnlyList<SyncOverAsyncSweep.Offender> offenders) =>
        string.Join("; ", offenders.Select(o => $"{o.RelativePath}:{o.Line}"));

    private static string CoreSrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
            dir = dir.Parent;
        dir.ShouldNotBeNull("the solution root was not found from the test binaries");
        return Path.Combine(dir!.FullName, "src", "Squeezr.Core");
    }

    private sealed class ScratchDir : IDisposable
    {
        public ScratchDir() => Root = Directory.CreateTempSubdirectory("sync-over-async-").FullName;

        public string Root { get; }

        public void Write(string relativePath, string content)
        {
            var fullPath = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
