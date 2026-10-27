namespace Squeezr.Core.Indexing;

// Config-shaped files whose whole text may hold a secret next to ordinary settings. They are not
// excluded like the guard's protected paths — they are indexed with their secret-key values masked,
// so structure and non-secret settings stay searchable. The list is built-in only: secretGuardExtra
// keeps naming whole files to exclude, and it can add protection but never subtract this coverage.
internal static class SecretSnippetCoverage
{
    private static readonly string[] PatternNames =
    [
        "**/appsettings*.json",
        "**/.env.example",
        "**/.env.sample",
        "**/.env.template",
        "**/docker-compose*.yml",
        "**/docker-compose*.yaml",
        "**/*.config",
    ];

    internal static bool IsCovered(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var normalized = relativePath.Replace('\\', '/').ToLowerInvariant();
        return PatternNames.Any(pattern => GlobMatcher.IsMatch(pattern, normalized));
    }
}
