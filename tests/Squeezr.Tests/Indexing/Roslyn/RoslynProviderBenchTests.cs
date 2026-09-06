using System.Diagnostics;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Roslyn;
using Xunit.Abstractions;

namespace Squeezr.Tests.Indexing.Roslyn;

public class RoslynProviderBenchTests
{
    private readonly ITestOutputHelper _output;

    public RoslynProviderBenchTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Bench_SqueezrCore_ParsingThroughput()
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            _output.WriteLine("repo root not found; bench skipped");
            return;
        }

        var coreProject = Path.Combine(repoRoot, "src", "Squeezr.Core");
        if (!File.Exists(Path.Combine(coreProject, "obj", "project.assets.json")))
        {
            _output.WriteLine("Squeezr.Core not restored; bench skipped");
            return;
        }

        var files = Directory.EnumerateFiles(coreProject, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        files.ShouldNotBeEmpty();

        var sessionStopwatch = Stopwatch.StartNew();
        using var session = RoslynSession.Create(coreProject, semantic: true);
        sessionStopwatch.Stop();
        session.Semantic.ShouldBeTrue();

        var provider = new RoslynProvider(session, SqueezrConfig.CreateDefault());
        var beforeMemory = GC.GetTotalMemory(forceFullCollection: true);

        var parseStopwatch = Stopwatch.StartNew();
        var symbolCount = 0;
        var chunkCount = 0;
        var refCount = 0;
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(coreProject, file);
            var result = provider.Parse(new FileRef("squeezr.core", relative), File.ReadAllBytes(file));
            symbolCount += result.Symbols.Count;
            chunkCount += result.Chunks.Count;
            refCount += result.Refs.Count;
        }

        parseStopwatch.Stop();
        var memoryDelta = GC.GetTotalMemory(forceFullCollection: true) - beforeMemory;

        var report = $"files={files.Count} symbols={symbolCount} chunks={chunkCount} refs={refCount} " +
                     $"sessionMs={sessionStopwatch.ElapsedMilliseconds} parseMs={parseStopwatch.ElapsedMilliseconds} " +
                     $"memMB={memoryDelta / 1024.0 / 1024.0:F1}";
        _output.WriteLine(report);

        foreach (var warning in provider.Warnings)
        {
            _output.WriteLine($"warning: {warning}");
        }

        sessionStopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromMinutes(2));
        parseStopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromMinutes(2));
        symbolCount.ShouldBeGreaterThan(0);
        chunkCount.ShouldBeGreaterThan(0);
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
