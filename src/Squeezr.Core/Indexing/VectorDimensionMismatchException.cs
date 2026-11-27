using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing;

public sealed class VectorDimensionMismatchException : WorkspaceException
{
    public VectorDimensionMismatchException(string collection, ulong collectionSize, int modelDimensions)
        : base(
            $"Vector collection '{collection}' holds vectors of size {collectionSize}, but the active model " +
            $"produces {modelDimensions} ({collectionSize} \u2260 {modelDimensions}). Changing the model " +
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
