namespace Squeezr.Cli.Output;

public sealed record ServicesUpReport(
    string ComposeFile,
    string Project,
    bool Rocm,
    string QdrantEndpoint,
    string QdrantRestEndpoint,
    string OllamaEndpoint,
    string? ComposeVersion,
    IReadOnlyList<ServicesUpServiceReport>? Services = null);
