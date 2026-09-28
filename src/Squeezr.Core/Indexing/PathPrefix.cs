using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing;

public static class PathPrefix
{
    private const int CandidateLimit = 5;

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var segments = new List<string>();
        foreach (var segment in raw.Trim().Replace('\\', '/').Split('/'))
        {
            switch (segment)
            {
                case "":
                case ".":
                    continue;
                case "..":
                    throw new WorkspaceException(
                        "prefix must stay inside the repository — '..' segments are not allowed.",
                        WorkspaceException.InvalidQueryCode);
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? null : string.Join('/', segments) + "/";
    }

    public static bool Matches(string normalizedPrefix, string relativePath) =>
        relativePath.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase);

    public static string Strip(string normalizedPrefix, string relativePath) =>
        relativePath[normalizedPrefix.Length..];

    public static WorkspaceException NoSubtreeException(string normalizedPrefix, IEnumerable<string> indexedPaths)
    {
        var nearest = NearestDirectories(normalizedPrefix, indexedPaths);
        var message = nearest.Count == 0
            ? $"No indexed files under prefix '{normalizedPrefix}'."
            : $"No indexed files under prefix '{normalizedPrefix}'. Nearest directories: {string.Join(", ", nearest)}.";
        return new WorkspaceException(message, WorkspaceException.InvalidQueryCode);
    }

    private static IReadOnlyList<string> NearestDirectories(string normalizedPrefix, IEnumerable<string> indexedPaths) =>
        indexedPaths
            .Select(DirName)
            .Where(dir => dir != ".")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(dir => CommonPrefixLength(normalizedPrefix, dir))
            .ThenBy(dir => dir.Length)
            .ThenBy(dir => dir, StringComparer.Ordinal)
            .Take(CandidateLimit)
            .Select(dir => dir + "/")
            .ToList();

    private static int CommonPrefixLength(string left, string right)
    {
        var length = Math.Min(left.Length, right.Length);
        var i = 0;
        while (i < length && char.ToUpperInvariant(left[i]) == char.ToUpperInvariant(right[i]))
        {
            i++;
        }

        return i;
    }

    private static string DirName(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? "." : path[..index];
    }
}
