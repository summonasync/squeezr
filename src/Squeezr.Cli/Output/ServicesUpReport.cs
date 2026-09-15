namespace Squeezr.Cli.Output;

public sealed record ServicesUpReport(
    string ComposeFile,
    string Project,
    bool Rocm,
    string QdrantEndpoint,
    string OllamaEndpoint,
    string? ComposeVersion);
