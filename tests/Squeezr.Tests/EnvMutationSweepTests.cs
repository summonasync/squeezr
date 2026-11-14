using System.Text.RegularExpressions;
using Shouldly;
using Xunit.Abstractions;

namespace Squeezr.Tests;

public class EnvMutationSweepTests
{
    private readonly ITestOutputHelper _output;

    public EnvMutationSweepTests(ITestOutputHelper output) => _output = output;

    private static readonly Regex SerializedDefinition =
        new(@"\[CollectionDefinition\(""(?<name>[^""]+)""(?<body>.*?)\)\]", RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void TestTree_ClassesTouchingProcessEnvOrProcesses_AllSitInSerializedCollections()
    {
        var testsRoot = TestsRoot();
        var offenders = EnvMutationSweep.Scan(testsRoot, SerializedCollections(testsRoot), out var scannedClasses);

        // The denominator is part of the assertion: an empty offender list over nothing scanned would be
        // the green that proves nothing.
        _output.WriteLine($"env sweep: {scannedClasses} classes scanned, {offenders.Count} offenders");
        scannedClasses.ShouldBeGreaterThan(0, "the sweep found no class to judge — the root or the filter is broken");

        offenders.ShouldBeEmpty(
            $"env/process/registry-touching test classes must sit in a DisableParallelization collection, " +
            $"and env restores must name a field the file assigns: {Describe(offenders)}");
    }

    [Fact]
    public void Scan_StaticRegistryWriterOutsideSerializedCollection_IsReported()
    {
        using var scratch = new ScratchDir();
        scratch.Write("RowWriterTests.cs",
            """
            public class RowWriterTests
            {
                [Fact]
                public void Do_Thing() => WorkspaceRegistry.Add(new WorkspaceEntry("x", "/tmp/x"));
            }
            """);

        var offender = offenders(scratch).Single(o => o.ClassName == "RowWriterTests");
        offender.Evidence.ShouldContain("WorkspaceRegistry.Add(");
    }

    [Fact]
    public void Scan_EnvRestoredFromNeverAssignedField_IsReportedEvenUnderASerializedCollection()
    {
        using var scratch = new ScratchDir();
        scratch.Write("ClobberTests.cs",
            """
            [Collection("ProcessGlobals")]
            public class ClobberTests : IDisposable
            {
                private readonly string? _previousEnv;

                [Fact]
                public void Do_Thing() => Environment.SetEnvironmentVariable("SQUEEZR_HOME", "/tmp/scratch");

                public void Dispose() =>
                    Environment.SetEnvironmentVariable("SQUEEZR_HOME", _previousEnv);
            }
            """);

        var offender = offenders(scratch).Single(o => o.ClassName == "ClobberTests");
        offender.Evidence.ShouldContain(e => e.Contains("_previousEnv"));
    }

    [Fact]
    public void Scan_EnvRestoredFromAssignedField_IsClean()
    {
        using var scratch = new ScratchDir();
        scratch.Write("HonestTests.cs",
            """
            [Collection("ProcessGlobals")]
            public class HonestTests : IDisposable
            {
                private readonly string? _previousEnv = Environment.GetEnvironmentVariable("SQUEEZR_HOME");

                [Fact]
                public void Do_Thing() => Environment.SetEnvironmentVariable("SQUEEZR_HOME", "/tmp/scratch");

                public void Dispose() =>
                    Environment.SetEnvironmentVariable("SQUEEZR_HOME", _previousEnv);
            }
            """);

        offenders(scratch).ShouldBeEmpty();
    }

    [Fact]
    public void Scan_EnvRestoredFromFieldAssignedOnTheNextLine_IsClean()
    {
        using var scratch = new ScratchDir();
        scratch.Write("WrappedTests.cs",
            """
            [Collection("ProcessGlobals")]
            public class WrappedTests : IDisposable
            {
                private readonly string? _previousEnv =
                    Environment.GetEnvironmentVariable("SQUEEZR_HOME");

                [Fact]
                public void Do_Thing() => Environment.SetEnvironmentVariable("SQUEEZR_HOME", "/tmp/scratch");

                public void Dispose() =>
                    Environment.SetEnvironmentVariable("SQUEEZR_HOME", _previousEnv);
            }
            """);

        offenders(scratch).ShouldBeEmpty();
    }

    [Fact]
    public void Scan_ClassMutatingEnvWithoutCollection_IsReportedWithItsName()
    {
        using var scratch = new ScratchDir();
        scratch.Write("OffenderTests.cs",
            """
            public class OffenderTests
            {
                [Fact]
                public void Do_Thing()
                {
                    Environment.SetEnvironmentVariable("SQUEEZR_HOME", null);
                }
            }
            """);

        var offenders = EnvMutationSweep.Scan(scratch.Root, ["ProcessGlobals"]);

        var offender = offenders.Single(o => o.ClassName == "OffenderTests");
        offender.Evidence.ShouldContain("Environment.SetEnvironmentVariable");
        offender.RelativePath.ShouldBe("OffenderTests.cs");
    }

    [Fact]
    public void Scan_SameClassUnderSerializedCollection_IsClean()
    {
        using var scratch = new ScratchDir();
        scratch.Write("CoveredTests.cs",
            """
            [Collection("ProcessGlobals")]
            public class CoveredTests
            {
                [Fact]
                public void Do_Thing()
                {
                    Environment.SetEnvironmentVariable("SQUEEZR_HOME", null);
                }
            }
            """);

        EnvMutationSweep.Scan(scratch.Root, ["ProcessGlobals"]).ShouldBeEmpty();
    }

    [Fact]
    public void Scan_MutationInNestedHelper_OfUncoveredTestClass_NamesTheOuterClass()
    {
        using var scratch = new ScratchDir();
        scratch.Write("OuterTests.cs",
            """
            public class OuterTests
            {
                [Fact]
                public void Do_Thing() => Nested.Mutate();

                private sealed class Nested
                {
                    public static void Mutate() => Environment.SetEnvironmentVariable("HOME", null);
                }
            }
            """);

        offenders(scratch).Select(o => o.ClassName).ShouldBe(["OuterTests"]);
    }

    [Fact]
    public void Scan_MutationInNestedHelper_OfCoveredTestClass_IsClean()
    {
        using var scratch = new ScratchDir();
        scratch.Write("OuterTests.cs",
            """
            [Collection("ProcessGlobals")]
            public class OuterTests
            {
                [Fact]
                public void Do_Thing() => Nested.Mutate();

                private sealed class Nested
                {
                    public static void Mutate() => Environment.SetEnvironmentVariable("HOME", null);
                }
            }
            """);

        offenders(scratch).ShouldBeEmpty();
    }

    [Fact]
    public void Scan_CollectionAttributeAndClassOnOneLine_LeakToNextClassIsStillReported()
    {
        using var scratch = new ScratchDir();
        scratch.Write("Mixed.cs",
            """
            [Collection("ProcessGlobals")] public class Covered { }

            [Collection("ProcessGlobals")] public class CoveredWithBody
            {
                public void Helper() { }
            }

            public class SneakyTests
            {
                [Fact]
                public void Do_Thing()
                {
                    Environment.SetEnvironmentVariable("SQUEEZR_HOME", null);
                }
            }
            """);

        offenders(scratch).Select(o => o.ClassName).ShouldBe(["SneakyTests"]);
    }

    [Fact]
    public void Scan_ProcessSpawningHelperWithoutTests_IsIgnored()
    {
        using var scratch = new ScratchDir();
        scratch.Write("CliTestProcess.cs",
            """
            public static class CliTestProcess
            {
                public static Process Start(string home) =>
                    Process.Start(new ProcessStartInfo("squeezr") { Environment["SQUEEZR_HOME"] = home });
            }
            """);

        offenders(scratch).ShouldBeEmpty();
    }

    private static IReadOnlyList<EnvMutationSweep.Offender> offenders(ScratchDir scratch) =>
        EnvMutationSweep.Scan(scratch.Root, ["ProcessGlobals"]);

    private static string Describe(IReadOnlyList<EnvMutationSweep.Offender> offenders) =>
        string.Join("; ", offenders.Select(o => $"{o.ClassName} ({o.RelativePath}: {string.Join(", ", o.Evidence)})"));

    private static string TestsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
            dir = dir.Parent;
        dir.ShouldNotBeNull();
        return Path.Combine(dir!.FullName, "tests");
    }

    private static IReadOnlyList<string> SerializedCollections(string testsRoot) =>
        Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(IsSourceFile)
            .Select(File.ReadAllText)
            .SelectMany(text => SerializedDefinition.Matches(text))
            .Where(match => match.Groups["body"].Value.Contains("DisableParallelization = true"))
            .Select(match => match.Groups["name"].Value)
            .ToList();

    private static bool IsSourceFile(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
        !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}");

    private sealed class ScratchDir : IDisposable
    {
        public ScratchDir()
        {
            Root = Directory.CreateTempSubdirectory("env-sweep-").FullName;
        }

        public string Root { get; }

        public void Write(string fileName, string content) =>
            File.WriteAllText(Path.Combine(Root, fileName), content);

        public void Dispose() => Directory.Delete(Root, true);
    }
}
