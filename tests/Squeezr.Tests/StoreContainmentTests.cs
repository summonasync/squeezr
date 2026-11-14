using System.Runtime.Versioning;
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

    // The 118/T3 runtime half, split the way the guard is split: a store captured from an explicit pin is
    // accepted, a store captured from the fallback is refused on sight, and at module load a value that IS
    // a profile home is refused with the path named. Every arm is asserted in both directions because a
    // guard that only ever throws is as useless as one that never does.
    [Fact]
    public void RunHomeGuard_PinCapturedStore_IsAcceptedUnchanged()
    {
        var scratch = TestDirs.NewTempDir();

        TestRunHomeGuard.RequirePinnedSnapshot(scratch, capturedFromPin: true).ShouldBe(scratch);
    }

    [Fact]
    public void RunHomeGuard_FallbackCapturedStore_IsRefusedEvenWhileHomePointsAtTheSandbox()
    {
        var fallback = SqueezrHome.DefaultHome();
        (fallback.Length > 0).ShouldBeTrue(customMessage: "no profile home resolves on this machine, so " +
            "this arm would assert that an empty string is contained — a green that measured nothing");

        // The state the real suite reaches: a neighbour has restored HOME by the time a child spawns, so
        // the ambient environment looks healthy and only the provenance of the frozen value is wrong.
        var error = Should.Throw<InvalidOperationException>(
            () => TestRunHomeGuard.RequirePinnedSnapshot(fallback, capturedFromPin: false));

        error.Message.ShouldContain(fallback);
        error.Message.ShouldContain(TestRunHomeGuard.Remedy);
    }

    [Fact]
    public void RunHomeGuard_StoreThatResolvedToNothing_IsRefusedWithTheRemedy()
    {
        var error = Should.Throw<InvalidOperationException>(
            () => TestRunHomeGuard.RequireContainedAtLoad(string.Empty));

        error.Message.ShouldContain(TestRunHomeGuard.Remedy);
    }

    [Fact]
    public void RunHomeGuard_ExternalPinPointingAtTheProfileHome_IsRefused()
    {
        var profileHome = SqueezrHome.DefaultHome();
        (profileHome.Length > 0).ShouldBeTrue(customMessage: "no profile home resolves here, so this arm " +
            "would assert that an empty pin is refused for the wrong reason");

        var previousEnv = Environment.GetEnvironmentVariable(SqueezrHome.HomeEnvironmentVariable);
        try
        {
            // The branch AC3 names first: a pin that exists, is honoured, and happens to name the developer's
            // profile store - the CI/debug shape REQ-1 calls a hard error. It must be refused at module load,
            // not discovered as leaked rows three classes later.
            Environment.SetEnvironmentVariable(SqueezrHome.HomeEnvironmentVariable, profileHome);
            SqueezrHome.Reset();

            var error = Should.Throw<InvalidOperationException>(
                () => TestRunHomeGuard.RequireContainedAtLoad(SqueezrHome.Resolve()));

            error.Message.ShouldContain(profileHome);
            error.Message.ShouldContain(TestRunHomeGuard.Remedy);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SqueezrHome.HomeEnvironmentVariable, previousEnv);
            SqueezrHome.Reset();
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void RunHomeGuard_ProfileHomeResolution_IsRefusedAndNamesThePath()
    {
        var previousEnv = Environment.GetEnvironmentVariable("SQUEEZR_HOME");
        var previousHome = Environment.GetEnvironmentVariable("HOME");
        var sentinelProfile = TestDirs.NewTempDir();
        try
        {
            Environment.SetEnvironmentVariable("SQUEEZR_HOME", null);
            Environment.SetEnvironmentVariable("HOME", sentinelProfile);
            SqueezrHome.Reset();
            var profileHome = SqueezrHome.Resolve();

            profileHome.ShouldStartWith(sentinelProfile);
            var error = Should.Throw<InvalidOperationException>(
                () => TestRunHomeGuard.RequireContainedAtLoad(profileHome));

            error.Message.ShouldContain(profileHome);
            error.Message.ShouldContain(TestRunHomeGuard.Remedy);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", previousHome);
            Environment.SetEnvironmentVariable("SQUEEZR_HOME", previousEnv);
            SqueezrHome.Reset();
        }
    }
}
