using System.Text;
using System.Text.Json;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Testing;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class SymbolBudgetStateTests
{
    // 120 tokens refuse the body and still carry all twelve member rows: that answer is mapped, not cut.
    private const int MembersFitBudget = 120;

    // 40 tokens refuse the body and lose part of the member map: that one really is clipped.
    private const int MembersOverflowBudget = 40;

    private const int MethodCount = 12;

    private const string MembersRemedy = "read a member with expand <file> <line> or raise --max-tokens";

    private static readonly TruncationHint MembersHint = new(
    [
        new("read a member with", ["expand_span"]),
        new("raise", ["maxTokens"])
    ]);

    [Fact]
    public void BudgetLine_Complete_AnswersPlainTokens()
    {
        var output = Render(() => SymbolCommand.WriteBudgetLine(36, AnswerState.Complete, MembersRemedy));

        output.ShouldBe("tokens: 36");
    }

    [Fact]
    public void BudgetLine_Mapped_AnswersPlainTokensWithNoTruncationClaim()
    {
        var output = Render(() => SymbolCommand.WriteBudgetLine(164, AnswerState.Mapped, MembersRemedy));

        output.ShouldBe("tokens: 164");
        output.ShouldNotContain("truncated");
    }

    [Fact]
    public void BudgetLine_Clipped_NamesTheLossAndTheRemedy()
    {
        var output = Render(() => SymbolCommand.WriteBudgetLine(40, AnswerState.Clipped, MembersRemedy));

        output.ShouldBe($"tokens: 40, truncated — {MembersRemedy}");
    }

    [Fact]
    public void BudgetLine_ClippedWithNoRemedyOffered_PrintsTheLossWithoutAWord()
    {
        var output = Render(() => SymbolCommand.WriteBudgetLine(12, AnswerState.Clipped, null));

        output.ShouldBe("tokens: 12, truncated");
    }

    [Fact]
    public void Remedy_BodyRefusedWithNoMemberMap_NamesItsPrice() =>
        Answer(AnswerState.Clipped, refusedBodyTokens: 225, MembersHint)
            .RemedyForCli()
            .ShouldBe("raise --max-tokens (the body alone needs ~225 tokens)");

    [Fact]
    public void Remedy_FullMemberMapCarried_NamesTheRowRemedyDespiteThePrice() =>
        Answer(AnswerState.Mapped, refusedBodyTokens: 225, MembersHint, new SymbolMember("Method01", "method", 5))
            .RemedyForCli()
            .ShouldBe(MembersRemedy);

    [Fact]
    public void Remedy_NoHintCarried_FallsBackToRaisingTheCeiling() =>
        Answer(AnswerState.Clipped, refusedBodyTokens: 0, hint: null)
            .RemedyForCli()
            .ShouldBe("raise --max-tokens");

    [Fact]
    public async Task Cli_Symbol_MappedAnswer_MakesNoTruncationClaimInEitherOutputForm()
    {
        var workspace = NewHugeWorkspace();

        var pretty = await CliTestProcess.RunAsync(
            workspace, "symbol", "Huge", "--max-tokens", MembersFitBudget.ToString());
        pretty.ExitCode.ShouldBe(0);
        pretty.StdOut.ShouldContain("body: omitted (budget) — members:");
        pretty.StdOut.ShouldContain("Method01 (method) :5");
        BudgetLine(pretty.StdOut).ShouldMatch(@"^tokens: \d+$");
        pretty.StdOut.ShouldNotContain("truncated");

        var json = await CliTestProcess.RunAsync(
            workspace, "symbol", "Huge", "--max-tokens", MembersFitBudget.ToString(), "--json");
        json.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(json.StdOut.Trim());
        doc.RootElement.GetProperty("state").GetString().ShouldBe("mapped");
        doc.RootElement.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        doc.RootElement.TryGetProperty("hint", out _).ShouldBeFalse();
        var match = doc.RootElement.GetProperty("matches").EnumerateArray().ShouldHaveSingleItem();
        match.GetProperty("bodyOmitted").GetBoolean().ShouldBeTrue();
        match.TryGetProperty("body", out _).ShouldBeFalse();
        match.GetProperty("members").EnumerateArray().Count().ShouldBe(MethodCount);
    }

    [Fact]
    public async Task Cli_Symbol_ClippedAnswer_NamesTheLossInBothOutputForms()
    {
        var workspace = NewHugeWorkspace();

        var pretty = await CliTestProcess.RunAsync(
            workspace, "symbol", "Huge", "--max-tokens", MembersOverflowBudget.ToString());
        pretty.ExitCode.ShouldBe(0);
        BudgetLine(pretty.StdOut).ShouldMatch($@"^tokens: \d+, truncated — {MembersRemedy}$");

        var json = await CliTestProcess.RunAsync(
            workspace, "symbol", "Huge", "--max-tokens", MembersOverflowBudget.ToString(), "--json");
        json.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(json.StdOut.Trim());
        doc.RootElement.GetProperty("state").GetString().ShouldBe("clipped");
        doc.RootElement.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("hint").GetString().ShouldBe(MembersRemedy);
        var match = doc.RootElement.GetProperty("matches").EnumerateArray().ShouldHaveSingleItem();
        match.GetProperty("membersOmitted").GetInt32().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Cli_Symbol_BodyRefusedWithNoSubstitute_PricedRemedyOnTheHumanLine()
    {
        var workspace = NewHugeWorkspace();

        var pretty = await CliTestProcess.RunAsync(workspace, "symbol", "Method01", "--max-tokens", "25");
        pretty.ExitCode.ShouldBe(0);
        BudgetLine(pretty.StdOut)
            .ShouldMatch(@"^tokens: \d+, truncated — raise --max-tokens \(the body alone needs ~\d+ tokens\)$");
    }

    [Fact]
    public async Task Cli_MapBudgetLine_ReportsTheLossOnlyWhereTheMapLostRows()
    {
        var workspace = NewHugeWorkspace();

        var fits = await CliTestProcess.RunAsync(workspace, "map");
        fits.ExitCode.ShouldBe(0);
        BudgetLine(fits.StdOut).ShouldMatch(@"^tokens: \d+$");
        fits.StdOut.ShouldNotContain("truncated");

        var cut = await CliTestProcess.RunAsync(workspace, "map", "--max-tokens", "4");
        cut.ExitCode.ShouldBe(0);
        BudgetLine(cut.StdOut)
            .ShouldMatch(@"^tokens: \d+, truncated — lower --depth or raise --max-tokens$");
    }

    private static SymbolLookupResult Answer(
        AnswerState state,
        int refusedBodyTokens,
        TruncationHint? hint,
        SymbolMember? member = null) =>
        new(
            [
                new SymbolHit(
                    "demo", "src/Huge.cs", 3, 60, "Huge", "class",
                    null, null, null, null, BodyOmitted: true,
                    member is null ? [] : [member], 0)
            ],
            36,
            state is not AnswerState.Complete,
            hint,
            null,
            null,
            state,
            refusedBodyTokens);

    private static string BudgetLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("tokens: ", StringComparison.Ordinal)).Trim();

    private static string Render(Action write)
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = false;
        console.Profile.Width = 400;
        var previous = AnsiConsole.Console;
        AnsiConsole.Console = console;
        try
        {
            write();
        }
        finally
        {
            AnsiConsole.Console = previous;
        }

        return console.Output.Trim();
    }

    private static string NewHugeWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        var file = Path.Combine(workspace, "repo/src/Huge.cs".Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, HugeClass());
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static string HugeClass()
    {
        var text = new StringBuilder("namespace Demo;\n\npublic class Huge\n{\n");
        foreach (var index in Enumerable.Range(1, MethodCount))
        {
            text.Append($"    public int Method{index:00}(int a)\n");
            text.Append("    {\n");
            text.Append($"        return a + {index};\n");
            text.Append("    }\n\n");
        }

        return text.Append("}\n").ToString();
    }
}
