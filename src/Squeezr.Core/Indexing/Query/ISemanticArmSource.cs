namespace Squeezr.Core.Indexing.Query;

public interface ISemanticArmSource
{
    string? ModelKey { get; }

    Task<SemanticArmResult> SearchAsync(SearchQuery query, CancellationToken ct, IReadOnlySet<uint>? fileScope, SecretGuardReader guard);
}

public sealed record SemanticArmResult(SemanticArm Arm, IReadOnlyList<string> Warnings);
