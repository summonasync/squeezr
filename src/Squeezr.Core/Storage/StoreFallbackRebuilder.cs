using LiteDB;

namespace Squeezr.Core.Storage;

// LiteDB 5.0.21 cannot rebuild a store whose collections outgrow the barely-born temp file its own loop
// guard is calibrated against, so the engine's Rebuild() refuses and the freed pages keep their bytes.
// This pass rebuilds without the engine: live rows stream out of the refusing store into a fresh database
// (indexes created before the rows, so no mass index walk ever runs), every collection's count is checked
// before and after the swap, and the original rides aside as a .db backup until the swapped-in file has
// answered for itself. A failure at any point rolls the original back — a refused rebuild must never cost
// the store.
internal sealed class StoreFallbackRebuilder(string dbPath)
{
    internal const string TempSuffix = ".rebuild-tmp";

    public StoreFallbackRebuildResult Rebuild()
    {
        var tempPath = dbPath + TempSuffix;
        var backupPath = BackupPath();
        DeleteLeftovers(tempPath);
        DeleteStaleBackup(backupPath);
        var counts = new Dictionary<string, int>();
        string? failedCollection = null;
        var swapped = false;

        try
        {
            using (var source = new LiteDatabase(new ConnectionString
            {
                Filename = dbPath,
                Connection = ConnectionType.Direct
            }))
            using (var target = new LiteDatabase(new ConnectionString
            {
                Filename = tempPath,
                Connection = ConnectionType.Direct
            }))
            {
                foreach (var name in source.GetCollectionNames().Where(n => !n.StartsWith('$')))
                {
                    failedCollection = name;
                    CopyCollection(source, target, name, counts);
                }

                failedCollection = null;
                target.Checkpoint();
            }

            File.Move(dbPath, backupPath);
            File.Move(tempPath, dbPath);
            swapped = true;
            VerifySwapped(counts);
            return new StoreFallbackRebuildResult(true, null, null);
        }
        catch (Exception ex)
        {
            var rollbackNote = swapped ? RollBack(backupPath) : null;
            DeleteLeftovers(tempPath);
            var reason = rollbackNote is null ? ex.Message : $"{ex.Message}; {rollbackNote}";
            return new StoreFallbackRebuildResult(false, failedCollection, reason);
        }
    }

    private static void CopyCollection(
        LiteDatabase source,
        LiteDatabase target,
        string name,
        Dictionary<string, int> counts)
    {
        var targetCollection = target.GetCollection(name);
        foreach (var index in source.GetCollection("$indexes").FindAll()
                     .Where(i => i["collection"] == name && i["name"] != "_id"))
        {
            targetCollection.EnsureIndex(
                index["name"].AsString,
                index["expression"].AsString,
                index["unique"].AsBoolean);
        }

        var sourceCollection = source.GetCollection(name);
        var expected = sourceCollection.Count();
        long copied = 0;
        foreach (var batch in sourceCollection.FindAll().Chunk(1_000))
        {
            copied += targetCollection.Insert(batch);
        }

        if (copied != expected)
        {
            throw new InvalidDataException(
                $"collection '{name}': source holds {expected} row(s), the copy took {copied}");
        }

        counts[name] = expected;
    }

    private void VerifySwapped(Dictionary<string, int> counts)
    {
        using var verify = new LiteDatabase(new ConnectionString
        {
            Filename = dbPath,
            Connection = ConnectionType.Direct
        });
        foreach (var (name, expected) in counts)
        {
            var actual = verify.GetCollection(name).Count();
            if (actual != expected)
            {
                throw new InvalidDataException(
                    $"collection '{name}' holds {actual} row(s) after the swap, expected {expected}");
            }
        }
    }

    private string? RollBack(string backupPath)
    {
        if (!File.Exists(backupPath))
        {
            return null;
        }

        try
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }

            File.Move(backupPath, dbPath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"rollback failed: the pre-rebuild copy stays at '{Path.GetFileName(backupPath)}' ({ex.Message})";
        }
    }

    private string BackupPath() =>
        Path.Combine(
            Path.GetDirectoryName(dbPath)!,
            Path.GetFileNameWithoutExtension(dbPath) + "-reclaim-backup.db");

    private static void DeleteLeftovers(string tempPath)
    {
        foreach (var stale in new[] { tempPath, tempPath + "-log" })
        {
            try
            {
                if (File.Exists(stale))
                {
                    File.Delete(stale);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void DeleteStaleBackup(string backupPath)
    {
        if (!File.Exists(dbPath) || !File.Exists(backupPath))
        {
            return;
        }

        try
        {
            File.Delete(backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
