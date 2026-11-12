namespace Squeezr.Core.Indexing.Watch.Supervision;

// IOException, not a bespoke type: every fleet surface already reports read/write faults of this file,
// and a refusal to rebuild an unparseable one is exactly such a fault.
public sealed class SupervisionStateUnreadableException : IOException
{
    internal SupervisionStateUnreadableException(SupervisionSnapshot snapshot)
        : base($"supervision state '{snapshot.FilePath}' {snapshot.Fault}; it is not overwritten — rebuilding " +
               "it from what cannot be read would drop the desires of every other store. Repair the JSON, or " +
               "move the file away to start supervision from the running default")
    {
    }
}
