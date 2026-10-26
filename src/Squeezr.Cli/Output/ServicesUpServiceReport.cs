namespace Squeezr.Cli.Output;

public sealed record ServicesUpServiceReport(
    string Service,
    string State,
    string? Owner,
    string Action,
    string? Detail = null);
