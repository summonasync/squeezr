namespace Squeezr.Cli.Output;

public sealed record ServicesStatusReport(
    string ComposeFile,
    string Project,
    bool Rocm,
    string? ComposeVersion,
    IReadOnlyList<ServiceInstanceEntry> Services);
