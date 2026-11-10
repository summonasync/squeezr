using Shouldly;
using Squeezr.Core.Indexing.TreeSitter;

namespace Squeezr.Tests.Indexing.TreeSitter;

// Registration-only state: this class pins the line between 'known' and 'handled' that each language
// crosses on two separate days. python crossed both (symbol table in T2, references in T3); go crossed
// the first in T4 and is now the resident of the middle state — routed, and with no reference pool until
// T5. No language is left registered ahead of its vocabulary, which the last test in this file states
// as an invariant because it is what keeps the zero-chunk trap unwritable.
public class TreeSitterLanguagesRegistrationTests
{
    // Two axes that grow on different days, so they get two theories: a language joins this first list
    // the day its symbol vocabulary lands (python in T2, go in T4) — routing is derived from that row —
    // and only later the second, when its reference rules land (python in T3, go in T5). A routed language
    // with no reference rules is a legal state, and go is in it right now; the second list below is what
    // says whether a language is still there
    [Theory]
    [InlineData(TreeSitterLanguages.JavaScript)]
    [InlineData(TreeSitterLanguages.TypeScript)]
    [InlineData(TreeSitterLanguages.Tsx)]
    [InlineData(TreeSitterLanguages.Rust)]
    [InlineData(TreeSitterLanguages.Python)]
    [InlineData(TreeSitterLanguages.Go)]
    public void Routed_CarriesASymbolVocabulary(string language)
    {
        TreeSitterLanguages.Routed(language).ShouldBeTrue();
        TreeSitterLanguages.SymbolsFor(language).Kinds.Count.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData(TreeSitterLanguages.JavaScript)]
    [InlineData(TreeSitterLanguages.TypeScript)]
    [InlineData(TreeSitterLanguages.Tsx)]
    [InlineData(TreeSitterLanguages.Rust)]
    [InlineData(TreeSitterLanguages.Python)]
    public void PoolBearingLanguage_CarriesAReferencePool(string language)
    {
        TreeSitterLanguages.ReferencesFor(language).ShouldNotBeNull();
        TreeSitterLanguages.ReferencePoolFor(language).ShouldNotBeNullOrEmpty();
    }

    // go took the routing flip in T4 by landing a symbol table, and the reference rules are T5's, so
    // this is the state python passed through in T2 — the proof that the two axes really are separate
    // rows, now with a live resident instead of a historical note. It also replaces T1's
    // 'registered-but-unrouted' case, whose subject disappeared when the last placeholder row filled
    [Fact]
    public void GoSymbols_LandedAlone_RoutedWithoutReferenceRules()
    {
        TreeSitterLanguages.Routed(TreeSitterLanguages.Go).ShouldBeTrue();

        // the seven kinds the table keys on and the four that can own a row, read straight from the
        // grammar measurement — 'type_alias' is in both lists because 'type Alias = int' is not a
        // type_spec and an alias body can hold a method_elem the same way an interface does
        TreeSitterLanguages.SymbolsFor(TreeSitterLanguages.Go).Kinds.Count.ShouldBe(7);
        TreeSitterLanguages.SymbolsFor(TreeSitterLanguages.Go).ContainerTypes
            .ShouldBe(["type_spec", "type_alias", "function_declaration", "method_declaration"], ignoreOrder: true);

        // no pool yet, and — the part that matters for T5 — no borrowing one either: the phase has to
        // skip this language for reference rows rather than fall through to a neighbour's pool
        TreeSitterLanguages.ReferencesFor(TreeSitterLanguages.Go).ShouldBeNull();
        TreeSitterLanguages.ReferencePoolFor(TreeSitterLanguages.Go).ShouldBeNull();
    }

    [Fact]
    public void PythonReferenceRules_LandedInItsOwnPoolNotASharedOne()
    {
        // the axis independence this file used to pin from the other side (routed, pool-less) is gone now
        // that both rows are filled — what stays load-bearing is WHICH pool: python's edges may only ever
        // match python files, because a pool shared with javascript would let 'mul' in a .js file bind a
        // python symbol of the same name
        TreeSitterLanguages.Routed(TreeSitterLanguages.Python).ShouldBeTrue();
        TreeSitterLanguages.SymbolsFor(TreeSitterLanguages.Python).Kinds.Count.ShouldBe(3);
        var rules = TreeSitterLanguages.ReferencesFor(TreeSitterLanguages.Python);
        rules.ShouldNotBeNull();
        rules!.Pool.ShouldBe(TreeSitterLanguages.PythonPool);
        rules.CallTypes.ShouldContain("call");
        rules.TypeTargetTypes.ShouldContain("class_definition");

        var pools = new[]
        {
            TreeSitterLanguages.ReferencePoolFor(TreeSitterLanguages.JavaScript),
            TreeSitterLanguages.ReferencePoolFor(TreeSitterLanguages.Rust),
            TreeSitterLanguages.ReferencePoolFor(TreeSitterLanguages.Python),
        };
        pools.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public void UnknownLabel_IsNotRoutedAndAnswersWithTheEmptyVocabulary()
    {
        // every language the home knows is routed now, so the unrouted shape can only be asked about a
        // label that has no row at all — and 'csharp' is the one the detector really produces for the
        // Roslyn path, which is why it must keep answering empty here rather than borrowing a table
        TreeSitterLanguages.Routed("csharp").ShouldBeFalse();
        TreeSitterLanguages.Routed("cobol").ShouldBeFalse();
        TreeSitterLanguages.SymbolsFor("csharp").Kinds.ShouldBeEmpty();
        TreeSitterLanguages.SymbolsFor("cobol").ContainerTypes.ShouldBeEmpty();
        TreeSitterLanguages.ReferencesFor("csharp").ShouldBeNull();
        TreeSitterLanguages.ReferencePoolFor("cobol").ShouldBeNull();
    }

    [Fact]
    public void EveryKnownLanguageRowCarriesASymbolTable()
    {
        // a hand-listed enumeration, not a reflection over the home (the Sets dictionary is private and
        // staying that way is fine) — which is exactly why it says so: the thing it protects is a shape,
        // not a behavior. Routing is derived
        // from a non-empty table, so a row with empty Kinds would take a language's files off the
        // fallback chunker and index them to zero symbols AND zero chunks (the trap 111/T1 nearly
        // shipped). With every placeholder filled, 'Routed' could be re-derived from ContainsKey and no
        // test here would go red — which the T4 matrix records as an equivalent mutant (N10) rather than
        // a win. A new language must be added to this list, and forgetting to is a visible omission in a
        // diff, not a silent hole: the row would still route itself the moment it gets a table
        var known = new[]
        {
            TreeSitterLanguages.JavaScript,
            TreeSitterLanguages.TypeScript,
            TreeSitterLanguages.Tsx,
            TreeSitterLanguages.Rust,
            TreeSitterLanguages.Python,
            TreeSitterLanguages.Go,
        };

        known.Count(TreeSitterLanguages.Routed).ShouldBe(known.Length);
        foreach (var language in known)
        {
            TreeSitterLanguages.SymbolsFor(language).Kinds.Count.ShouldBeGreaterThan(0);
        }
    }

    [Fact]
    public void PoolNamesAreDistinctPerGrammarAndReservedForPythonAndGo()
    {
        var pools = new[]
        {
            TreeSitterLanguages.JsTsPool,
            TreeSitterLanguages.RustPool,
            TreeSitterLanguages.PythonPool,
            TreeSitterLanguages.GoPool,
        };
        pools.Distinct().Count().ShouldBe(4);
        TreeSitterLanguages.PythonPool.ShouldBe(TreeSitterLanguages.Python);
        TreeSitterLanguages.GoPool.ShouldBe(TreeSitterLanguages.Go);
    }
}
