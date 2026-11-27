using Squeezr.Core.Storage;

namespace Squeezr.Lite.Mcp;

internal static class StartupNotice
{
    internal static void WriteNoIndexerLine(string workspaceRoot, TextWriter stderr)
    {
        if (!File.Exists(SqueezrPaths.MetaDbPath(workspaceRoot)))
        {
            return;
        }

        stderr.WriteLine(
            "sqzrlite serve: no background indexer runs in lite — tools answer from the index as of the " +
            $"last 'sqzrlite index' pass; re-run '{LiteRemedy.IndexCommand}' after editing files");
    }
}
