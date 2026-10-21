using System.Text;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Navigation;

[Collection("ProcessGlobals")]
public class ConstructorMembersTests
{
    private const int MembersBudget = 140;
    private const int MethodCount = 10;
    private const int CtorLine = 6;

    [Fact]
    public void Symbol_OversizedGenericType_ListsTheCtorAsAMemberOfItsOwnType()
    {
        var workspace = NewIndexedWorkspace();

        var hit = new SymbolLookup(workspace)
            .Find("Box", null, "class", MembersBudget, body: true)
            .Matches.ShouldHaveSingleItem();

        hit.BodyOmitted.ShouldBeTrue();
        hit.Members[0].ShouldBe(new SymbolMember("Box", "constructor", CtorLine));
        hit.Members.Select(m => m.Name)
            .ShouldBe(new[] { "Box", "Value" }.Concat(Enumerable.Range(1, MethodCount).Select(i => $"Echo{i:00}")));
        hit.Members.Select(m => m.Kind)
            .ShouldBe(new[] { "constructor", "property" }.Concat(Enumerable.Repeat("method", MethodCount)));
        hit.MembersOmitted.ShouldBe(0);
    }

    [Fact]
    public void Index_CtorRow_CarriesTheTypeNameAndTheGenericContainer()
    {
        var workspace = NewIndexedWorkspace();

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repo = store.FindRepo("demo").ShouldNotBeNull();
        var box = store.ListSymbolsByRepo(repo.Id).Single(s => s.Name == "Box" && s.Kind == "class");
        var members = store.ListSymbolsByFile(box.FileId);
        var ctor = members.Single(s => s.Kind == "constructor");

        ctor.Name.ShouldBe("Box");
        ctor.ContainerName.ShouldBe("Box<T>");
        ctor.StartLine.ShouldBe(CtorLine);
        ctor.Signature.ShouldNotBeNull().ShouldContain("Box(T value)");
        ctor.DocComment.ShouldNotBeNull().ShouldContain("Builds a box around one value.");
        members.ShouldNotContain(s => s.Name == "public");
    }

    [Fact]
    public void Symbol_NameOnly_ReturnsTheTypeAndItsCtorAsSeparateHits()
    {
        var workspace = NewIndexedWorkspace();

        var result = new SymbolLookup(workspace)
            .Find("Box", null, null, SymbolLookup.DefaultMaxTokens, body: true);

        result.Matches.Select(m => m.Kind).OrderBy(kind => kind).ShouldBe(["class", "constructor"]);
    }

    [Fact]
    public void Index_SingleLineTypeWithCtor_SecondPassKeepsOneRowPerDeclaration()
    {
        var workspace = NewIndexedWorkspace();
        WriteFile(workspace, "repo/src/Tiny.cs", "namespace Demo;\n\npublic class Tiny { public Tiny() { } }\n");
        new WorkspaceIndexer().Index(workspace);

        new WorkspaceIndexer().Index(workspace);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
        var repo = store.FindRepo("demo").ShouldNotBeNull();
        var tiny = store.ListSymbolsByRepo(repo.Id).Where(s => s.Name == "Tiny").ToList();
        tiny.Select(s => s.Kind).OrderBy(kind => kind).ShouldBe(["class", "constructor"]);
        tiny.Select(s => s.StartLine).ShouldBe([3, 3]);
    }

    [Fact]
    public async Task Cli_Symbol_OversizedGenericType_RendersTheConstructorMemberLine()
    {
        var workspace = NewIndexedWorkspace();

        var pretty = await CliTestProcess.RunAsync(
            workspace, "symbol", "Box", "--kind", "class", "--max-tokens", MembersBudget.ToString());

        pretty.ExitCode.ShouldBe(0);
        pretty.StdOut.ShouldContain("body: omitted (budget) — members:");
        pretty.StdOut.ShouldContain($"Box (constructor) :{CtorLine}");
        pretty.StdOut.ShouldNotContain("public (method)");
    }

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/Box.cs", GenericBoxClass());
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static string GenericBoxClass()
    {
        var text = new StringBuilder(
            """
            namespace Demo;

            public class Box<T>
            {
                /// <summary>Builds a box around one value.</summary>
                public Box(T value)
                {
                    Value = value;
                }

                public T Value { get; }

            """);
        foreach (var index in Enumerable.Range(1, MethodCount))
        {
            text.Append($"    public T Echo{index:00}(T item)\n");
            text.Append("    {\n");
            text.Append($"        return Equals(item, default) ? Value : item;\n");
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
