using Squeezr.Core.Workspace;

namespace Squeezr.Core.Embeddings;

public sealed class VectorDimensionMismatchException : WorkspaceException
{
    public VectorDimensionMismatchException(string collection, ulong collectionSize, int modelDimensions)
        : base(
            $"Qdrant collection '{collection}' has vector size {collectionSize}, but the active embedding model " +
            $"produces {modelDimensions} ({collectionSize} \u2260 {modelDimensions}). Changing the embedding model " +
            "requires re-embedding: run 'squeezr index --reembed'.",
            "dimension_mismatch")
    {
        Collection = collection;
        CollectionSize = collectionSize;
        ModelDimensions = modelDimensions;
    }

    public string Collection { get; }

    public ulong CollectionSize { get; }

    public int ModelDimensions { get; }
}
