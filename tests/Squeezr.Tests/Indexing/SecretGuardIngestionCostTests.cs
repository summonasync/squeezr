using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Xunit.Abstractions;

namespace Squeezr.Tests.Indexing;

// The guard answers once per walked path, so the number that matters is one decision with a mask configured.
// Next to an empty mask list the regression is invisible — with no masks the compiled half is short-circuited
// away — so the ingestion scope is measured against the per-call facade over the same three masks. A scope
// that compiles per path costs what the facade costs, and that is the shape this kills.
[Collection("ProcessGlobals")]
public class SecretGuardIngestionCostTests(ITestOutputHelper output)
{
    private const int Decisions = 20_000;
    private static readonly string[] Masks = ["**/tokens.txt", "**/*.credentials", "**/*.crt"];
    private static readonly string[] SweptPaths = BuildSweep();

    [Fact]
    public void IngestionScopeDecidesCheaperThanThePerCallFacade()
    {
        var workspace = IngestionWorkspaces.NewWorkspaceWithPlainFile();
        SecretGuardFixture.SetGuardExtra(workspace, Masks);
        var guard = SecretGuardIngestion.Load(workspace);
        var extras = ConfigFile.Load(workspace).Indexing.SecretGuardExtra;

        var perScope = BytesPerDecision(guard.IsProtected);
        var perCall = BytesPerDecision(path => SecretGuard.IsProtected(path, extras));

        output.WriteLine($"ingestion scope {perScope:F0} B/decision; per-call facade {perCall:F0} B/decision; " +
            $"ratio {perScope / perCall:F2}");

        perScope.ShouldBeLessThan(perCall * 0.6,
            "the ingestion scope allocates like a per-call compile: the mask list is being parsed once per path");
    }

    [Fact]
    public void ScopeCarriesTheCompiledHalfOfItsOwnMaskList()
    {
        new SecretGuard.Profile(Masks).CompiledExtras.ShouldBe(3);
        new SecretGuard.Profile([]).CompiledExtras.ShouldBe(0);
        new SecretGuard.Profile(["", "   ", null!, "!**/tokens.txt", "tokens/", "# comment"])
            .CompiledExtras.ShouldBe(0);
    }

    [Fact]
    public void ScopeAndFacadeAgreeOnEveryShapeTheyAreAskedAbout()
    {
        var profile = new SecretGuard.Profile(Masks);

        foreach (var shape in new[]
                 {
                     "notes/tokens.txt", "tokens.txt", "a/b/c/credentials", "server.crt", "App.cs",
                     "web/.env.example", "web/.env", "deploy/keys/id_ed25519", string.Empty, "UPPER.TXT",
                 })
        {
            profile.IsProtected(shape).ShouldBe(
                SecretGuard.IsProtected(shape, Masks), $"scope and facade diverged on '{shape}'");
        }
    }

    private static double BytesPerDecision(Func<string, bool> decide)
    {
        foreach (var path in SweptPaths.Take(500))
        {
            decide(path);
        }

        // per-thread accounting: the suite runs other classes in parallel, and a process-wide counter would
        // charge this sweep for allocations the neighbouring tests never touched
        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var path in Repeat(Decisions))
        {
            decide(path);
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Decisions;
    }

    private static IEnumerable<string> Repeat(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return SweptPaths[i % SweptPaths.Length];
        }
    }

    // both arms must pay the same per-path shaping cost, so the sweep mixes depths, cases and mask names
    private static string[] BuildSweep()
    {
        var names = new[] { "App.cs", "tokens.txt", "notes.md", "server.crt", "readme.md", "policy.credentials" };
        var paths = new List<string>();
        for (var depth = 0; depth < 6; depth++)
        {
            var prefix = string.Join('/', Enumerable.Range(0, depth).Select(i => $"dir{i}"));
            foreach (var name in names)
            {
                paths.Add(prefix.Length == 0 ? name : $"{prefix}/module{depth}/{name}");
            }
        }

        return [.. paths, .. paths.Select(p => p.ToUpperInvariant())];
    }
}
