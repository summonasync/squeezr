using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

// The report of one deliberate registration: what the registry holds now, not what the caller hoped
// for. Every path and column comes back off the row the pass ended with, so the answer can never
// claim a parent, a kind or a branch the store does not actually carry.
public sealed record WorktreeRegistrationOutcome(
    WorktreeRegistrationState State,
    string RootPath,
    string? StoreKey,
    string? WorkspaceName,
    WorkspaceKind? Kind,
    string? ParentRootPath,
    string? Branch,
    bool IsLinked,
    bool IsDetached,
    bool? ReconcilePending,
    string? Reason,
    string? RepairHint)
{
    internal static WorktreeRegistrationOutcome FromRow(
        WorktreeRegistrationState state,
        WorkspaceRegistryEntry row,
        WorktreeFacts facts,
        string? reason = null,
        string? repairHint = null)
    {
        return new WorktreeRegistrationOutcome(
            State: state,
            RootPath: row.RootPath,
            StoreKey: row.StoreKey,
            WorkspaceName: row.Name,
            Kind: row.Kind,
            ParentRootPath: row.ParentRootPath,
            Branch: row.Branch,
            IsLinked: facts.IsLinked,
            IsDetached: facts.IsDetached,
            // A store this pass just minted is empty by construction, so pending is a fact. An adopted
            // store's reconcile state lives on the store side and nothing reports it yet, so the honest
            // answer is unknown — claiming "not pending" is the direction that lies across trees.
            ReconcilePending: state == WorktreeRegistrationState.Registered ? true : null,
            Reason: reason,
            RepairHint: repairHint);
    }

    internal static WorktreeRegistrationOutcome NothingCreated(
        WorktreeRegistrationState state,
        string rootPath,
        WorktreeFacts facts,
        string reason)
    {
        return new WorktreeRegistrationOutcome(
            State: state,
            RootPath: rootPath,
            StoreKey: null,
            WorkspaceName: null,
            Kind: null,
            ParentRootPath: null,
            Branch: facts.Branch,
            IsLinked: facts.IsLinked,
            IsDetached: facts.IsDetached,
            ReconcilePending: null,
            Reason: reason,
            RepairHint: null);
    }
}
