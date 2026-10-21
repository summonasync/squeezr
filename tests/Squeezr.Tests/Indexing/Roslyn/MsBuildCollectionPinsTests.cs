using System.Reflection;
using Shouldly;

namespace Squeezr.Tests.Indexing.Roslyn;

public class MsBuildCollectionPinsTests
{
    [Theory]
    [InlineData(typeof(PacedMsBuildSessionTests))]
    [InlineData(typeof(SemanticSessionPoolTests))]
    public void TestClass_CreatingSessionsThroughMsBuildLocator_SitsInTheSerializedCollection(Type testClass)
    {
        CollectionNameOf(testClass).ShouldBe("ProcessGlobals");
    }

    [Fact]
    public void ProcessGlobalsCollection_DisablesParallelization()
    {
        var definition = typeof(ProcessGlobalsCollection).Assembly
            .GetType("Squeezr.Tests.ProcessGlobalsCollection", throwOnError: false);
        definition.ShouldNotBeNull();
        CollectionNameOf(definition!).ShouldBe("ProcessGlobals");
        definition!.GetCustomAttribute<CollectionDefinitionAttribute>()
            .ShouldNotBeNull().DisableParallelization.ShouldBeTrue();
    }

    private static string CollectionNameOf(Type type)
    {
        var data = type.GetCustomAttributesData().SingleOrDefault(a =>
            a.AttributeType == typeof(CollectionAttribute) ||
            a.AttributeType == typeof(CollectionDefinitionAttribute));
        data.ShouldNotBeNull();
        return (string)data!.ConstructorArguments[0].Value;
    }
}
