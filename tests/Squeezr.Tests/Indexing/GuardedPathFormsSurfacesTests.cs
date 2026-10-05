using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

// review-064-07 W-1: one security predicate was decided by three clients that each kept their own idea of which
// root a recorded path is written against — the store-side scrub, the stats ledger, the read hook. The blind spot
// was the same on each: an anchored extra matches the stored shape and nothing else, so the repo-qualified and
// absolute readings of a protected file were answered 'not protected' on two surfaces while the third folded it.
// One mask, six readings of the same never-stored path, three surfaces — and the plain file's six readings beside
// them, so a widening that denies everything cannot pass as agreement.
public class GuardedPathFormsSurfacesTests
{
    private const string AnchoredExtra = "infra/*.cs";
    private const string GoneFile = "infra/Gone.cs";
    private const string WorkspaceRoot = "{ws}";

    // store-root, name-qualified, repo-dir-qualified, absolute, nested-workspace-relative, '..' alias, and the
    // readings that only a strip chain of two or more retires — the transitive fold is the mechanism this slice
    // added for the hook's absolute-form blind spot, so it is pinned here rather than in prose
    [Theory]
    [InlineData("infra/Gone.cs", true)]
    [InlineData("demo/infra/Gone.cs", true)]
    [InlineData("repo/infra/Gone.cs", true)]
    [InlineData("{ws}/repo/infra/Gone.cs", true)]
    [InlineData("repos/demo/infra/Gone.cs", true)]
    [InlineData("repos/demo/infra/../infra/Gone.cs", true)]
    [InlineData("{ws}/demo/infra/Gone.cs", true)]
    [InlineData("./demo/repos/demo/infra/Gone.cs", true)]
    [InlineData("{ws}/demo/repos/demo/infra/Gone.cs", true)]
    [InlineData("{ws}/demo/demo/infra/Gone.cs", true)]
    [InlineData("{ws}/demo/main/repo/infra/Gone.cs", true)]
    [InlineData("src/App.cs", false)]
    [InlineData("demo/src/App.cs", false)]
    [InlineData("repo/src/App.cs", false)]
    [InlineData("{ws}/repo/src/App.cs", false)]
    [InlineData("repos/demo/src/App.cs", false)]
    [InlineData("repos/demo/src/../src/App.cs", false)]
    [InlineData("{ws}/demo/src/App.cs", false)]
    [InlineData("{ws}/demo/repos/demo/src/App.cs", false)]
    public void Surfaces_AnchoredExtra_AnswerTheSameForEveryRecordedForm(string written, bool protectedByMask)
    {
        var workspace = SurfacesWorkspace();
        var form = written.Replace(WorkspaceRoot, workspace);
        var verdicts = new[]
        {
            new Surface("hook", HookSecretPaths.IsGuarded(
                Path.GetFullPath(form, workspace), RegistryEntry(workspace))),
            new Surface("stats", LedgerHidesTheRow(workspace, form)),
            new Surface("redaction", ScrubRedactsTheValue(workspace, form)),
        };

        var diverging = verdicts.Where(v => v.Protected != protectedByMask).Select(v => v.Name).ToList();
        diverging.ShouldBeEmpty();

        // the mask alone answers only the stored shape: without the fold every row here would be a silent fail-open
        SecretGuard.IsProtected(form, [AnchoredExtra]).ShouldBe(protectedByMask && form == GoneFile);
    }

    private sealed record Surface(string Name, bool Protected);

    // the ledger's way of saying 'protected': the row leaves the arithmetic before either bucket, so a guarded
    // path can neither inflate the baseline nor land in 'not in the index'
    private static bool LedgerHidesTheRow(string workspace, string form)
    {
        var summary = QueryTelemetrySummary.Summarize(
        [
            new QueryTelemetryEntry(
                DateTime.UtcNow, Guid.NewGuid(), "expand", new { file = form },
                7, false, 0, 5, false, null, "cli", null, false),
        ], workspace);

        return summary.Savings.CounterfactualTokens == 0 && summary.Savings.UnknownFiles == 0;
    }

    private static bool ScrubRedactsTheValue(string workspace, string form)
    {
        QueryTelemetry.Record(workspace, Guid.NewGuid(), "expand", new { file = form }, 120, false, 1, 5);
        var forms = GuardedPathForms.Load(workspace, SecretGuardIngestion.Load(workspace));

        return QueryTelemetry.RedactProtectedParams(workspace, forms.NamesAProtectedPath) == 1;
    }

    private static WorkspaceRegistryEntry RegistryEntry(string workspace) =>
        new("surfaces", workspace, "surfaces", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    // 'main' is a single-segment repo dir, 'demo' sits under 'repos/' — so the qualified readings of one file are
    // closed by different halves of the fold, and the mask is anchored on purpose: an unanchored one would match
    // several shapes without the fold doing anything.
    private static string SurfacesWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        Directory.CreateDirectory(Path.Combine(workspace, "repos", "demo"));
        manager.AddRepo("repo", "main");
        manager.AddRepo("repos/demo", "demo");
        Write(workspace, "repo/src/App.cs", "public class App { public void Run() { } }\n");
        new WorkspaceIndexer().Index(workspace);

        SecretGuardFixture.SetGuardExtra(workspace, AnchoredExtra);
        return workspace;
    }

    private static void Write(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
