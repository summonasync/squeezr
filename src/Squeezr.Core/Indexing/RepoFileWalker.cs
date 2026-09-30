using Squeezr.Core.Indexing.Ignore;

namespace Squeezr.Core.Indexing;

public sealed record SourceFile(string RelativePath, string FullPath, string Language, long SizeBytes, string Sha256);

public sealed record FileWalkResult(
    IReadOnlyList<SourceFile> Files,
    int Skipped,
    int Ignored,
    IReadOnlyDictionary<IgnoreSource, int> IgnoredBySource);

public static class RepoFileWalker
{
    private static readonly string[] AlwaysSkippedDirs = [".git", ".squeezr"];

    public static FileWalkResult Walk(string repoRoot, IgnoreRuleset rules, int maxFileSizeKb, Action<string>? warn = null)
    {
        var files = new List<SourceFile>();
        var skipped = 0;
        var ignored = 0;
        var ignoredBySource = new Dictionary<IgnoreSource, int>();
        var maxBytes = (long)maxFileSizeKb * 1024;
        var pending = new Queue<string>();
        pending.Enqueue(repoRoot);

        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
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
                var relativePath = Path.GetRelativePath(repoRoot, entry).Replace('\\', '/');

                if (Directory.Exists(entry))
                {
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(entry);
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
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
                    warn?.Invoke($"skipped '{relativePath}': {ex.Message}");
                    isBinary = null;
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
                    warn?.Invoke($"skipped '{relativePath}': {ex.Message}");
                    skipped++;
                    continue;
                }

                files.Add(new SourceFile(relativePath, entry, LanguageDetector.Detect(entry), info.Length, hash));
            }
        }

        files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return new FileWalkResult(files, skipped, ignored, ignoredBySource);
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
