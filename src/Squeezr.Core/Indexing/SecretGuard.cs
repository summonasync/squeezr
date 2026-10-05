using Squeezr.Core.Indexing.Ignore;

namespace Squeezr.Core.Indexing;

// Path-shape classifier for files that must never be indexed or rendered. Deliberately not a layer of
// IgnoreRuleset: that one is last-match-wins, so a '!' negation in a user file could reopen the hole.
// User extras borrow the ignore layer's parser, so one pattern means one thing in every section of
// config.json, but they stay union-only here: they can name more secrets, never fewer.
public static class SecretGuard
{
    private static readonly string[] EnvFamilyPatternNames =
    [
        "**/.env",
        "**/.env.*"
    ];

    private static readonly string[] OtherBuiltInPatternNames =
    [
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

    private static readonly string[][] EnvFamilyGlobs = Normalize(EnvFamilyPatternNames);
    private static readonly string[][] OtherBuiltInGlobs = Normalize(OtherBuiltInPatternNames);

    private static readonly string[] BuiltInPatternNames =
        [.. EnvFamilyPatternNames, .. OtherBuiltInPatternNames];

    // conventional placeholders: empty by agreement, and useful in search results
    private static readonly HashSet<string> EnvCarveOuts =
        new([".env.example", ".env.sample", ".env.template"], StringComparer.Ordinal);

    public static IReadOnlyList<string> BuiltInPatterns { get; } = BuiltInPatternNames.AsReadOnly();

    public static bool IsProtected(string relativePath, IReadOnlyList<string> extraPatterns)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var segments = relativePath.Replace('\\', '/').ToLowerInvariant().Split('/');

        // extras are checked first so a user glob can re-protect a name the built-in list exempts
        if (extraPatterns is { Count: > 0 } && MatchesAnyExtra(CompileExtras(extraPatterns), segments))
        {
            return true;
        }

        // the carve-out exempts its names from the .env family, never from the rest of the set
        return MatchesAny(OtherBuiltInGlobs, segments)
            || (!EnvCarveOuts.Contains(segments[^1]) && MatchesAny(EnvFamilyGlobs, segments));
    }

    private static string[][] Normalize(IEnumerable<string> patterns) =>
        [.. patterns.Select(GlobMatcher.NormalizePattern)];

    // A negation, a comment, a directory-only shape or an unsupported one names no file the guard can see.
    // The ignore layer reads them the same way, so extras carry its dialect and subtract nothing: the
    // negated half in particular would otherwise start meaning "unprotect" in a knob that cannot subtract.
    // Character classes and escapes join that dialect as widening-only shapes: they name more files, never
    // fewer, and '!' still reaches this filter before any of them is compiled.
    private static CompiledExtra[] CompileExtras(IReadOnlyList<string> extraPatterns)
    {
        var compiled = new List<CompiledExtra>(extraPatterns.Count);

        foreach (var raw in extraPatterns)
        {
            var parsed = GitignoreMatcher.Parse(raw ?? string.Empty, IgnoreSource.Config, "config.json", ".");
            if (parsed is null || parsed.Negate || parsed.DirOnly || parsed.Unsupported)
            {
                continue;
            }

            compiled.Add(new CompiledExtra(
                GlobMatcher.NormalizeGitignorePattern(parsed.CompiledPattern), parsed.Anchored));
        }

        return [.. compiled];
    }

    private static bool MatchesAnyExtra(CompiledExtra[] extras, string[] loweredSegments)
    {
        foreach (var extra in extras)
        {
            var window = extra.MatchesWholePath
                ? loweredSegments.AsSpan()
                : loweredSegments.AsSpan(loweredSegments.Length - 1, 1);

            if (GlobMatcher.IsMatch(extra.Segments, window))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAny(string[][] patterns, string[] segments) =>
        patterns.Any(glob => GlobMatcher.IsMatch(glob, segments));

    private sealed record CompiledExtra(string[] Segments, bool MatchesWholePath);
}
