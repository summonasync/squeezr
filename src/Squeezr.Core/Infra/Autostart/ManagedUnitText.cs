namespace Squeezr.Core.Infra.Autostart;

// Reading the entry that is on the machine is a diagnosis step, not an action, so a file the process
// cannot read comes back as a note instead of an exception: the caller still has the paths and the
// enable link to report, and the unreadable file is one of the facts about them.
internal static class ManagedUnitText
{
    internal static (string? Text, string? Note) Read(string path)
    {
        try
        {
            return (File.ReadAllText(path), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"'{path}' could not be read: {ex.Message}");
        }
    }
}
