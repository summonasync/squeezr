namespace Squeezr.Core.Indexing;

public sealed record SourceFile(string RelativePath, string FullPath, string Language, long SizeBytes, string Sha256);

public sealed record FileWalkResult(IReadOnlyList<SourceFile> Files, int Skipped);

public static class RepoFileWalker
{
    private static readonly string[] AlwaysSkippedDirs = [".git", ".squeezr"];

    public static FileWalkResult Walk(string repoRoot, IReadOnlyList<string> ignoreGlobs, int maxFileSizeKb, Action<string>? warn = null)
    {
        var files = new List<SourceFile>();
        var skipped = 0;
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
                        || AlwaysSkippedDirs.Contains(name, StringComparer.OrdinalIgnoreCase)
                        || ignoreGlobs.Any(g => GlobMatcher.IsMatch(g, relativePath)))
                    {
                        continue;
                    }

                    pending.Enqueue(entry);
                    continue;
                }

                if (ignoreGlobs.Any(g => GlobMatcher.IsMatch(g, relativePath)))
                {
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
        return new FileWalkResult(files, skipped);
    }
}
