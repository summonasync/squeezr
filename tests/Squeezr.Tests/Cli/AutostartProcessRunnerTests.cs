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
}
