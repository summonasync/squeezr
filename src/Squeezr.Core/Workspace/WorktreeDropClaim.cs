namespace Squeezr.Core.Workspace;

// One registry row naming one spelling of one tree. The spelling is kept because the collection a store
// reads is hashed from it: dropping a tree must clear every collection its rows point at, and a row
// registered through a link names a different collection from the same tree registered physically.
public sealed record WorktreeDropClaim(
    string StoreKey,
    string Name,
    string RegisteredRootPath);
