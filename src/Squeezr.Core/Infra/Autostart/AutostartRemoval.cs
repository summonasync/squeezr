namespace Squeezr.Core.Infra.Autostart;

// Removal carries a second channel besides yes/no: an init system that refuses to answer (a user bus
// that is down) must not keep the managed file on disk forever, and the operator still has to read
// what the tool said. A warning never turns into a failed removal — it is what a best-effort step
// could not get checked.
public sealed record AutostartRemoval(bool WasRemoved, IReadOnlyList<string> Warnings)
{
    public static AutostartRemoval NothingManaged { get; } = new(false, []);

    public static AutostartRemoval Removed(params string[] warnings) => new(true, warnings);
}
