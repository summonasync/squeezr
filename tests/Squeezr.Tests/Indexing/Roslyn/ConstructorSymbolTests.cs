using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Roslyn;

namespace Squeezr.Tests.Indexing.Roslyn;

public class ConstructorSymbolTests
{
    [Fact]
    public void Extract_DocumentedCtor_TakesTheDeclaringTypeNameInsteadOfTheModifier()
    {
        var symbols = Extract(
            """
            namespace Demo;

            public class Widget
            {
                /// <summary>Builds a widget of the given size.</summary>
                public Widget(int size)
                {
                    Size = size;
                }

                public int Size { get; }
            }
            """);

        var ctor = symbols.Single(s => s.Kind == SymbolKind.Constructor);
        ctor.Name.ShouldBe("Widget");
        ctor.ContainerName.ShouldBe("Widget");
        ctor.Span.StartLine.ShouldBe(6);
        ctor.DocComment.ShouldNotBeNull().ShouldContain("Builds a widget of the given size.");
        symbols.ShouldNotContain(s => s.Name == "public");
        symbols.Single(s => s.Name == "Size").Kind.ShouldBe(SymbolKind.Property);
    }

    [Fact]
    public void Extract_GenericClassCtor_KeepsTheBareTypeName()
    {
        var symbols = Extract(
            """
            namespace Demo;

            public class Box<T>
            {
                public Box(T value)
                {
                    Value = value;
                }

                public T Value { get; }
            }
            """);

        symbols.Single(s => s.Kind == SymbolKind.Class).Name.ShouldBe("Box");
        var ctor = symbols.Single(s => s.Kind == SymbolKind.Constructor);
        ctor.Name.ShouldBe("Box");
        ctor.Span.ShouldBe(new CodeSpan(5, 8));
    }

    [Fact]
    public void Extract_CtorOverloads_ShareTheTypeNameOnTheirOwnLines()
    {
        var symbols = Extract(
            """
            namespace Demo;

            public class Widget
            {
                public Widget()
                {
                }

                public Widget(int size)
                {
                }
            }
            """);

        var ctors = symbols.Where(s => s.Kind == SymbolKind.Constructor).ToList();
        ctors.Select(c => c.Name).ShouldBe(["Widget", "Widget"]);
        ctors.Select(c => c.Span.StartLine).ShouldBe([5, 9]);
    }

    [Fact]
    public void Extract_SingleLineTypeWithCtor_PutsBothDeclarationsOnTheSameNameAndLine()
    {
        var symbols = Extract("namespace Demo;\n\npublic class Tiny { public Tiny() { } }\n");

        symbols.Count.ShouldBe(2);
        symbols.Select(s => s.Name).ShouldBe(["Tiny", "Tiny"]);
        symbols.Select(s => s.Kind).ShouldBe([SymbolKind.Class, SymbolKind.Constructor]);
        symbols.Select(s => s.Span.StartLine).ShouldBe([3, 3]);
    }

    private static IReadOnlyList<Symbol> Extract(string source) =>
        SymbolExtractor.Extract(CSharpSyntaxTree.ParseText(source).GetRoot(), model: null, source)
            .Select(s => s.Symbol)
            .ToList();
}
