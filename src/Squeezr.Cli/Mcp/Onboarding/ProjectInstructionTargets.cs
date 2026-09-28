using System.Text.RegularExpressions;

namespace Squeezr.Cli.Mcp.Onboarding;

public static class ProjectInstructionTargets
{
    internal static readonly string[] CarrierFiles = ["AGENTS.md", "CLAUDE.md", "QWEN.md"];

    private static readonly Regex HtmlCommentPattern = new("<!--.*?-->", RegexOptions.Singleline);

    public static IReadOnlyList<string> Resolve(string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(projectRoot);
        var targets = new List<string> { Path.Combine(projectRoot, CarrierFiles[0]) };
        for (var i = 1; i < CarrierFiles.Length; i++)
        {
            var candidate = Path.Combine(projectRoot, CarrierFiles[i]);
            if (File.Exists(candidate) && !IsImportPointer(candidate))
            {
                targets.Add(candidate);
            }
        }
        return targets;
    }

    internal static bool IsImportPointer(string path)
    {
        var content = File.ReadAllText(path);
        var uncommented = HtmlCommentPattern.Replace(content, string.Empty);
        string? single = null;
        var meaningful = 0;
        foreach (var raw in uncommented.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }
            meaningful++;
            single = line;
            if (meaningful > 1)
            {
                return false;
            }
        }
        return meaningful == 1 && IsImportLine(single!);
    }

    private static bool IsImportLine(string line) =>
        line.Length > 1 && line[0] == '@' && !line[1..].Any(char.IsWhiteSpace);
}
