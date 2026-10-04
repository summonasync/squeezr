using Shouldly;
using Squeezr.Core.Indexing.Roslyn;

namespace Squeezr.Tests.Indexing.Roslyn;

public class SemanticSessionPoolTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    private readonly List<string> _tempDirs = [];
    private DateTime _now = Start;

    [Fact]
    public void Acquire_WhileRetryIsThrottled_KeepsSessionNullAndCreateCounted()
    {
        var root = NewRepoWithAnUnopenableProject();
        using var pool = new SemanticSessionPool(() => _now);
        var skips = new List<string>();

        var degraded = pool.Acquire(root, semantic: true, out var pooled, ex => skips.Add(ex.Message));

        degraded.ShouldNotBeNull().Dispose();
        pooled.ShouldBeFalse("the fallback session still belongs to the pass that paid for it");
        pool.CreatedSessions.ShouldBe(1);

        _now = Start.AddSeconds(2);
        var throttled = pool.Acquire(root, semantic: true, out pooled, ex => skips.Add(ex.Message));

        throttled.ShouldBeNull("a repo whose project never opens must not buy a solution open every debounce");
        pooled.ShouldBeFalse();
        skips.Count.ShouldBe(1, "a skipped pass has to say so instead of going quiet");
        skips[0].ShouldContain("throttled");
        pool.CreatedSessions.ShouldBe(1, "two debounces inside the window are one actual retry");

        _now = Start.AddSeconds(61);
        var retry = pool.Acquire(root, semantic: true, out _, ex => skips.Add(ex.Message));

        retry.ShouldNotBeNull().Dispose();
        pool.CreatedSessions.ShouldBe(2, "the window expires and the project load is attempted again");
        skips.Count.ShouldBe(1, "a fallback session the pool refused to cache is reported by its own warning, not here");
    }

    [Fact]
    public void Acquire_WhenCreateThrows_ThrottlesTheNextAttemptToo()
    {
        var root = NewDir("dead-project");
        WriteRepoFile(root, "broken.csproj", "<this is not a project file");

        using var pool = new SemanticSessionPool(() => _now);
        var failures = new List<string>();

        pool.Acquire(root, semantic: true, out _, ex => failures.Add(ex.Message)).ShouldBeNull();
        failures.Count.ShouldBe(1);
        failures[0].ShouldNotContain("throttled");

        _now = Start.AddSeconds(5);
        pool.Acquire(root, semantic: true, out _, ex => failures.Add(ex.Message)).ShouldBeNull();
        pool.Acquire(root, semantic: true, out _, ex => failures.Add(ex.Message)).ShouldBeNull();

        failures.Count.ShouldBe(3);
        failures[1].ShouldContain("throttled");
        failures[2].ShouldContain("throttled");
        pool.CreatedSessions.ShouldBe(0);
    }

    [Fact]
    public void Acquire_OnceTheRootCachesASession_IsNeverThrottled()
    {
        var root = NewRepoWithAnUnopenableProject();
        using var pool = new SemanticSessionPool(() => _now);
        var skips = new List<string>();

        pool.Acquire(root, semantic: true, out var pooled, ex => skips.Add(ex.Message))
            .ShouldNotBeNull().Dispose();
        pool.IsWarm(root, semantic: true).ShouldBeFalse();

        File.Delete(Path.Combine(root, "broken.csproj"));
        _now = Start.AddSeconds(61);
        pool.Acquire(root, semantic: true, out pooled, ex => skips.Add(ex.Message)).ShouldNotBeNull();
        pooled.ShouldBeTrue("a root that reached the requested mode is cached, not retried");
        pool.IsWarm(root, semantic: true).ShouldBeTrue();

        pool.Discard(root);
        pool.Acquire(root, semantic: true, out pooled, ex => skips.Add(ex.Message)).ShouldNotBeNull();
        pooled.ShouldBeTrue("dropping a cached session is not a failed create");
        skips.ShouldBeEmpty();
        pool.CreatedSessions.ShouldBe(3);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private string NewDir(string hint)
    {
        var dir = Path.Combine(Path.GetTempPath(), "squeezr-tests", $"{hint}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string NewRepoWithAnUnopenableProject()
    {
        var root = NewDir("unopenable-project");
        WriteRepoFile(root, "broken.csproj", "<this is not a project file");
        WriteRepoFile(root, "Loose.cs", """
            public static class Loose
            {
                public static int Value => 1;
            }
            """);
        return root;
    }

    private static void WriteRepoFile(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
