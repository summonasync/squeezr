namespace Squeezr.Core.Indexing.Navigation;

internal static class TypeKinds
{
    private static readonly HashSet<string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "class", "interface", "enum"
    };

    public static bool Contains(string kind) => Kinds.Contains(kind);
}
