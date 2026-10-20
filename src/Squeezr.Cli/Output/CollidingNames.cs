using Squeezr.Core.Storage;

namespace Squeezr.Cli.Output;

public static class CollidingNames
{
    public static IReadOnlySet<string> SharedByLiveEntries(IReadOnlyList<WorkspaceRegistryEntry> entries) =>
        entries
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
}
