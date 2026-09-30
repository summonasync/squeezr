using Shouldly;
using Spectre.Console;
using Spectre.Console.Testing;
using Squeezr.Cli.Output;

namespace Squeezr.Tests.Cli;

public class RedirectedOutputTests
{
    private const string BudgetLine =
        "tokens: 36, truncated - read a member with expand <file> <line> or raise --max-tokens";

    [Fact]
    public void ApplyLayout_OutputRedirected_KeepsContractLineOnOneLogicalLine()
    {
        var console = new TestConsole();
        console.Profile.Width = 80;

        RedirectedOutput.ApplyLayout(console, outputRedirected: true);
        console.MarkupLine(BudgetLine.EscapeMarkup());

        console.Output.ShouldContain(BudgetLine);
    }

    [Fact]
    public void ApplyLayout_OutputIsTerminal_LeavesWrappingToIntact()
    {
        var console = new TestConsole();
        console.Profile.Width = 80;

        RedirectedOutput.ApplyLayout(console, outputRedirected: false);
        console.MarkupLine(BudgetLine.EscapeMarkup());

        console.Output.ShouldNotContain(BudgetLine);
        console.Output.ShouldContain("tokens: 36, truncated");
    }
}
