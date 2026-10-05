using Squeezr.Core.Configuration;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing;

// Read-side enforcement of the guard: the single decision maker between "a name was requested" and
// "content or a path is rendered", plus the tally that says how much of an answer it dropped.
// The built-in set never depends on the config, so an unreadable config costs only the extensions —
// and says so on every answer, because silently dropping the user half of a guard is a fail-open.
public sealed class SecretGuardReader
{
    private const string RefusalReason = "protected by squeezr's built-in secret guard: never indexed, never rendered";

    private readonly SecretGuard.Profile _profile;
    private readonly IReadOnlyList<string> _extraPatterns;
    private readonly string? _configReadFailure;
    private int _withheld;
    private string? _firstWithheldPath;
    private bool _firstMatchedBuiltIns;

    private SecretGuardReader(IReadOnlyList<string> extraPatterns, string? configReadFailure = null)
    {
        _extraPatterns = extraPatterns;
        _profile = new SecretGuard.Profile(extraPatterns);
        _configReadFailure = configReadFailure;
    }

    public string? FirstWithheldPath => _firstWithheldPath;

    // the built-in half is a compile-time constant, so this is all a cached answer needs to be keyed on:
    // widening the config list must not keep serving the envelope computed before the widening
    public IReadOnlyList<string> ExtraPatterns => _extraPatterns;

    public IReadOnlyList<string>? Warnings
    {
        get
        {
            var lines = new List<string>();
            if (_withheld > 0)
            {
                lines.Add(Format());
            }

            if (_configReadFailure is { } failure)
            {
                lines.Add($"could not read indexing config ({failure}); user secret-guard masks are inactive");
            }

            return lines.Count == 0 ? null : lines;
        }
    }

    public static SecretGuardReader Load(string workspaceRoot)
    {
        try
        {
            return new SecretGuardReader(ConfigFile.Load(workspaceRoot).Indexing.SecretGuardExtra);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            return new SecretGuardReader([], ex.Message);
        }
    }

    // agents copy directory-shaped paths, and the classifier reads the last segment: an empty trailing
    // one would hide a live secret behind a "not found" answer, so the separator goes here, not in T1
    public bool IsProtected(string relativePath) => _profile.IsProtected(Trim(relativePath));

    // origin of a decision, for the surfaces that report a count and have to say where the mask came from
    public bool MatchesBuiltIns(string relativePath) => _profile.MatchesBuiltIns(Trim(relativePath));

    private static string Trim(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return relativePath.TrimEnd('/', '\\');
    }

    public bool TryWithhold(string relativePath)
    {
        if (!IsProtected(relativePath))
        {
            return false;
        }

        if (_withheld == 0)
        {
            _firstWithheldPath = relativePath;
            _firstMatchedBuiltIns = MatchesBuiltIns(relativePath);
        }

        _withheld++;
        return true;
    }

    public WorkspaceException Refusal(string relativePath) => new($"'{relativePath}' is {RefusalReason}");

    private string Format()
    {
        var source = _firstMatchedBuiltIns ? SecretGuard.BuiltInSourceName : SecretGuard.ConfiguredSourceName;
        return $"secret guard: {_withheld} result(s) withheld ('{_firstWithheldPath}' matches {source})";
    }
}
