using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Squeezr.Cli.Embeddings;

internal interface IOnnxSession : IDisposable
{
    IReadOnlyCollection<string> InputNames { get; }

    DenseTensor<float> Run(IReadOnlyCollection<NamedOnnxValue> inputs);
}
