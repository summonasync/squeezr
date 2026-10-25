using Squeezr.Core.Embeddings;

namespace Squeezr.Cli.Embeddings;

internal static class OnnxExecutorEvidence
{
    // OnnxInferenceSession builds its InferenceSession without SessionOptions, so onnxruntime always
    // selects its default CPU execution provider. The available-provider list names what the runtime
    // could do with an explicitly appended EP — evidence for the reason text, never for the device,
    // which keeps a gpu verdict out of reach until a session actually selects one.
    internal static EmbeddingExecutor Derive(IReadOnlyList<string> availableProviders)
    {
        var providers = availableProviders.Count == 0
            ? "none reported"
            : string.Join(", ", availableProviders);
        return new EmbeddingExecutor(
            "cpu",
            $"session built without session options; onnxruntime execution providers: {providers}");
    }
}
