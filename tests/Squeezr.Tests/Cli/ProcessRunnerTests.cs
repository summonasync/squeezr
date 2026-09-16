using System.Diagnostics;
using Shouldly;
using Squeezr.Cli.Infra;

namespace Squeezr.Tests.Cli;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_CancelledDuringExecution_KillsProcessAndThrowsOperationCanceled()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var invocation = new ProcessInvocation("/bin/sh", ["-c", "sleep 30"], TimeSpan.FromMinutes(5));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var stopwatch = Stopwatch.StartNew();
        var ex = await Should.ThrowAsync<OperationCanceledException>(
            () => new ProcessRunner().RunAsync(invocation, cts.Token));
        stopwatch.Stop();

        ex.Message.ShouldNotContain("timed out");
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task RunAsync_ProgressHandler_ReceivesOutputChunksWhileBuffering()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var chunks = new List<string>();
        var invocation = new ProcessInvocation(
            "/bin/sh",
            ["-c", "echo stdout-marker; echo stderr-marker >&2"],
            TimeSpan.FromMinutes(1),
            ProgressHandler: chunks.Add);

        var result = await new ProcessRunner().RunAsync(invocation, CancellationToken.None);

        result.StdOut.ShouldContain("stdout-marker");
        result.StdErr.ShouldContain("stderr-marker");
        chunks.ShouldContain(chunk => chunk.Contains("stdout-marker"));
        chunks.ShouldContain(chunk => chunk.Contains("stderr-marker"));
    }
}
