using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests;

// 118/T2. T1 measured the leak: the test host itself wrote 17 registrations into the developer's
// ~/.squeezr, every one of them after a fact in Storage.SqueezrHomeTests removed SQUEEZR_HOME from the
// process and called SqueezrHome.Reset(). What is still not pinned down is WHICH value the process-wide
// cache took — SqueezrTestEnvironment redirects HOME to a sandbox at module load, so DefaultHome()
// should already be confined, yet the rows landed in the real profile store. This class states the
// invariant the harness must hold and, while it fails, prints the four values that settle the question.
// The assertion is deliberately a containment predicate, not "home != DefaultHome()": a run pinned to
// REDACTED-PATH/scratch/... (the documented convention) is legitimate and must not trip.
[Collection("ProcessGlobals")]
public sealed class StoreContainmentTests
{
    [Fact]
    public void InProcHome_AfterAnUnpinInsideTheCollection_StaysInsideTheRunSandbox()
    {
        var pin = Environment.GetEnvironmentVariable("SQUEEZR_HOME");
        var home = Environment.GetEnvironmentVariable("HOME");
        var resolvedBefore = SqueezrHome.Resolve();

        // Reproduce exactly what SqueezrHomeTests does on its way through the serialized collection.
        Environment.SetEnvironmentVariable("SQUEEZR_HOME", null);
        SqueezrHome.Reset();
        var resolvedDuring = SqueezrHome.Resolve();

        Environment.SetEnvironmentVariable("SQUEEZR_HOME", pin);
        SqueezrHome.Reset();
        var resolvedAfter = SqueezrHome.Resolve();

        var runRoot = WorkspaceCanonicalPath.ResolveThroughLinks(TestDirs.FixtureBaseRoot());
        var sandbox = WorkspaceCanonicalPath.ResolveThroughLinks(TestDirs.SharedClientHomeDir);
        var during = WorkspaceCanonicalPath.ResolveThroughLinks(resolvedDuring);

        var escapes = !during.StartsWith(runRoot, StringComparison.Ordinal)
                   && !during.StartsWith(sandbox, StringComparison.Ordinal);

        // The message is the diagnosis: whichever of the four homes sits outside the sandbox is the one
        // a later in-proc WorkspaceManager.Init will inherit if the cache is not repaired.
        escapes.ShouldBeFalse(customMessage: $"""
            in-proc home escaped the run sandbox while SQUEEZR_HOME was unset
              resolvedBefore = {resolvedBefore}
              resolvedDuring = {resolvedDuring}
              resolvedAfter  = {resolvedAfter}
              process HOME   = {home}
              SQUEEZR_HOME   = {pin ?? "(unset)"}
              runRoot        = {runRoot}
              sandbox        = {sandbox}
              DefaultHome()  = {SqueezrHome.DefaultHome()}
            """);
    }

    [Fact]
    public void InProcHome_AfterWhitespacePin_ReportsWhichValueTheCacheFroze()
    {
        var pin = Environment.GetEnvironmentVariable("SQUEEZR_HOME");
        Environment.SetEnvironmentVariable("SQUEEZR_HOME", "   ");
        SqueezrHome.Reset();
        var whitespace = SqueezrHome.Resolve();
        Environment.SetEnvironmentVariable("SQUEEZR_HOME", pin);
        SqueezrHome.Reset();

        var during = WorkspaceCanonicalPath.ResolveThroughLinks(whitespace);
        var runRoot = WorkspaceCanonicalPath.ResolveThroughLinks(TestDirs.FixtureBaseRoot());
        var sandbox = WorkspaceCanonicalPath.ResolveThroughLinks(TestDirs.SharedClientHomeDir);

        (during.StartsWith(runRoot, StringComparison.Ordinal) || during.StartsWith(sandbox, StringComparison.Ordinal))
            .ShouldBeTrue(customMessage: $"""
                a whitespace SQUEEZR_HOME resolved to {whitespace};
                DefaultHome() = {SqueezrHome.DefaultHome()}, process HOME = {Environment.GetEnvironmentVariable("HOME")}
                """);
    }
}
