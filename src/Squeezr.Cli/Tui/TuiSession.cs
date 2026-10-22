namespace Squeezr.Cli.Tui;

public sealed class TuiSession
{
    private bool _vectorStoreUnavailableShown;

    public bool TryMarkVectorStoreUnavailableShown()
    {
        if (_vectorStoreUnavailableShown)
        {
            return false;
        }

        _vectorStoreUnavailableShown = true;
        return true;
    }
}
