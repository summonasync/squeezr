using Squeezr.Core.Infra;
using Squeezr.Semantic.Infra;

namespace Squeezr.Semantic.Embeddings;

public static class QdrantEndpointHint
{
    private static readonly int RestPort = new Uri(ServicesComposeFile.QdrantRestEndpoint).Port;

    public static string? RestPortMisconfiguration(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Port != RestPort)
        {
            return null;
        }

        return
            $"the configured qdrant endpoint '{endpoint}' points at the REST port — squeezr speaks gRPC; " +
            $"set qdrant.endpoint to {ServicesComposeFile.QdrantGrpcEndpoint} in the workspace config";
    }
}
