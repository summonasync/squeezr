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

    // node-kind names measured on the grammars bundled for linux-x64, before any table exists.
    // 'function_declaration' is deliberately in both go and javascript assertions: the names repeat
    // across grammars, which is why a reference pool cannot be keyed on a node kind alone
    private const string PythonSource = """
        import os

        @staticmethod
        def load(path: str) -> str:
            return open(path).read()

        async def fetch(url: str) -> str:
            return url

        class Registry:
            def add(self, item):
                self.items = []
                return self
        """;

    private const string GoSource = """
        package registry

        import "fmt"

        type Registry struct {
            items []string
        }

        type Lookuper interface {
            Lookup(key string) string
        }

        func New() *Registry {
            return &Registry{}
        }

        func (r *Registry) Add(item string) *Registry {
            r.items = append(r.items, item)
            fmt.Println(item)
            return r
        }
        """;

    [Fact]
    public void Python_Parse_ProducesExpectedNodeTypes()
    {
        using var language = new Language("Python");
        using var parser = new Parser(language);
        using var tree = Parse(parser, PythonSource);

        tree.RootNode!.HasError.ShouldBeFalse();
        var types = CollectTypes(tree.RootNode!);
        types.ShouldContain("import_statement");
        types.ShouldContain("function_definition");
        types.ShouldContain("class_definition");
        types.ShouldContain("decorated_definition");
    }

    [Fact]
    public void Python_AsyncAndDecoratedFunctions_BothCarryAFunctionDefinitionNode()
    {
        using var language = new Language("Python");
        using var parser = new Parser(language);
        using var tree = Parse(parser, PythonSource);

        var definitions = CollectNodes(tree.RootNode!).Where(n => n.Type == "function_definition").ToList();
        definitions.Select(n => n.GetChildForField("name")?.Text).ShouldBe(["load", "fetch", "add"]);

        // the decorator wraps rather than replaces, and 'async' is a keyword inside the same node:
        // a vocabulary keyed only on function_definition reaches both, no decorated/async extra needed
        CollectTypes(CollectNodes(tree.RootNode!).First(n => n.Type == "decorated_definition"))
            .ShouldContain("function_definition");
    }

    // the shapes the python symbol table keys on, measured before the table existed: a decorator is a
    // wrapper node with no name of its own, a module-level binding is a bare 'assignment' statement,
    // and neither is a node kind another bundled grammar produces for the same source
    private const string PythonVocabularySource = """
        CONST = 3
        lower = 4

        @dataclass
        class Box:
            name: str
            MAX_ITEMS: int = 5

        @staticmethod
        def decorated(b):
            MAX_INSIDE = 1
            double = lambda x: x * 2
            return double(b)
        """;

    [Fact]
    public void Python_DecoratedDefinition_WrapsTheItemAndCarriesNoNameOfItsOwn()
    {
        using var language = new Language("Python");
        using var parser = new Parser(language);
        using var tree = Parse(parser, PythonVocabularySource);

        var wrappers = CollectNodes(tree.RootNode!).Where(n => n.Type == "decorated_definition").ToList();
        wrappers.Count.ShouldBe(2);
        foreach (var wrapper in wrappers)
        {
            // no name field: a row keyed on the wrapper would have to reach through the inner item for
            // its name anyway, so the inner definition is the keyed node and the wrapper is the span
            wrapper.GetChildForField("name").ShouldBeNull();
            var inner = wrapper.GetChildForField("definition");
            inner.ShouldNotBeNull();
            inner!.Type.ShouldBeOneOf("function_definition", "class_definition");

            // relational on purpose: the wrapper starts above the definition it decorates, ends with it,
            // and its first source line is the decorator text
            wrapper.StartPosition.Row.ShouldBeLessThan(inner.StartPosition.Row);
            wrapper.EndPosition.Row.ShouldBe(inner.EndPosition.Row);
            wrapper.Text.Split('\n')[0].ShouldStartWith("@");

            // exactly one definition under each wrapper, so one wrapper can stand for one item — and the
            // name lives on that inner node only: keying the wrapper itself was tried and produced no
            // row at all (NameOf answers null), a dead table entry rather than a double count
            CollectNodes(wrapper).Count(n => n.Type is "function_definition" or "class_definition")
                .ShouldBe(1);
        }
    }

    [Fact]
    public void Python_ModuleLevelBinding_IsAnAssignmentNodeWithNoNameField()
    {
        using var language = new Language("Python");
        using var parser = new Parser(language);
        using var tree = Parse(parser, PythonVocabularySource);

        var assignments = CollectNodes(tree.RootNode!).Where(n => n.Type == "assignment").ToList();

        // CONST, lower, the bare annotation, MAX_ITEMS, and both assignments inside the def body share
        // one node kind: the shape cannot tell a constant from a local write, so the table alone is not
        // enough — the extractor gate (const-shaped target outside a def body) is what keeps noise out
        assignments.Select(n => n.GetChildForField("left")?.Text)
            .ShouldBe(["CONST", "lower", "name", "MAX_ITEMS", "MAX_INSIDE", "double"]);
        assignments.Count(n => n.GetChildForField("name") is null).ShouldBe(assignments.Count);
        assignments.Count(n => n.GetChildForField("left")?.Type == "identifier").ShouldBe(assignments.Count);

        // the value field of a python assignment is named 'right', and a bare annotation carries none —
        // which is how 'name: str' differs in shape from 'MAX_ITEMS: int = 5'
        assignments.Where(n => n.GetChildForField("right") is null)
            .Select(n => n.GetChildForField("left")?.Text).ShouldBe(["name"]);
    }

    [Fact]
    public void Python_NodeKindNamesTheTableKeys_AreAbsentFromJsAndGoTrees()
    {
        using var python = new Language("Python");
        using var pythonParser = new Parser(python);
        using var pyTree = Parse(pythonParser, PythonVocabularySource);
        var pythonTypes = CollectTypes(pyTree.RootNode!);

        using var go = new Language("Go");
        using var goParser = new Parser(go);
        using var goTree = Parse(goParser, GoSource);
        var goTypes = CollectTypes(goTree.RootNode!);

        using var js = new Language("JavaScript");
        using var jsParser = new Parser(js);
        using var jsTree = Parse(jsParser, JsVocabularySource);
        var jsTypes = CollectTypes(jsTree.RootNode!);

        // the collision guard for the python row, measured on the trees the extractor actually walks:
        // every kind the table keys on is absent from both foreign grammars, even though those grammars
        // write the same concept under a near-miss name — go's 'r.items = append(...)' is an
        // assignment_statement, javascript's 'ITEMS[0] = total' and 'this.items = []' are
        // assignment_expressions. Key the pool on node kinds alone and both statements arrive as python
        // constants; the js tree also carries class Holder and function run, which the python table has
        // no name for, so a shared table would harvest nothing there either way
        foreach (var kind in new[] { "function_definition", "class_definition", "decorated_definition", "assignment" })
        {
            goTypes.ShouldNotContain(kind);
            jsTypes.ShouldNotContain(kind);
            pythonTypes.ShouldContain(kind);
        }

        goTypes.ShouldContain("assignment_statement");
        jsTypes.ShouldContain("assignment_expression");

        // and the names repeat where the concept is the same, which is why pools stay per-language
        goTypes.ShouldContain("function_declaration");
        jsTypes.ShouldContain("function_declaration");
    }

    [Fact]
    public void Python_Docstring_IsAStringNode_AndHashComment_IsACommentNode()
    {
        using var language = new Language("Python");
        using var parser = new Parser(language);
        using var tree = Parse(parser, """
            # leading comment

            '''
            Module docstring.
            '''

            def load(path):
                '''What it does.'''
                return path
            """);

        var types = CollectTypes(tree.RootNode!);

        // python documentation is a string expression, not a comment node, so the comment collector that
        // rust and js reach never sees it — the fact the doc-capture fence in the python tests stands on
        types.ShouldContain("string");
        types.ShouldContain("comment");
        types.ShouldNotContain("block_comment");
        CollectNodes(tree.RootNode!).Where(n => n.Type == "string").Select(n => n.Text)
            .ShouldBe(["'''\nModule docstring.\n'''", "'''What it does.'''"]);
    }

    // reference-class shapes measured on tree-sitter-python before the reference table existed. The
    // source is deliberately one file with every shape the table keys on, and the assertions read fields
    // relationally — a count here would only prove the fixture did not change
    private const string PythonReferenceSource = """
        import os
        import os, sys
        import a.b.c as abc
        from typing import Dict, List as L
        from os.path import join
        from . import sibling
        from ..pkg import thing
        from typing import *

        class Bare:
            pass

        @dataclass
        @app.route("/x")
        class Widget(Base, metaclass=ABCMeta):
            def render(self, items):
                funcs[0](1)
                joined = os.path.join("a", "b").upper()
                return helper(sum(items), Base(), items.map(lambda x: inner(x)))
        """;

    [Fact]
    public void Python_CallCallee_VariantsExposeTheirNameThroughFields()
    {
        using var language = new Language("Python");
        using var parser = new Parser(language);
        using var tree = Parse(parser, PythonReferenceSource);
        var calls = CollectNodes(tree.RootNode!).Where(n => n.Type == "call").ToList();

        // a python call is 'call' (never call_expression), its callee is field 'function' and its
        // argument list is field 'arguments'
        calls.Count(c => c.GetChildForField("arguments")?.Type == "argument_list").ShouldBe(calls.Count);
        calls.Count(c => c.GetChildForField("function") is not null).ShouldBe(calls.Count);

        // select by exact text: 'os.path.join("a", "b")' and 'os.path.join("a", "b").upper()' share a
        // prefix, and the outer node of a chained call starts where the inner one does
        var dotted = calls.Single(c => c.Text == "os.path.join(\"a\", \"b\")");
        dotted.GetChildForField("function")!.Type.ShouldBe("attribute");
        // an attribute chain nests to the LEFT and names to the right: 'os.path.join' is
        // attribute(object=attribute('os.path'), attribute='join'), so the callee's last segment is the
        // field named 'attribute' — javascript reaches the same judgement through
        // member_expression.property and rust through field_expression.field, under three different
        // node-kind names
        var joinChain = dotted.GetChildForField("function")!;
        joinChain.GetChildForField("attribute")!.Text.ShouldBe("join");
        joinChain.GetChildForField("object")!.Text.ShouldBe("os.path");

        // the chained form's own callee is an attribute whose object is a call: the name is still the
        // member, never the receiver's chain
        var chained = calls.Single(c => c.Text == "os.path.join(\"a\", \"b\").upper()");
        var chainedCallee = chained.GetChildForField("function")!;
        chainedCallee.Type.ShouldBe("attribute");
        chainedCallee.GetChildForField("attribute")!.Text.ShouldBe("upper");
        chainedCallee.GetChildForField("object")!.Type.ShouldBe("call");

        // a bare identifier callee, and the subscript callee that has no name to read
        calls.First(c => c.Text.StartsWith("sum(", StringComparison.Ordinal))
            .GetChildForField("function")!.Type.ShouldBe("identifier");
        var subscriptCallee = calls.First(c => c.Text.StartsWith("funcs[0](", StringComparison.Ordinal))
            .GetChildForField("function")!;
        subscriptCallee.Type.ShouldBe("subscript");
        subscriptCallee.GetChildForField("value")!.Text.ShouldBe("funcs");
    }

    [Fact]
    public void Python_ImportStatement_RepeatNameFieldsAndNestAliasesInTheirOwnNodes()
    {
        using var language = new Language("Python");
        using var parser = new Parser(language);
        using var tree = Parse(parser, PythonReferenceSource);
        var statements = CollectNodes(tree.RootNode!).Where(n => n.Type == "import_statement").ToList();
        statements.Count.ShouldBe(3);

        // 'import os, sys' repeats the name field: GetChildForField answers only 'os' while the node
        // carries two dotted_name children — an emitter that reads the field would index half the file's
        // imports and never say so
        var multi = statements.Single(n => n.Text == "import os, sys");
        multi.GetChildForField("name")!.Text.ShouldBe("os");
        multi.NamedChildren.Where(c => c.Type == "dotted_name").Select(c => c.Text)
            .ShouldBe(["os", "sys"]);

        // an aliased import is its own node kind with both halves as fields, not a dotted_name plus text
        var aliased = statements.Single(n => n.Text == "import a.b.c as abc");
        var aliasNode = aliased.GetChildForField("name")!;
        aliasNode.Type.ShouldBe("aliased_import");
        aliasNode.GetChildForField("name")!.Text.ShouldBe("a.b.c");
        aliasNode.GetChildForField("alias")!.Text.ShouldBe("abc");
        statements.Single(n => n.Text == "import os").GetChildForField("name")!.Type.ShouldBe("dotted_name");
    }

    [Fact]
    public void Python_ImportFromStatement_HoldsModuleOnLeftBindingsOnRightAndGlobHasNoBinding()
    {
        using var language = new Language("Python");
        using var parser = new Parser(language);
        using var tree = Parse(parser, PythonReferenceSource);
        var froms = CollectNodes(tree.RootNode!).Where(n => n.Type == "import_from_statement").ToList();
        froms.Count.ShouldBe(5);

        // the module lives in field 'module_name'; the bindings are the statement's dotted_name /
        // aliased_import children — and the module node is itself a dotted_name child, so a collector
        // that iterates dotted_name harvests 'typing' as a binding too
        var typed = froms.Single(n => n.Text == "from typing import Dict, List as L");
        typed.GetChildForField("module_name")!.Text.ShouldBe("typing");
        typed.NamedChildren.Where(c => c.Type == "dotted_name").Select(c => c.Text)
            .ShouldBe(["typing", "Dict"]);
        typed.NamedChildren.Where(c => c.Type == "aliased_import").Select(c => c.Text)
            .ShouldBe(["List as L"]);

        // a relative import is a node kind of its own whose text is the dot prefix
        froms.Single(n => n.Text == "from . import sibling")
            .GetChildForField("module_name")!.Type.ShouldBe("relative_import");
        var package = froms.Single(n => n.Text == "from ..pkg import thing")
            .GetChildForField("module_name")!;
        package.Type.ShouldBe("relative_import");
        package.Text.ShouldBe("..pkg");

        // 'from typing import *' has no binding field at all — only a wildcard_import child
        var glob = froms.Single(n => n.Text == "from typing import *");
        glob.GetChildForField("name").ShouldBeNull();
        glob.NamedChildren.ShouldContain(c => c.Type == "wildcard_import");
    }

    [Fact]
    public void Python_ClassSuperclasses_AreAnArgumentListMixingBasesAndKeywordArguments()
    {
        using var language = new Language("Python");
        using var parser = new Parser(language);
        using var tree = Parse(parser, PythonReferenceSource);
        var classes = CollectNodes(tree.RootNode!).Where(n => n.Type == "class_definition").ToList();

        // a class without parentheses carries no superclasses field; with them the field is an
        // argument_list, and 'metaclass=ABCMeta' is a keyword_argument child of the SAME list as the
        // base — a reader that takes every child indexes a configuration option as a parent class
        classes.Single(c => c.GetChildForField("name")!.Text == "Bare")
            .GetChildForField("superclasses").ShouldBeNull();
        var widget = classes.Single(c => c.GetChildForField("name")!.Text == "Widget")
            .GetChildForField("superclasses")!;
        widget.Type.ShouldBe("argument_list");
        widget.NamedChildren.Where(c => c.Type != "keyword_argument").Select(c => c.Text).ShouldBe(["Base"]);
        var keyword = widget.NamedChildren.Single(c => c.Type == "keyword_argument");
        keyword.GetChildForField("name")!.Text.ShouldBe("metaclass");
        keyword.GetChildForField("value")!.Text.ShouldBe("ABCMeta");
    }

    [Fact]
    public void Python_DecoratorWrapsEitherACallOrABareExpression()
    {
        using var language = new Language("Python");
        using var parser = new Parser(language);
        using var tree = Parse(parser, PythonReferenceSource);
        var decorators = CollectNodes(tree.RootNode!).Where(n => n.Type == "decorator").ToList();
        decorators.Select(n => n.Text).ShouldBe(["@dataclass", "@app.route(\"/x\")"]);

        // the call-shaped decorator is a real 'call' node inside the decorator, so a call rule reaches it
        // the same way it reaches any call (and emits 'route'); a bare decorator carries only an
        // identifier and produces nothing — the difference is node shape, not name
        CollectTypes(decorators.First(n => n.Text == "@app.route(\"/x\")")).ShouldContain("call");
        CollectTypes(decorators.First(n => n.Text == "@dataclass")).ShouldNotContain("call");
    }

    [Fact]
    public void PythonReferenceKindNames_ExposeThreeCrossGrammarTwinsAndTwoSafeGates()
    {
        using var python = new Language("Python");
        using var pythonParser = new Parser(python);
        using var pyTree = Parse(pythonParser, PythonReferenceSource);
        var pythonNames = CollectTypes(pyTree.RootNode!).ToHashSet(StringComparer.Ordinal);

        using var js = new Language("JavaScript");
        using var jsParser = new Parser(js);
        using var jsTree = Parse(jsParser, JsVocabularySource);
        var jsNames = CollectTypes(jsTree.RootNode!).ToHashSet(StringComparer.Ordinal);

        using var rust = new Language("Rust");
        using var rustParser = new Parser(rust);
        using var rustTree = Parse(rustParser, """
            #[derive(Debug)]
            pub struct Registry {
                entries: u32,
            }

            impl Registry {
                pub fn lookup(&self, key: &str) -> u32 {
                    self.entries.max(1)
                }
            }
            """);
        var rustNames = CollectTypes(rustTree.RootNode!).ToHashSet(StringComparer.Ordinal);

        using var go = new Language("Go");
        using var goParser = new Parser(go);
        using var goTree = Parse(goParser, GoSource);
        var goNames = CollectTypes(goTree.RootNode!).ToHashSet(StringComparer.Ordinal);

        // THE twin that decides the design: python writes an import node named import_statement, exactly
        // the name javascript uses, while the shape under it is nothing alike (js: 'source' +
        // 'import_clause'; python: 'module_name' + dotted_name children). A dispatch keyed on the node
        // kind would run the javascript reader over a python import, find no source field and emit zero
        // references — silent, and green in every test that only checks 'no crash'
        pythonNames.ShouldContain("import_statement");
        jsNames.ShouldContain("import_statement");

        // the second twin: rust's attribute node is the #[...] shape, an unrelated concept to python's
        // dotted attribute — safe today only because the call rule never asks a rust node for its
        // 'attribute' field, which is why the python call gate is keyed on a name rust does not use
        pythonNames.ShouldContain("attribute");
        rustNames.ShouldContain("attribute");

        // the third: go's call argument list is argument_list too, the same name python gives it
        pythonNames.ShouldContain("argument_list");
        goNames.ShouldContain("argument_list");

        // the two names the reference table gates on ARE disjoint: every other grammar spells a call
        // call_expression, and a class is class_declaration (js) or type_declaration (go)
        pythonNames.ShouldContain("call");
        foreach (var names in new[] { jsNames, rustNames, goNames })
        {
            names.ShouldNotContain("call");
            names.ShouldNotContain("class_definition");
            names.ShouldContain("call_expression");
        }

        jsNames.ShouldNotContain("import_from_statement");
        jsNames.ShouldNotContain("aliased_import");
        rustNames.ShouldNotContain("import_from_statement");
        goNames.ShouldNotContain("import_from_statement");
    }

    private const string JsVocabularySource = """
        import { mul } from './util';

        const ITEMS = [1, 2];

        export function run(items) {
          let total = 0;
          for (const item of items) {
            total = mul(total, item);
            ITEMS[0] = total;
          }
          return total;
        }

        export class Holder {
          clear() {
            this.items = [];
          }
        }
        """;

    [Fact]
    public void Go_Parse_ProducesExpectedNodeTypes()
    {
        using var language = new Language("Go");
        using var parser = new Parser(language);
        using var tree = Parse(parser, GoSource);

        tree.RootNode!.HasError.ShouldBeFalse();
        var types = CollectTypes(tree.RootNode!);
        types.ShouldContain("package_clause");
        types.ShouldContain("import_declaration");
        types.ShouldContain("type_declaration");
        types.ShouldContain("struct_type");
        types.ShouldContain("interface_type");
        types.ShouldContain("function_declaration");
        types.ShouldContain("method_declaration");
    }

    [Fact]
    public void JavaScriptGrammar_FlagsGoSourceAsError()
    {
        using var language = new Language("JavaScript");
        using var parser = new Parser(language);
        using var tree = Parse(parser, GoSource);
        var types = CollectTypes(tree.RootNode!);

        // the collision proof for the per-language pools, measured on this exact source: reading go as
        // javascript is not a silent near-miss — it is an error tree, and its go-only kinds vanish —
        // yet the go 'import "fmt"' line still arrives as javascript's own import_statement, the node
        // name the javascript reference rules treat as an import source. A pool keyed on node kinds
        // would therefore invent an import reference from a go file that has no import to resolve
        tree.RootNode!.HasError.ShouldBeTrue();
        CollectNodes(tree.RootNode!).ShouldContain(n => n.IsError);
        types.ShouldContain("import_statement");
        types.ShouldContain("call_expression");
        types.ShouldNotContain("type_declaration");
        types.ShouldNotContain("method_declaration");
        types.ShouldNotContain("struct_type");
        types.ShouldNotContain("package_clause");
    }

    [Fact]
    public void JavaScriptGrammar_FlagsPythonSourceAsError()
    {
        using var language = new Language("JavaScript");
        using var parser = new Parser(language);
        using var tree = Parse(parser, PythonSource);
        var types = CollectTypes(tree.RootNode!);

        tree.RootNode!.HasError.ShouldBeTrue();
        CollectNodes(tree.RootNode!).ShouldContain(n => n.IsError);
        types.ShouldNotContain("function_definition");
        types.ShouldNotContain("class_definition");
        types.ShouldNotContain("decorated_definition");
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
