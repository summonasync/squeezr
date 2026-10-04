using Squeezr.Core.Indexing.Roslyn;

namespace Squeezr.Core.Indexing;

public sealed record PacedPass(SemanticSessionPool Sessions, IReadOnlyList<string> ChangedPaths)
{
    public bool IsIncremental(string repoRoot, bool semantic) =>
        ChangedPaths.Count > 0 && Sessions.IsWarm(repoRoot, semantic);
}
