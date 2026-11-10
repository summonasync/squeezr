using Shouldly;
using Squeezr.Cli.Infra.Autostart;
using Squeezr.Core.Infra.Autostart;

namespace Squeezr.Tests.Cli;

public sealed class LaunchabilityTests
{
    private const string RemedyFragment = "installed squeezr binary";

    [LinuxFact]
    public void Require_WhenTheProgramCarriesTheExecuteBit_AcceptsIt()
    {
        var program = WriteProgram("squeezr", UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        Should.NotThrow(() => Launchability.Require(AutostartPlatform.SystemdUser, program));
    }

    [LinuxFact]
    public void Require_OnTheLaunchAgentBranch_UsesTheSameExecuteBitRule()
    {
        var program = WriteProgram("squeezr", UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var error = Should.Throw<AutostartException>(
            () => Launchability.Require(AutostartPlatform.LaunchAgent, program));

        error.Message.ShouldContain("execute bit");
    }

    [LinuxFact]
    public void Require_WhenTheProgramCarriesNoExecuteBit_RefusesNamingThePathAndTheRemedy()
    {
        var program = WriteProgram("squeezr", UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var error = Should.Throw<AutostartException>(
            () => Launchability.Require(AutostartPlatform.SystemdUser, program));

        error.Message.ShouldContain(program);
        error.Message.ShouldContain("does not carry the execute bit");
        error.Message.ShouldContain(RemedyFragment);
    }

    // The execute bit alone is not enough: an assembly that was chmod'ed anyway still cannot be the
    // program a unit names, and the guard says so before the bit is ever read.
    [LinuxFact]
    public void Require_WhenTheAssemblyCarriesTheExecuteBit_StillRefusesAsAManagedAssembly()
    {
        var program = WriteProgram(
            "squeezr.dll",
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var error = Should.Throw<AutostartException>(
            () => Launchability.Require(AutostartPlatform.SystemdUser, program));

        error.Message.ShouldContain(program);
        error.Message.ShouldContain("managed assembly");
    }

    [Fact]
    public void Require_WhenTheProgramIsAbsent_RefusesWithoutTouchingTheInitSystem()
    {
        var program = Path.Combine(TestDirs.NewTempDir(), "squeezr");

        var error = Should.Throw<AutostartException>(
            () => Launchability.Require(AutostartPlatform.SystemdUser, program));

        error.Message.ShouldContain(program);
        error.Message.ShouldContain("does not exist");
    }

    [Fact]
    public void Require_OnTheWindowsBranch_AcceptsAProgramExecutable()
    {
        var program = WriteProgram("squeezr.exe", UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Should.NotThrow(() => Launchability.Require(AutostartPlatform.WindowsScheduledTask, program));
    }

    // cmd.exe re-parses the /TR token, so on this branch executability is a question of the program
    // type, never of the file mode the host filesystem happens to carry.
    [Fact]
    public void Require_OnTheWindowsBranch_RefusesACommandLineScript()
    {
        var program = WriteProgram("squeezr.ps1", UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute);

        var error = Should.Throw<AutostartException>(
            () => Launchability.Require(AutostartPlatform.WindowsScheduledTask, program));

        error.Message.ShouldContain(program);
        error.Message.ShouldContain(".ps1");
        error.Message.ShouldContain(RemedyFragment);
    }

    private static string WriteProgram(string fileName, UnixFileMode mode)
    {
        var path = Path.Combine(TestDirs.NewTempDir(), fileName);
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }

        return path;
    }
}
