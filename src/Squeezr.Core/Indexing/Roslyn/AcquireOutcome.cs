namespace Squeezr.Core.Indexing.Roslyn;

/// <summary>
/// What a pool Acquire handed the pass: Pooled belongs to the pool and must not be disposed,
/// OneShot belongs to the caller, Throttled and CreateFailed mean there is no session this pass
/// and the files it wanted to parse must go back into scope instead of trusting the committed sha.
/// </summary>
public enum AcquireOutcome
{
    Pooled,
    OneShot,
    Throttled,
    CreateFailed,
}
