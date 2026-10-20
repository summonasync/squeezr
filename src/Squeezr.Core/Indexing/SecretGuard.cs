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

    // the words every guard surface uses to name where a decision came from, so a read-side withholding and
    // an ingestion-side one are reported in one vocabulary
    public const string BuiltInSourceName = "built-in patterns";
    public const string ConfiguredSourceName = "indexing.secretGuardExtra";

    public static bool IsProtected(string relativePath, IReadOnlyList<string> extraPatterns) =>
        new Profile(extraPatterns).IsProtected(relativePath);

    // The count-and-origin phrase every withheld tally is reported with, so a pass report and a status ledger
    // cannot drift apart in the one sentence a reader uses to decide whether to widen their masks.
    public static string DescribeOrigins(int withheld, int withheldBuiltIns) => withheldBuiltIns switch
    {
        0 => ConfiguredSourceName,
        _ when withheldBuiltIns == withheld => BuiltInSourceName,
        _ => $"{withheldBuiltIns} {BuiltInSourceName}, {withheld - withheldBuiltIns} {ConfiguredSourceName}",
    };

    // Compiling the user half is the expensive part of a decision, and it is the same work for every path
    // in a scope. A caller that classifies a store or a walk builds one of these once; the static facade
    // above stays for one-shot questions and pays per call, which is what a single lookup can afford.
    public sealed class Profile(IReadOnlyList<string>? extraPatterns)
    {
        private readonly (CompiledExtra[] Compiled, IReadOnlyList<MaskFailureCount> Failed) _compiled =
            extraPatterns is { Count: > 0 } ? CompileExtras(extraPatterns) : ([], []);

        public int CompiledExtras => _compiled.Compiled.Length;

        // the configured masks that protect nothing, grouped by why — the reader turns these into the
        // degradation lines; the shapes themselves are dead ends, never a narrower protection
        internal IReadOnlyList<MaskFailureCount> FailedMasks => _compiled.Failed;

        public bool IsProtected(string relativePath)
        {
            ArgumentNullException.ThrowIfNull(relativePath);

            var segments = Segments(relativePath);

            // extras are checked first so a user glob can re-protect a name the built-in list exempts
            return MatchesAnyExtra(_compiled.Compiled, segments) || MatchesBuiltIns(segments);
        }

        // origin attribution for the withheld counters: the built-in half alone, with the user masks ignored.
        // A name the built-ins protect is reported as built-in even when a user glob covers it too — the
        // narrower claim is the one a reader can act on, and it matches what the read-side tally reports.
        public bool MatchesBuiltIns(string relativePath)
        {
            ArgumentNullException.ThrowIfNull(relativePath);

            return MatchesBuiltIns(Segments(relativePath));
        }

        private static bool MatchesBuiltIns(string[] segments) =>
            MatchesAny(OtherBuiltInGlobs, segments)
            || (!EnvCarveOuts.Contains(segments[^1]) && MatchesAny(EnvFamilyGlobs, segments));

        private static string[] Segments(string relativePath) =>
            relativePath.Replace('\\', '/').ToLowerInvariant().Split('/');
    }

    private static string[][] Normalize(IEnumerable<string> patterns) =>
        [.. patterns.Select(GlobMatcher.NormalizePattern)];

    // A negation, a comment, a directory-only shape or an unsupported one names no file the guard can see.
    // The ignore layer reads them the same way, so extras carry its dialect and subtract nothing: the
    // negated half in particular would otherwise start meaning "unprotect" in a knob that cannot subtract.
    // Character classes and escapes join that dialect as widening-only shapes: they name more files, never
    // fewer, and '!' still reaches this filter before any of them is compiled. A null parse is a blank or a
    // comment — no mask was attempted, so it fails no count either.
    private static (CompiledExtra[] Compiled, IReadOnlyList<MaskFailureCount> Failed) CompileExtras(
        IReadOnlyList<string> extraPatterns)
    {
        var compiled = new List<CompiledExtra>(extraPatterns.Count);
        var failures = new Dictionary<SecretMaskFailure, int>();

        foreach (var raw in extraPatterns)
        {
            var parsed = GitignoreMatcher.Parse(raw ?? string.Empty, IgnoreSource.Config, "config.json", ".");
            if (parsed is null)
            {
                continue;
            }

            if (FailureOf(parsed) is { } failure)
            {
                failures[failure] = failures.TryGetValue(failure, out var count) ? count + 1 : 1;
                continue;
            }

            compiled.Add(new CompiledExtra(
                GlobMatcher.NormalizeGitignorePattern(parsed.CompiledPattern), parsed.Anchored));
        }

        return ([.. compiled],
            [.. failures.Select(f => new MaskFailureCount(f.Key, f.Value)).OrderBy(f => f.Reason)]);
    }

    private static SecretMaskFailure? FailureOf(IgnorePattern parsed) => parsed switch
    {
        { Negate: true } => SecretMaskFailure.Negated,
        { DirOnly: true } => SecretMaskFailure.DirectoryOnly,
        { Unsupported: true } => SecretMaskFailure.UnsupportedPattern,
        _ => null,
    };

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
