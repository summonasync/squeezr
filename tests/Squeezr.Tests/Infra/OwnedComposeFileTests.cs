using System.Text.Json;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Infra;
using Squeezr.Core.Serialization;
using Squeezr.Semantic.Configuration;
using Squeezr.Semantic.Infra;

namespace Squeezr.Tests.Infra;

public class OwnedComposeFileTests
{
    [Fact]
    public void Generation_IsQdrantOnly_WithOwnedPortsVolumeAndRestart()
    {
        var yaml = OwnedComposeFile.Generate();

        yaml.ShouldContain("qdrant/qdrant:latest");
        yaml.ShouldContain("\"6335:6333\"");
        yaml.ShouldContain("\"6336:6334\"");
        yaml.ShouldContain("squeezr-owned-qdrant:/qdrant/storage");
        yaml.ShouldContain("restart: unless-stopped");
        yaml.ShouldContain("squeezr-owned-qdrant:");
        yaml.ShouldNotContain("ollama");
    }

    [Fact]
    public void Generation_HonorsCustomPorts()
    {
        var yaml = OwnedComposeFile.Generate(7335, 7336);

        yaml.ShouldContain("\"7335:6333\"");
        yaml.ShouldContain("\"7336:6334\"");
        yaml.ShouldNotContain("6335");
        yaml.ShouldNotContain("6336");
    }

    [Fact]
    public void ResolvedEndpoint_OwnedWithoutEndpoint_DerivesFromRestPort()
    {
        new QdrantConfig { Owned = true }.ResolvedEndpoint.ShouldBe("http://localhost:6335");
        new QdrantConfig { Owned = true, RestPort = 7335 }.ResolvedEndpoint.ShouldBe("http://localhost:7335");
    }

    [Fact]
    public void ResolvedEndpoint_ExplicitEndpointAlwaysWins()
    {
        new QdrantConfig { Owned = true, Endpoint = "http://q.internal:6333" }
            .ResolvedEndpoint.ShouldBe("http://q.internal:6333");
        new QdrantConfig { Endpoint = "http://localhost:6334" }
            .ResolvedEndpoint.ShouldBe("http://localhost:6334");
    }

    [Fact]
    public void ConfigRoundTrip_PreservesOwnedAndPorts()
    {
        var config = SemanticConfig.Bind(SqueezrConfig.CreateDefault()) with
        {
            Qdrant = new QdrantConfig { Owned = true, RestPort = 7335, GrpcPort = 7336 },
        };

        var json = JsonSerializer.Serialize(SemanticConfigFile.Attach(config.Core, config), SqueezrJson.FileOptions);
        var parsed = SemanticConfig.Bind(JsonSerializer.Deserialize<SqueezrConfig>(json, SqueezrJson.FileOptions)!);

        parsed.Qdrant.Owned.ShouldBeTrue();
        parsed.Qdrant.RestPort.ShouldBe(7335);
        parsed.Qdrant.GrpcPort.ShouldBe(7336);
    }

    [Fact]
    public void ConfigWithoutOwnedField_BehavesAsLegacyExternal()
    {
        const string legacy = """
            {
              "chunking": { "maxTokensPerChunk": 512, "fallbackWindowLines": 60, "fallbackOverlapLines": 15 },
              "indexing": { "ignore": [], "maxFileSizeKb": 512 },
              "qdrant": { "endpoint": "http://localhost:6334" }
            }
            """;

        var parsed = SemanticConfig.Bind(JsonSerializer.Deserialize<SqueezrConfig>(legacy, SqueezrJson.FileOptions)!);

        parsed.Qdrant.Owned.ShouldBeFalse();
        parsed.Qdrant.Endpoint.ShouldBe("http://localhost:6334");
        parsed.Qdrant.ResolvedEndpoint.ShouldBe("http://localhost:6334");
    }
}
