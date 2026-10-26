using System.Text.Json.Serialization;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing;

public sealed record FileFreshness(
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("lagSeconds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? LagSeconds);

// the cheap projection behind the envelope's fileFreshness field: a stat against the indexed stamp,
// never a hash read — the guard owns the content-exact classification (and refresh-on-read); this
// answers "does the index know the file's current state" for every file a response names
public static class FreshnessVerdict
{
    public const string Fresh = "fresh";
    public const string Lagging = "lagging";
    public const string Unknown = "unknown";

    public static IReadOnlyList<FileFreshness> Classify(
        string workspaceRoot,
        IReadOnlyList<(string Repo, string Path)> files,
        MetaStore? store = null,
        IReadOnlyList<string>? guardStaleKeys = null)
    {
        var distinct = files.Distinct().ToList();
        try
        {
            return ClassifyCore(workspaceRoot, distinct, store, guardStaleKeys);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return distinct.Select(f => new FileFreshness(f.Repo, f.Path, Unknown, null)).ToList();
        }
    }

    private static IReadOnlyList<FileFreshness> ClassifyCore(
        string workspaceRoot,
        IReadOnlyList<(string Repo, string Path)> files,
        MetaStore? store,
        IReadOnlyList<string>? guardStaleKeys)
    {
        using var owned = store is null ? new MetaStore(SqueezrPaths.MetaDbPath(workspaceRoot)) : null;
        var session = store ?? owned!;

        WorkspaceManifest manifest;
        try
        {
            manifest = WorkspaceManifestFile.Load(workspaceRoot);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return files.Select(f => new FileFreshness(f.Repo, f.Path, Unknown, null)).ToList();
        }

        var verdicts = new List<FileFreshness>();
        foreach (var file in files.Distinct())
        {
            var repoDoc = session.FindRepo(file.Repo);
            var doc = repoDoc is null ? null : session.FindFile(repoDoc.Id, file.Path);
            if (doc is null)
            {
                verdicts.Add(new FileFreshness(file.Repo, file.Path, Unknown, null));
                continue;
            }

            var entry = manifest.Repos.FirstOrDefault(r =>
                string.Equals(r.Name, file.Repo, StringComparison.OrdinalIgnoreCase));
            var fullPath = entry is null
                ? null
                : Path.Combine(
                    NavigationContext.ResolveRepoRoot(workspaceRoot, entry),
                    file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (fullPath is null || !File.Exists(fullPath) || !UnixFileType.IsRegularFile(fullPath))
            {
                verdicts.Add(new FileFreshness(file.Repo, file.Path, Unknown, null));
                continue;
            }

            var info = new FileInfo(fullPath);
            var indexedAt = AsUtc(doc.IndexedAt);
            var modified = AsUtc(info.LastWriteTimeUtc);
            if (info.Length != doc.SizeBytes || modified > indexedAt)
            {
                long? lag = modified > indexedAt ? (long)(modified - indexedAt).TotalSeconds : null;
                verdicts.Add(new FileFreshness(file.Repo, file.Path, Lagging, lag));
                continue;
            }

            // the guard's content-exact verdict outranks the stat: a file whose inline refresh failed
            // (sha mismatch, take-over, IO) is lagging even when its mtime still reads older than the stamp
            if (guardStaleKeys is not null && guardStaleKeys.Contains(Key(file), StringComparer.Ordinal))
            {
                verdicts.Add(new FileFreshness(file.Repo, file.Path, Lagging, null));
                continue;
            }

            verdicts.Add(new FileFreshness(file.Repo, file.Path, Fresh, null));
        }

        return verdicts;
    }

    private static string Key((string Repo, string Path) file) => $"{file.Repo}/{file.Path}";

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static bool IsRecoverable(Exception ex) =>
        ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException;
}
