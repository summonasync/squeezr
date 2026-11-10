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
    public const string WorktreeNameClashCode = "worktree_name_clash";

    public WorkspaceException(string message, string code = DefaultCode, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
