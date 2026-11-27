namespace Squeezr.Core.Infra;

// Answers only whether a compose file next to a workspace was written by this product, so a drop or a
// legacy migration may delete it. The writer of that file and everything it starts live in the semantic
// assembly; Core knows the two identifying markers and nothing else about what the file does.
internal static class GeneratedComposeFile
{
    private const string ManagedByMarker = "# managed by squeezr";
    private const string VolumeMarker = "squeezr-qdrant";

    internal static bool IsSqueezrGenerated(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var content = File.ReadAllText(path);
        return content.Contains(ManagedByMarker, StringComparison.Ordinal) ||
               content.Contains(VolumeMarker, StringComparison.Ordinal);
    }
}
