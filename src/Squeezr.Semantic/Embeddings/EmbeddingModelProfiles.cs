namespace Squeezr.Semantic.Embeddings;

public static class EmbeddingModelProfiles
{
    public static readonly EmbeddingModelProfile NomicEmbedTextV15 = new()
    {
        Id = "nomic-embed-text-v1.5",
        Dimensions = 768,
        Pooling = EmbeddingPooling.MeanL2,
        DocumentPrefix = "search_document: ",
        QueryPrefix = "search_query: ",
        DefaultMaxSequenceLength = 512
    };

    public static readonly EmbeddingModelProfile BgeCodeV1 = new()
    {
        Id = "bge-code-v1",
        Dimensions = 1536,
        Pooling = EmbeddingPooling.LastToken,
        DefaultMaxSequenceLength = 512
    };

    private static readonly EmbeddingModelProfile[] KnownProfiles = [NomicEmbedTextV15, BgeCodeV1];

    public static EmbeddingModelProfile Default => NomicEmbedTextV15;

    public static IReadOnlyList<string> KnownIds => Array.ConvertAll(KnownProfiles, static profile => profile.Id);

    public static EmbeddingModelProfile Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Default;

        var candidate = name.Trim();
        foreach (var profile in KnownProfiles)
        {
            if (candidate.Equals(profile.Id, StringComparison.OrdinalIgnoreCase))
                return profile;
        }

        throw new ArgumentException(
            $"Unknown embedding model profile '{name}'. Known profiles: {string.Join(", ", KnownIds)}.",
            nameof(name));
    }
}
