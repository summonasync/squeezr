using Shouldly;
using Squeezr.Core.Indexing.TreeSitter;

namespace Squeezr.Tests.Indexing.TreeSitter;

// Registration-only state: python and go are known to the home before they have vocabularies, and this
// class pins exactly the line between 'known' and 'handled' that T2/T4 move languages across.
public class TreeSitterLanguagesRegistrationTests
{
    // Two axes that grow on different days, so they get two theories: a language joins this first list
    // the day its symbol vocabulary lands (python in T2, go in T4) — routing is derived from that row —
    // and only later the second, when its reference rules land (T3/T5). A routed language with no
    // reference rules is a legal state, and that is exactly what the next task will leave behind.
    [Theory]
    [InlineData(TreeSitterLanguages.JavaScript)]
    [InlineData(TreeSitterLanguages.TypeScript)]
    [InlineData(TreeSitterLanguages.Tsx)]
    [InlineData(TreeSitterLanguages.Rust)]
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
    public void PoolBearingLanguage_CarriesAReferencePool(string language)
    {
        TreeSitterLanguages.ReferencesFor(language).ShouldNotBeNull();
        TreeSitterLanguages.ReferencePoolFor(language).ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(TreeSitterLanguages.Python)]
    [InlineData(TreeSitterLanguages.Go)]
    public void RegisteredLanguage_IsNotRoutedAndCarriesNoRules(string language)
    {
        // the trap this pins: a routed file is taken off the fallback chunker, so routing a language
        // with an empty vocabulary would leave it with no symbols and no chunks at all
        TreeSitterLanguages.Routed(language).ShouldBeFalse();
        TreeSitterLanguages.SymbolsFor(language).Kinds.ShouldBeEmpty();
        TreeSitterLanguages.SymbolsFor(language).ContainerTypes.ShouldBeEmpty();
        TreeSitterLanguages.ReferencesFor(language).ShouldBeNull();
        TreeSitterLanguages.ReferencePoolFor(language).ShouldBeNull();
    }

    [Fact]
    public void UnroutedLanguageAnswersTheSameAsAnUnknownOne()
    {
        TreeSitterLanguages.SymbolsFor(TreeSitterLanguages.Python).Kinds.Count
            .ShouldBe(TreeSitterLanguages.SymbolsFor("csharp").Kinds.Count);
        TreeSitterLanguages.ReferencesFor(TreeSitterLanguages.Go)
            .ShouldBe(TreeSitterLanguages.ReferencesFor("csharp"));
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
