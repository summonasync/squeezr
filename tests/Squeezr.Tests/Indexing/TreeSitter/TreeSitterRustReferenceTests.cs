using System.Text;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.TreeSitter;
using TreeSitter;

namespace Squeezr.Tests.Indexing.TreeSitter;

public class TreeSitterRustReferenceTests
{
    private static readonly SqueezrConfig Config = SqueezrConfig.CreateDefault();

    private const string UseAndCallSource = """
        use std::collections::HashMap;
        use serde::{Deserialize as De, serialize::Serializer};
        use rand::prelude::*;

        pub trait Lookup {
            fn find(&self, key: &str) -> Option<&String>;
        }

        impl HashMap {
            pub fn build() -> Self {
                let map = HashMap::new();
                shout!("ready");
                map.len()
            }
        }
        """;

    private const string ImplTargetSource = """
        impl fmt::Display for Registry {
            fn render(&self) -> String {
                <Registry as fmt::Display>::render(self)
            }
        }

        impl<T> Container<T> {
            pub fn pick(items: Vec<T>) {
            }
        }

        impl<T> app::Registry<T> {
        }
        """;

    private const string MacroSource = """
        macro_rules! shout {
            ($msg:expr) => {
                println!("{}", $msg);
            };
        }

        pub fn emit() {
            shout!("hi");
        }
        """;

    [Fact]
    public void Rust_UseDeclaration_EmitsOneImportRefPerBoundNameWithPathAsModule()
    {
        var refs = Parse("src/lib.rs", UseAndCallSource).Refs;

        var imports = refs.Where(r => r.Kind == RefKind.Import).ToList();
        imports.Select(r => (r.ToSymbolId, r.ToName)).ShouldBe(
        [
            ("std::collections", "HashMap"),
            ("serde", "De"),
            ("serde::serialize", "Serializer"),
            ("rand", "prelude"),
        ]);
        imports[0].Line.ShouldBe(1);
        imports[1].Line.ShouldBe(2);
        imports[2].Line.ShouldBe(2);
        imports[3].Line.ShouldBe(3);
    }

    [Fact]
    public void Rust_GlobalUse_BindsTheGlobbedModuleAtEveryPathDepth()
    {
        var imports = Parse("src/glob.rs", """
            use prelude::*;
            use app::prelude::*;
            """).Refs;

        // one semantics, one binding name: the bare form used to bind the module row while the
        // qualified one carried the whole path as its name and matched nothing — same import,
        // two outcomes depending on how deep the path happened to be
        imports.Select(r => (r.ToSymbolId, r.ToName)).ShouldBe(
        [
            (null, "prelude"),
            ("app", "prelude"),
        ]);
        imports.ShouldAllBe(r => r.Kind == RefKind.Import);
    }

    [Fact]
    public void Rust_UseList_BindsSelfAndNestedAliasesUnderTheirModule()
    {
        var imports = Parse("src/uses.rs", """
            use std::io::{self, Read as Rd};
            use serde::{Deserialize as De, serialize::Serializer};
            """).Refs;

        // 'self' binds the module it sits in under its own last segment, the same pair of columns
        // a javascript import binding carries: where it came from, and what it is called here
        imports.Select(r => (r.ToSymbolId, r.ToName)).ShouldBe(
        [
            ("std::io", "io"),
            ("std::io", "Rd"),
            ("serde", "De"),
            ("serde::serialize", "Serializer"),
        ]);
        imports.ShouldAllBe(r => r.Kind == RefKind.Import);
        imports.Select(r => r.Line).ShouldBe([1, 1, 2, 2]);
    }

    [Fact]
    public void Rust_AliasedNestedPath_ModuleCarriesListPrefixAndInnerPath()
    {
        var imports = Parse("src/nested.rs", """
            use app::model::{User as U, order::Sort as S};
            """).Refs;

        // 'U' comes from app::model, 'S' from app::model::order — the inner segment of an aliased
        // nested path is part of the module, dropping it would point both bindings at the same place
        imports.Select(r => (r.ToSymbolId, r.ToName)).ShouldBe(
        [
            ("app::model", "U"),
            ("app::model::order", "S"),
        ]);
    }

    [Fact]
    public void Rust_FreeScopedAndMethodCalls_EmitCallRefsNamedAfterTheCallee()
    {
        var calls = Parse("src/lib.rs", UseAndCallSource).Refs.Where(r => r.Kind == RefKind.Call).ToList();

        calls.Select(r => (r.ToName, r.Line)).ShouldBe([("new", 11), ("shout", 12), ("len", 13)]);
        calls.ShouldAllBe(r => r.ToSymbolId == null);
        calls.ShouldAllBe(r => r.EnclosingName == "HashMap.build");
    }

    [Fact]
    public void Rust_TraitSignature_BodyEmitsNoCallRefs()
    {
        var refs = Parse("src/lib.rs", UseAndCallSource).Refs;

        // denominator first: a walker that produced nothing would satisfy the negative too
        refs.Count.ShouldBe(8);
        refs.ShouldNotContain(r => r.ToName == "find");
    }

    [Fact]
    public void Rust_ImplItem_EmitsImplementTargetForTraitAndReferenceTargetForSelfType()
    {
        var refs = Parse("src/impls.rs", ImplTargetSource).Refs;

        refs.Select(r => (r.ToName, r.Kind, r.Line)).ShouldBe(
        [
            ("Display", RefKind.Implement, 1),
            ("Registry", RefKind.Reference, 1),
            ("render", RefKind.Call, 3),
            ("Container", RefKind.Reference, 7),
            ("Registry", RefKind.Reference, 12),
        ]);

        var traitRef = refs[0];
        traitRef.ToSymbolId.ShouldBe("fmt");
        refs[1].ToSymbolId.ShouldBeNull();
        refs[2].EnclosingName.ShouldBe("fmt::Display for Registry.render");
        // the qualified generic target keeps its bare name: 'app::Registry' would never match the
        // struct row, and the loss is silent — this is the form the generic unwrap exists for
        refs[4].ToName.ShouldNotBe("app::Registry");
    }

    [Fact]
    public void Rust_ReferenceTypeTarget_IsUnwrappedToTheNamedType()
    {
        var refs = Parse("src/reffed.rs", """
            impl fmt::Display for &Registry {
                fn dump(&self) {
                }
            }
            """).Refs;

        // review-111-04 N-1: '&Registry' is a reference_type wrapping the type identifier; reading
        // its text would put '&Registry' in the name column, and the type row would never answer
        refs.Select(r => (r.ToName, r.Kind, r.Line)).ShouldBe(
        [
            ("Display", RefKind.Implement, 1),
            ("Registry", RefKind.Reference, 1),
        ]);
        refs[0].ToSymbolId.ShouldBe("fmt");
        refs[1].ToSymbolId.ShouldBeNull();
    }

    [Fact]
    public void Rust_GenericQualifiedTraitAndTarget_KeepTheirBareNamesWithQualifiedModules()
    {
        var refs = Parse("src/combo.rs", """
            impl<T> serde::Serialize<T> for Holder<T> {
                fn dump(&self) {
                    String::new()
                }
            }
            """).Refs;

        // review-111-04 N-4: the generic and the qualifier land together on both columns, so the
        // unwrap has to step out of generic_type before it reads the scoped name — doing only one
        // side leaves 'Serialize<T>' or 'Holder<T>' in the name column and drops the edge silently
        refs.Select(r => (r.ToName, r.Kind, r.Line)).ShouldBe(
        [
            ("Serialize", RefKind.Implement, 1),
            ("Holder", RefKind.Reference, 1),
            ("new", RefKind.Call, 3),
        ]);
        refs[0].ToSymbolId.ShouldBe("serde");
        refs[1].ToSymbolId.ShouldBeNull();
    }

    [Fact]
    public void Rust_MacroInvocation_EmitsCallRefAndDefinitionBodyStaysQuiet()
    {
        var refs = Parse("src/macro.rs", MacroSource).Refs;

        refs.ShouldHaveSingleItem();
        refs[0].ToName.ShouldBe("shout");
        refs[0].Kind.ShouldBe(RefKind.Call);
        refs[0].Line.ShouldBe(8);
        refs[0].EnclosingName.ShouldBe("emit");
    }

    [Fact]
    public void Rust_ReferenceRows_CarryTheSourcePathOfTheirFile()
    {
        var refs = Parse("src/lib.rs", UseAndCallSource).Refs;

        refs.ShouldNotBeEmpty();
        refs.ShouldAllBe(r => r.FromPath == "src/lib.rs");
    }

    [Fact]
    public void Extract_UnknownLanguageLabel_EmitsNoReferencesWhereRustEmitsItsOwn()
    {
        using var grammar = new Language("Rust");
        using var parser = new Parser(grammar);
        using var tree = parser.Parse(UseAndCallSource)!;

        // same tree, different label: unrouted languages must not borrow the js/ts walker, whose
        // import and member-expression shapes this tree does have analogues of by name alone
        TsReferenceExtractor.Extract(tree.RootNode!, "rust", "src/lib.rs").Count.ShouldBe(8);
        TsReferenceExtractor.Extract(tree.RootNode!, "python", "src/lib.rs").ShouldBe([]);
    }

    private static ParseResult Parse(string path, string text) =>
        new TreeSitterProvider(Config).Parse(new FileRef("demo", path), Encoding.UTF8.GetBytes(text));
}
