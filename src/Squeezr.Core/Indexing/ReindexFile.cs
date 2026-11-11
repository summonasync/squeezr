namespace Squeezr.Core.Indexing;

// the re-read route a freshness guard may start, named so a test can count starts without paying for a real
// index pass; production hands it the indexer
internal delegate void ReindexFile(
    string workspaceRoot,
    string repo,
    string path,
    Action<string>? onStaleTakeover);
