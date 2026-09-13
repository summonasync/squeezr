namespace Squeezr.Core.Workspace;

public class WorkspaceException : Exception
{
    public const string DefaultCode = "workspace_error";
    public const string NotInitializedCode = "workspace_not_initialized";
    public const string IndexNotBuiltCode = "index_not_built";
    public const string InvalidQueryCode = "invalid_query";

    public WorkspaceException(string message, string code = DefaultCode) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
