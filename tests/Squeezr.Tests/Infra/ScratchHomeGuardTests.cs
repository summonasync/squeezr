using System.Diagnostics;
using System.Text.RegularExpressions;
using Shouldly;

namespace Squeezr.Tests.Infra;

public sealed partial class ScratchHomeGuardTests
{
    private static string ScriptsDir => Path.Combine(FindRepoRoot(), "scripts");

    // The 2026-10-03 pile-up came from a flow that registered temp roots in the live registry. No
    // shipped script creates workspaces today; if one ever does, this gate forces the author to pin
    // SQUEEZR_HOME right where the workspace is created.
    [Fact]
    public void ShippedScripts_CreateNoWorkspaces()
    {
        var offenders = Directory
            .EnumerateFiles(ScriptsDir, "*.*")
            .Where(file => file.EndsWith(".sh", StringComparison.Ordinal) || file.EndsWith(".ps1", StringComparison.Ordinal))
            .Select(file => (File: file, Lines: OffendingLines(File.ReadAllText(file))))
            .Where(pair => pair.Lines.Count > 0)
            .ToList();

        offenders.ShouldBeEmpty(
            "a script started creating workspaces without a SQUEEZR_HOME pin: " +
            string.Join("; ", offenders.Select(pair =>
                $"{Path.GetFileName(pair.File)} → {string.Join(" | ", pair.Lines)}")));
    }

    // Positive + negative controls: the gate must stay blind to flows that pin the home and catch
    // flows that register workspaces against the default one. The gate is textual and single-line: an
    // echo naming the flow counts as a hit, a flow split over a continuation does not.
    [Fact]
    public void Gate_CatchesUnpinnedWorkspaceCreation_AndPassesPinnedForms()
    {
        foreach (var unpinned in new[]
                 {
                     "squeezr init",
                     "squeezr add ./probe --name probe",
                     "run squeezr init --name t",
                     "& squeezr add $root",
                     "echo squeezr init is documented somewhere",
                     "# honor SQUEEZR_HOME someday\n\n\n\n\nsqueezr init",
                 })
        {
            OffendingLines(unpinned).ShouldNotBeEmpty(unpinned);
        }

        foreach (var pinned in new[]
                 {
                     "source \"$(dirname \"$0\")/scratch-home.sh\"\nsqueezr init",
                     "export SQUEEZR_HOME=\"$(mktemp -d)\"\nsqueezr init",
                     "$env:SQUEEZR_HOME = Join-Path ([System.IO.Path]::GetTempPath()) 'squeezr-scratch'\nsqueezr add ./probe",
                     "squeezr index",
                     "squeezr drop --store-key demo-1a2b3c4d",
                     "export SQUEEZR_HOME=\"$(mktemp -d)\"\n\ncd repo\nsqueezr init",
                 })
        {
            OffendingLines(pinned).ShouldBeEmpty(pinned);
        }
    }

    // Saying the token is not pinning it: a comment or an echo about SQUEEZR_HOME used to exempt every
    // unpinned flow near it, which is the fail-open the 2026-10-19 review caught.
    [Fact]
    public void Gate_DoesNotExemptOnMention_PinFormIsRequired()
    {
        foreach (var mentionOnly in new[]
                 {
                     "# note: this reads SQUEEZR_HOME for the models dir\n\n\nsqueezr init /tmp/x",
                     "echo \"see docs about SQUEEZR_HOME\"\n\n\nsqueezr add ./probe",
                     "squeezr init # SQUEEZR_HOME set by parent shell",
                 })
        {
            OffendingLines(mentionOnly).ShouldNotBeEmpty(mentionOnly);
        }
    }

    // The window is protected from both sides: the same form pin is a pass three lines above the flow
    // and an offender four lines above. Without the second case, widening PinWindowLines keeps green.
    [Fact]
    public void Gate_PinWindowIsThreeLines()
    {
        var insideWindow = "export SQUEEZR_HOME=\"$(mktemp -d)\"\n\ncd repo\nsqueezr init";
        var outsideWindow = "export SQUEEZR_HOME=\"$(mktemp -d)\"\n\n\ncd repo\nsqueezr init";

        OffendingLines(insideWindow).ShouldBeEmpty(insideWindow);
        OffendingLines(outsideWindow).ShouldNotBeEmpty(outsideWindow);
    }

    [Fact]
    public void ScratchHomeHelper_CarriesThePinTheFailLoudCheckAndTheTrapChain()
    {
        var helper = File.ReadAllText(Path.Combine(ScriptsDir, "scratch-home.sh"));

        helper.ShouldContain("mktemp -d");
        helper.ShouldContain("export SQUEEZR_HOME");
        helper.ShouldContain("--assert");
        helper.ShouldContain("squeezr init");

        // Reverting to a bare `trap ... EXIT` silently drops the caller's own cleanup; the live test
        // below is what proves the chain, this pin only keeps the mechanism visible in the file.
        helper.ShouldContain("trap -p EXIT");
    }

    [LinuxFact]
    public void ScratchHomeHelper_AssertMode_RefusesUnsetAndLiveHome_PassesAPinnedOne()
    {
        var helper = Path.Combine(ScriptsDir, "scratch-home.sh");
        var envRoot = TestDirs.NewTempDir();
        // the run root is a fixture, not the ambient directory: the test host runs out of the build
        // output, which any earlier flow can leave holding a legacy store the guard is right to refuse
        var runRoot = TestDirs.NewTempDir();
        try
        {
            var unset = RunAssert(helper, squeezrHome: null, envRoot, runRoot);
            unset.ExitCode.ShouldBe(1, unset.StdErr);
            unset.StdErr.ShouldContain("Fix:");

            Directory.CreateDirectory(Path.Combine(envRoot, ".squeezr"));
            var live = RunAssert(helper, Path.Combine(envRoot, ".squeezr"), envRoot, runRoot);
            live.ExitCode.ShouldBe(1, live.StdErr);
            live.StdErr.ShouldContain("live home");

            Directory.CreateDirectory(Path.Combine(envRoot, "pinned"));
            var pinned = RunAssert(helper, Path.Combine(envRoot, "pinned"), envRoot, runRoot);
            pinned.ExitCode.ShouldBe(0, pinned.StdErr);
        }
        finally
        {
            Delete(envRoot);
            Delete(runRoot);
        }
    }

    // The vector that actually destroyed the live store on 2026-10-19: a pinned temp home plus a run
    // sitting under a home that holds .squeezr. The walk up from the cwd turns the live store into
    // "legacy content" and relocates it into the pinned home, which the EXIT cleanup then deletes.
    [LinuxFact]
    public void ScratchHomeHelper_AssertMode_RefusesWhenCwdSitsUnderTheLiveHome()
    {
        var helper = Path.Combine(ScriptsDir, "scratch-home.sh");
        var envRoot = TestDirs.NewTempDir();
        var pinnedHome = TestDirs.NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(envRoot, ".squeezr", "workspaces"));
            File.WriteAllText(Path.Combine(envRoot, ".squeezr", "workspaces.json"), "{\"rows\":[]}");

            var startInfo = NewBash(helper, "--assert");
            startInfo.WorkingDirectory = envRoot;
            startInfo.Environment["HOME"] = envRoot;
            startInfo.Environment["SQUEEZR_HOME"] = pinnedHome;
            startInfo.Environment.Remove("SQUEEZR_SCRATCH_HOME");
            startInfo.Environment.Remove("CLAUDE_PROJECT_DIR");

            var run = Start(startInfo);

            run.ExitCode.ShouldBe(1, run.StdErr);
            run.StdErr.ShouldContain("refusing");
        }
        finally
        {
            Delete(envRoot);
            Delete(pinnedHome);
        }
    }

    // Sourcing into a harness means the caller keeps its own EXIT trap, with the caller's positional
    // parameters, and still sees the scratch home. A bare `trap ... EXIT` or a function-wrapped eval
    // loses both, and the suite would not notice without this test. The trap body carries an inner single
    // quote on purpose: that is the shape a decode which strips the outer quotes by hand destroys.
    [LinuxFact]
    public void ScratchHomeHelper_ChainsCallerTrap_AndRemovesItsOwnScratchHome()
    {
        var helper = Path.Combine(ScriptsDir, "scratch-home.sh");
        var envRoot = TestDirs.NewTempDir();
        var workRoot = TestDirs.NewTempDir();
        var log = Path.Combine(envRoot, "caller.log");
        try
        {
            File.WriteAllText(Path.Combine(envRoot, "caller.sh"),
                "#!/usr/bin/env bash\nset -u\n" +
                "trap 'echo \"caller-trap:$1\" >> \"$LOG\"; " +
                "echo \"quote: it'\\''s fine\" >> \"$LOG\"; " +
                "echo \"dir-state=$([ -d \"${SQUEEZR_SCRATCH_HOME:-}\" ] && echo present || echo gone)\" >> \"$LOG\"; " +
                "export SQUEEZR_SCRATCH_HOME=\"$VICTIM\"' EXIT\n" +
                $"source \"{helper}\"\n" +
                "echo \"pin=$SQUEEZR_HOME\" >> \"$LOG\"\n" +
                "VICTIM=\"$(mktemp -d \"$VICTIM_ROOT/squeezr-scratch-home.XXXXXX\")\"\n" +
                "export VICTIM\n" +
                "echo \"victim=$VICTIM\" >> \"$LOG\"\n");

            var startInfo = new ProcessStartInfo("bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workRoot,
            };
            startInfo.ArgumentList.Add(Path.Combine(envRoot, "caller.sh"));
            startInfo.ArgumentList.Add("alpha");
            startInfo.Environment["HOME"] = envRoot;
            startInfo.Environment["LOG"] = log;
            startInfo.Environment["VICTIM_ROOT"] = workRoot;
            startInfo.Environment.Remove("SQUEEZR_HOME");
            startInfo.Environment.Remove("SQUEEZR_SCRATCH_HOME");
            startInfo.Environment.Remove("CLAUDE_PROJECT_DIR");

            var run = Start(startInfo);

            run.ExitCode.ShouldBe(0, run.StdErr);
            run.StdErr.ShouldBe(string.Empty);
            var lines = File.ReadAllLines(log);
            lines.ShouldContain(l => l == "caller-trap:alpha", "the chained caller trap lost the caller's positional parameters");
            lines.ShouldContain(l => l == "quote: it's fine", "the chained caller trap lost a handler body holding an inner single quote");
            lines.ShouldContain(l => l == "dir-state=present", "the scratch home was removed before the caller trap could use it");

            var pinned = lines.Single(l => l.StartsWith("pin=", StringComparison.Ordinal))["pin=".Length..];
            Directory.Exists(pinned).ShouldBe(false, "the helper left its scratch home behind");

            // A victim spelled exactly like a scratch home, aimed at by a caller that repoints the
            // exported variable: only the path frozen at pin time may ever be removed.
            var victim = lines.Single(l => l.StartsWith("victim=", StringComparison.Ordinal))["victim=".Length..];
            victim.ShouldMatch(@"squeezr-scratch-home\.[A-Za-z0-9]{6}$");
            Directory.Exists(victim).ShouldBe(true, "the helper removed a scratch-home-shaped directory it never created");
        }
        finally
        {
            Delete(envRoot);
            Delete(workRoot);
        }
    }

    // The two branches that let a harness keep running unpinned are the ones that destroyed the live
    // store, and assert mode never reaches them because it only ever executes the file: a refusal in
    // source mode has to stop the caller, and a HOME the guard cannot read is a refusal, not a pass.
    [LinuxFact]
    public void ScratchHomeHelper_SourceMode_StopsAHarnessItCannotVouchFor()
    {
        var helper = Path.Combine(ScriptsDir, "scratch-home.sh");
        var envRoot = TestDirs.NewTempDir();
        var storeRoot = TestDirs.NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(storeRoot, ".squeezr", "workspaces"));
            var canary = Path.Combine(storeRoot, ".squeezr", "canary.txt");
            File.WriteAllText(canary, "canary-content");
            var runRoot = Path.Combine(storeRoot, "work", "probe");
            Directory.CreateDirectory(runRoot);
            Directory.CreateDirectory(Path.Combine(envRoot, "home"));
            Directory.CreateDirectory(Path.Combine(envRoot, "work"));
            File.WriteAllText(Path.Combine(envRoot, "harness.sh"),
                "#!/usr/bin/env bash\nset -u\n" +
                $"source \"{helper}\"\n" +
                "echo \"BODY-RAN pin=$SQUEEZR_HOME\"\n");

            var harness = Path.Combine(envRoot, "harness.sh");

            // the store sits on the ancestor chain of the run root and nowhere near HOME — the shape a
            // $HOME comparison cannot see, and the weapon the 2026-10-19 cleanup fired with
            var ancestor = Start(NewHarness(harness, runRoot, Path.Combine(envRoot, "home")));
            ancestor.ExitCode.ShouldBe(1, ancestor.StdErr);
            ancestor.StdErr.ShouldContain("refusing");
            ancestor.StdErr.ShouldContain("Fix:");
            ancestor.StdOut.ShouldNotContain("BODY-RAN");
            File.Exists(canary).ShouldBe(true, "the refusal disturbed the store it refused to run under");

            // HOME unset leaves the guard no central store to name: refuse loudly instead of dying on an
            // unbound variable or pinning blind
            var blind = Start(NewHarness(harness, Path.Combine(envRoot, "work"), home: null));
            blind.ExitCode.ShouldBe(1, blind.StdErr);
            blind.StdErr.ShouldContain("refusing");
            blind.StdErr.ShouldContain("Fix:");
            blind.StdOut.ShouldNotContain("BODY-RAN");
        }
        finally
        {
            Delete(envRoot);
            Delete(storeRoot);
        }
    }

    // Resolve climbs a second time, from CLAUDE_PROJECT_DIR, when the cwd chain names no workspace. That is
    // the shape a hook or an `mcp install --project` session actually runs in: the project directory sits
    // under a home holding the live store while the run root itself and every ancestor above it stay clean.
    // A guard mirroring only the first climb certifies that combination as legal, pins, and then deletes
    // what the product moved into the pin.
    [LinuxFact]
    public void ScratchHomeHelper_BothModes_RefuseAStoreOnTheClaudeProjectDirChain()
    {
        var helper = Path.Combine(ScriptsDir, "scratch-home.sh");
        var envRoot = TestDirs.NewTempDir();
        var pinnedHome = TestDirs.NewTempDir();
        try
        {
            var liveHome = Path.Combine(envRoot, "home");
            Directory.CreateDirectory(Path.Combine(liveHome, ".squeezr", "workspaces"));
            var canary = Path.Combine(liveHome, ".squeezr", "canary.txt");
            File.WriteAllText(canary, "canary-content");
            var projectDir = Path.Combine(liveHome, "project");
            Directory.CreateDirectory(projectDir);

            var runRoot = Path.Combine(envRoot, "work");
            Directory.CreateDirectory(runRoot);
            File.WriteAllText(Path.Combine(envRoot, "harness.sh"),
                "#!/usr/bin/env bash\nset -u\n" +
                $"source \"{helper}\"\n" +
                "echo \"BODY-RAN pin=$SQUEEZR_HOME\"\n");
            var harness = Path.Combine(envRoot, "harness.sh");

            var assert = RunAssert(helper, pinnedHome, liveHome, runRoot, claudeProjectDir: projectDir);
            assert.ExitCode.ShouldBe(1, assert.StdErr);
            assert.StdErr.ShouldContain("refusing");
            assert.StdErr.ShouldContain("(CLAUDE_PROJECT_DIR) sits under a directory holding a live store");
            assert.StdErr.ShouldContain("Fix:");
            File.Exists(canary).ShouldBe(true, "the refusal disturbed the store it refused to run under");

            var sourced = Start(NewHarness(harness, runRoot, liveHome, claudeProjectDir: projectDir));
            sourced.ExitCode.ShouldBe(1, sourced.StdErr);
            sourced.StdErr.ShouldContain("(CLAUDE_PROJECT_DIR) sits under a directory holding a live store");
            sourced.StdOut.ShouldNotContain("BODY-RAN");
            File.Exists(canary).ShouldBe(true, "source mode pinned and carried on over the project directory");
        }
        finally
        {
            Delete(envRoot);
            Delete(pinnedHome);
        }
    }

    [GeneratedRegex(@"^[^#]*\bsqueezr\s+(?:init|add)\b")]
    private static partial Regex WorkspaceCreatingFlow();

    [GeneratedRegex(@"SQUEEZR_HOME\s*=|SQUEEZR_SCRATCH_HOME\s*=|source\s+.*scratch-home\.sh")]
    private static partial Regex PinForm();

    private const int PinWindowLines = 3;

    private static List<string> OffendingLines(string content)
    {
        var lines = content.Split('\n');
        var offending = new List<string>();
        for (var index = 0; index < lines.Length; index++)
        {
            if (WorkspaceCreatingFlow().IsMatch(lines[index]) && !PinnedNear(lines, index))
            {
                offending.Add($"line {index + 1}: '{lines[index].Trim()}' needs an SQUEEZR_HOME assignment or a " +
                    $"scratch-home.sh source on that line or within {PinWindowLines} above it");
            }
        }

        return offending;
    }

    // A file-wide or mention-wide lookup lets one stray "honor SQUEEZR_HOME" comment exempt every
    // unpinned flow in the script; only a pin where the workspace is created counts. The window counts
    // physical lines, not non-blank ones: blanks pushing a pin out of range fail closed (more
    // offenders), while a non-blank window would let an author park a pin arbitrarily far away.
    private static bool PinnedNear(string[] lines, int flowLineIndex)
    {
        for (var index = Math.Max(0, flowLineIndex - PinWindowLines); index <= flowLineIndex; index++)
        {
            if (PinForm().IsMatch(lines[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunAssert(
        string helper, string? squeezrHome, string homeRoot, string workingDirectory,
        string? claudeProjectDir = null)
    {
        var startInfo = NewBash(helper, "--assert");
        startInfo.WorkingDirectory = workingDirectory;
        startInfo.Environment["HOME"] = homeRoot;
        if (claudeProjectDir is null)
        {
            startInfo.Environment.Remove("CLAUDE_PROJECT_DIR");
        }
        else
        {
            startInfo.Environment["CLAUDE_PROJECT_DIR"] = claudeProjectDir;
        }

        if (squeezrHome is null)
        {
            startInfo.Environment.Remove("SQUEEZR_HOME");
        }
        else
        {
            startInfo.Environment["SQUEEZR_HOME"] = squeezrHome;
        }

        startInfo.Environment.Remove("SQUEEZR_SCRATCH_HOME");
        return Start(startInfo);
    }

    private static ProcessStartInfo NewBash(string helper, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(helper);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    // A sourced harness runs in the working directory the store walk starts from, so the case picks both
    // the HOME the guard reads and the root it climbs from. home: null means HOME is not passed at all.
    // claudeProjectDir is the second start of the walk: set where it is the subject, cleared everywhere
    // else so an outer session's project directory cannot leak into a case that does not probe it.
    private static ProcessStartInfo NewHarness(
        string harness, string workingDirectory, string? home, string? claudeProjectDir = null)
    {
        var startInfo = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        startInfo.ArgumentList.Add(harness);
        if (home is not null)
        {
            startInfo.Environment["HOME"] = home;
        }
        else
        {
            startInfo.Environment.Remove("HOME");
        }

        if (claudeProjectDir is null)
        {
            startInfo.Environment.Remove("CLAUDE_PROJECT_DIR");
        }
        else
        {
            startInfo.Environment["CLAUDE_PROJECT_DIR"] = claudeProjectDir;
        }

        startInfo.Environment.Remove("SQUEEZR_HOME");
        startInfo.Environment.Remove("SQUEEZR_SCRATCH_HOME");
        return startInfo;
    }

    private static (int ExitCode, string StdOut, string StdErr) Start(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    private static void Delete(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Repository root (Squeezr.slnx) not found above {AppContext.BaseDirectory}; " +
            "the scratch-home guard cannot run outside a checkout.");
    }
}
