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
        // A test that writes the shared registry from the host process is contained only while the
        // ambient pin holds, so it has to sit in a collection nothing can clobber the env inside.
        "WorkspaceRegistry.Add(",
        "WorkspaceRegistry.Save(",
    ];

    private static readonly Regex ClassDeclaration = new(@"\b(?:class|struct)\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
    private static readonly Regex CollectionUsage = new(@"\[Collection\(""([^""]+)""\)\]", RegexOptions.Compiled);
    private static readonly string[] MachineryFiles = ["EnvMutationSweep.cs", "EnvMutationSweepTests.cs"];

    // The 118 mechanism, made machine-checkable: Dispose restores an environment variable from a field,
    // and the field was never read, so SetEnvironmentVariable(name, null) strips the run's own pin from the
    // whole host process. The compiler already says this (CS0649); the tree only stopped shipping it because
    // a scan now names the class before a burst of leaked registry rows does.
    private static readonly Regex EnvRestoreFromField =
        new(@"Environment\.SetEnvironmentVariable\s*\([^,)]+,\s*(?<field>_[A-Za-z_]\w*)\s*\)", RegexOptions.Compiled);

    private static readonly Regex AssignedField =
        new(@"(?<field>_[A-Za-z_]\w*)\s*=(?!=)", RegexOptions.Compiled);

    public sealed record Offender(string ClassName, string RelativePath, IReadOnlyList<string> Evidence);

    public static IReadOnlyList<Offender> Scan(string testsRoot, IReadOnlyCollection<string> serializedCollections) =>
        Scan(testsRoot, serializedCollections, out _);

    public static IReadOnlyList<Offender> Scan(
        string testsRoot,
        IReadOnlyCollection<string> serializedCollections,
        out int scannedClasses)
    {
        var offenders = new List<Offender>();
        var classes = 0;
        foreach (var file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file) || MachineryFiles.Contains(Path.GetFileName(file)))
                continue;
            classes += ScanFile(file, Path.GetRelativePath(testsRoot, file), serializedCollections, offenders);
        }

        scannedClasses = classes;
        return offenders;
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
        path.Contains($"{Path.DirectorySeparatorChar}TestResults{Path.DirectorySeparatorChar}") ||
        path.Contains($"{Path.DirectorySeparatorChar}Fixtures{Path.DirectorySeparatorChar}");

    private static int ScanFile(
        string path,
        string relativePath,
        IReadOnlyCollection<string> serializedCollections,
        List<Offender> offenders)
    {
        var lines = File.ReadAllLines(path);
        if (!lines.Any(line => Markers.Any(line.Contains)))
            return 0;

        // Assigned anywhere in the file counts, including the initializer that sits on the declaration and
        // the one whose value continues on the next line.
        var assigned = AssignedField.Matches(File.ReadAllText(path))
            .Select(match => match.Groups["field"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var stack = new Stack<Region>();
        string? pendingCollection = null;
        var pendingDepth = -1;
        var depth = 0;
        var classes = 0;

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
                classes++;
                pendingCollection = null;
            }

            var marker = Markers.FirstOrDefault(line.Contains);
            if (marker != null)
                foreach (var region in stack)
                    region.Evidence.Add(marker);

            foreach (var restore in EnvRestoreFromField.Matches(line))
            {
                var field = restore.Groups["field"].Value;
                if (!assigned.Contains(field))
                    foreach (var region in stack)
                        region.Restores.Add($"{field} is restored into the environment but never assigned (line {i + 1})");
            }

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
                var outsideSerialized =
                    region.HasTests && region.Evidence.Count > 0 && !region.EffectivelySerialized;
                if (outsideSerialized || region.Restores.Count > 0)
                {
                    var evidence = outsideSerialized ? new List<string>(region.Evidence) : new List<string>();
                    evidence.AddRange(region.Restores);
                    offenders.Add(new Offender(region.Name, relativePath, [.. evidence.Distinct()]));
                }
            }

            if (pendingDepth > depth)
            {
                pendingCollection = null;
                pendingDepth = -1;
            }
        }

        return classes;
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
        public List<string> Restores { get; } = [];
        public bool EffectivelySerialized { get; } =
            (ownCollection != null && serializedCollections.Contains(ownCollection)) || parentSerialized;
    }
}
