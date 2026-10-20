namespace Squeezr.Core.Storage;

public sealed record OrphanAdoptionFailure(OrphanInfo Orphan, string Cause);
