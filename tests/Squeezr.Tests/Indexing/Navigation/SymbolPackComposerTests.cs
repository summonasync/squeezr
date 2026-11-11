using System.Text;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Navigation;

public class SymbolPackComposerTests
{
    private const int MemberCount = 8;
    private const int MapBudget = 120;
    private const int RowsOverflowBudget = 25;

    [Fact]
    public void Compose_KnownMethod_CarriesDefinitionBodyAndCallers()
    {
        var workspace = NewWorkspace();

        var pack = new SymbolPackComposer(workspace).Compose("Add", null, null, 2000);

        pack.Name.ShouldBe("Add");
        pack.Signature.ShouldNotBeNull();
        pack.BodyOmitted.ShouldBeFalse();
        pack.BodyLines.ShouldNotBeEmpty();
        pack.Callers.ShouldNotBeEmpty();
        pack.Callers.ShouldContain(c => c.File.EndsWith("Runner.cs"));
        pack.WithheldCallers.ShouldBe(0);
        pack.Complete.ShouldBeTrue();
        pack.LookupTokens.ShouldBeGreaterThan(0);
        pack.BudgetTokens.ShouldBe(2000);
    }

    [Fact]
    public void Compose_TinyBudget_TruncatesCallersFirst_WithWithheldCount()
    {
        var workspace = NewWorkspace();
        var full = new SymbolPackComposer(workspace).Compose("Add", null, null, 2000);
        full.WithheldCallers.ShouldBe(0);
        full.Complete.ShouldBeTrue();

        var tight = new SymbolPackComposer(workspace).Compose("Add", null, null, full.LookupTokens);

        tight.LookupTokens.ShouldBe(full.LookupTokens);
        tight.WithheldCallers.ShouldBeGreaterThan(0);
        tight.Callers.ShouldBeEmpty();
        tight.Complete.ShouldBeFalse();
        tight.BodyLines.ShouldNotBeEmpty();
    }

    [Fact]
    public void Compose_DegenerateBudget_LookupTruncated_MarksIncomplete()
    {
        var workspace = NewWorkspace();

        var pack = new SymbolPackComposer(workspace).Compose("Add", null, null, 1);

        pack.LookupTruncated.ShouldBeTrue();
        pack.Complete.ShouldBeFalse();
    }

    [Fact]
    public void Compose_BudgetConsumedByLookup_CallersReportZeroDelivered()
    {
        var workspace = NewWorkspace();

        var pack = new SymbolPackComposer(workspace).Compose("Add", null, null, 25);

        pack.WithheldCallers.ShouldBeGreaterThan(0);
        pack.CallerTokens.ShouldBe(0);
        pack.LookupTokens.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Compose_UnknownSymbol_SurfacesDidYouMean()
    {
        var workspace = NewWorkspace();

        var pack = new SymbolPackComposer(workspace).Compose("Materialzr", null, null, 2000);

        pack.Callers.ShouldBeEmpty();
        pack.DidYouMean.ShouldNotBeNull();
        pack.DidYouMean!.ShouldContain("Materializer");
    }

    [Fact]
    public void Compose_InjectedStore_UsesTheGivenSession()
    {
        var workspace = NewWorkspace();
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));

        var pack = new SymbolPackComposer(workspace).Compose("Add", null, null, 2000, store);

        pack.Name.ShouldBe("Add");
        pack.Callers.ShouldNotBeEmpty();
    }

    [Fact]
    public void Compose_TypeBodyRefusedWithFullMemberMap_StateMapped_PackStaysComplete()
    {
        var workspace = NewBigTypeWorkspace();

        var pack = new SymbolPackComposer(workspace).Compose("Widget", null, null, MapBudget);

        pack.BodyOmitted.ShouldBeTrue();
        pack.BodyLines.ShouldBeEmpty();
        pack.Members.Count.ShouldBe(MemberCount);
        pack.MembersOmitted.ShouldBe(0);
        pack.WithheldCallers.ShouldBe(0);
        pack.State.ShouldBe(AnswerState.Mapped);
        pack.LookupTruncated.ShouldBeFalse();
        pack.Complete.ShouldBeTrue();
    }

    [Fact]
    public void Compose_MemberRowsRefused_StateClipped_MarksPackIncompleteWithoutAnyWithheldCaller()
    {
        var workspace = NewBigTypeWorkspace();

        var pack = new SymbolPackComposer(workspace).Compose("Widget", null, null, RowsOverflowBudget);

        pack.MembersOmitted.ShouldBeGreaterThan(0);
        pack.WithheldCallers.ShouldBe(0);
        pack.State.ShouldBe(AnswerState.Clipped);
        pack.LookupTruncated.ShouldBeTrue();
        pack.Complete.ShouldBeFalse();
    }

    private static string NewBigTypeWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        WriteFile(workspace, "repo/src/Widget.cs", Widget());
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static string Widget()
    {
        var text = new StringBuilder("namespace Demo;\n\npublic class Widget\n{\n");
        foreach (var index in Enumerable.Range(1, MemberCount))
        {
            text.Append($"    public int Part{index:00}(int a)\n");
            text.Append("    {\n");
            text.Append($"        return a * {index};\n");
            text.Append("    }\n\n");
        }

        return text.Append("}\n").ToString();
    }

    private static string NewWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        WriteFile(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                /// <summary>Adds numbers.</summary>
                public int Add(int a, int b)
                {
                    return a + b;
                }

                public string Add(string a, string b)
                {
                    return a + b;
                }
            }
            """);
        WriteFile(workspace, "repo/src/Runner.cs", """
            namespace Demo;

            public class Runner
            {
                public int Go()
                {
                    return new Calc().Add(1, 2);
                }

                public string Join()
                {
                    return new Calc().Add("x", "y");
                }
            }
            """);
        WriteFile(workspace, "repo/src/Materializer.cs", """
            namespace Demo;

            public class Materializer
            {
                public string Flatten(string input)
                {
                    return input.Trim();
                }
            }
            """);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
