using Shouldly;
using Squeezr.Core.Indexing.Roslyn;

namespace Squeezr.Tests.Indexing.Roslyn;

[Collection("ProcessGlobals")]
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

        var first = pool.Acquire(root, semantic: true, out var degraded, msg => skips.Add(msg));

        first.ShouldBe(AcquireOutcome.OneShot, "the fallback session still belongs to the pass that paid for it");
        degraded.ShouldNotBeNull().Dispose();
        pool.CreatedSessions.ShouldBe(1);

        _now = Start.AddSeconds(2);
        var second = pool.Acquire(root, semantic: true, out degraded, msg => skips.Add(msg));

        second.ShouldBe(AcquireOutcome.Throttled);
        degraded.ShouldBeNull("a repo whose project never opens must not buy a solution open every debounce");
        skips.Count.ShouldBe(1, "a skipped pass has to say so instead of going quiet");
        skips[0].ShouldContain("throttled");
        pool.CreatedSessions.ShouldBe(1, "two debounces inside the window are one actual retry");

        _now = Start.AddSeconds(61);
        var third = pool.Acquire(root, semantic: true, out var retry, msg => skips.Add(msg));

        third.ShouldBe(AcquireOutcome.OneShot);
        retry.ShouldNotBeNull().Dispose();
        pool.CreatedSessions.ShouldBe(2, "the window expires and the project load is attempted again");
        skips.Count.ShouldBe(1, "a fallback session the pool refused to cache is reported by its own warning, not here");
    }

    [Fact]
    public void Acquire_WhenCreateThrows_ThrottlesTheNextAttemptToo()
    {
        var root = NewDir();
        WriteRepoFile(root, "broken.csproj", "<this is not a project file");

        using var pool = new SemanticSessionPool(() => _now);
        var failures = new List<string>();

        pool.Acquire(root, semantic: true, out _, msg => failures.Add(msg)).ShouldBe(AcquireOutcome.CreateFailed);
        failures.Count.ShouldBe(1);
        failures[0].ShouldNotContain("throttled");

        _now = Start.AddSeconds(5);
        pool.Acquire(root, semantic: true, out _, msg => failures.Add(msg)).ShouldBe(AcquireOutcome.Throttled);
        pool.Acquire(root, semantic: true, out _, msg => failures.Add(msg)).ShouldBe(AcquireOutcome.Throttled);

        failures.Count.ShouldBe(2, "the window announces itself once; the rest of its passes ride the throttled counter");
        failures[1].ShouldContain("throttled");

        _now = Start.AddSeconds(61);
        pool.Acquire(root, semantic: true, out _, msg => failures.Add(msg)).ShouldBe(AcquireOutcome.CreateFailed);
        failures.Count.ShouldBe(3, "a fresh failure after the expiry arms a fresh window that announces again");
        failures[2].ShouldNotContain("throttled");
        pool.CreatedSessions.ShouldBe(0);
    }

    [Fact]
    public void PooledSession_DisposedByThePassThatMisreadOwnership_IsNotHandedOutAsWarm()
    {
        var root = NewDir();
        WriteRepoFile(root, "Plain.cs", """
            public static class Plain
            {
                public static int Value => 1;
            }
            """);
        using var pool = new SemanticSessionPool(() => _now);

        pool.Acquire(root, semantic: true, out var corpse, _ => { }).ShouldBe(AcquireOutcome.Pooled);
        corpse.ShouldNotBeNull();
        corpse.Dispose();

        pool.IsWarm(root, semantic: true).ShouldBeFalse("a disposed session must not answer warm");

        pool.Acquire(root, semantic: true, out var fresh, _ => { }).ShouldBe(AcquireOutcome.Pooled);
        fresh.ShouldNotBeNull();
        fresh.ShouldNotBeSameAs(corpse, "the pool recreates instead of sitting on the corpse");
        pool.CreatedSessions.ShouldBe(2);
    }

    [Fact]
    public void ShouldReopen_RemembersRejectedPathsBeyondTheCooldown()
    {
        using var pool = new SemanticSessionPool(() => _now);

        pool.ShouldReopen("/root/repo", ["Loose.cs"]).ShouldBeTrue();

        _now = Start.AddMinutes(2);
        pool.ShouldReopen("/root/repo", ["Loose.cs"]).ShouldBeFalse(
            "the rejected-path memory outlives the cooldown: a remembered orphan cannot buy a second reopen");
        pool.ShouldReopen("/root/repo", ["Other.cs"]).ShouldBeTrue(
            "a never-rejected path is new information once the cooldown passed");
    }

    [Fact]
    public void Acquire_OnceTheRootCachesASession_IsNeverThrottled()
    {
        var root = NewRepoWithAnUnopenableProject();
        using var pool = new SemanticSessionPool(() => _now);
        var skips = new List<string>();

        pool.Acquire(root, semantic: true, out var session, msg => skips.Add(msg))
            .ShouldBe(AcquireOutcome.OneShot);
        session.ShouldNotBeNull().Dispose();
        pool.IsWarm(root, semantic: true).ShouldBeFalse();

        File.Delete(Path.Combine(root, "broken.csproj"));
        _now = Start.AddSeconds(61);
        pool.Acquire(root, semantic: true, out session, msg => skips.Add(msg))
            .ShouldBe(AcquireOutcome.Pooled, "a root that reached the requested mode is cached, not retried");
        session.ShouldNotBeNull();
        pool.IsWarm(root, semantic: true).ShouldBeTrue();

        pool.Discard(root);
        pool.Acquire(root, semantic: true, out session, msg => skips.Add(msg))
            .ShouldBe(AcquireOutcome.Pooled, "dropping a cached session is not a failed create");
        session.ShouldNotBeNull();
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

    private string NewDir()
    {
        var dir = TestDirs.NewTempDir();
        _tempDirs.Add(dir);
        return dir;
    }

    private string NewRepoWithAnUnopenableProject()
    {
        var root = NewDir();
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
