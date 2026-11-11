using System.Text;
using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Xunit.Abstractions;

namespace Squeezr.Tests.Indexing.Navigation;

[Collection("ProcessGlobals")]
public class SymbolMembersTests(ITestOutputHelper output)
{
    private const int MembersFitBudget = 120;
    private const int MembersOverflowBudget = 40;
    private const int GenericFitBudget = 60;
    private const int MethodCount = 12;
    private const int HugeBodyTokens = 225;

    [Fact]
    public void Symbol_TypeBodyOverBudget_ListsMembersWithLines()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SymbolLookup(workspace).Find("Huge", null, null, MembersFitBudget, body: true);

        var hit = result.Matches.ShouldHaveSingleItem();
        hit.BodyOmitted.ShouldBeTrue();
        hit.Body.ShouldBeNull();
        hit.Members.Count.ShouldBe(MethodCount);
        hit.MembersOmitted.ShouldBe(0);
        hit.Members[0].ShouldBe(new SymbolMember("Method01", "method", 5));
        hit.Members.Select(m => m.Name).ShouldBe(Enumerable.Range(1, MethodCount).Select(i => $"Method{i:00}"));
        var lines = hit.Members.Select(m => m.Line).ToList();
        lines.ShouldBe(lines.OrderBy(line => line).ToList());
        result.Truncated.ShouldBeTrue();
        result.TokensUsed.ShouldBeLessThanOrEqualTo(MembersFitBudget);
    }

    [Fact]
    public void Symbol_MembersOverBudget_TruncatesWithRemainingCount()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SymbolLookup(workspace).Find("Huge", null, null, MembersOverflowBudget, body: true);

        var hit = result.Matches.ShouldHaveSingleItem();
        hit.Members.ShouldNotBeEmpty();
        hit.Members.Count.ShouldBeLessThan(MethodCount);
        (hit.Members.Count + hit.MembersOmitted).ShouldBe(MethodCount);
        result.Truncated.ShouldBeTrue();
        result.TokensUsed.ShouldBeLessThanOrEqualTo(MembersOverflowBudget);
    }

    [Fact]
    public void Symbol_GenericTypeBodyOverBudget_ListsMembersOfTheGenericContainer()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SymbolLookup(workspace).Find("Box", null, null, GenericFitBudget, body: true);

        var hit = result.Matches.ShouldHaveSingleItem();
        hit.BodyOmitted.ShouldBeTrue();
        hit.Body.ShouldBeNull();
        hit.Members.Select(m => m.Name).ShouldBe(["Value", "Count", "Echo", "Same", "Describe"]);
        hit.Members.Select(m => m.Kind).ShouldBe(["field", "property", "method", "method", "method"]);
        hit.MembersOmitted.ShouldBe(0);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repo = store.FindRepo("demo").ShouldNotBeNull();
        var container = store.ListSymbolsByRepo(repo.Id).Single(symbol => symbol.Name == "Box");
        store.ListSymbolsByFile(container.FileId)
            .Single(symbol => symbol.Name == "Echo")
            .ContainerName.ShouldBe("Box<T>");
    }

    [Fact]
    public void Symbol_BodyFits_KeepsBodyWithoutMembers()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SymbolLookup(workspace)
            .Find("Huge", null, null, SymbolLookup.DefaultMaxTokens, body: true);

        var hit = result.Matches.ShouldHaveSingleItem();
        hit.Body!.ShouldContain($"return a + {MethodCount};");
        hit.BodyOmitted.ShouldBeFalse();
        hit.Members.ShouldBeEmpty();
        hit.MembersOmitted.ShouldBe(0);
        result.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void Symbol_BudgetTooTightForAnyMember_FallsBackToGenericHint()
    {
        var workspace = NewIndexedWorkspace();

        // 15 tokens carry the header and signature but not a single 'Name (kind) line' row
        var result = new SymbolLookup(workspace).Find("Huge", null, null, 15, body: true);

        var hit = result.Matches.ShouldHaveSingleItem();
        hit.BodyOmitted.ShouldBeTrue();
        hit.Members.ShouldBeEmpty();
        hit.MembersOmitted.ShouldBe(0);
        result.Truncated.ShouldBeTrue();
        result.Hint.ShouldNotBeNull().Render(p => p)
            .ShouldBe("narrow the query (repo, kind) or raise maxTokens");
    }

    [Fact]
    public void Symbol_MethodBodyOverBudget_KeepsGenericHint()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SymbolLookup(workspace).Find("Method01", null, null, 25, body: true);

        var hit = result.Matches.ShouldHaveSingleItem();
        hit.BodyOmitted.ShouldBeTrue();
        hit.Members.ShouldBeEmpty();
        hit.MembersOmitted.ShouldBe(0);
        result.Hint.ShouldNotBeNull().Render(p => p)
            .ShouldBe("narrow the query (repo, kind) or raise maxTokens");
    }

    [Fact]
    public void Symbol_MembersFallback_HintNamesExpandSpanPerTransport()
    {
        var workspace = NewIndexedWorkspace();

        var hint = new SymbolLookup(workspace)
            .Find("Huge", null, null, MembersFitBudget, body: true)
            .Hint.ShouldNotBeNull();

        hint.ForMcp().ShouldBe("read a member with expand_span or raise maxTokens");
        hint.ForCli().ShouldBe("read a member with expand <file> <line> or raise --max-tokens");
    }

    [Fact]
    public async Task Cli_Symbol_OversizedType_RendersMemberLinesInBothOutputForms()
    {
        var workspace = NewIndexedWorkspace();

        var pretty = await CliTestProcess.RunAsync(
            workspace, "symbol", "Huge", "--max-tokens", MembersOverflowBudget.ToString());
        output.WriteLine($"symbol Huge --max-tokens {MembersOverflowBudget} -> exit {pretty.ExitCode}" +
                         $"\n{pretty.StdOut}\n{pretty.StdErr}");
        pretty.ExitCode.ShouldBe(0);
        pretty.StdOut.ShouldContain("body: omitted (budget)");
        pretty.StdOut.ShouldContain("members:");
        pretty.StdOut.ShouldContain("Method01 (method) :5");
        pretty.StdOut.ShouldContain("more (budget)");
        pretty.StdOut.ShouldContain("read a member with expand <file> <line> or raise --max-tokens");

        var json = await CliTestProcess.RunAsync(
            workspace, "symbol", "Huge", "--max-tokens", MembersFitBudget.ToString(), "--json");
        json.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(json.StdOut.Trim());
        var match = doc.RootElement.GetProperty("matches").EnumerateArray().ShouldHaveSingleItem();
        match.GetProperty("bodyOmitted").GetBoolean().ShouldBeTrue();
        var members = match.GetProperty("members").EnumerateArray().ToList();
        members.Count.ShouldBe(MethodCount);
        members[0].GetProperty("name").GetString().ShouldBe("Method01");
        members[0].GetProperty("kind").GetString().ShouldBe("method");
        members[0].GetProperty("line").GetInt32().ShouldBe(5);
        match.GetProperty("membersOmitted").GetInt32().ShouldBe(0);
    }

    [Fact]
    public void Symbol_TypeBodyOverBudget_FullMemberMap_ReportsMappedState()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SymbolLookup(workspace).Find("Huge", null, null, MembersFitBudget, body: true);

        result.State.ShouldBe(AnswerState.Mapped);
        result.Truncated.ShouldBeTrue();
        result.TokensUsed.ShouldBeLessThanOrEqualTo(MembersFitBudget);
    }

    [Fact]
    public void Symbol_MembersOverBudget_PartialMemberMap_ReportsClippedState()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SymbolLookup(workspace).Find("Huge", null, null, MembersOverflowBudget, body: true);

        var hit = result.Matches.ShouldHaveSingleItem();
        hit.MembersOmitted.ShouldBeGreaterThan(0);
        result.State.ShouldBe(AnswerState.Clipped);
    }

    [Fact]
    public void Symbol_BudgetTooTightForAnyMember_EmptyMapIsClippedNotMapped()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SymbolLookup(workspace).Find("Huge", null, null, 15, body: true);

        var hit = result.Matches.ShouldHaveSingleItem();
        hit.BodyOmitted.ShouldBeTrue();
        hit.Members.ShouldBeEmpty();
        hit.MembersOmitted.ShouldBe(0);
        result.State.ShouldBe(AnswerState.Clipped);
    }

    [Fact]
    public void Symbol_HeaderOverBudget_HitNeverLands_ReportsClippedState()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SymbolLookup(workspace).Find("Huge", null, null, 1, body: true);

        result.Matches.ShouldBeEmpty();
        result.Truncated.ShouldBeTrue();
        result.State.ShouldBe(AnswerState.Clipped);
        result.RefusedBodyTokens.ShouldBe(0);
    }

    [Fact]
    public void Symbol_BodyFitsWithinBudget_ReportsCompleteState()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SymbolLookup(workspace).Find("Huge", null, null, SymbolLookup.DefaultMaxTokens, body: true);

        result.State.ShouldBe(AnswerState.Complete);
        result.RefusedBodyTokens.ShouldBe(0);
    }

    [Fact]
    public void Symbol_RefusedBody_PriceIsTheEstimateOfTheSpanTurnedAway()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SymbolLookup(workspace).Find("Huge", null, null, MembersFitBudget, body: true);

        var hit = result.Matches.ShouldHaveSingleItem();
        hit.Body.ShouldBeNull();
        result.RefusedBodyTokens.ShouldBe(HugeBodyTokens);
        var lines = File.ReadAllLines(
            Path.Combine(workspace, "repo/src/Huge.cs".Replace('/', Path.DirectorySeparatorChar)));
        result.RefusedBodyTokens
            .ShouldBe(TokenCounter.Estimate(string.Join("\n", lines[(hit.StartLine - 1)..hit.EndLine])));
    }

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/Huge.cs", HugeClass());
        WriteFile(workspace, "repo/src/Box.cs", GenericBoxClass());
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static string GenericBoxClass() =>
        """
        namespace Demo;

        public class Box<T>
        {
            public T Value;

            public int Count => 1;

            public T Echo(T item)
            {
                return item;
            }

            public bool Same(T left, T right)
            {
                return Equals(left, right);
            }

            public string Describe(T item)
            {
                return item?.ToString() ?? string.Empty;
            }
        }
        """;

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

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
