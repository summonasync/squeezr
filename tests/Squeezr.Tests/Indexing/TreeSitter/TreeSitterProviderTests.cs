using System.Text;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.TreeSitter;

namespace Squeezr.Tests.Indexing.TreeSitter;

public class TreeSitterProviderTests
{
    private static readonly SqueezrConfig Config = SqueezrConfig.CreateDefault();

    [Fact]
    public void CanHandle_JavaScriptTypeScriptExtensions_AcceptedOthersRejected()
    {
        var provider = new TreeSitterProvider(Config);

        provider.CanHandle("app.js", null).ShouldBeTrue();
        provider.CanHandle("app.jsx", null).ShouldBeTrue();
        provider.CanHandle("app.mjs", null).ShouldBeTrue();
        provider.CanHandle("app.cjs", null).ShouldBeTrue();
        provider.CanHandle("app.mjs", "js").ShouldBeTrue();
        provider.CanHandle("app.mjs", "python").ShouldBeFalse();
        provider.CanHandle("app.ts", null).ShouldBeTrue();
        provider.CanHandle("app.tsx", null).ShouldBeTrue();
        provider.CanHandle("app.tsx", "typescript").ShouldBeTrue();
        provider.CanHandle("app.cs", null).ShouldBeFalse();
        provider.CanHandle("notes.md", null).ShouldBeFalse();
        provider.CanHandle("app.ts", "python").ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_RustExtension_AcceptsRustHintsAndRejectsForeign()
    {
        var provider = new TreeSitterProvider(Config);

        provider.CanHandle("app.rs", null).ShouldBeTrue();
        provider.CanHandle("app.rs", "rust").ShouldBeTrue();
        provider.CanHandle("app.rs", "rs").ShouldBeTrue();
        provider.CanHandle("src/model.RS", "RUST").ShouldBeTrue();
        provider.CanHandle("app.rs", "javascript").ShouldBeFalse();
        provider.CanHandle("app.rs", "typescript").ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_PythonAndGoExtensions_AcceptOwnHintsAndRejectForeign()
    {
        var provider = new TreeSitterProvider(Config);

        provider.CanHandle("app.py", null).ShouldBeTrue();
        provider.CanHandle("app.py", "python").ShouldBeTrue();
        provider.CanHandle("app.py", "py").ShouldBeTrue();
        provider.CanHandle("src/MODEL.PY", "PYTHON").ShouldBeTrue();
        provider.CanHandle("app.py", "javascript").ShouldBeFalse();
        provider.CanHandle("main.go", null).ShouldBeTrue();
        provider.CanHandle("main.go", "go").ShouldBeTrue();
        provider.CanHandle("cmd/Main.GO", "GO").ShouldBeTrue();
        provider.CanHandle("main.go", "rust").ShouldBeFalse();
    }

    [Theory]
    [InlineData("src/app.js", "JavaScript", "javascript")]
    [InlineData("src/app.jsx", "JavaScript", "javascript")]
    [InlineData("tools/a.mjs", "JavaScript", "javascript")]
    [InlineData("tools/b.cjs", "JavaScript", "javascript")]
    [InlineData("src/store.ts", "TypeScript", "typescript")]
    [InlineData("src/Badge.tsx", "TSX", "tsx")]
    [InlineData("src/lib.rs", "Rust", "rust")]
    [InlineData("src/LIB.RS", "Rust", "rust")]
    [InlineData("src/main.py", "Python", "python")]
    [InlineData("src/MAIN.PY", "Python", "python")]
    [InlineData("cmd/server.go", "Go", "go")]
    [InlineData("cmd/SERVER.GO", "Go", "go")]
    [InlineData("notes.md", "JavaScript", "javascript")]
    public void GrammarAndLanguageArms_PerExtension_ResolvesOffTheDefault(string path, string grammar, string language)
    {
        // both switches end in a JavaScript default, so every routed extension needs its own row here
        TreeSitterProvider.GrammarName(path).ShouldBe(grammar);
        TreeSitterProvider.LanguageName(path).ShouldBe(language);
    }

    [Fact]
    public void Rust_Sample_ParsesThroughTheRustArmsWithoutWarnings()
    {
        var provider = new TreeSitterProvider(Config);
        var result = provider.Parse(new FileRef("demo", "src/registry.rs"), Encoding.UTF8.GetBytes("""
            use std::fmt;

            pub struct Registry {
                items: Vec<String>,
            }

            impl Registry {
                pub fn render(&self) -> String {
                    self.items.join(", ")
                }
            }

            fn boot() -> Registry {
                Registry { items: Vec::new() }
            }
            """));

        // the extension arms are pinned by GrammarAndLanguageArms_PerExtension_ResolvesOffTheDefault;
        // here the rust grammar has to actually reach the rust symbol vocabulary
        result.Language.ShouldBe("rust");
        provider.Warnings.ShouldBeEmpty();
        result.Symbols.Select(s => (s.Name, s.Kind, s.ContainerName)).ShouldBe(
        [
            ("Registry", SymbolKind.Class, null),
            ("Registry", SymbolKind.Other, null),
            ("render", SymbolKind.Method, "Registry"),
            ("boot", SymbolKind.Function, null),
        ]);
        result.Refs.Select(r => (r.ToName, r.Kind, r.Line)).ShouldBe(
        [
            ("fmt", RefKind.Import, 1),
            ("Registry", RefKind.Reference, 7),
            ("join", RefKind.Call, 9),
            ("new", RefKind.Call, 14),
        ]);
        result.Refs[0].ToSymbolId.ShouldBe("std");
        result.Refs[2].EnclosingName.ShouldBe("Registry.render");
    }

    [Fact]
    public void CommonJsAssignment_NamedFunctionExpression_ExtractedAsFunction()
    {
        var result = Parse("module.cjs", "module.exports = function run() {\n  return 'ready';\n};\n");

        result.Language.ShouldBe("javascript");
        result.Symbols.Select(s => (s.Name, s.Kind)).ShouldBe([("run", SymbolKind.Function)]);
        result.Symbols[0].Signature.ShouldBe("function run()");
    }

    [Fact]
    public void AnonymousFunctionExpression_YieldsNoSymbol()
    {
        var result = Parse("handler.js", "fn(function (item) {\n  return item;\n});\n");

        result.Symbols.ShouldBe([]);
    }

    [Fact]
    public void JavaScript_Symbols_InsideNamedFunctionExpression_ContainerIsTheFunctionName()
    {
        var result = Parse("app.js", """
            const bootstrap = function run() {
              function inner() {
                return 1;
              }
            };
            """);

        result.Symbols.Select(s => (s.Name, s.Kind)).ShouldBe([
            ("run", SymbolKind.Function),
            ("inner", SymbolKind.Function),
        ]);

        var run = result.Symbols[0];
        run.ContainerName.ShouldBeNull();

        var inner = result.Symbols[1];
        inner.ContainerName.ShouldBe("run");
    }

    [Fact]
    public void JavaScript_Refs_InsideNamedFunctionExpression_EnclosingIsTheFunctionName()
    {
        var result = Parse("app.js", """
            const bootstrap = function run() {
              helper();
            };
            """);

        var callRef = result.Refs.Single(r => r.Kind == RefKind.Call);
        callRef.ToName.ShouldBe("helper");
        callRef.EnclosingName.ShouldBe("run");
    }

    [Fact]
    public void JavaScript_Symbols_FunctionArrowClassMethod_ExtractedWithSpansAndContainers()
    {
        var result = Parse("app.js", """
            import { mul } from './util';

            export function add(a, b) {
              return a + b;
            }

            const square = (x) => x * x;

            export class Calc {
              double(n) {
                return mul(2, n);
              }
            }
            """);

        result.Language.ShouldBe("javascript");
        result.Symbols.Select(s => (s.Name, s.Kind)).ShouldBe([
            ("add", SymbolKind.Function),
            ("square", SymbolKind.Function),
            ("Calc", SymbolKind.Class),
            ("double", SymbolKind.Method),
        ]);

        var add = result.Symbols[0];
        add.Span.ShouldBe(new CodeSpan(3, 5));
        add.Signature.ShouldBe("function add(a, b)");
        add.ContainerName.ShouldBeNull();

        var square = result.Symbols[1];
        square.Span.ShouldBe(new CodeSpan(7, 7));
        square.Signature.ShouldBe("const square = (x) => x * x;");

        var calc = result.Symbols[2];
        calc.Span.ShouldBe(new CodeSpan(9, 13));

        var doubleMethod = result.Symbols[3];
        doubleMethod.Span.ShouldBe(new CodeSpan(10, 12));
        doubleMethod.ContainerName.ShouldBe("Calc");
        doubleMethod.Signature.ShouldBe("double(n)");

        result.Chunks.Count.ShouldBe(4);
        var addChunk = result.Chunks.Single(c => c.Span == add.Span);
        addChunk.SymbolId.ShouldBeNull();
        addChunk.Text.ShouldStartWith("export function add(a, b) {");
        addChunk.TokenCount.ShouldBeGreaterThan(0);
        addChunk.ChunkHash.ShouldMatch("^[0-9A-F]{64}$");
    }

    [Fact]
    public void JavaScript_Refs_ImportExactCallBestEffortWithEnclosing()
    {
        var result = Parse("app.js", """
            import { mul } from './util';

            export function add(a, b) {
              return a + b;
            }

            const square = (x) => x * x;

            export class Calc {
              double(n) {
                return mul(2, n);
              }
            }
            """);

        var importRef = result.Refs.Single(r => r.Kind == RefKind.Import);
        importRef.ToName.ShouldBe("mul");
        importRef.ToSymbolId.ShouldBe("./util");
        importRef.Line.ShouldBe(1);
        importRef.FromPath.ShouldBe("app.js");
        importRef.EnclosingName.ShouldBeNull();

        var callRef = result.Refs.Single(r => r.Kind == RefKind.Call);
        callRef.ToName.ShouldBe("mul");
        callRef.Line.ShouldBe(11);
        callRef.FromPath.ShouldBe("app.js");
        callRef.EnclosingName.ShouldBe("Calc.double");
    }

    [Fact]
    public void JavaScript_ReExport_EmitsImportRefPerBinding()
    {
        var result = Parse("reexport.js", """
            export { add, mul as multiply } from './util';
            export * from './extra';
            """);

        var refs = result.Refs.Where(r => r.Kind == RefKind.Import).ToList();
        refs.Select(r => r.ToName).ShouldBe(["add", "multiply", "./extra"]);
        refs[0].ToSymbolId.ShouldBe("./util");
        refs[1].ToSymbolId.ShouldBe("./util");
        refs[2].ToSymbolId.ShouldBeNull();
        refs.ShouldAllBe(r => r.Line == 1 || r.Line == 2);
    }

    [Fact]
    public void JavaScript_ImportVariants_DefaultAndNamespace_BindingsCaptured()
    {
        var result = Parse("imports.js", """
            import def from './def';
            import * as util from './util';
            import './polyfill';
            """);

        var refs = result.Refs.Where(r => r.Kind == RefKind.Import).ToList();
        refs.Select(r => (r.ToName, r.ToSymbolId)).ShouldBe([
            ("def", "./def"),
            ("util", "./util"),
            ("./polyfill", null),
        ]);
    }

    [Fact]
    public void TypeScript_Symbols_InterfaceTypeAliasTypedMethod_Extracted()
    {
        var result = Parse("store.ts", """
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

        result.Language.ShouldBe("typescript");
        result.Symbols.Select(s => (s.Name, s.Kind)).ShouldBe([
            ("User", SymbolKind.Interface),
            ("UserId", SymbolKind.Other),
            ("Store", SymbolKind.Class),
            ("find", SymbolKind.Method),
        ]);

        result.Symbols[0].Span.ShouldBe(new CodeSpan(1, 4));
        result.Symbols[1].Signature.ShouldBe("type UserId = string;");
        var find = result.Symbols[3];
        find.ContainerName.ShouldBe("Store");
        find.Signature.ShouldBe("find(id: UserId): User | undefined");
    }

    [Fact]
    public void TypeScript_ClassConstructor_KindParityWithCSharp()
    {
        var result = Parse("widget.ts", """
            export class Widget {
              private size: number;

              constructor(size: number) {
                this.size = size;
              }

              grow(): number {
                return this.size + 1;
              }
            }
            """);

        var ctor = result.Symbols.Single(s => s.Name == "constructor");
        ctor.Kind.ShouldBe(SymbolKind.Constructor);
        ctor.ContainerName.ShouldBe("Widget");
        ctor.Span.ShouldBe(new CodeSpan(4, 6));

        result.Symbols.Single(s => s.Name == "grow").Kind.ShouldBe(SymbolKind.Method);
    }

    [Fact]
    public void Tsx_ComponentFunction_ExtractedWithJsxBody()
    {
        var result = Parse("badge.tsx", """
            export function Badge({ count }: { count: number }) {
              return <div className="badge">{count}</div>;
            }
            """);

        var badge = result.Symbols.ShouldHaveSingleItem();
        badge.Name.ShouldBe("Badge");
        badge.Kind.ShouldBe(SymbolKind.Function);
        result.Language.ShouldBe("tsx");
        var chunk = result.Chunks.ShouldHaveSingleItem();
        chunk.Text.ShouldContain("className=\"badge\"");
    }

    [Fact]
    public void JavaScript_JSDocBeforeFunction_BecomesDocComment()
    {
        var result = Parse("doc.js", """
            /**
             * Adds two numbers.
             * @param a first
             */
            function add(a, b) {
              return a + b;
            }
            """);

        result.Symbols.ShouldHaveSingleItem().DocComment.ShouldBe("Adds two numbers.\n@param a first");
    }

    [Fact]
    public void JavaScript_PlainCommentBeforeFunction_NoDocComment()
    {
        var result = Parse("plain.js", """
            // just a note
            function add(a, b) {
              return a + b;
            }
            """);

        result.Symbols.ShouldHaveSingleItem().DocComment.ShouldBeNull();
    }

    [Fact]
    public void JavaScript_CallInsideTopLevelFunction_EnclosingIsFunctionName()
    {
        var result = Parse("main.js", """
            function main() {
              helper();
            }
            """);

        var callRef = result.Refs.ShouldHaveSingleItem();
        callRef.ToName.ShouldBe("helper");
        callRef.EnclosingName.ShouldBe("main");
    }

    [Fact]
    public void JavaScript_BrokenFile_PartialSymbolsWithoutException()
    {
        var provider = new TreeSitterProvider(Config);
        var result = provider.Parse(
            new FileRef("demo", "broken.js"),
            Encoding.UTF8.GetBytes("""
            function good(a) {
              return a;
            }

            function broken(a, b {
              return a + b;
            """));

        provider.Warnings.ShouldBeEmpty();
        result.Symbols.Select(s => s.Name).ShouldContain("good");
    }

    [Fact]
    public void JavaScript_SymbolOverTokenBudget_SplitIntoSubChunksWithSignaturePrefix()
    {
        var config = Config with
        {
            Chunking = Config.Chunking with { MaxTokensPerChunk = 20 },
        };
        var body = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"  const value{i} = compute({i}) + {i};"));
        var result = Parse("big.js", $"function big() {{\n{body}\n}}\n", config);

        var symbol = result.Symbols.ShouldHaveSingleItem();
        var chunks = result.Chunks;
        chunks.Count.ShouldBeGreaterThan(1);
        chunks.ShouldAllBe(c => c.SymbolId == null);
        chunks.ShouldAllBe(c => c.TokenCount <= 20);
        chunks[0].Text.ShouldStartWith("function big()\n");
        chunks.ShouldAllBe(c => c.Text.Contains("function big()"));
        chunks[^1].Span.EndLine.ShouldBe(symbol.Span.EndLine);
    }

    // python left this list in T2 — its table landed, so it is no longer the empty-vocabulary shape
    [Theory]
    [InlineData("cmd/server.go", "go", "package main\n\nfunc Load(path string) string {\n  return path\n}\n")]
    public void UnroutedButRegisteredLanguage_ParsesCleanlyAndYieldsNoRows(string path, string language, string source)
    {
        var provider = new TreeSitterProvider(Config);
        var result = provider.Parse(new FileRef("demo", path), Encoding.UTF8.GetBytes(source));

        // the warnings check is what makes the empty rows mean 'nothing to extract' rather than 'the
        // parse blew up and the catch-all hid it'; the arms resolve to the real grammar, the home
        // answers with an empty vocabulary, and the indexer keeps such a file on the fallback chunker
        // because Routed() is false — none of those three facts may silently flip
        provider.Warnings.ShouldBeEmpty();
        result.Language.ShouldBe(language);
        result.Symbols.ShouldBe([]);
        result.Refs.ShouldBe([]);
        result.Chunks.ShouldBe([]);
    }

    [Fact]
    public void Python_RoutedWithSymbolsButNoReferenceRules_YieldsRowsWithoutEdges()
    {
        var provider = new TreeSitterProvider(Config);
        var result = provider.Parse(
            new FileRef("demo", "src/loader.py"),
            Encoding.UTF8.GetBytes("def load(path):\n  return open(path).read()\n"));

        // the middle state of the two axes, read through the provider: the symbol table is live, the
        // reference vocabulary is still null, so rows are extracted and named from the real grammar
        // while no edge is invented for a language that has no pool yet
        provider.Warnings.ShouldBeEmpty();
        result.Language.ShouldBe("python");
        result.Symbols.Select(s => (s.Name, s.Kind)).ShouldBe([("load", SymbolKind.Function)]);
        result.Refs.ShouldBe([]);
        result.Chunks.Count.ShouldBe(1);
    }

    [Fact]
    public void JavaScript_MemberExpressionCall_NamedAfterTheProperty()
    {
        var result = Parse("run.js", """
            export function run(items, each) {
              return items.map(each);
            }
            """);

        var call = result.Refs.ShouldHaveSingleItem();
        call.ToName.ShouldBe("map");
        call.Kind.ShouldBe(RefKind.Call);
        call.Line.ShouldBe(2);
        call.EnclosingName.ShouldBe("run");
    }

    private static ParseResult Parse(string path, string text, SqueezrConfig? config = null) =>
        new TreeSitterProvider(config ?? Config).Parse(new FileRef("demo", path), Encoding.UTF8.GetBytes(text));
}

