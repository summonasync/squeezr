using Shouldly;
using Squeezr.Cli.Embeddings;

namespace Squeezr.Tests.Embeddings;

public class QdrantEndpointHintTests
{
    [Fact]
    public void RestPort_LocalhostEndpoint_ReturnsGrpcHint()
    {
        var hint = QdrantEndpointHint.RestPortMisconfiguration("http://localhost:6333");

        hint.ShouldNotBeNull();
        hint.ShouldContain("gRPC");
        hint.ShouldContain("http://localhost:6333");
        hint.ShouldContain("http://localhost:6334");
    }

    [Fact]
    public void RestPort_CustomHost_ReturnsHint()
    {
        QdrantEndpointHint.RestPortMisconfiguration("http://qdrant.internal:6333").ShouldNotBeNull();
    }

    [Fact]
    public void GrpcPort_ReturnsNull()
    {
        QdrantEndpointHint.RestPortMisconfiguration("http://localhost:6334").ShouldBeNull();
    }

    [Fact]
    public void OtherPort_ReturnsNull()
    {
        QdrantEndpointHint.RestPortMisconfiguration("http://localhost:9999").ShouldBeNull();
    }

    [Fact]
    public void NonUri_ReturnsNull()
    {
        QdrantEndpointHint.RestPortMisconfiguration("not-a-uri").ShouldBeNull();
    }
}
