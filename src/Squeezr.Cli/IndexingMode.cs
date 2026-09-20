using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli;

public static class IndexingMode
{
    public static bool EffectiveNoSemantic(string workspaceRoot, bool noSemanticFlag = false)
    {
        if (noSemanticFlag)
        {
            return true;
        }

        if (!File.Exists(SqueezrPaths.ConfigPath(workspaceRoot)))
        {
            return false;
        }

        try
        {
            return ConfigFile.Load(workspaceRoot).Indexing.NoSemantic;
        }
        catch (WorkspaceException)
        {
            return false;
        }
    }
}
