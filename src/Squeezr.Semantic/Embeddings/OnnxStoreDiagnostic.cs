namespace Squeezr.Semantic.Embeddings;

public static class OnnxStoreDiagnostic
{
    public const string ReembedCommand = "squeezr index --reembed";

    public static IReadOnlyList<string> Lines() =>
    [
        "ONNX embedding store detected: this workspace was indexed with the removed onnx provider.",
        "Its vectors are incompatible with every other embedding model and there is no conversion.",
        $"Migration path: set embeddings.provider to 'ollama' or 'openai', then run '{ReembedCommand}' — existing vectors are dropped and rebuilt.",
        "Until migrated, vector search stays degraded in this workspace."
    ];

    public static string Text() => string.Join(" ", Lines());
}
