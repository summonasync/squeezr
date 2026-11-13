using System.Reflection;
using Shouldly;
using Squeezr.Cli.Daemons;

namespace Squeezr.Tests.Indexing.Watch.Supervision;

public sealed class DaemonLauncherPathSelectionTests
{
    [Theory]
    [InlineData("/opt/squeezr/squeezr")]
    [InlineData("/usr/local/bin/squeezr")]
    public void SelfContainedBinary_IsTheDaemonBinaryItself(string processPath)
    {
        new DaemonLauncher(() => processPath).DaemonBinaryPath().ShouldBe(processPath);
    }

    [Theory]
    [InlineData("/usr/lib/dotnet/dotnet")]
    [InlineData("/usr/lib/dotnet/dotnet.exe")]
    [InlineData("/usr/lib/dotnet/DOTNET")]
    public void DotnetMuxerd_ResolvesTheRunningAssemblyInstead(string processPath)
    {
        new DaemonLauncher(() => processPath).DaemonBinaryPath()
            .ShouldBe(Assembly.GetEntryAssembly()!.Location);
    }

    // The launch pair an autostart entry is built from. DaemonBinaryPath alone is not a program under
    // the muxer — it answers with the assembly, which is exactly the file the platform refuses to exec —
    // so the pair carries the host as its program and that assembly as the argument behind it.
    [Theory]
    [InlineData("/opt/squeezr/squeezr")]
    [InlineData("/usr/local/bin/squeezr")]
    public void SelfContainedBinary_IsTheWholeLaunchPair(string processPath)
    {
        var launch = new DaemonLauncher(() => processPath).WatchdLaunchPair();

        launch.Executable.ShouldBe(processPath);
        launch.Arguments.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/usr/lib/dotnet/dotnet")]
    [InlineData("/usr/lib/dotnet/dotnet.exe")]
    [InlineData("/usr/lib/dotnet/DOTNET")]
    public void DotnetMuxerd_PutsTheHostInThePairAndTheAssemblyBehindIt(string processPath)
    {
        var launch = new DaemonLauncher(() => processPath).WatchdLaunchPair();

        launch.Executable.ShouldBe(processPath);
        launch.Arguments.ShouldHaveSingleItem().ShouldBe(Assembly.GetEntryAssembly()!.Location);
    }
}
