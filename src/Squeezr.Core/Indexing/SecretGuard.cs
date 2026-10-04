namespace Squeezr.Core.Indexing;

// Path-shape classifier for files that must never be indexed or rendered. Deliberately not a layer of
// IgnoreRuleset: that one is last-match-wins, so a '!' negation in a user file could reopen the hole.
public static class SecretGuard
{
    private static readonly string[] BuiltInPatternNames =
    [
        "**/.env",
        "**/.env.*",
        "**/*.key",
        "**/*.pem",
        "**/*.pfx",
        "**/*.p12",
        "**/*.jks",
        "**/*.kdbx",
        "**/id_rsa*",
        "**/id_dsa*",
        "**/id_ecdsa*",
        "**/id_ed25519*",
        "**/.npmrc",
        "**/.netrc",
        "**/.git-credentials",
        "**/.pgpass",
        "**/secrets.json",
        "**/secrets.*.json",
        "**/*.secret"
    ];

    private static readonly string[][] BuiltInGlobs = Normalize(BuiltInPatternNames);

    // conventional placeholders: empty by agreement, and useful in search results
    private static readonly HashSet<string> EnvCarveOuts =
        new([".env.example", ".env.sample", ".env.template"], StringComparer.Ordinal);

    public static IReadOnlyList<string> BuiltInPatterns => BuiltInPatternNames;

    public static bool IsProtected(string relativePath) => IsProtected(relativePath, []);

    public static bool IsProtected(string relativePath, IReadOnlyList<string> extraPatterns)
    {
        var segments = relativePath.Replace('\\', '/').ToLowerInvariant().Split('/');

        // extras are checked first so a user glob can re-protect a name the built-in list exempts
        if (extraPatterns is { Count: > 0 } && MatchesAny(Normalize(extraPatterns), segments))
        {
            return true;
        }

        return !EnvCarveOuts.Contains(segments[^1]) && MatchesAny(BuiltInGlobs, segments);
    }

    private static string[][] Normalize(IEnumerable<string> patterns) =>
        [.. patterns.Where(raw => !string.IsNullOrWhiteSpace(raw)).Select(GlobMatcher.NormalizePattern)];

    private static bool MatchesAny(string[][] patterns, string[] segments) =>
        patterns.Any(glob => GlobMatcher.IsMatch(glob, segments));
}
