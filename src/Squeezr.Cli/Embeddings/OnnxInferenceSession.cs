using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Squeezr.Cli.Embeddings;

internal sealed class OnnxInferenceSession : IOnnxSession
{
    private readonly InferenceSession _session;
    private readonly IReadOnlyCollection<string> _inputNames;

    public OnnxInferenceSession(string modelPath)
    {
        _session = new InferenceSession(modelPath);
        _inputNames = _session.InputMetadata.Keys.ToList();
    }

    public IReadOnlyCollection<string> InputNames => _inputNames;

    public DenseTensor<float> Run(IReadOnlyCollection<NamedOnnxValue> inputs)
    {
        using var output = _session.Run(inputs);
        var tensor = output[0].AsTensor<float>()
            ?? throw new InvalidOperationException("The embedding model returned no tensor output.");
        var copy = new DenseTensor<float>(tensor.Dimensions);
        var destination = copy.Buffer.Span;
        var index = 0;
        foreach (var value in tensor)
            destination[index++] = value;
        return copy;
    }

    public void Dispose() => _session.Dispose();
}
