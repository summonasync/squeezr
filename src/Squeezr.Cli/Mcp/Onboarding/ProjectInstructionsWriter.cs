namespace Squeezr.Cli.Mcp.Onboarding;

public static class ProjectInstructionsWriter
{
    public static ProjectBlockChange Refresh(string projectRoot) => Refresh(projectRoot, ProjectInstructions.Block);

    // The block a root is told about its own index is not one constant: a checkout that is answered by another
    // tree's store must be told that, or the file it carries teaches its agents to trust an index it does not
    // have. The text is handed in rather than derived here, so the writer stays a byte-level editor and the
    // decision about which checkout is which stays with the door that resolved it.
    public static ProjectBlockChange Refresh(string projectRoot, string block)
    {
        ArgumentNullException.ThrowIfNull(projectRoot);
        var targets = ProjectInstructionTargets.Resolve(projectRoot);
        var written = new List<string>();
        var orphaned = new List<string>();
        foreach (var target in targets)
        {
            if (HasOrphanMarker(target))
            {
                orphaned.Add(target);
                continue;
            }
            Upsert(target, block);
            written.Add(target);
        }
        return new ProjectBlockChange(written, orphaned);
    }

    public static IReadOnlyList<string> Strip(string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(projectRoot);
        var stripped = new List<string>();
        foreach (var name in ProjectInstructionTargets.CarrierFiles)
        {
            var path = Path.Combine(projectRoot, name);
            if (!File.Exists(path))
            {
                continue;
            }
            var content = File.ReadAllText(path);
            if (content.Contains(ProjectInstructions.StartMarker, StringComparison.Ordinal) &&
                content.Contains(ProjectInstructions.EndMarker, StringComparison.Ordinal))
            {
                Remove(path);
                stripped.Add(path);
            }
        }
        return stripped;
    }

    public static void Upsert(string path) => Upsert(path, ProjectInstructions.Block);

    public static void Upsert(string path, string block)
    {
        if (HasOrphanMarker(path))
        {
            return;
        }
        InstructionsWriter.Upsert(
            path,
            ProjectInstructions.StartMarker, ProjectInstructions.EndMarker, block,
            SqueezrInstructions.StartMarker, SqueezrInstructions.EndMarker);
    }

    public static void Remove(string path) =>
        InstructionsWriter.Remove(path, ProjectInstructions.StartMarker, ProjectInstructions.EndMarker, deleteWhenEmpty: false);

    internal static bool HasOrphanMarker(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        var content = File.ReadAllText(path);
        return content.Contains(ProjectInstructions.StartMarker, StringComparison.Ordinal) !=
            content.Contains(ProjectInstructions.EndMarker, StringComparison.Ordinal);
    }
}
