using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing.Ignore;

namespace Squeezr.Tests.Indexing.Ignore;

public class TrackedSetMemoTests
{
    private readonly List<string> warnings = [];

    [Fact]
    public void For_SameRootWithinTtl_LoadsOnce()
    {
        var calls = 0;
        var memo = new TrackedSetMemo(load: (_, _) => { calls++; return null; });

        memo.For("/repo/a");
        memo.For("/repo/a");
        memo.For("/repo/a");

        calls.ShouldBe(1);
    }

    [Fact]
    public void For_DifferentRoots_LoadSeparately()
    {
        var calls = 0;
        var memo = new TrackedSetMemo(load: (_, _) => { calls++; return null; });

        memo.For("/repo/a");
        memo.For("/repo/b");

        calls.ShouldBe(2);
    }

    [Fact]
    public void For_ZeroTtl_ReloadsOnEveryCall()
    {
        var calls = 0;
        var memo = new TrackedSetMemo(TimeSpan.Zero, (_, _) => { calls++; return null; });

        memo.For("/repo/a");
        memo.For("/repo/a");

        calls.ShouldBe(2);
    }

    [Fact]
    public void For_NullResult_IsCachedToo()
    {
        var calls = 0;
        var memo = new TrackedSetMemo(load: (_, _) => { calls++; return null; });

        memo.For("/repo/a").ShouldBeNull();
        memo.For("/repo/a").ShouldBeNull();

        calls.ShouldBe(1);
    }

    [Fact]
    public void Clear_ForcesTheNextReadToLoad()
    {
        var calls = 0;
        var memo = new TrackedSetMemo(load: (_, _) => { calls++; return null; });

        memo.For("/repo/a");
        memo.Clear();
        memo.For("/repo/a");

        calls.ShouldBe(2);
    }

    [Fact]
    public void For_PassesWarnThroughToTheLoader()
    {
        string? seenRoot = null;
        var memo = new TrackedSetMemo(load: (root, warn) =>
        {
            seenRoot = root;
            warn?.Invoke("gitignore decides alone");
            return null;
        });

        memo.For("/repo/a", warnings.Add);

        seenRoot.ShouldBe("/repo/a");
        warnings.ShouldContain("gitignore decides alone");
    }

    [Fact]
    public void ForFile_RepeatedReadsOnOneRepo_ReuseOneTrackedSet()
    {
        var root = NewPluginsRepo();
        try
        {
            var config = SqueezrConfig.CreateDefault();
            var first = IgnoreRuleset.ForFile(root, config, "plugins/a.js");
            var second = IgnoreRuleset.ForFile(root, config, "plugins/a.js");

            first.Tracked.ShouldNotBeNull();
            second.Tracked.ShouldBe(first.Tracked);
        }
        finally
        {
            TrackedSetMemo.Shared.Clear();
        }
    }

    [Fact]
    public void ForFile_AfterMemoClear_LoadsAFreshSet()
    {
        var root = NewPluginsRepo();
        try
        {
            var config = SqueezrConfig.CreateDefault();
            var first = IgnoreRuleset.ForFile(root, config, "plugins/a.js");
            TrackedSetMemo.Shared.Clear();
            var second = IgnoreRuleset.ForFile(root, config, "plugins/a.js");

            first.Tracked.ShouldNotBeNull();
            second.Tracked.ShouldNotBeNull();
            second.Tracked.ShouldNotBe(first.Tracked);
        }
        finally
        {
            TrackedSetMemo.Shared.Clear();
        }
    }

    [Fact]
    public void ForFile_DecisionSemantics_UnchangedByTheMemo()
    {
        var root = NewPluginsRepo();
        try
        {
            var decision = IgnoreRuleset.ForFile(root, SqueezrConfig.CreateDefault(), "plugins/a.js")
                .Decision("plugins/a.js", false);

            decision.Excluded.ShouldBeFalse();
            decision.TrackedButExcluded.ShouldBeTrue();
        }
        finally
        {
            TrackedSetMemo.Shared.Clear();
        }
    }

    private static string NewPluginsRepo()
    {
        var root = TestDirs.NewTempDir();
        TestGitRepo.Init(root);
        Directory.CreateDirectory(Path.Combine(root, "plugins"));
        File.WriteAllText(Path.Combine(root, "plugins", "a.js"), "export function boot() { return 1; }\n");
        File.WriteAllText(Path.Combine(root, ".gitignore"), "/plugins\n");
        TestGitRepo.Add(root, ".gitignore", "plugins/a.js");
        return root;
    }
}
