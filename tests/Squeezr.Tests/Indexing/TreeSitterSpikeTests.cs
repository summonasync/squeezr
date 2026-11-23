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

    // the shapes the go symbol table keys on, measured on tree-sitter-python's sibling grammar before the
    // table was written: a type is named inside its declaration wrapper, a method's owner is a field
    // rather than an ancestor, and a package-level const is the same node kind as a local one
    private const string GoVocabularySource = """
        package registry

        import "fmt"

        // Run starts the service.
        func Run(items []string) string {
            return fmt.Sprint(items)
        }

        type Registry struct {
            items []string
        }

        type Lookuper interface {
            Lookup(key string) string
        }

        type Mode string

        const MaxSize = 128

        const (
            A int = 1
            B = 2
        )

        func (r *Registry) Add(item string) *Registry {
            const local = 1
            return r
        }

        func (g *Generic[T]) Get() T {
            var zero T
            return zero
        }

        type Generic[T any] struct {
            value T
        }

        func use(items []string) int {
            handler := func(n int) int { return n * 2 }
            return handler(len(items))
        }
        """;

    [Fact]
    public void Go_TypeSpecCarriesTheNameItsDeclarationWrapperDoesNot()
    {
        using var language = new Language("Go");
        using var parser = new Parser(language);
        using var tree = Parse(parser, GoVocabularySource);

        var wrappers = CollectNodes(tree.RootNode!).Where(n => n.Type == "type_declaration").ToList();
        wrappers.Count.ShouldBe(4);

        // one row per type, each with its own span: a grouped 'const ( A; B )' shows the same wrapper
        // shape, and a wrapper keyed as a row — or used as a span anchor the way python's decorator is —
        // would hand every member of the group one identical span — the duplicate-chunk shape
        wrappers.Count(w => w.GetChildForField("name") == null).ShouldBe(wrappers.Count);

        // three const_declarations here — 'const MaxSize', the 'const ( A; B )' group and the 'const
        // local' inside a method body — all the same node kind, and the last two differ only by scope.
        // That is the fact the extractor's scope test rests on, and why a group cannot be a span anchor:
        // the group node holds two specs, so one span would belong to two types
        var declarations = CollectNodes(tree.RootNode!).Where(n => n.Type == "const_declaration").ToList();
        declarations.Count.ShouldBe(3);
        declarations.Single(d => d.Text.StartsWith("const (", StringComparison.Ordinal))
            .NamedChildren.Count(c => c.Type == "const_spec").ShouldBe(2);
        declarations.Where(d => !d.Text.StartsWith("const (", StringComparison.Ordinal))
            .Select(d => d.NamedChildren.Count(c => c.Type == "const_spec")).ShouldBe([1, 1]);
        var specs = CollectNodes(tree.RootNode!).Where(n => n.Type == "type_spec").ToList();
        specs.Select(s => s.GetChildForField("name")?.Text).ShouldBe(["Registry", "Lookuper", "Mode", "Generic"]);
        specs.Select(s => s.Text.Split('\n')[0]).ShouldBe(
            ["Registry struct {", "Lookuper interface {", "Mode string", "Generic[T any] struct {"]);

        // the kind distinction lives in the type field, not in a node kind of its own
        specs.Select(s => s.GetChildForField("type")?.Type)
            .ShouldBe(["struct_type", "interface_type", "type_identifier", "struct_type"]);
    }

    [Fact]
    public void Go_MethodDeclaration_HoldsItsOwnerInAReceiverFieldNotAnAncestor()
    {
        using var language = new Language("Go");
        using var parser = new Parser(language);
        using var tree = Parse(parser, GoVocabularySource);
        var methods = CollectNodes(tree.RootNode!).Where(n => n.Type == "method_declaration").ToList();

        methods.Count.ShouldBe(2);
        foreach (var method in methods)
        {
            // a method sits directly in source_file, so no ancestor walk can name its owner
            method.Parent!.Type.ShouldBe("source_file");
            var receiver = method.GetChildForField("receiver");
            receiver.ShouldNotBeNull();
            receiver!.Type.ShouldBe("parameter_list");
            var parameter = receiver.NamedChildren.Single(c => c.Type == "parameter_declaration");
            parameter.GetChildForField("name")?.Text.ShouldBeOneOf("r", "g");
        }

        // the receiver's type arrives as type_identifier, pointer_type (whose type is a plain child, not
        // a field) or generic_type (field 'type' plus a type_arguments child), so reading an owner means
        // unwrapping twice: '*Registry' and 'Generic[T]' both end at the name the type row itself uses
        var pointer = methods.First(m => m.GetChildForField("name")?.Text == "Add")
            .GetChildForField("receiver")!.NamedChildren.Single(c => c.Type == "parameter_declaration")
            .GetChildForField("type")!;
        pointer.Type.ShouldBe("pointer_type");
        pointer.GetChildForField("type").ShouldBeNull();
        pointer.NamedChildren.Single().Type.ShouldBe("type_identifier");

        var generic = methods.Single(m => m.GetChildForField("name")?.Text == "Get")
            .GetChildForField("receiver")!.NamedChildren.Single(c => c.Type == "parameter_declaration")
            .GetChildForField("type")!;
        generic.Type.ShouldBe("pointer_type");
        var genericType = generic.NamedChildren.Single();
        genericType.Type.ShouldBe("generic_type");
        genericType.GetChildForField("type")!.Text.ShouldBe("Generic");
        genericType.NamedChildren.ShouldContain(c => c.Type == "type_arguments");
    }

    [Fact]
    public void Go_ConstAndVarSpecsShareOneKindBetweenPackageAndFunctionBody()
    {
        using var language = new Language("Go");
        using var parser = new Parser(language);
        using var tree = Parse(parser, GoVocabularySource);
        var specs = CollectNodes(tree.RootNode!).Where(n => n.Type == "const_spec").ToList();

        // 'MaxSize', 'A', 'B' are package-level and 'local' is inside a method body — four nodes, one
        // kind, and the only difference is whether a function/method/func_literal encloses them, so the
        // table cannot separate them and the extractor has to look at the ancestor chain
        specs.Select(s => s.GetChildForField("name")?.Text).ShouldBe(["MaxSize", "A", "B", "local"]);
        HasAncestor(specs.Single(s => s.GetChildForField("name")?.Text == "MaxSize"), "method_declaration")
            .ShouldBeFalse();
        HasAncestor(specs.Single(s => s.GetChildForField("name")?.Text == "local"), "method_declaration")
            .ShouldBeTrue();

        // a 'var zero T' inside a body is a var_spec too, and ':=' is a different kind entirely —
        // short_var_declaration, which the table has no row for and therefore never reads as a symbol
        CollectNodes(tree.RootNode!).Count(n => n.Type == "var_spec").ShouldBe(1);
        CollectTypes(tree.RootNode!).ShouldContain("short_var_declaration");
    }

    [Fact]
    public void Go_NodesTheTableDeliberatelyIgnores_ExistWithNames()
    {
        using var language = new Language("Go");
        using var parser = new Parser(language);
        using var tree = Parse(parser, GoVocabularySource);
        var types = CollectTypes(tree.RootNode!);

        // struct fields carry a name field of their own and type parameters do too, so 'items' and 'T'
        // would arrive as rows if field_declaration or type_parameter_declaration were keyed. Rust
        // doesn't index struct fields either, so the silence is the shipped shape, not an oversight
        types.ShouldContain("field_declaration");
        types.ShouldContain("type_parameter_declaration");
        types.ShouldContain("func_literal");
        CollectNodes(tree.RootNode!).Where(n => n.Type == "field_declaration")
            .Select(n => n.GetChildForField("name")?.Text).ShouldBe(["items", "value"]);

        // and the doc line above 'func Run' is the same node kind javascript uses, which is why go's
        // documentation gap is a renderer question rather than a collector one (python's is both)
        types.ShouldContain("comment");
        types.ShouldNotContain("doc_comment");
    }

    [Fact]
    public void Go_AliasesBlanksMultiNamesAndGroupLists_HaveTheShapesTheGateReads()
    {
        using var language = new Language("Go");
        using var parser = new Parser(language);
        using var tree = Parse(parser, """
            package p

            type Alias = int

            const _ = iota

            var _ Sailer = (*Boat)(nil)

            const A, B = 1, 2

            var Sink interface{ Do() error }

            var (
                Counter int
            )
            """)!;

        // an alias is its own node kind with its own name field — it is NOT a type_spec, which is why an
        // alias needed a table row of its own and why 'which node carries the name' had to be asked per
        // form rather than once per concept
        var alias = CollectNodes(tree.RootNode!).Single(n => n.Type == "type_alias");
        alias.GetChildForField("name")!.Text.ShouldBe("Alias");
        alias.GetChildForField("type")!.Text.ShouldBe("int");
        CollectNodes(tree.RootNode!).Where(n => n.Type == "type_spec").ShouldBe([]);

        // the blank is an ordinary identifier node with the text '_': nothing about the node kind says
        // 'throwaway', so the refusal can only be a name test
        CollectNodes(tree.RootNode!).Where(n => n.Type == "const_spec")
            .Select(n => n.GetChildForField("name")?.Text).ShouldBe(["_", "A"]);
        CollectNodes(tree.RootNode!).Where(n => n.Type == "var_spec")
            .Select(n => n.GetChildForField("name")?.Text).ShouldBe(["_", "Sink", "Counter"]);

        // and a multi-name spec is ONE node carrying TWO identifier children while its name field points
        // at the first — the second name has no field of its own, which is what makes per-name rows
        // share one span instead of getting their own
        var multi = CollectNodes(tree.RootNode!).Single(n => n.Text == "A, B = 1, 2");
        multi.NamedChildren.Count(c => c.Type == "identifier").ShouldBe(2);
        multi.GetChildForField("name")!.Text.ShouldBe("A");

        // an interface that no type declaration names: its method_elem's ancestors reach a var_spec and
        // nothing with a name, which is the case the owner rule refuses
        var element = CollectNodes(tree.RootNode!).Single(n => n.Type == "method_elem");
        element.Text.ShouldBe("Do() error");
        CollectNodes(element).ShouldNotContain(n => n.Type == "type_spec");
        var namedOwner = false;
        for (var current = element.Parent; current is not null; current = current.Parent)
        {
            namedOwner |= current.Type is "type_spec" or "type_alias";
        }

        namedOwner.ShouldBeFalse();

        // the group list is the reason a spec's parent is not always the declaration: 'var (' wraps its
        // members in a var_spec_list, so both the hoist decision and the group test have to look one
        // level up or they read '(' as a signature
        var counter = CollectNodes(tree.RootNode!).Single(n => n.Text == "Counter int");
        counter.Parent!.Type.ShouldBe("var_spec_list");
        counter.Parent!.Parent!.Type.ShouldBe("var_declaration");
    }

    [Fact]
    public void Go_AndJavascript_ShareTheFunctionDeclarationKind_AndShareNothingElse()
    {
        using var go = new Language("Go");
        using var goParser = new Parser(go);
        using var goTree = Parse(goParser, GoVocabularySource);
        var goNames = CollectTypes(goTree.RootNode!).ToHashSet(StringComparer.Ordinal);

        using var js = new Language("JavaScript");
        using var jsParser = new Parser(js);
        using var jsTree = Parse(jsParser, JsVocabularySource);
        var jsNames = CollectTypes(jsTree.RootNode!).ToHashSet(StringComparer.Ordinal);

        using var rust = new Language("Rust");
        using var rustParser = new Parser(rust);
        using var rustTree = Parse(rustParser, RustVocabularySource);
        var rustNames = CollectTypes(rustTree.RootNode!).ToHashSet(StringComparer.Ordinal);

        using var python = new Language("Python");
        using var pythonParser = new Parser(python);
        using var pyTree = Parse(pythonParser, PythonReferenceSource);
        var pythonNames = CollectTypes(pyTree.RootNode!).ToHashSet(StringComparer.Ordinal);

        // THE collision of the whole feature, in one name: go's plain function and javascript's are both
        // function_declaration, so one grammar's table reads the other's functions and produces rows —
        // the guard tests in TreeSitterGoSymbolTests pin exactly how many and which
        goNames.ShouldContain("function_declaration");
        jsNames.ShouldContain("function_declaration");

        // every other name the go table keys on is absent from js, and the method owner has no js analogue
        foreach (var name in new[] { "method_declaration", "method_elem", "type_spec", "const_spec", "var_spec" })
        {
            goNames.ShouldContain(name);
            jsNames.ShouldNotContain(name);
        }

        // and against the grammars whose tables are already shipped, go shares nothing it keys on
        foreach (var other in new[] { rustNames, pythonNames })
        {
            other.ShouldNotContain("method_declaration");
            other.ShouldNotContain("type_spec");
            other.ShouldNotContain("const_spec");
            other.ShouldNotContain("var_spec");
            other.ShouldNotContain("method_elem");
        }

        // rust shares go's root kind, so a root-node check could separate go from javascript and python
        // but never from rust — recorded here because it is the obvious 'obvious fix' for the mislabel
        // exposure and it does not fully work
        goTree.RootNode!.Type.ShouldBe("source_file");
        rustTree.RootNode!.Type.ShouldBe("source_file");
        jsTree.RootNode!.Type.ShouldBe("program");
        pyTree.RootNode!.Type.ShouldBe("module");
    }

    // the reference-class shapes of tree-sitter-go, measured before GoReferences was written; like the
    // python block above, nothing here is inferred from another grammar's names
    private const string GoReferenceSource = """
        package registry

        import (
            "fmt"
            http "net/http"
            . "strings"
            _ "database/sql"
        )
        import "os"

        type Store struct {
            handler http.Handler
            items   map[string]Item
            list    []Measure
            ptr     *Widget
            emb
        }

        type Set interface {
            Store
            Do() error
        }

        type Alias = Store

        func (s *Store) Put(key string) error {
            fmt.Println(key)
            http.HandleFunc("/", nil)
            helper(1)
            s2 := &Store{}
            return nil
        }

        func helper(n int) int {
            return n
        }
        """;

    [Fact]
    public void Go_CallExpressions_NameTheFieldOfTheSelector()
    {
        using var language = new Language("Go");
        using var parser = new Parser(language);
        using var tree = Parse(parser, GoReferenceSource);
        var calls = CollectNodes(tree.RootNode!).Where(n => n.Type == "call_expression").ToList();

        // go's call node is called call_expression — the SAME name javascript and rust use — while the
        // member name lives in a field none of the others have: go says 'field', javascript says
        // 'property' on member_expression, rust says 'field' on field_expression. Three grammars, one
        // judgement, three field names, and one shared node kind for the call itself
        calls.Count(c => c.GetChildForField("function") is not null).ShouldBe(calls.Count);
        var qualified = calls.Single(c => c.Text == "fmt.Println(key)");
        var selector = qualified.GetChildForField("function")!;
        selector.Type.ShouldBe("selector_expression");
        selector.GetChildForField("field")!.Text.ShouldBe("Println");
        selector.GetChildForField("operand")!.Text.ShouldBe("fmt");

        var bare = calls.Single(c => c.Text == "helper(1)");
        bare.GetChildForField("function")!.Type.ShouldBe("identifier");
        bare.GetChildForField("function")!.Text.ShouldBe("helper");

        // a method call on a variable is a selector like any other, so 's.Put' inside a method body would
        // name 'Put' — the same best-effort javascript and rust make of a member call
        calls.ShouldContain(c => c.Text == "http.HandleFunc(\"/\", nil)");

        // and a composite literal is NOT a call: it has its own kind with an addressable type name, so
        // leaving it edge-less is a decision the extractor makes, not something the grammar forces
        var literal = CollectNodes(tree.RootNode!).Single(n => n.Type == "composite_literal");
        literal.GetChildForField("type")!.Text.ShouldBe("Store");
    }

    [Fact]
    public void Go_ImportSpecs_BindTheLastPathElementUnlessNamed()
    {
        using var language = new Language("Go");
        using var parser = new Parser(language);
        using var tree = Parse(parser, GoReferenceSource);
        var specs = CollectNodes(tree.RootNode!).Where(n => n.Type == "import_spec").ToList();
        specs.Count.ShouldBe(5);

        // the path is a quoted literal and the spec's 'name' field is present ONLY when the file writes
        // one, and its kind says which of the three special forms it is: an alias (package_identifier),
        // a dot-import ('.' as a node of kind dot), or a side-effect import (blank_identifier). Nothing in
        // the node kind distinguishes them from each other except by these kinds, so the reader has to
        // look at three names, not one
        specs.Select(s => (s.GetChildForField("name")?.Type ?? "none")).ShouldBe(
            ["none", "package_identifier", "dot", "blank_identifier", "none"]);
        specs.Select(s => s.GetChildForField("path")!.Text).ShouldBe(
            ["\"fmt\"", "\"net/http\"", "\"strings\"", "\"database/sql\"", "\"os\""]);
        specs.Single(s => s.Text.Contains("http \"", StringComparison.Ordinal))
            .GetChildForField("name")!.Text.ShouldBe("http");

        // the form the binding rule exists for, measured on its own: an un-aliased multi-element path carries
        // NO name field, so the reader must derive the binding from the path text — and the file can only
        // ever type the LAST element ('json.Marshal'), the exact inverse of python's top-package rule
        // (BLOCKER-1 read from the other side of the wall). The selector node is the evidence that 'json'
        // is the name in scope, not a guess from the path string
        using var bareParser = new Parser(new Language("Go"));
        using var bareTree = Parse(bareParser, """
            package p

            import "encoding/json"

            func Use() {
                json.Marshal(1)
            }
            """);
        var bareSpec = CollectNodes(bareTree.RootNode!).Single(n => n.Type == "import_spec");
        bareSpec.GetChildForField("name").ShouldBeNull();
        bareSpec.GetChildForField("path")!.Text.ShouldBe("\"encoding/json\"");
        var selector = CollectNodes(bareTree.RootNode!).Single(n => n.Type == "selector_expression");
        selector.GetChildForField("operand")!.Text.ShouldBe("json");
        selector.GetChildForField("field")!.Text.ShouldBe("Marshal");

        // two container shapes: the parenthesised group wraps its specs in import_spec_list, the single
        // 'import "os"' form hangs the spec directly off import_declaration
        CollectNodes(tree.RootNode!).Count(n => n.Type == "import_spec_list").ShouldBe(1);
        CollectNodes(tree.RootNode!).Single(n => n.Text == "import \"os\"")
            .NamedChildren.Single().Type.ShouldBe("import_spec");
    }

    [Fact]
    public void Go_EmbeddedFieldsAndElements_CarryNoNameField()
    {
        using var language = new Language("Go");
        using var parser = new Parser(language);
        using var tree = Parse(parser, GoReferenceSource);

        // embedding is go's inheritance, and the grammar marks it by ABSENCE: an embedded struct field
        // and an embedded interface element are the same node kinds as their named cousins minus the name
        // field, so a reader that took every field_declaration or type_elem as a reference would name the
        // base as a mere user of the type
        var embedded = CollectNodes(tree.RootNode!).Where(n =>
            n.Type == "field_declaration" && n.GetChildForField("name") is null).ToList();
        embedded.Select(n => n.GetChildForField("type")!.Text).ShouldBe(["emb"]);
        var named = CollectNodes(tree.RootNode!).Where(n =>
            n.Type == "field_declaration" && n.GetChildForField("name") is not null).ToList();
        named.Select(n => n.GetChildForField("name")!.Text).ShouldBe(["handler", "items", "list", "ptr"]);

        var elements = CollectNodes(tree.RootNode!).Where(n => n.Type == "type_elem").ToList();
        elements.ShouldHaveSingleItem();
        elements[0].GetChildForField("name").ShouldBeNull();
        elements[0].NamedChildren.Single().Type.ShouldBe("type_identifier");
        elements[0].Text.ShouldBe("Store");

        // where the kind actually lives: measured over a file with unnamed parameters and a grouped
        // import, an unnamed parameter is a parameter_declaration with no name field — not a type_elem —
        // and every type_elem here sat directly in an interface_type. The parent check is not decoration:
        // the next block shows the same kind reused for generic instantiation arguments
        using var shapeParser = new Parser(new Language("Go"));
        using var shapeTree = Parse(shapeParser, """
            package p

            type Iface interface {
                Reader
                Do() error
            }

            type Reader struct{}

            func Take(fn func(int) error, _ string) (n int, err error) {
                _ = fn
                return 0, nil
            }
            """);
        var everyElem = CollectNodes(shapeTree.RootNode!).Where(n => n.Type == "type_elem").ToList();
        everyElem.Select(e => e.Text).ShouldBe(["Reader"]);
        everyElem.ShouldAllBe(e => e.Parent!.Type == "interface_type");
        CollectNodes(shapeTree.RootNode!).Where(n => n.Type == "parameter_declaration")
            .Count(n => n.GetChildForField("name") is null).ShouldBeGreaterThan(0);

        // type_elem has a SECOND home, and this is the measurement that decides the reader's parent guard:
        // an instantiated generic writes its type arguments as type_elem children of a type_arguments node,
        // so 'Generic[int]' contains a type_elem exactly like an interface's embedded element does. Two
        // meanings, one kind name, no name field on either — the parent is the only discriminator
        using var genericParser = new Parser(new Language("Go"));
        using var genericTree = Parse(genericParser, """
            package p

            type Store struct{}

            type Box interface {
                Store
            }

            func Use(b Box[int]) {}
            """);
        var genericElements = CollectNodes(genericTree.RootNode!).Where(n => n.Type == "type_elem").ToList();
        genericElements.Select(e => (e.Text, e.Parent!.Type))
            .ShouldBe([("Store", "interface_type"), ("int", "type_arguments")]);
        CollectNodes(genericTree.RootNode!).Single(n => n.Type == "generic_type")
            .GetChildForField("type")!.Text.ShouldBe("Box");

        // and the type positions do not agree on a field name: qualified_type has 'name' (with the
        // package as an unnamed sibling), generic_type has 'type', pointer_type has NO field at all, and
        // each aggregate names its element differently
        var qualified = CollectNodes(tree.RootNode!).Single(n => n.Type == "qualified_type");
        qualified.GetChildForField("name")!.Text.ShouldBe("Handler");
        var pointer = CollectNodes(tree.RootNode!).Single(n => n.Text == "*Widget");
        pointer.NamedChildren.Single().Type.ShouldBe("type_identifier");
        CollectNodes(tree.RootNode!).Where(n => n.Type == "pointer_type")
            .Count(p => p.GetChildForField("type") is not null).ShouldBe(0);
        CollectNodes(tree.RootNode!).Single(n => n.Type == "map_type").GetChildForField("value")!.Text.ShouldBe("Item");
        CollectNodes(tree.RootNode!).Single(n => n.Type == "slice_type").GetChildForField("element")!.Text.ShouldBe("Measure");
    }

    [Fact]
    public void Go_ThreeFieldNamesLookAlikeAndOnlyOneOfThemBindsAnything()
    {
        using var language = new Language("Go");
        using var parser = new Parser(language);
        using var tree = Parse(parser, """
            package p

            type T struct {
                emb
                _ int
                a, b Base
                svc http.Handler
            }

            type Base struct{}
            """);

        // the discriminator a reader has to get right, measured in one file: an embedded field has NO name
        // field, a blank field has a name field whose TEXT is '_', and a two-name field repeats the name
        // field with only the first one reachable through GetChildForField. Absence, text and repetition
        // are three different facts and each answers a different question — embed, discard, or use once
        var fields = CollectNodes(tree.RootNode!).Where(n => n.Type == "field_declaration").ToList();
        fields.ShouldAllBe(f => f.Parent!.Type == "field_declaration_list");
        fields.Count(f => f.GetChildForField("name") is null).ShouldBe(1);

        var blank = fields.Single(f => f.GetChildForField("name")?.Text == "_");
        blank.GetChildForField("name")!.Type.ShouldBe("field_identifier");

        var multi = fields.Single(f => f.Text.Contains("a, b", StringComparison.Ordinal));
        multi.GetChildForField("name")!.Text.ShouldBe("a");
        multi.NamedChildren.Count(c => c.Type == "field_identifier").ShouldBe(2);
        multi.NamedChildren.Last().Type.ShouldBe("type_identifier");

        // so a reader keyed on the name field's presence alone would call the discarded blank a use, and a
        // reader keyed per name would sign the same span twice for 'a, b' — the two refusals the extractor
        // makes, measured here rather than argued
        fields.Count(f => f.GetChildForField("type") is not null).ShouldBe(fields.Count);
        fields.Where(f => f.GetChildForField("name")?.Text != "_")
            .Select(f => f.GetChildForField("type")!.Text).ShouldBe(["emb", "Base", "http.Handler"]);
    }

    [Fact]
    public void Go_CallExpressionIsSharedWithJavascriptAndRust_OnTheReferenceSideToo()
    {
        using var go = new Language("Go");
        using var goParser = new Parser(go);
        using var goTree = Parse(goParser, GoReferenceSource);
        var goNames = CollectTypes(goTree.RootNode!).ToHashSet(StringComparer.Ordinal);

        using var js = new Language("JavaScript");
        using var jsParser = new Parser(js);
        using var jsTree = Parse(jsParser, JsVocabularySource);
        var jsNames = CollectTypes(jsTree.RootNode!).ToHashSet(StringComparer.Ordinal);

        using var rust = new Language("Rust");
        using var rustParser = new Parser(rust);
        using var rustTree = Parse(rustParser, RustVocabularySource);
        var rustNames = CollectTypes(rustTree.RootNode!).ToHashSet(StringComparer.Ordinal);

        // the second shared kind in the feature, on the reference side: go, javascript and rust all name
        // their call node call_expression, while python keeps 'call' to itself. So the per-language gate
        // buys nothing here by ITSELF — what keeps the grammars apart is that each one's gate is consulted
        // only for its own label, and the callee field names (field / property / field-on-
        // field_expression) never collide because the callee node kinds do
        goNames.ShouldContain("call_expression");
        jsNames.ShouldContain("call_expression");
        rustNames.ShouldContain("call_expression");
        goNames.ShouldNotContain("member_expression");
        jsNames.ShouldNotContain("selector_expression");
        rustNames.ShouldNotContain("import_declaration");
        jsNames.ShouldNotContain("import_declaration");
        // a FOURTH shared kind, and the one that would have been easy to assume wrong: rust spells its
        // struct fields 'field_declaration' exactly as go does, so the only thing keeping rust's fields
        // out of go's type-target reading — and go's out of rust's — is that the reader is chosen by the
        // language row and the gate is consulted per label. Two grammars, same kind name, different
        // meanings: go's embedded field has no name field, rust's never has one either but its shape is
        // name+type, so a merged reader would mis-read both
        rustNames.ShouldContain("field_declaration");
        goNames.ShouldContain("field_declaration");
        jsNames.ShouldNotContain("type_spec");
        goNames.ShouldNotContain("use_declaration");
        goNames.ShouldNotContain("import_statement");

        // go's import container shares no name with python/javascript's import_statement, which is the
        // reason the import READER could stay a per-grammar arm and only the statement kinds overlap
        goNames.ShouldContain("import_declaration");
        jsNames.ShouldContain("import_statement");
        goNames.ShouldNotContain("import_statement");
    }

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

    private static bool HasAncestor(Node node, string type)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current.Type == type)
            {
                return true;
            }
        }

        return false;
    }

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
