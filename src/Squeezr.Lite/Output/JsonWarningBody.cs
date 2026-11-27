namespace Squeezr.Lite.Output;

public sealed record JsonWarningBody(string Code, string Message)
{
    public const string SemanticUnsupported = "semantic_unsupported";
}
