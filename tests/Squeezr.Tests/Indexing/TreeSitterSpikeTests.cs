using Shouldly;
using TsQuery = TreeSitter.Query;
using TreeSitter;

namespace Squeezr.Tests.Indexing;

public class TreeSitterSpikeTests
{
    private const string RustSource = """
        use std::collections::HashMap;

        pub struct Registry {
            entries: HashMap<String, String>,
        }

        pub trait Lookup {
            fn get(&self, key: &str) -> Option<&String>;
        }

        impl Lookup for Registry {
            fn get(&self, key: &str) -> Option<&String> {
                self.entries.get(key)
            }
        }
        """;

    private const string RustVocabularySource = """
        use std::collections::HashMap;

        pub struct Registry {
            entries: HashMap<String, String>,
        }

        pub enum Color {
            Red,
            Blue(u32),
        }

        pub union Slot {
            raw: u32,
        }

        pub trait Lookup {
            fn get(&self, key: &str) -> Option<&String>;
        }

        impl Lookup for Registry {
            fn get(&self, key: &str) -> Option<&String> {
                self.entries.get(key)
            }
        }

        impl Registry {
            pub fn empty() -> Self {
                Registry { entries: HashMap::new() }
            }
        }

        mod utils {
            pub fn double(n: u32) -> u32 {
                let times_two = |x| x * 2;
                times_two(n)
            }
        }

        type Id = u64;

        const MAX: usize = 10;

        static BASE: usize = 42;

        macro_rules! shout {
            ($msg:expr) => {
                println!("{}", $msg);
            };
        }
        """;

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
    public void Rust_Parse_ProducesExpectedNodeTypes()
    {
        using var language = new Language("Rust");
        using var parser = new Parser(language);
        using var tree = Parse(parser, RustSource);

        tree.RootNode!.HasError.ShouldBeFalse();
        var types = CollectTypes(tree.RootNode!);
        types.ShouldContain("use_declaration");
        types.ShouldContain("struct_item");
        types.ShouldContain("trait_item");
        types.ShouldContain("impl_item");
        types.ShouldContain("function_item");
    }

    [Fact]
    public void Rust_JavaScriptGrammar_FlagsRustSourceAsError()
    {
        using var language = new Language("JavaScript");
        using var parser = new Parser(language);
        using var tree = Parse(parser, RustSource);
        var types = CollectTypes(tree.RootNode!);

        tree.RootNode!.HasError.ShouldBeTrue();
        types.ShouldNotContain("struct_item");
        types.ShouldNotContain("impl_item");
        CollectNodes(tree.RootNode!).ShouldContain(n => n.IsError);
    }

    [Fact]
    public void Rust_Parse_ExposesEverySymbolVocabularyNodeKind()
    {
        using var language = new Language("Rust");
        using var parser = new Parser(language);
        using var tree = Parse(parser, RustVocabularySource);

        tree.RootNode!.HasError.ShouldBeFalse();
        var types = CollectTypes(tree.RootNode!);
        types.ShouldContain("function_item");
        types.ShouldContain("function_signature_item");
        types.ShouldContain("struct_item");
        types.ShouldContain("enum_item");
        types.ShouldContain("union_item");
        types.ShouldContain("trait_item");
        types.ShouldContain("impl_item");
        types.ShouldContain("mod_item");
        types.ShouldContain("type_item");
        types.ShouldContain("const_item");
        types.ShouldContain("static_item");
        types.ShouldContain("macro_definition");
        types.ShouldContain("call_expression");
        types.ShouldContain("field_expression");
    }

    [Theory]
    [InlineData("struct_item", "Registry")]
    [InlineData("enum_item", "Color")]
    [InlineData("union_item", "Slot")]
    [InlineData("trait_item", "Lookup")]
    [InlineData("mod_item", "utils")]
    [InlineData("type_item", "Id")]
    [InlineData("const_item", "MAX")]
    [InlineData("static_item", "BASE")]
    [InlineData("macro_definition", "shout")]
    [InlineData("function_item", "get")]
    [InlineData("function_signature_item", "get")]
    public void Rust_NamedItems_NameFieldResolves(string nodeType, string expectedName)
    {
        using var language = new Language("Rust");
        using var parser = new Parser(language);
        using var tree = Parse(parser, RustVocabularySource);

        var item = FindNode(tree.RootNode!, nodeType);
        item.ShouldNotBeNull($"the fixture has no {nodeType}");
        item!.GetChildForField("name")!.Text.ShouldBe(expectedName);
    }

    [Fact]
    public void Rust_ImplItem_HasNoNameFieldAndCarriesTraitAndTypeFields()
    {
        using var language = new Language("Rust");
        using var parser = new Parser(language);
        using var tree = Parse(parser, RustVocabularySource);

        var traitImpl = FindNode(tree.RootNode!, "impl_item")!;
        traitImpl.GetChildForField("name").ShouldBeNull();
        traitImpl.GetChildForField("trait")!.Text.ShouldBe("Lookup");
        traitImpl.GetChildForField("type")!.Text.ShouldBe("Registry");

        // an inherent impl differs only by the missing trait half of the composition
        var inherent = FindNode(tree.RootNode!, "impl_item", after: traitImpl)!;
        inherent.GetChildForField("trait").ShouldBeNull();
        inherent.GetChildForField("type")!.Text.ShouldBe("Registry");
    }

    [Fact]
    public void Rust_TraitMethod_IsFunctionSignatureItem_NotFunctionItem()
    {
        using var language = new Language("Rust");
        using var parser = new Parser(language);
        using var tree = Parse(parser, RustVocabularySource);

        var trait = FindNode(tree.RootNode!, "trait_item")!;
        var bodyTypes = CollectTypes(trait);
        bodyTypes.ShouldContain("function_signature_item");
        bodyTypes.ShouldNotContain("function_item");
    }

    [Fact]
    public void Rust_FieldAccess_IsFieldExpression_NotMemberExpression()
    {
        using var language = new Language("Rust");
        using var parser = new Parser(language);
        using var tree = Parse(parser, RustVocabularySource);

        var types = CollectTypes(tree.RootNode!);
        types.ShouldContain("field_expression");
        types.ShouldNotContain("member_expression");
        types.ShouldNotContain("method_call_expression");
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

    private static Node? FindNode(Node node, string type, Node? after = null)
    {
        if (node.Type == type && (after is null || node.StartIndex > after.StartIndex))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            if (FindNode(child, type, after) is { } match)
            {
                return match;
            }
        }

        return null;
    }

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
