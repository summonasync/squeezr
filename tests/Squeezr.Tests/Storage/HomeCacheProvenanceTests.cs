using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Storage;

// 118/T2. The process-wide home cache answers from a value it computed once, and the 118 burst measured
// what that costs when the once happened to fall in a window where the pin was gone: every later write in
// the process lands in whichever home the blank window named. Two branches, deliberately side by side -
// a fallback fill that a later pin must displace, and a pin fill that stays frozen when the variable is
// cleared afterwards. The second is what CliTestProcess.TestRunHome depends on and what REQ-5 forbids
// changing, so it is pinned here rather than left to the reader's trust.
[Collection("ProcessGlobals")]
public sealed class HomeCacheProvenanceTests : IDisposable
{
    private readonly string? _previousEnv =
        Environment.GetEnvironmentVariable(SqueezrHome.HomeEnvironmentVariable);

    [Fact]
    public void Resolve_PinWrittenAfterAFallbackFill_DisplacesTheFrozenFallback()
    {
        var scratch = TestDirs.NewTempDir();

        Environment.SetEnvironmentVariable(SqueezrHome.HomeEnvironmentVariable, null);
        SqueezrHome.Reset();
        SqueezrHome.Resolve().ShouldNotBe(scratch);

        Environment.SetEnvironmentVariable(SqueezrHome.HomeEnvironmentVariable, scratch);
        SqueezrHome.Resolve().ShouldBe(scratch);
    }

    [Fact]
    public void Resolve_PinReadAtTheFill_StaysTheAnswerAfterTheVariableIsCleared()
    {
        var scratch = TestDirs.NewTempDir();

        Environment.SetEnvironmentVariable(SqueezrHome.HomeEnvironmentVariable, scratch);
        SqueezrHome.Reset();
        SqueezrHome.Resolve().ShouldBe(scratch);

        Environment.SetEnvironmentVariable(SqueezrHome.HomeEnvironmentVariable, null);
        SqueezrHome.Resolve().ShouldBe(scratch,
            customMessage: "a value that came from an explicit pin is the run's intent, not a snapshot of " +
                           "whichever blank window came second");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SqueezrHome.HomeEnvironmentVariable, _previousEnv);
        SqueezrHome.Reset();
    }
}
