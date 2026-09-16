using Squeezr.Core.Configuration;

namespace Squeezr.Tests;

public static class TestWorkspaceConfig
{
    public const string DeadQdrantEndpoint = "http://localhost:1";

    public static void PointQdrantAtDeadEndpoint(string workspaceRoot)
    {
        var config = ConfigFile.Load(workspaceRoot);
        ConfigFile.Save(workspaceRoot, config with
        {
            Qdrant = config.Qdrant with { Endpoint = DeadQdrantEndpoint }
        });
    }
}
