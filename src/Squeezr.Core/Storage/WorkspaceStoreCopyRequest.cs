using Squeezr.Core.Workspace;

namespace Squeezr.Core.Storage;

// One store filling, named. The two store dirs are the bytes' ends; the root and the manifest path are
// what the destination owns afterwards, and a store-to-store pass cannot discover either through the
// registry — the caller that resolved the lane's row already knows both, so it hands them down instead of
// the copy guessing a store dir out of a root.
public sealed record WorkspaceStoreCopyRequest(
    string SourceStoreDir,
    string TargetStoreDir,
    string TargetRoot,
    string TargetManifestPath,
    IReadOnlyList<RepoEntry> SourceRepos);
