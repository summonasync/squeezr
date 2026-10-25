namespace Squeezr.Core.Storage;

// LiteDB 5.0.21's ENSURE overload formats the message and then throws the unformatted template, so a loop
// refusal reaches the operator as "Detected loop in FindAll({0})" with the name it refused to carry. The
// placeholder gets filled here, from the best evidence our own rebuild pass produced.
internal static class RebuildRefusalText
{
    private const string LoopTemplate = "Detected loop in FindAll({0})";

    public static string FillLoopPlaceholder(string engineMessage, string? collectionName) =>
        engineMessage.Replace(LoopTemplate, $"Detected loop in FindAll({collectionName ?? "unknown"})");
}
