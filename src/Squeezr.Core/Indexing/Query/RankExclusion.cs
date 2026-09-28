using Squeezr.Core.Configuration;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing.Query;

public sealed class RankExclusion
{
    public static readonly RankExclusion None = new([], []);

    private readonly string[] _patterns;

    private RankExclusion(string[] patterns, IReadOnlyList<string> warnings)
    {
        _patterns = patterns;
        Patterns = patterns;
        Warnings = warnings;
    }

    public IReadOnlyList<string> Patterns { get; }

    public IReadOnlyList<string> Warnings { get; }

    public bool IsActive => _patterns.Length > 0;

    public bool Matches(string relativePath) =>
        _patterns.Any(pattern => GlobMatcher.IsMatch(pattern, relativePath));

    public static RankExclusion Parse(IEnumerable<string>? configured)
    {
        if (configured is null)
        {
            return None;
        }

        var patterns = new List<string>();
        var warnings = new List<string>();
        foreach (var raw in configured)
        {
            var pattern = raw?.Trim() ?? string.Empty;
            var flaw = Diagnose(pattern);
            if (flaw is null)
            {
                patterns.Add(pattern);
                continue;
            }

            warnings.Add($"indexing.rankExclude pattern '{raw}' {flaw}; pattern ignored.");
        }

        return patterns.Count == 0 && warnings.Count == 0
            ? None
            : new RankExclusion([.. patterns], warnings);
    }

    // Fail-soft: an unreadable config leaves ranking uncurated instead of breaking the query —
    // the config itself is already reported by the embedding runtime probe on the same response.
    public static RankExclusion Load(string workspaceRoot)
    {
        try
        {
            return Parse(ConfigFile.Load(workspaceRoot).Indexing.RankExclude);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            return None;
        }
    }

    // GlobMatcher never throws, so "malformed" here means "cannot express what the author meant":
    // every case below would silently match nothing, or something other than the obvious reading.
    private static string? Diagnose(string pattern)
    {
        if (pattern.Length == 0)
        {
            return "is empty";
        }

        var segments = pattern.Replace('\\', '/').Split('/');
        if (segments[0].Length == 0)
        {
            return "must stay relative to the repository root — drop the leading '/'";
        }

        if (segments.Contains("..", StringComparer.Ordinal))
        {
            return "cannot climb above the repository root";
        }

        return segments.Any(segment => segment != "**" && segment.Contains("**", StringComparison.Ordinal))
            ? "carries '**' inside a segment, where it degrades to '*' — write '**/<name>' to span directories"
            : null;
    }
}
