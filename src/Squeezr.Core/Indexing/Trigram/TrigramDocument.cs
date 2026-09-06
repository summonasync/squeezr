namespace Squeezr.Core.Indexing.Trigram;

public sealed record TrigramDocument(uint RepoId, uint FileId, IReadOnlyDictionary<uint, int[]> Postings);
