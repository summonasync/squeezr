namespace Squeezr.Core.Indexing;

// Ingestion-side half of the guard: the decision a pass makes before a path can become a row. It reuses the
// reader's compiled profile rather than the static facade, because a walk asks once per file.
// Nothing here names what it refused — a withheld path quoted in a warning is the leak this type exists to
// prevent, so its report is a count and an origin.
public sealed class SecretGuardIngestion(SecretGuardReader reader)
{
    private int _withheld;
    private int _matchedBuiltIns;

    public static SecretGuardIngestion Load(string workspaceRoot) => new(SecretGuardReader.Load(workspaceRoot));

    public int Withheld => _withheld;

    public int WithheldByBuiltIns => _matchedBuiltIns;

    public int WithheldByConfig => _withheld - _matchedBuiltIns;

    // deny-wins, and unclassifiable is denied: a path shape the classifier rejects with an exception has no
    // caller that could act on it mid-pass, and indexing it would be the fail-open this guard never allows.
    public bool Deny(string relativePath)
    {
        if (!IsProtected(relativePath))
        {
            return false;
        }

        _withheld++;
        if (OriginIsBuiltIns(relativePath))
        {
            _matchedBuiltIns++;
        }

        return true;
    }

    // naming decision for the store-side routes: an already-stored path either goes in a warning verbatim
    // or does not go in it at all. No tally — a drop the guard did not cause is not a withholding.
    public bool IsProtected(string relativePath)
    {
        try
        {
            return reader.IsProtected(relativePath);
        }
        catch (ArgumentNullException)
        {
            return true;
        }
    }

    private bool OriginIsBuiltIns(string relativePath)
    {
        try
        {
            return reader.MatchesBuiltIns(relativePath);
        }
        catch (ArgumentNullException)
        {
            return true;
        }
    }

    // How a path may be written into a message. A protected name is what the guard exists to keep out of
    // output, and a warning about a file the guard already refused says nothing a count cannot say — so the
    // shape is kept and the name is not.
    public string Describe(string relativePath) =>
        IsProtected(relativePath) ? "a secret-guard protected path" : $"'{relativePath}'";

    public string? WarningLine()
    {
        if (_withheld == 0)
        {
            return null;
        }

        return $"secret guard: {_withheld} file(s) withheld from indexing " +
            $"({SecretGuard.DescribeOrigins(_withheld, _matchedBuiltIns)})";
    }
}
