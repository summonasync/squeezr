using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing;

public sealed class WorkspaceIndexerSpecialFileTests
{
    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task IndexFile_PathSubstitutedWithAWriterlessFifo_IsNeverOpened()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"oldneedle\"; }");
        new WorkspaceIndexer().Index(workspace);

        var fifo = Path.Combine(workspace, "repo", "a.cs");
        File.Delete(fifo);
        Assert.Equal(0, mkfifo(fifo, 0b100_100_100));

        // the probe is asserted first so a regressed stat probe fails fast instead of hanging here
        UnixFileType.IsRegularFile(fifo).ShouldBeFalse();

        // and the call is bounded: if the indexer's own guard ever disappears, open() on the writerless
        // channel never returns, so the regression has to surface as a failed assert, not a wedged suite
        var call = Task.Run(() => new WorkspaceIndexer().IndexFile(workspace, "demo", "a.cs"));
        (await Task.WhenAny(call, Task.Delay(WedgeBudget))).ShouldBe(call, $"IndexFile blocked on {fifo}");
        (await call).ShouldBeNull();

        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            var repoId = store.FindRepo("demo")!.Id;
            store.ListFiles(repoId).ShouldBeEmpty("a channel carries no indexable content for the row");
        }

        using (IndexLock.Acquire(SqueezrPaths.StoreDir(workspace)))
        {
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task RebuildTrigram_RowWhosePathTurnedIntoAFifo_KeepsTheRowAndWarns()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"trigramneedle\"; }");
        new WorkspaceIndexer().Index(workspace);

        var fifo = Path.Combine(workspace, "repo", "a.cs");
        File.Delete(fifo);
        Assert.Equal(0, mkfifo(fifo, 0b100_100_100));
        UnixFileType.IsRegularFile(fifo).ShouldBeFalse();

        // rows reach this loop from the store, never from the walk, so the walker's gate does not cover it
        var warnings = new List<string>();
        var rebuild = Task.Run(() =>
        {
            using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace));
            return WorkspaceIndexer.RebuildTrigramIndex(
                workspace,
                store,
                WorkspaceManifestFile.Load(workspace),
                warnings,
                new Dictionary<uint, string>(),
                SqueezrPaths.TrigramIndexPath(workspace));
        });
        (await Task.WhenAny(rebuild, Task.Delay(WedgeBudget))).ShouldBe(rebuild, $"the rebuild blocked on {fifo}");

        warnings.ShouldContain(w =>
            w.Contains("keeping 'a.cs'", StringComparison.Ordinal)
            && w.Contains("not a regular file", StringComparison.Ordinal)
            && w.Contains("rows kept", StringComparison.Ordinal));

        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspace)))
        {
            var repoId = store.FindRepo("demo")!.Id;
            store.ListFiles(repoId).ShouldHaveSingleItem().Path.ShouldBe("a.cs");
        }

        using (IndexLock.Acquire(SqueezrPaths.StoreDir(workspace)))
        {
        }
    }

    private static readonly TimeSpan WedgeBudget = TimeSpan.FromSeconds(30);

    [SupportedOSPlatform("linux")]
    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string pathname, uint mode);

    private static string NewWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        manager.AddRepo("repo", "demo");
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
