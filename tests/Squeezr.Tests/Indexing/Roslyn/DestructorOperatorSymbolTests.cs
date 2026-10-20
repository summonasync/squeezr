using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Roslyn;

namespace Squeezr.Tests.Indexing.Roslyn;

public class DestructorOperatorSymbolTests
{
    [Fact]
    public void Extract_Destructor_TakesTheTildeTypeName()
    {
        var symbols = Extract(
            """
            namespace Demo;

            public class Widget
            {
                ~Widget()
                {
                    Disposed = true;
                }

                public bool Disposed { get; private set; }
            }
            """);

        var dtor = symbols.Single(s => s.Name == "~Widget");
        dtor.Kind.ShouldBe(SymbolKind.Method);
        dtor.ContainerName.ShouldBe("Widget");
        dtor.Span.ShouldBe(new CodeSpan(5, 8));
    }

    [Fact]
    public void Extract_AttributedDestructor_SkipsTheDefaultModifierBranch()
    {
        var symbols = Extract(
            """
            namespace Demo;

            public class Widget
            {
                [Obsolete("use Dispose")]
                ~Widget() => Disposed = true;

                public bool Disposed { get; private set; }
            }
            """);

        symbols.ShouldContain(s => s.Name == "~Widget");
        symbols.ShouldNotContain(s => s.Name == "public" || s.Name == "extern" || s.Name.StartsWith("[Obsolete"));
    }

    [Fact]
    public void Extract_Operator_TakesTheOperatorConventionName()
    {
        var symbols = Extract(
            """
            namespace Demo;

            public struct Money
            {
                public decimal Amount { get; }

                public Money(decimal amount) => Amount = amount;

                public static Money operator +(Money left, Money right) => new(left.Amount + right.Amount);
            }
            """);

        var op = symbols.Single(s => s.Name == "operator +");
        op.Kind.ShouldBe(SymbolKind.Method);
        op.ContainerName.ShouldBe("Money");
        op.Span.StartLine.ShouldBe(9);
        symbols.ShouldNotContain(s => s.Name == "public" || s.Name == "static");
    }

    [Fact]
    public void Extract_ConversionOperators_CarryTheImplicitOrExplicitKeywordAndTargetType()
    {
        var symbols = Extract(
            """
            namespace Demo;

            public struct Money
            {
                public static implicit operator Money(decimal amount) => new(amount);

                public static explicit operator decimal(Money money) => money.Amount;
            }
            """);

        symbols.Select(s => s.Name).ShouldContain("implicit operator Money");
        symbols.Select(s => s.Name).ShouldContain("explicit operator decimal");
        symbols.ShouldNotContain(s => s.Name == "public" || s.Name == "static");
    }

    private static IReadOnlyList<Symbol> Extract(string source) =>
        SymbolExtractor.Extract(CSharpSyntaxTree.ParseText(source).GetRoot(), model: null, source)
            .Select(s => s.Symbol)
            .ToList();
}
