namespace Squeezr.Core.Indexing;

internal sealed record MaskFailureCount(SecretMaskFailure Reason, int Count);
