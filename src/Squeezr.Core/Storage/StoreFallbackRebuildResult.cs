namespace Squeezr.Core.Storage;

// What one copy-out pass came back with. A refusal names the collection the pass died on — the engine's
// own refusal hides that name behind a placeholder it never fills, so this is the only honest source.
internal sealed record StoreFallbackRebuildResult(
    bool Rebuilt,
    string? FailedCollection,
    string? FailureReason);
