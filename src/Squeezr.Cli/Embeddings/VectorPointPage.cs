using Squeezr.Semantic.Embeddings;

namespace Squeezr.Cli.Embeddings;

// One page of a vector scroll: the points it carried, the id to ask after next, and the points on that page
// this product did not write. A copy walks pages instead of materialising a whole collection because a
// parent store of this product's own size holds tens of thousands of vectors and the pass must not need that
// much memory to move them. The third field is not an error channel: a page cannot carry a point whose
// coordinates cannot be named, and a copy that dropped those in silence would report a collection short of
// its source with no number left to point at.
public sealed record VectorPointPage(
    IReadOnlyList<VectorPoint> Points,
    ulong? NextOffset,
    IReadOnlyList<ForeignVectorPoint> ForeignPoints)
{
    public static VectorPointPage Empty { get; } = new([], null, []);
}
