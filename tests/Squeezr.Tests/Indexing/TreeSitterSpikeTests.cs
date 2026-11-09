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

    private const string RustReferenceSource = """
        use std::collections::HashMap;
        use serde::{Deserialize, Serialize};
        use std::io::{self, Read as Rd};
        use app::model::{order::Sort as S};
        use rand::prelude::*;
        use crate::util::helper;

        macro_rules! shout {
            ($msg:expr) => {
                println!("{}", $msg);
            };
        }

        pub struct Registry {
            entries: u32,
        }

        impl fmt::Display for Registry {
            fn render(&self) -> String {
                self.entries.to_string()
            }
        }

        impl Registry {
            pub fn build() -> Self {
                let map = HashMap::new();
                shout!("built");
                println!("{}", map.len());
                let items = vec![1, 2, 3];
                helper(items.len())
            }
        }

        impl<T> app::Boxed<T> {
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
    public void Rust_UseDeclaration_ExposesArgumentPathTreeAndBindingFields()
    {
        using var language = new Language("Rust");
        using var parser = new Parser(language);
        using var tree = Parse(parser, RustReferenceSource);

        tree.RootNode!.HasError.ShouldBeFalse();
        var types = CollectTypes(tree.RootNode!);
        types.ShouldContain("use_declaration");
        types.ShouldContain("scoped_identifier");
        types.ShouldContain("scoped_use_list");
        types.ShouldContain("use_list");
        types.ShouldContain("use_as_clause");
        types.ShouldContain("use_wildcard");

        // plain path: argument = scoped_identifier, name = the binding, path = everything before it
        var plain = FindNode(tree.RootNode!, "use_declaration")!;
        var scoped = plain.GetChildForField("argument")!;
        scoped.Type.ShouldBe("scoped_identifier");
        scoped.GetChildForField("name")!.Text.ShouldBe("HashMap");
        scoped.GetChildForField("path")!.Text.ShouldBe("std::collections");

        // brace list: path plus a use_list of bindings, aliases carry their own name
        var listed = FindNode(tree.RootNode!, "use_declaration", after: plain)!;
        var argument = listed.GetChildForField("argument")!;
        argument.Type.ShouldBe("scoped_use_list");
        argument.GetChildForField("path")!.Text.ShouldBe("serde");
        argument.NamedChildren.ShouldContain(c => c.Type == "use_list");
        var aliased = FindNode(tree.RootNode!, "use_as_clause")!;
        aliased.GetChildForField("path")!.Text.ShouldBe("Read");
        aliased.GetChildForField("alias")!.Text.ShouldBe("Rd");

        // an alias can sit on a nested path: the clause keeps its own path node, so the module of
        // the binding is the list prefix plus that path, not just the prefix
        var nestedAlias = FindNode(tree.RootNode!, "use_as_clause", after: aliased)!;
        nestedAlias.GetChildForField("path")!.Type.ShouldBe("scoped_identifier");
        nestedAlias.GetChildForField("path")!.GetChildForField("path")!.Text.ShouldBe("order");
        nestedAlias.GetChildForField("alias")!.Text.ShouldBe("S");

        // glob: no binding name at all, only the path the star expands
        var wildcard = FindNode(tree.RootNode!, "use_wildcard")!;
        wildcard.NamedChildren.ShouldContain(c => c.Type == "scoped_identifier");
    }

    [Fact]
    public void Rust_MacroInvocation_IsItsOwnNodeKindWithMacroNameField()
    {
        using var language = new Language("Rust");
        using var parser = new Parser(language);
        using var tree = Parse(parser, RustReferenceSource);

        var invocations = CollectNodes(tree.RootNode!).Where(n => n.Type == "macro_invocation").ToList();
        invocations.Select(n => n.GetChildForField("macro")!.Text)
            .ShouldBe(["shout", "println", "vec"]);
    }

    [Fact]
    public void Rust_MacroDefinitionBody_HasNoMacroInvocationNodes()
    {
        using var language = new Language("Rust");
        using var parser = new Parser(language);
        using var tree = Parse(parser, RustReferenceSource);

        // macro_rules! bodies are raw token trees: the println! inside the definition is not an
        // invocation node, so a definition must not be walked as a call site
        var definition = FindNode(tree.RootNode!, "macro_definition")!;
        CollectTypes(definition).ShouldNotContain("macro_invocation");
        CollectTypes(definition).ShouldContain("token_tree");
    }

    [Fact]
    public void Rust_CallCallee_VariantsAreIdentifierFieldExpressionAndScopedIdentifier()
    {
        using var language = new Language("Rust");
        using var parser = new Parser(language);
        using var tree = Parse(parser, RustReferenceSource);

        var callees = CollectNodes(tree.RootNode!)
            .Where(n => n.Type == "call_expression")
            .Select(n => n.GetChildForField("function")!.Type)
            .ToList();
        callees.ShouldContain("identifier");
        callees.ShouldContain("field_expression");
        callees.ShouldContain("scoped_identifier");

        var method = CollectNodes(tree.RootNode!)
            .First(n => n.Type == "call_expression" && n.GetChildForField("function")!.Type == "field_expression");
        method.GetChildForField("function")!.GetChildForField("field")!.Text.ShouldBe("to_string");

        var pathCall = CollectNodes(tree.RootNode!)
            .First(n => n.Type == "call_expression" && n.GetChildForField("function")!.Type == "scoped_identifier");
        pathCall.GetChildForField("function")!.GetChildForField("name")!.Text.ShouldBe("new");
        pathCall.GetChildForField("function")!.GetChildForField("path")!.Text.ShouldBe("HashMap");

        // this grammar spells neither of the js names, so the callee rules cannot be shared with js
        var types = CollectTypes(tree.RootNode!);
        types.ShouldNotContain("scope_resolution");
        types.ShouldNotContain("member_expression");
    }

    [Fact]
    public void Rust_ImplItem_TraitFieldCarriesPathAndName()
    {
        using var language = new Language("Rust");
        using var parser = new Parser(language);
        using var tree = Parse(parser, RustReferenceSource);

        var impl = FindNode(tree.RootNode!, "impl_item")!;
        impl.GetChildForField("trait")!.Type.ShouldBe("scoped_type_identifier");
        impl.GetChildForField("trait")!.GetChildForField("name")!.Text.ShouldBe("Display");
        impl.GetChildForField("trait")!.GetChildForField("path")!.Text.ShouldBe("fmt");
        impl.GetChildForField("type")!.Text.ShouldBe("Registry");
    }

    [Fact]
    public void Rust_GenericTypeTargetFieldUnwrapsToTheScopedTypeIdentifier()
    {
        using var language = new Language("Rust");
        using var parser = new Parser(language);
        using var tree = Parse(parser, RustReferenceSource);

        // the name of a generic target lives one node deeper than the field, and one deeper again
        // for a qualified path: reading the generic's own text would put 'app::Boxed' in the name
        // column and the edge would silently miss the struct row
        var generic = CollectNodes(tree.RootNode!)
            .Select(n => n.GetChildForField("type"))
            .First(child => child is not null && child.Type == "generic_type")!;
        var bare = generic.GetChildForField("type")!;
        bare.Type.ShouldBe("scoped_type_identifier");
        bare.GetChildForField("name")!.Text.ShouldBe("Boxed");
        bare.GetChildForField("path")!.Text.ShouldBe("app");
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
