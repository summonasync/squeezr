using System.Diagnostics;
using Shouldly;
using Squeezr.Cli.Infra.Autostart;
using Squeezr.Core.Infra.Autostart;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class AutostartProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_PassesArgumentsAsTokensAndReadsTheAnswer()
    {
        var runner = new AutostartProcessRunner();

        var result = await runner.RunAsync("dotnet", ["--version"], CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.Trim().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task RunRawAsync_HandsTheCommandLineToTheProcessUnchanged()
    {
        var runner = new AutostartProcessRunner();

        var result = await runner.RunRawAsync("dotnet", "--version", CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.StandardOutput.Trim().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task RunAsync_WhenTheToolIsNotThere_FailsWithAReadableRefusal()
    {
        var runner = new AutostartProcessRunner();
        var missing = Path.Combine(Path.GetTempPath(), "squeezr-autostart-no-such-tool");

        var error = await Should.ThrowAsync<AutostartException>(
            () => runner.RunAsync(missing, ["--version"], CancellationToken.None));

        error.Message.ShouldContain(missing);
        error.Message.ShouldStartWith("Could not start");
    }

    // The cancellation is the caller's, not the 30 s budget, so the branch that walks away from a live
    // tool is reachable in a test without paying for the budget: the operator's cancellation has to
    // come back as itself, not as a timeout, and the run has to be over promptly.
    [LinuxFact]
    public async Task RunAsync_WhenTheCallerCancels_KillsTheToolAndRethrowsTheCancellation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var started = Stopwatch.StartNew();

        var error = await Should.ThrowAsync<OperationCanceledException>(
            () => new AutostartProcessRunner().RunAsync("/bin/sh", ["-c", "sleep 30"], cts.Token));

        started.Stop();
        error.Message.ShouldNotContain("did not finish within");
        started.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    // The other half of that branch split: when nobody cancels and only the budget runs out, the run
    // has to come back as a timeout refusal naming the tool and the seconds it was given. The budget
    // is injected at two seconds so the test measures the leg, not the shipped thirty.
    [LinuxFact]
    public async Task RunAsync_WhenTheBudgetRunsOut_RefusesWithTheTimeoutItSaw()
    {
        var runner = new AutostartProcessRunner { RunBudget = TimeSpan.FromSeconds(2) };
        var started = Stopwatch.StartNew();

        var error = await Should.ThrowAsync<AutostartException>(
            () => runner.RunAsync("/bin/sh", ["-c", "sleep 30"], CancellationToken.None));

        started.Stop();
        error.Message.ShouldBe("'/bin/sh' did not finish within 2 seconds.");
        started.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    // A killed tool leaves its pipe readers mid-flight. Draining means two things and both are
    // measured here: every open read is waited for before the caller moves on, and the answer a torn
    // stream gives is discarded rather than thrown at the cancellation it follows.
    [Fact]
    public async Task DrainQuietly_WaitsForEveryOpenReadAndKeepsATornStreamQuiet()
    {
        var stdout = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderr = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var draining = AutostartProcessRunner.DrainQuietly(stdout.Task, stderr.Task);
        draining.IsCompleted.ShouldBeFalse();

        stdout.SetResult("x");
        draining.IsCompleted.ShouldBeFalse();

        stderr.SetException(new IOException("the redirected stream was torn"));
        await draining;

        stdout.Task.IsCompleted.ShouldBeTrue();
        stderr.Task.IsCompleted.ShouldBeTrue();
    }
}
