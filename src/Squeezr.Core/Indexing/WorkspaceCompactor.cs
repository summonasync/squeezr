using Squeezr.Core.Indexing.Trigram;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing;

public sealed record CompactResult(
    long SizeBeforeBytes,
    long SizeAfterBytes,
    int SegmentsDropped,
    bool RebuiltFromSources,
    int PathsPurged = 0,
    int RowsPurged = 0,
    int ReapJournalRedacted = 0,
    int PointsQueued = 0,
    int TelemetryValuesRedacted = 0,
    long StoreSizeBeforeBytes = 0,
    long StoreSizeAfterBytes = 0,
    IReadOnlyList<string>? Warnings = null);

// The operator's reclaim command: it rewrites the trigram index without its tombstones, retires the stored
// paths the secret guard refuses, redacts the reap journal and the recorded query parameters those paths left
// behind, and rebuilds meta.db so freed pages stop counting against the store. Every number in the report is
// a count — a purge that names what it removed would tell more than the index it just cleaned.
public static class WorkspaceCompactor
{
    public static CompactResult Compact(string workspaceRoot, Action<string>? onStaleTakeover = null)
    {
        if (!WorkspaceManifestFile.Exists(workspaceRoot))
        {
            throw new WorkspaceException(
                RemedyText.WorkspaceNotInitialized,
                WorkspaceException.NotInitializedCode);
        }

        var trigramPath = SqueezrPaths.TrigramIndexPath(workspaceRoot);
        if (!File.Exists(trigramPath))
        {
            throw new WorkspaceException(
                RemedyText.IndexNotBuilt,
                WorkspaceException.IndexNotBuiltCode);
        }

        using (IndexLock.Acquire(SqueezrPaths.StoreDir(workspaceRoot), onStaleTakeover))
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspaceRoot)))
        {
            var warnings = new List<string>();
            var sizeBefore = new FileInfo(trigramPath).Length;
            var tempPath = trigramPath + ".compact.tmp";
            StorePurgeResult purge;
            int telemetryRedacted;
            int segmentsDropped;
            bool rebuiltFromSources;
            StoreReclaimResult reclaim;

            // The guard decides before anything is rewritten: rows leave the store, the postings that named
            // them get tombstoned, and the rewrite below is what takes their bytes back. One classifier for
            // both halves — the journal, the rows and the recorded parameters all answer to the same answer,
            // and the recorded ones get folded back to the stored shape first because a parameter carries
            // 'demo/infra/VaultKeys.cs' or '/work/repo/infra/VaultKeys.cs' for the same file the store knows
            // as 'infra/VaultKeys.cs', which is the shape an anchored mask actually matches.
            var guard = SecretGuardIngestion.Load(workspaceRoot);
            purge = WorkspaceIndexer.PurgeProtectedPaths(workspaceRoot, store, guard, warnings);
            telemetryRedacted = QueryTelemetry.RedactProtectedParams(
                workspaceRoot,
                GuardedPathForms.Load(workspaceRoot, guard).NamesAProtectedPath,
                out var telemetryScrubBusy);
            if (telemetryScrubBusy)
            {
                warnings.Add(
                    "queries.jsonl scrub skipped: the telemetry write lock stayed busy past the wait budget — " +
                    "protected names can survive in recorded parameters until the next compact");
            }
            else if (telemetryRedacted > 0)
            {
                warnings.Add(
                    $"secret guard: {telemetryRedacted} recorded parameter value(s) redacted in queries.jsonl " +
                    "(paths withheld by the secret guard)");
            }

            try
            {
                var reader = new TrigramIndexReader(trigramPath);
                if (reader.Version == 1)
                {
                    RebuildFromSources(workspaceRoot, store, tempPath, warnings);
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

            reclaim = store.Reclaim();
            if (!reclaim.Reclaimed)
            {
                warnings.Add($"index store rebuild skipped: {reclaim.SkippedReason}");
            }

            if (reclaim.SnapshotCopiesLeft > 0)
            {
                warnings.Add(
                    $"{reclaim.SnapshotCopiesLeft} index store snapshot copy(ies) left next to the store — " +
                    "each one holds what the store held before the rebuild");
            }

            var sizeAfter = new FileInfo(trigramPath).Length;
            return new CompactResult(
                sizeBefore,
                sizeAfter,
                segmentsDropped,
                rebuiltFromSources,
                purge.PathsPurged,
                purge.RowsPurged,
                purge.ReapJournalRedacted,
                purge.ReapsQueued,
                telemetryRedacted,
                reclaim.SizeBeforeBytes,
                reclaim.SizeAfterBytes,
                warnings.Count > 0 ? warnings : null);
        }
    }

    private static void RebuildFromSources(string workspaceRoot, MetaStore store, string targetPath, List<string> warnings)
    {
        var manifest = WorkspaceManifestFile.Load(workspaceRoot);
        WorkspaceIndexer.RebuildTrigramIndex(
            workspaceRoot,
            store,
            manifest,
            warnings,
            new Dictionary<uint, string>(),
            targetPath);
    }
}
