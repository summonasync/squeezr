namespace Squeezr.Core.Workspace;

public class WorkspaceException : Exception
{
    public const string DefaultCode = "workspace_error";
    public const string NotInitializedCode = "workspace_not_initialized";
    public const string IndexNotBuiltCode = "index_not_built";
    public const string InvalidQueryCode = "invalid_query";
    public const string RegistryBusyCode = "workspace_registry_busy";
    public const string HomeUnavailableCode = "home_unavailable";
    public const string StorageUnavailableCode = "storage_unavailable";
    public const string OnnxProviderCode = "onnx_provider_removed";
    public const string NotAWorktreeCode = "not_a_worktree";
    public const string WorktreeNotRegisteredCode = "worktree_not_registered";
    public const string WorktreeNameClashCode = "worktree_name_clash";
    public const string WorktreeModelMismatchCode = "worktree_model_mismatch";
    public const string WorktreeModelUnresolvedCode = "worktree_model_unresolved";
    public const string WorktreeStoreForkCode = "worktree_store_fork";
    public const string WorktreeStoreUnresolvedCode = "worktree_store_unresolved";
    public const string WorktreeSameStoreCode = "worktree_same_store";
    public const string WorktreeVectorMismatchCode = "worktree_vector_mismatch";
    public const string WorktreeVectorDriftCode = "worktree_vector_drift";
    public const string WorktreeVectorDimensionCode = "worktree_vector_dimension";
    public const string VectorPointForeignCode = "vector_point_foreign";

    public WorkspaceException(string message, string code = DefaultCode, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
