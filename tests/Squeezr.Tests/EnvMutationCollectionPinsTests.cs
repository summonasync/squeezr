using System.Reflection;
using Shouldly;

namespace Squeezr.Tests;

public class EnvMutationCollectionPinsTests
{
    [Theory]
    [InlineData(typeof(Tui.TuiIndexRunnerTests))]
    [InlineData(typeof(Tui.TuiSearchFlowTests))]
    [InlineData(typeof(Mcp.McpToolsTests))]
    [InlineData(typeof(Embeddings.OpenAiCompatibleEmbeddingProviderTests))]
    public void TestClass_MutatingProcessEnvironment_SitsInTheSerializedCollection(Type testClass)
    {
        CollectionNameOf(testClass).ShouldBe("ProcessGlobals");
    }

    private static string CollectionNameOf(Type type)
    {
        var data = type.GetCustomAttributesData().SingleOrDefault(a =>
            a.AttributeType == typeof(CollectionAttribute));
        data.ShouldNotBeNull();
        return (string)data!.ConstructorArguments[0].Value;
    }
}
