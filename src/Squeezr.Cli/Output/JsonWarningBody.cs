namespace Squeezr.Cli.Output;

// Mirror of JsonErrorBody for advisory channel: same envelope discipline on stderr,
// stdout keeps the single document a --json consumer parses.
public sealed record JsonWarningBody(string Code, string Message)
{
    public const string EmbeddingsUnavailable = "embeddings_unavailable";
}
