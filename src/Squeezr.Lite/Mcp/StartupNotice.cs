using Squeezr.Core.Indexing.Watch;
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

        var health = WatchStoreHealth.Probe(SqueezrPaths.StoreDir(workspaceRoot), DateTime.UtcNow);
        stderr.WriteLine(health.IndexerRunning
            ? $"sqzrlite serve: a watcher pass keeps this index fresh (pid {health.OwnerPid}); queries answer " +
              "with a content verdict and heal within the refresh budget"
            : "sqzrlite serve: no background indexer runs against this store — queries answer with a content " +
              $"verdict that heals within the refresh budget; '{LiteRemedy.IndexCommand}' reconciles everything " +
              "now, or start with --watch to keep it fresh");
    }
}
