using System.Text.RegularExpressions;

namespace Squeezr.Tests;

internal static class EnvMutationSweep
{
    private static readonly string[] Markers =
    [
        "Environment.SetEnvironmentVariable",
        "Environment.GetEnvironmentVariable",
        ".Environment[",
        "Process.Start",
        "ProcessStartInfo",
    ];

    private static readonly Regex ClassDeclaration = new(@"\b(?:class|struct)\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
    private static readonly Regex CollectionUsage = new(@"\[Collection\(""([^""]+)""\)\]", RegexOptions.Compiled);
    private static readonly string[] MachineryFiles = ["EnvMutationSweep.cs", "EnvMutationSweepTests.cs"];

    public sealed record Offender(string ClassName, string RelativePath, IReadOnlyList<string> Evidence);

    public static IReadOnlyList<Offender> Scan(string testsRoot, IReadOnlyCollection<string> serializedCollections)
    {
        var offenders = new List<Offender>();
        foreach (var file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file) || MachineryFiles.Contains(Path.GetFileName(file)))
                continue;
            ScanFile(file, Path.GetRelativePath(testsRoot, file), serializedCollections, offenders);
        }

        return offenders;
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
        path.Contains($"{Path.DirectorySeparatorChar}TestResults{Path.DirectorySeparatorChar}") ||
        path.Contains($"{Path.DirectorySeparatorChar}Fixtures{Path.DirectorySeparatorChar}");

    private static void ScanFile(
        string path,
        string relativePath,
        IReadOnlyCollection<string> serializedCollections,
        List<Offender> offenders)
    {
        var lines = File.ReadAllLines(path);
        if (!lines.Any(line => Markers.Any(line.Contains)))
            return;

        var stack = new Stack<Region>();
        string? pendingCollection = null;
        var pendingDepth = -1;
        var depth = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            var collectionName = CollectionUsage.Match(line);
            if (collectionName.Success)
            {
                pendingCollection = collectionName.Groups[1].Value;
                pendingDepth = depth;
            }

            var declaration = ClassDeclaration.Match(line);
            var bodyless = declaration.Success && line.TrimEnd().EndsWith(';');
            if (declaration.Success && !bodyless)
            {
                var parentSerialized = stack.Count > 0 && stack.Peek().EffectivelySerialized;
                var region = new Region(declaration.Groups[1].Value, depth, pendingCollection, parentSerialized, serializedCollections);
                if (line.Contains('{'))
                    region.Entered = true;
                stack.Push(region);
                pendingCollection = null;
            }

            var marker = Markers.FirstOrDefault(line.Contains);
            if (marker != null)
                foreach (var region in stack)
                    region.Evidence.Add(marker);

            if (line.Contains("[Fact]") || line.Contains("[Theory]"))
                foreach (var region in stack)
                    region.HasTests = true;

            depth += line.Count(c => c == '{');
            depth -= line.Count(c => c == '}');

            if (stack.Count > 0 && depth > stack.Peek().OpenDepth)
                stack.Peek().Entered = true;

            while (stack.Count > 0 && stack.Peek().Entered && stack.Peek().OpenDepth >= depth)
            {
                var region = stack.Pop();
                if (region.HasTests && region.Evidence.Count > 0 && !region.EffectivelySerialized)
                    offenders.Add(new Offender(region.Name, relativePath, [.. region.Evidence.Distinct()]));
            }

            if (pendingDepth > depth)
            {
                pendingCollection = null;
                pendingDepth = -1;
            }
        }
    }

    private sealed class Region(
        string name,
        int openDepth,
        string? ownCollection,
        bool parentSerialized,
        IReadOnlyCollection<string> serializedCollections)
    {
        public string Name { get; } = name;
        public int OpenDepth { get; } = openDepth;
        public bool Entered { get; set; }
        public bool HasTests { get; set; }
        public List<string> Evidence { get; } = [];
        public bool EffectivelySerialized { get; } =
            (ownCollection != null && serializedCollections.Contains(ownCollection)) || parentSerialized;
    }
}
