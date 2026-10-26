namespace Squeezr.Core.Storage;

// The sole key to the deliberate-relocation channel: no public constructor and no ambient instance,
// so code either receives an approval as a parameter or cannot act on one. init, status and the
// Resolve walk take no approval parameter anywhere, which is what keeps the auto path structurally
// approval-free.
public sealed class WorkspaceMigrationApproval
{
    private WorkspaceMigrationApproval() { }

    public static WorkspaceMigrationApproval Explicit { get; } = new();
}
