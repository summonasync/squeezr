using Squeezr.Core.Configuration;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing;

// Read-side enforcement of the guard: the single decision maker between "a name was requested" and
// "content or a path is rendered", plus the tally that says how much of an answer it dropped.
// The built-in set never depends on the config, so an unreadable config costs only the extensions.
public sealed class SecretGuardReader
{
    private const string RefusalReason = "protected by squeezr's built-in secret guard: never indexed, never rendered";

    private readonly IReadOnlyList<string> _extraPatterns;
    private int _withheld;
    private string? _firstWithheldPath;
    private bool _firstMatchedBuiltIns;

    private SecretGuardReader(IReadOnlyList<string> extraPatterns) => _extraPatterns = extraPatterns;

    public string? FirstWithheldPath => _firstWithheldPath;

    // the built-in half is a compile-time constant, so this is all a cached answer needs to be keyed on:
    // widening the config list must not keep serving the envelope computed before the widening
    public IReadOnlyList<string> ExtraPatterns => _extraPatterns;

    public IReadOnlyList<string>? Warnings => _withheld == 0 ? null : [Format()];

    public static SecretGuardReader Load(string workspaceRoot)
    {
        try
        {
            return new SecretGuardReader(ConfigFile.Load(workspaceRoot).Indexing.SecretGuardExtra);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            return new SecretGuardReader([]);
        }
    }

    public bool IsProtected(string relativePath) => SecretGuard.IsProtected(relativePath, _extraPatterns);

    public bool TryWithhold(string relativePath)
    {
        if (!IsProtected(relativePath))
        {
            return false;
        }

        if (_withheld == 0)
        {
            _firstWithheldPath = relativePath;
            // the two-argument form everywhere on purpose: an empty extension list is the built-in half,
            // and the single-argument overload is not a contract this slice may lean on
            _firstMatchedBuiltIns = SecretGuard.IsProtected(relativePath, []);
        }

        _withheld++;
        return true;
    }

    public WorkspaceException Refusal(string relativePath) => new($"'{relativePath}' is {RefusalReason}");

    private string Format()
    {
        var source = _firstMatchedBuiltIns ? "built-in patterns" : "indexing.secretGuardExtra";
        return $"secret guard: {_withheld} result(s) withheld ('{_firstWithheldPath}' matches {source})";
    }
}
