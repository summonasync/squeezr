using Shouldly;
using Squeezr.Cli;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

public sealed class WorkspaceRootResolverGuardTests
{
    [Fact]
    public void GuardRegistryIo_SharingViolation_IsReportedAsRegistryBusy()
    {
        var thrown = Should.Throw<WorkspaceException>(
            () => WorkspaceRootResolver.GuardRegistryIo<int>(() => throw new IOException("the process cannot access the file")));

        thrown.Code.ShouldBe(WorkspaceException.RegistryBusyCode);
    }

    [Fact]
    public void GuardRegistryIo_BusyMessage_CarriesTheUnderlyingCause()
    {
        var thrown = Should.Throw<WorkspaceException>(
            () => WorkspaceRootResolver.GuardRegistryIo<int>(
                () => throw new DirectoryNotFoundException("Could not find a part of the path 'C:\\'.")));

        thrown.Code.ShouldBe(WorkspaceException.RegistryBusyCode);
        thrown.Message.ShouldContain(nameof(DirectoryNotFoundException));
        thrown.Message.ShouldContain("Could not find a part of the path");
        thrown.InnerException.ShouldBeOfType<DirectoryNotFoundException>();
    }

    [Fact]
    public void GuardRegistryIo_SuccessfulOperation_ReturnsTheValue()
    {
        WorkspaceRootResolver.GuardRegistryIo(() => 42).ShouldBe(42);
    }
}
