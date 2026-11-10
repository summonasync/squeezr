namespace Squeezr.Core.Infra.Autostart;

// Removal carries a second channel besides yes/no, and it carries it in two meanings the CLI has to
// read apart. Beside a removal that happened, a warning is what a best-effort step could not get
// checked: the entry is gone and the operator still has to read what the tool answered. With nothing
// removed, the same channel says the removal was never confirmed at all, and an unconfirmed nothing is
// not a success to exit zero on.
public sealed record AutostartRemoval(bool WasRemoved, IReadOnlyList<string> Warnings)
{
    public static AutostartRemoval NothingManaged { get; } = new(false, []);

    // The entry may still be there: the platform tool would not say. Nothing was deleted, and saying
    // "no entry" out loud would be the other half of the same lie.
    public static AutostartRemoval Unconfirmed(string warning) => new(false, [warning]);

    public static AutostartRemoval Removed(params string[] warnings) => new(true, warnings);
}
