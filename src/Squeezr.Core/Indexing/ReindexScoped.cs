namespace Squeezr.Core.Indexing;

// the batch route a freshness guard may start, named so a test can count starts without paying for a real
// index pass; production hands it the scoped indexer. The takeover notice rides along although the scoped
// route has no slot to land it in (IndexScoped takes the store lock bare) — recorded as a loss on 032/T4;
// the parameter stays because a re-index route's contract is the one thing the seam must not reshape quietly.
internal delegate WorkspaceIndexer.ScopedIndexResult ReindexScoped(
    string workspaceRoot,
    IReadOnlyList<(string Repo, string Path, string AbsolutePath)> batch,
    Action<string>? onStaleTakeover);
