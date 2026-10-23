namespace Squeezr.Cli.Mcp;

// What serve says about its watch standing, on the two stderr channels it already speaks: one human line and one
// machine line cut from the same claim outcome. Claim-level faults reach the same writer and repeat only when the
// message changes — a beat failing every 5 s is not a log line a reader can use, and a silent one is the reason
// a wedged marker looked like a healthy owner. The CLI watch daemon reuses the fault writer with its own label;
// it speaks no machine channel, so it never calls Announce.
internal sealed class ServeWatchAnnouncer(TextWriter output, string faultLabel = "serve watch")
{
    private string? _lastFault;

    public void Announce(ServeWatchRole role)
    {
        if (!role.IsOwner)
        {
            output.WriteLine($"warning: {role.StartupLine}");
        }

        output.WriteLine(role.MachineLine);
        output.Flush();
    }

    public void Fault(string message)
    {
        if (string.Equals(message, _lastFault, StringComparison.Ordinal))
        {
            return;
        }

        _lastFault = message;
        output.WriteLine($"warning: {faultLabel}: {message}");
        output.Flush();
    }
}
