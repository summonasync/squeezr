namespace Squeezr.Core.Infra;

public sealed record ServiceOwnership(ServiceOwnership.Verdict Kind, string? Owner)
{
    public enum Verdict
    {
        Ours,
        Foreign,
        Unexplained
    }

    public static ServiceOwnership Ours { get; } = new(Verdict.Ours, "squeezr compose project");

    public static ServiceOwnership Foreign(string owner) => new(Verdict.Foreign, owner);

    public static ServiceOwnership Unexplained { get; } = new(Verdict.Unexplained, null);
}
