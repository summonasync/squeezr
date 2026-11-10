using Shouldly;
using Squeezr.Cli.Infra.Autostart;
using Squeezr.Core.Infra.Autostart;
using Squeezr.Tests.Infra.Autostart;

namespace Squeezr.Tests.Cli;

public sealed class LaunchabilityTests
{
    private const string RemedyFragment = "installed squeezr binary";

    [LinuxFact]
    public void Require_WhenTheProgramCarriesTheExecuteBit_AcceptsIt()
    {
        var program = TestProgram.Executable("squeezr");

        Should.NotThrow(() => Launchability.Require(AutostartPlatform.SystemdUser, program));
    }

    [LinuxFact]
    public void Require_OnTheLaunchAgentBranch_UsesTheSameExecuteBitRule()
    {
        var program = TestProgram.NotExecutable("squeezr");

        var error = Should.Throw<AutostartException>(
            () => Launchability.Require(AutostartPlatform.LaunchAgent, program));

        error.Message.ShouldContain("execute bit");
    }

    [LinuxFact]
    public void Require_WhenTheProgramCarriesNoExecuteBit_RefusesNamingThePathAndTheRemedy()
    {
        var program = TestProgram.NotExecutable("squeezr");

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
        var program = TestProgram.Write("squeezr.dll", TestProgram.UserReadWriteExecute);

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
        var program = TestProgram.NotExecutable("squeezr.exe");

        Should.NotThrow(() => Launchability.Require(AutostartPlatform.WindowsScheduledTask, program));
    }

    // cmd.exe re-parses the /TR token, so on this branch executability is a question of the program
    // type, never of the file mode the host filesystem happens to carry.
    [Fact]
    public void Require_OnTheWindowsBranch_RefusesACommandLineScript()
    {
        var program = TestProgram.Executable("squeezr.ps1");

        var error = Should.Throw<AutostartException>(
            () => Launchability.Require(AutostartPlatform.WindowsScheduledTask, program));

        error.Message.ShouldContain(program);
        error.Message.ShouldContain(".ps1");
        error.Message.ShouldContain(RemedyFragment);
    }
}
