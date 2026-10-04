using Squeezr.Core.Indexing.Roslyn;

namespace Squeezr.Core.Indexing;

public sealed record PacedPass(SemanticSessionPool Sessions, IReadOnlyList<string> ChangedPaths)
{
    // a warm session is the whole test: the walk compares stored sha against disk sha for every file
    // each pass anyway, so its drift set is complete even when the batch is empty (dropped watcher
    // events, forced pass). Without a warm session that set cannot be trusted and the pass goes full.
    public bool IsIncremental(string repoRoot, bool semantic) =>
        Sessions.IsWarm(repoRoot, semantic);
}
