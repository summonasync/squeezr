namespace Squeezr.Semantic.Embeddings;

// A point the scroll carried that this product did not write: named by id and by what it lacks, because
// neither its coordinates nor its chunk can be reconstructed from the rest, and the count of them is the
// difference between a source collection and a copy that took everything of ours.
public sealed record ForeignVectorPoint(ulong Id, string Missing);
