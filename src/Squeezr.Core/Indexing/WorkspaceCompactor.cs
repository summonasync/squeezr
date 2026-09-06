using Squeezr.Core.Indexing.Trigram;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing;

public sealed record CompactResult(
    long SizeBeforeBytes,
    long SizeAfterBytes,
    int SegmentsDropped,
    bool RebuiltFromSources);

public static class WorkspaceCompactor
{
    public static CompactResult Compact(string workspaceRoot)
    {
        if (!WorkspaceManifestFile.Exists(workspaceRoot))
        {
            throw new WorkspaceException("Workspace is not initialized. Run 'squeezr init' first.");
        }

        var trigramPath = SqueezrPaths.TrigramIndexPath(workspaceRoot);
        if (!File.Exists(trigramPath))
        {
            throw new WorkspaceException("Index is not built. Run 'squeezr index' first.");
        }

        using (IndexLock.Acquire(SqueezrPaths.StoreDir(workspaceRoot)))
        {
            var sizeBefore = new FileInfo(trigramPath).Length;
            var tempPath = trigramPath + ".compact.tmp";
            try
            {
                int segmentsDropped;
                bool rebuiltFromSources;
                var reader = new TrigramIndexReader(trigramPath);
                if (reader.Version == 1)
                {
                    RebuildFromSources(workspaceRoot, tempPath);
                    segmentsDropped = 0;
                    rebuiltFromSources = true;
                }
                else
                {
                    var writer = new TrigramIndexWriter();
                    for (uint docId = 0; docId < reader.DocCount; docId++)
                    {
                        var (repoId, fileId) = reader.GetDoc(docId);
                        writer.AddDocument(new TrigramDocument(repoId, fileId, reader.GetDocumentPostings(docId)));
                    }

                    writer.Write(tempPath);
                    segmentsDropped = reader.SegmentStats!.TombstonedSegments;
                    rebuiltFromSources = false;
                }

                File.Move(tempPath, trigramPath, overwrite: true);
                var sizeAfter = new FileInfo(trigramPath).Length;
                return new CompactResult(sizeBefore, sizeAfter, segmentsDropped, rebuiltFromSources);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }
    }

    private static void RebuildFromSources(string workspaceRoot, string targetPath)
    {
        var manifest = WorkspaceManifestFile.Load(workspaceRoot);
        var warnings = new List<string>();
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspaceRoot));
        WorkspaceIndexer.RebuildTrigramIndex(
            workspaceRoot,
            store,
            manifest,
            warnings,
            new Dictionary<uint, string>(),
            targetPath);
    }
}
