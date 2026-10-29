using Shouldly;
using Squeezr.Core.Indexing.Watch;

namespace Squeezr.Tests.Indexing.Watch;

public sealed class ProcStatTests
{
    [Theory]
    [InlineData("4242 (cat (x)) Z 1 4242 4242 0 -1 4194560 0 0 0", 'Z')]
    [InlineData("4242 (squeez r) R 1 4242 4242 0 -1 4194560 0 0 0", 'R')]
    [InlineData("4242 (bash) S ", 'S')]
    [InlineData("99 (sd-pam) S", 'S')]
    [InlineData("4242 (bash) Z", 'Z')]
    public void ParseState_CommWithParensAndSpaces_ReturnsKernelState(string line, char expected)
    {
        ProcStat.ParseState(line).ShouldBe(expected);
    }

    [Fact]
    public void ParseState_LineWithoutClosingParen_ReturnsNull()
    {
        ProcStat.ParseState("4242 (truncat").ShouldBeNull();
    }

    [Fact]
    public void ParseState_StateMissingAfterComm_ReturnsNull()
    {
        ProcStat.ParseState("4242 (bash) ").ShouldBeNull();
    }

    [Fact]
    public void ParseState_EmptyLine_ReturnsNull()
    {
        ProcStat.ParseState("").ShouldBeNull();
    }

    [LinuxFact]
    public void TryReadState_CurrentProcess_ReturnsItsKernelState()
    {
        ProcStat.TryReadState(Environment.ProcessId).ShouldNotBeNull();
    }

    [LinuxFact]
    public void TryReadState_GonePid_ReturnsNull()
    {
        ProcStat.TryReadState(1).ShouldNotBeNull();

        var gone = 4000000;
        while (Directory.Exists($"/proc/{gone}"))
        {
            gone++;
        }

        ProcStat.TryReadState(gone).ShouldBeNull();
    }
}
