using Shouldly;
using TsQuery = TreeSitter.Query;
using TreeSitter;

namespace Squeezr.Tests.Indexing;

public class TreeSitterSpikeTests
{
    [Fact]
    public void JavaScript_Parse_ProducesExpectedNodeTypes()
    {
        using var language = new Language("JavaScript");
        using var parser = new Parser(language);
        using var tree = Parse(parser, """
            export function add(a, b) {
              return a + b;
            }

            const square = (x) => x * x;

            export class Calc {
              double(n) {
                return n * 2;
              }
            }
            """);

        tree.RootNode!.HasError.ShouldBeFalse();
        var types = CollectTypes(tree.RootNode!);
        types.ShouldContain("function_declaration");
        types.ShouldContain("arrow_function");
        types.ShouldContain("class_declaration");
        types.ShouldContain("method_definition");
    }

    [Fact]
    public void JavaScript_Query_CapturesFunctionNames()
    {
        using var language = new Language("JavaScript");
        using var parser = new Parser(language);
        using var tree = Parse(parser,
            "function add(a, b) { return a + b; }\nfunction mul(a, b) { return a * b; }\n");
        using var query = new TsQuery(language, "(function_declaration name: (identifier) @fn)");

        var captures = query.Execute(tree.RootNode!).Captures.ToList();
        captures.Count.ShouldBe(2);
        captures.Select(c => c.Name).ShouldAllBe(name => name == "fn");
        captures.Select(c => c.Node!.Text).ShouldBe(["add", "mul"]);
    }

    [Fact]
    public void TypeScript_Parse_ProducesExpectedNodeTypes()
    {
        using var language = new Language("TypeScript");
        using var parser = new Parser(language);
        using var tree = Parse(parser, """
            interface User {
              id: number;
              greet(): void;
            }

            type UserId = string;

            export class Store {
              find(id: UserId): User | undefined {
                return undefined;
              }
            }
            """);

        tree.RootNode!.HasError.ShouldBeFalse();
        var types = CollectTypes(tree.RootNode!);
        types.ShouldContain("interface_declaration");
        types.ShouldContain("method_signature");
        types.ShouldContain("type_alias_declaration");
        types.ShouldContain("method_definition");
    }

    [Fact]
    public void TypeScript_Query_CapturesInterfaceName()
    {
        using var language = new Language("TypeScript");
        using var parser = new Parser(language);
        using var tree = Parse(parser, "interface User {\n  id: number;\n}\n");
        using var query = new TsQuery(language, "(interface_declaration name: (type_identifier) @iface)");

        var captures = query.Execute(tree.RootNode!).Captures.ToList();
        captures.ShouldHaveSingleItem().Node!.Text.ShouldBe("User");
    }

    [Fact]
    public void Tsx_GrammarLoadsAndParsesJsx()
    {
        using var language = new Language("TSX");
        using var parser = new Parser(language);
        using var tree = Parse(parser, """
            export function Badge({ count }: { count: number }) {
              return <div className="badge">{count}</div>;
            }
            """);

        tree.RootNode!.HasError.ShouldBeFalse();
        var types = CollectTypes(tree.RootNode!);
        types.ShouldContain("jsx_element");
        types.ShouldContain("function_declaration");
    }

    [Fact]
    public void Tsx_TypeScriptGrammar_FlagsJsxAsError()
    {
        using var language = new Language("TypeScript");
        using var parser = new Parser(language);
        using var tree = Parse(parser, "export const el = <div className=\"badge\">1</div>;\n");

        tree.RootNode!.HasError.ShouldBeTrue();
    }

    [Fact]
    public void BrokenJavaScript_RecoversWithErrorNodes()
    {
        using var language = new Language("JavaScript");
        using var parser = new Parser(language);
        using var tree = Parse(parser, "function add(a, b {\n  return a + b;\n");

        tree.RootNode!.HasError.ShouldBeTrue();
        var nodes = CollectNodes(tree.RootNode!);
        nodes.Any(n => n.IsError || n.IsMissing).ShouldBeTrue();
    }

    private static Tree Parse(Parser parser, string source) => parser.Parse(source)!;

    private static List<Node> CollectNodes(Node node)
    {
        var nodes = new List<Node> { node };
        foreach (var child in node.Children)
        {
            nodes.AddRange(CollectNodes(child));
        }

        return nodes;
    }

    private static List<string> CollectTypes(Node node)
    {
        var types = new List<string> { node.Type };
        foreach (var child in node.Children)
        {
            types.AddRange(CollectTypes(child));
        }

        return types;
    }
}
