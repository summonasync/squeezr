namespace Squeezr.Tests;

internal static class SyncOverAsyncSweep
{
    private static readonly string[] Markers = ["GetAwaiter().GetResult()"];

    private static readonly string[] MachineryFiles = ["SyncOverAsyncSweep.cs", "SyncOverAsyncSweepTests.cs"];

    public sealed record Offender(string RelativePath, int Line);

    public static IReadOnlyList<Offender> Scan(string srcRoot) => Scan(srcRoot, out _);

    public static IReadOnlyList<Offender> Scan(string srcRoot, out int scannedFiles)
    {
        var offenders = new List<Offender>();
        var files = 0;
        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file) || MachineryFiles.Contains(Path.GetFileName(file)))
                continue;
            files++;
            offenders.AddRange(ScanFile(file, Path.GetRelativePath(srcRoot, file)));
        }

        scannedFiles = files;
        return offenders;
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}");

    private static IEnumerable<Offender> ScanFile(string path, string relativePath)
    {
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
            if (Markers.Any(lines[i].Contains))
                yield return new Offender(relativePath, i + 1);
    }
}
