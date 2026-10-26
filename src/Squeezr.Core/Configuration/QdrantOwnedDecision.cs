using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Configuration;

// services up is machine-level while the owned flag lives in per-workspace configs —
// the decision is therefore "any registered workspace wants it"; unreadable workspaces
// cannot vote but must not break the raise
public static class QdrantOwnedDecision
{
    public static bool AnyWorkspaceWantsOwned()
    {
        foreach (var entry in WorkspaceRegistry.Load())
        {
            if (TryRead(entry.RootPath, out var qdrant) && qdrant.Owned)
            {
                return true;
            }
        }

        return false;
    }

    public static (int RestPort, int GrpcPort) OwnedPorts()
    {
        foreach (var entry in WorkspaceRegistry.Load())
        {
            if (TryRead(entry.RootPath, out var qdrant) && qdrant.Owned)
            {
                return (qdrant.RestPort, qdrant.GrpcPort);
            }
        }

        return (QdrantConfig.DefaultOwnedRestPort, QdrantConfig.DefaultOwnedGrpcPort);
    }

    private static bool TryRead(string root, out QdrantConfig qdrant)
    {
        qdrant = new QdrantConfig();
        try
        {
            qdrant = ConfigFile.Load(root).Qdrant;
            return true;
        }
        catch (Exception ex) when (ex is WorkspaceException or InvalidDataException or IOException)
        {
            return false;
        }
    }
}
