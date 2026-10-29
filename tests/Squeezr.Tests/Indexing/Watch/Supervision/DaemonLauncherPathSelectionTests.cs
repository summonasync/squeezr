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
}
