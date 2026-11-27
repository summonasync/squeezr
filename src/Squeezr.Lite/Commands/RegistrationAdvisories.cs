using Squeezr.Core.Storage;

namespace Squeezr.Lite.Commands;

internal static class RegistrationAdvisories
{
    public static IReadOnlyList<RegistrationAdvisory> Collect(string name, string canonicalRoot)
    {
        var advisories = new List<RegistrationAdvisory>();

        var entries = LoadRegistryForAdvisory();
        foreach (var entry in entries)
        {
            if (string.Equals(entry.Name, name, StringComparison.Ordinal) && !SamePath(entry.RootPath, canonicalRoot))
            {
                advisories.Add(new RegistrationAdvisory(
                    "name_collision",
                    $"name '{name}' is already used by workspace '{entry.Name}' (storeKey '{entry.StoreKey}') " +
                    $"at '{entry.RootPath}'; pass --name to register under a unique name"));
            }
        }

        if (UnderTempPath(canonicalRoot))
        {
            advisories.Add(new RegistrationAdvisory(
                "temp_path_root",
                $"root '{canonicalRoot}' lives under the temp path '{Path.GetTempPath()}' and will outlive it; " +
                "for scratch work pin SQUEEZR_HOME to a temp dir and create the workspace outside your home, " +
                "for a real workspace pick a root outside the temp path"));
        }

        return advisories;
    }

    private static IReadOnlyList<WorkspaceRegistryEntry> LoadRegistryForAdvisory()
    {
        try
        {
            return WorkspaceRegistry.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool UnderTempPath(string canonicalRoot) =>
        canonicalRoot.StartsWith(
            WorkspaceCanonicalPath.Canonicalize(Path.GetTempPath()) + Path.DirectorySeparatorChar,
            PathComparison);

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
