namespace Squeezr.Cli.Mcp.Onboarding;

public static class ProjectInstructionsWriter
{
    public static ProjectBlockChange Refresh(string projectRoot)
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
            Upsert(target);
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

    public static void Upsert(string path)
    {
        if (HasOrphanMarker(path))
        {
            return;
        }
        InstructionsWriter.Upsert(
            path,
            ProjectInstructions.StartMarker, ProjectInstructions.EndMarker, ProjectInstructions.Block,
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
