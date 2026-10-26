using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Navigation;

public class SymbolPackComposerTests
{
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
