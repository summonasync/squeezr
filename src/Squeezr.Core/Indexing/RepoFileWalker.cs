using Squeezr.Core.Indexing.Ignore;

namespace Squeezr.Core.Indexing;

public sealed record SourceFile(string RelativePath, string FullPath, string Language, long SizeBytes, string Sha256);

public sealed record FileWalkResult(
    IReadOnlyList<SourceFile> Files,
    int Skipped,
    int Ignored,
    IReadOnlyDictionary<IgnoreSource, int> IgnoredBySource,
    IReadOnlyList<string> UnreadableFiles,
    IReadOnlyList<string> UnreadableDirs)
{
    // An unreadable path is not a deleted one: the deleted-file reconciliation checks this before
    // reaping, so a chmod or a failed read keeps its rows until the path reads again or really disappears.
    public bool Keeps(string relativePath)
    {
        if (UnreadableFiles.Contains(relativePath))
        {
            return true;
        }

        foreach (var dir in UnreadableDirs)
        {
            if (dir == "." || relativePath.StartsWith(dir + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

public static class RepoFileWalker
{
    private static readonly string[] AlwaysSkippedDirs = [".git", ".squeezr"];

    public static FileWalkResult Walk(
        string repoRoot,
        IgnoreRuleset rules,
        SecretGuardIngestion guard,
        int maxFileSizeKb,
        Action<string>? warn = null,
        bool countIgnoredSubtrees = true,
        CancellationToken cancellationToken = default)
    {
        var files = new List<SourceFile>();
        var skipped = 0;
        var ignored = 0;
        var ignoredBySource = new Dictionary<IgnoreSource, int>();
        var unreadableFiles = new List<string>();
        var unreadableDirs = new List<string>();
        var maxBytes = (long)maxFileSizeKb * 1024;
        var pending = new Queue<string>();
        pending.Enqueue(repoRoot);

        void LedgerReadFailure(string relativePath, Exception ex)
        {
            skipped++;
            if (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                warn?.Invoke($"skipped '{relativePath}': {ex.Message}");
                return;
            }

            unreadableFiles.Add(relativePath);
            warn?.Invoke($"unreadable '{relativePath}': {ex.Message}; index rows kept");
        }

        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LedgerUnreadableDirectory(repoRoot, directory, unreadableDirs, warn, ex);
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(repoRoot, entry).Replace('\\', '/');

                if (Directory.Exists(entry))
                {
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(entry);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        LedgerUnreadableDirectory(repoRoot, entry, unreadableDirs, warn, ex);
                        continue;
                    }

                    var name = Path.GetFileName(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0
                        || AlwaysSkippedDirs.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var dirDecision = rules.Decision(relativePath, isDirectory: true);
                    if (dirDecision.Excluded)
                    {
                        // the walk stops at the directory, so the tree below it has to be counted by name
                        if (countIgnoredSubtrees)
                            CountIgnored(dirDecision.Source, ignoredBySource, ref ignored, CountDroppedFiles(entry));
                        continue;
                    }

                    pending.Enqueue(entry);
                    continue;
                }

                var fileDecision = rules.Decision(relativePath, isDirectory: false);
                if (fileDecision.Excluded)
                {
                    CountIgnored(fileDecision.Source, ignoredBySource, ref ignored, 1);
                    continue;
                }

                // the guard decides before anything touches the file: an unreadable secret would otherwise
                // take the reconciliation branches below and name itself in a warning
                if (guard.Deny(relativePath))
                {
                    skipped++;
                    continue;
                }

                // FileAttributes reports Normal for a FIFO and File.Exists answers true for any
                // non-directory, so neither can see special files; stat can. Opening a writerless FIFO
                // blocks open() forever, and the 2026-10-03 wedge was exactly that: a pass blocked here
                // held the store lock for 95 minutes.
                if (!UnixFileType.IsRegularFile(entry))
                {
                    skipped++;
                    warn?.Invoke($"skipped '{relativePath}': not a regular file (fifo, socket, device or dangling link)");
                    continue;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(entry);
                }
                catch (IOException)
                {
                    skipped++;
                    continue;
                }

                bool? isBinary;
                try
                {
                    isBinary = BinaryDetector.IsBinary(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LedgerReadFailure(relativePath, ex);
                    continue;
                }

                if (info.Length > maxBytes || isBinary != false)
                {
                    skipped++;
                    continue;
                }

                string hash;
                try
                {
                    hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(entry))).ToLowerInvariant();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LedgerReadFailure(relativePath, ex);
                    continue;
                }

                files.Add(new SourceFile(relativePath, entry, LanguageDetector.Detect(entry), info.Length, hash));
            }
        }

        files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return new FileWalkResult(files, skipped, ignored, ignoredBySource, unreadableFiles, unreadableDirs);
    }

    private static void LedgerUnreadableDirectory(
        string repoRoot, string directory, List<string> unreadableDirs, Action<string>? warn, Exception ex)
    {
        var relativePath = Path.GetRelativePath(repoRoot, directory).Replace('\\', '/');
        unreadableDirs.Add(relativePath);
        warn?.Invoke($"unreadable directory '{relativePath}': {ex.Message}; index rows under it kept");
    }

    private static void CountIgnored(
        IgnoreSource? source, Dictionary<IgnoreSource, int> bySource, ref int total, int count)
    {
        if (count == 0)
            return;

        total += count;
        if (source is not null)
        {
            bySource.TryGetValue(source.Value, out var existing);
            bySource[source.Value] = existing + count;
        }
    }

    private static int CountDroppedFiles(string directory)
    {
        var count = 0;
        var pending = new Queue<string>();
        pending.Enqueue(directory);

        while (pending.Count > 0)
        {
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(pending.Dequeue());
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (!Directory.Exists(entry))
                {
                    count++;
                    continue;
                }

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var name = Path.GetFileName(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0
                    || AlwaysSkippedDirs.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                pending.Enqueue(entry);
            }
        }

        return count;
    }
}
