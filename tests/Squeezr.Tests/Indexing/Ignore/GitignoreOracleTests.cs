using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing.Ignore;

namespace Squeezr.Tests.Indexing.Ignore;

// git is the oracle for every row below. Each answer was measured by materializing the path in a scratch
// repository and asking `git check-ignore -v` (git 2.53); the generator and its raw evidence are
// probe-t14/oracle-table.py and probe-t14/oracle-table.md, so nothing here is a guess about git.
// The built-in default list is emptied in these fixtures so only the .gitignore under test decides.
public sealed class GitignoreOracleTests : IDisposable
{
    private readonly string _root = TestDirs.NewTempDir();

    [Theory]
    [InlineData("[Dd]ebug/", "Debug", true, true)]
    [InlineData("[Dd]ebug/", "Debug/app.dll", false, true)]
    [InlineData("[Dd]ebug/", "debug/app.dll", false, true)]
    [InlineData("[Dd]ebug/", "src/Debug/app.dll", false, true)]
    [InlineData("[Dd]ebug/", "Debugx/app.dll", false, false)]
    [InlineData("[Dd]ebugPublic/", "DebugPublic/app.cs", false, true)]
    [InlineData("[Dd]ebugPublic/", "Debug/app.cs", false, false)]
    [InlineData("[Rr]elease/", "Release/app.dll", false, true)]
    [InlineData("[Rr]elease/", "release/app.dll", false, true)]
    [InlineData("[Rr]elease/", "Releases/app.dll", false, false)]
    [InlineData("[Rr]eleases/", "Releases/app.dll", false, true)]
    [InlineData("[Rr]eleases/", "Release/app.dll", false, false)]
    [InlineData("[Ww][Ii][Nn]32/", "Win32/app.exe", false, true)]
    [InlineData("[Ww][Ii][Nn]32/", "wIn32/app.exe", false, true)]
    [InlineData("[Ww][Ii][Nn]32/", "Win64/app.exe", false, false)]
    [InlineData("[Aa][Rr][Mm]/", "ARM/app.dll", false, true)]
    [InlineData("[Aa][Rr][Mm]/", "ARM64/app.dll", false, false)]
    [InlineData("[Aa][Rr][Mm]64/", "ARM64/app.dll", false, true)]
    [InlineData("[Aa][Rr][Mm]64/", "ARM/app.dll", false, false)]
    [InlineData("[Bb]in/", "bin/app.dll", false, true)]
    [InlineData("[Bb]in/", "src/Bin/app.dll", false, true)]
    [InlineData("[Bb]in/", "Bin/Debug/app.dll", false, true)]
    [InlineData("[Bb]in/", "binx/app.dll", false, false)]
    [InlineData("[Oo]bj/", "obj/project.assets.json", false, true)]
    [InlineData("[Oo]bj/", "object/keep.cs", false, false)]
    [InlineData("[Ll]og/", "log/app.txt", false, true)]
    [InlineData("[Ll]og/", "Log", true, true)]
    [InlineData("[Ll]og/", "Logs/app.txt", false, false)]
    [InlineData("[Ll]ogs/", "Logs/app.txt", false, true)]
    [InlineData("[Ll]ogs/", "Log/app.txt", false, false)]
    [InlineData("Generated\\ Files/", "Generated Files/model.cs", false, true)]
    [InlineData("Generated\\ Files/", "src/Generated Files/nested/model.cs", false, true)]
    [InlineData("Generated\\ Files/", "GeneratedFiles/model.cs", false, false)]
    [InlineData("Generated\\ Files/", "Generated  Files/model.cs", false, false)]
    [InlineData("[Tt]est[Rr]esult*/", "TestResults/run.trx", false, true)]
    [InlineData("[Tt]est[Rr]esult*/", "testResultZ/run.trx", false, true)]
    [InlineData("[Tt]est[Rr]esult*/", "TestResult/run.trx", false, true)]
    [InlineData("[Tt]est[Rr]esult*/", "Tests/run.trx", false, false)]
    [InlineData("[Bb]uild[Ll]og.*", "BuildLog.txt", false, true)]
    [InlineData("[Bb]uild[Ll]og.*", "buildlog.htm", false, true)]
    [InlineData("[Bb]uild[Ll]og.*", "BuildLog", false, false)]
    [InlineData("[Bb]uild[Ll]og.*", "BuildLogs.txt", false, false)]
    [InlineData("[Dd]ebugPS/", "DebugPS/app.dll", false, true)]
    [InlineData("[Dd]ebugPS/", "Debug/app.dll", false, false)]
    [InlineData("[Rr]eleasePS/", "ReleasePS/app.dll", false, true)]
    [InlineData("[Rr]eleasePS/", "Release/app.dll", false, false)]
    [InlineData("*.[Rr]e[Ss]harper", "foo.reSharper", false, true)]
    [InlineData("*.[Rr]e[Ss]harper", ".ReSharper", false, true)]
    [InlineData("*.[Rr]e[Ss]harper", "src/project.resharper", false, true)]
    [InlineData("*.[Rr]e[Ss]harper", "foo.resharperx", false, false)]
    [InlineData("*.[Rr]e[Ss]harper", "foo.xReSharper", false, false)]
    [InlineData("[Ee]xpress/", "Express/app.txt", false, true)]
    [InlineData("[Ee]xpress/", "expresso/app.txt", false, false)]
    [InlineData("*.[Pp]ublish.xml", "Web.Publish.xml", false, true)]
    [InlineData("*.[Pp]ublish.xml", "web.publish.xmll", false, false)]
    [InlineData("**/[Pp]ackages/*", "Packages/newtonsoft.json.dll", false, true)]
    [InlineData("**/[Pp]ackages/*", "src/Packages/a/b.dll", false, true)]
    [InlineData("**/[Pp]ackages/*", "packagesx/keep.dll", false, false)]
    [InlineData("**/[Pp]ackages/*\n!**/[Pp]ackages/build/", "Packages/build/keep.dll", false, false)]
    [InlineData("**/[Pp]ackages/*\n!**/[Pp]ackages/build/", "Packages/build", true, false)]
    [InlineData("**/[Pp]ackages/*\n!**/[Pp]ackages/build/", "Packages/a.dll", false, true)]
    [InlineData("*.[Cc]ache", "session.cache", false, true)]
    [InlineData("*.[Cc]ache", "src/token.Cache", false, true)]
    [InlineData("*.[Cc]ache", "session.cache2", false, false)]
    [InlineData("*.[Cc]ache", "cache", false, false)]
    [InlineData("*.[Cc]ache\n!?*.[Cc]ache/", "session.cache", false, true)]
    [InlineData("*.[Cc]ache\n!?*.[Cc]ache/", "session.cache", true, false)]
    [InlineData("*.[Cc]ache\n!?*.[Cc]ache/", "session.cache/inner.txt", false, false)]
    [InlineData("*- [Bb]ackup.rdl", "report - Backup.rdl", false, true)]
    [InlineData("*- [Bb]ackup.rdl", "report - backup.rdl", false, true)]
    [InlineData("*- [Bb]ackup.rdl", "report -Backup.rdl", false, false)]
    [InlineData("*- [Bb]ackup ([0-9]).rdl", "report - Backup (1).rdl", false, true)]
    [InlineData("*- [Bb]ackup ([0-9]).rdl", "report - Backup (12).rdl", false, false)]
    [InlineData("*- [Bb]ackup ([0-9]).rdl", "report - Backup (a).rdl", false, false)]
    [InlineData("*- [Bb]ackup ([0-9][0-9]).rdl", "report - Backup (12).rdl", false, true)]
    [InlineData("*- [Bb]ackup ([0-9][0-9]).rdl", "report - Backup (1).rdl", false, false)]
    [InlineData("[Dd]esktop.ini", "Desktop.ini", false, true)]
    [InlineData("[Dd]esktop.ini", "src/Desktop.ini", false, true)]
    [InlineData("[Dd]esktop.ini", "desktop.ini", false, true)]
    [InlineData("[Dd]esktop.ini", "Thumbs.db.ini", false, false)]
    [InlineData("[a-c]x", "bx", false, true)]
    [InlineData("[a-c]x", "dx", false, false)]
    [InlineData("[!x]y", "zy", false, true)]
    [InlineData("[!x]y", "xy", false, false)]
    [InlineData("[^x]y", "zy", false, true)]
    [InlineData("[^x]y", "xy", false, false)]
    [InlineData("[]a]z", "]z", false, true)]
    [InlineData("[]a]z", "az", false, true)]
    [InlineData("[]a]z", "bz", false, false)]
    [InlineData("[a-]b", "-b", false, true)]
    [InlineData("[a-]b", "ab", false, true)]
    [InlineData("[a-]b", "bb", false, false)]
    [InlineData("[--0]Q", ".Q", false, true)]
    [InlineData("[--0]Q", "-Q", false, true)]
    [InlineData("[--0]Q", "0Q", false, true)]
    [InlineData("[--0]Q", "aQ", false, false)]
    [InlineData("[.*]Q", ".Q", false, true)]
    [InlineData("[.*]Q", "*Q", false, true)]
    [InlineData("[.*]Q", "aQ", false, false)]
    [InlineData("[a^]b", "ab", false, true)]
    [InlineData("[a^]b", "^b", false, true)]
    [InlineData("[!^a]b", "ab", false, false)]
    [InlineData("[!^a]b", "^b", false, false)]
    [InlineData("[!^a]b", "bb", false, true)]
    [InlineData("[\\[-\\]]b", "[b", false, true)]
    [InlineData("[\\[-\\]]b", "]b", false, true)]
    [InlineData("[\\[-\\]]b", "ab", false, false)]
    [InlineData("[z-a]b", "zb", false, true)]
    [InlineData("[z-a]b", "ab", false, false)]
    [InlineData("[z-a]b", "-b", false, false)]
    [InlineData("[z-a]b", "{b", false, false)]
    [InlineData("[9-1]Q", "9Q", false, true)]
    [InlineData("[9-1]Q", "1Q", false, false)]
    [InlineData("[9-1]Q", "5Q", false, false)]
    [InlineData("[A-Z]*.cs", "Foo.cs", false, true)]
    [InlineData("[a]Q", "aQ", false, true)]
    [InlineData("a\\*b", "a*b", false, true)]
    [InlineData("a\\*b", "axb", false, false)]
    [InlineData("a\\[b", "a[b", false, true)]
    [InlineData("a\\?b", "a?b", false, true)]
    [InlineData("\\#hash", "#hash", false, true)]
    [InlineData("\\!bang", "!bang", false, true)]
    [InlineData("Generated\\ Files/x", "Generated Files/x", false, true)]
    [InlineData("Generated\\ Files/x", "Generated Files/y", false, false)]
    [InlineData("Generated\\ Files/x", "Generated Files/x/y", false, true)]
    [InlineData("foo\\", "foo\\", false, false)]
    [InlineData("foo\\", "foo", false, false)]
    [InlineData("[bracket", "[bracket", false, false)]
    [InlineData("[bracket", "b", false, false)]
    [InlineData("x[[y", "x[y", false, false)]
    [InlineData("x[[y", "xy", false, false)]
    [InlineData("[[:alpha:]]", "1", false, false)]
    [InlineData("a\\/b", "ab", false, false)]
    [InlineData("[a/]x", "ax", false, true)]
    [InlineData("[a/]x", "bracket/ax", false, false)]
    [InlineData("[!a/]z", "bz", false, true)]
    [InlineData("[.-0]x", ".x", false, true)]
    [InlineData("[.-0]x", "0x", false, true)]
    [InlineData("[.-0]x", "-x", false, false)]
    [InlineData("[.-/]x", ".x", false, true)]
    [InlineData("[[]", "[", false, true)]
    [InlineData("[[]", "a", false, false)]
    public void Decision_AgreeWithGitCheckIgnore(
        string gitignore, string path, bool isDirectory, bool gitIgnored)
    {
        var decision = Decide(gitignore, path, isDirectory);

        decision.Excluded.ShouldBe(
            gitIgnored,
            $"'{gitignore.Replace('\n', ' ')}' vs '{path}' (dir={isDirectory}); git check-ignore said {gitIgnored}");
    }

    // Measured disagreements, pinned instead of hidden: git answers the other way in every row here, each
    // one the consequence of a deliberate squeezr choice. Case folding runs both ways in the matcher, so a
    // class git reads case-sensitively is a superset for us; the path side folds '\' into '/' before any
    // pattern is compared; POSIX classes and the escaped separator stay residual forms — skipped, warned,
    // and never ignored.
    [Theory]
    [InlineData("[Dd]ebug/", "DEBUG/app.dll", false)]
    [InlineData("[A-Z]*.cs", "foo.cs", false)]
    [InlineData("*.[Cc]ache", "FOO.CACHE", false)]
    [InlineData("a\\\\b", "a\\b", true)]
    [InlineData("[[:alpha:]]", "b", true)]
    [InlineData("[[:digit:]]", "5", true)]
    [InlineData("a\\/b", "a/b", true)]
    public void Decision_KnownDivergencesFromGit(string gitignore, string path, bool gitIgnored)
    {
        var decision = Decide(gitignore, path, isDirectory: false);

        decision.Excluded.ShouldNotBe(
            gitIgnored,
            $"'{gitignore}' vs '{path}': git says {gitIgnored}, squeezr says {!gitIgnored} by documented design");
    }

    [Fact]
    public void Decision_ResidualFormsAreSkippedAndWarn()
    {
        var warnings = new List<string>();
        var ruleset = DecideRuleset("[[:alpha:]]x\na\\/b\nunterminated[abc\ndangling\\\n", warnings);

        ruleset.Decision("bx", false).Excluded.ShouldBeFalse();
        ruleset.Decision("a/b", false).Excluded.ShouldBeFalse();
        ruleset.Decision("unterminated[abc", false).Excluded.ShouldBeFalse();

        warnings.Count.ShouldBe(4);
        warnings.ShouldAllBe(w => w.Contains("unsupported ignore pattern", StringComparison.Ordinal));
    }

    private IgnoreDecision Decide(string gitignore, string path, bool isDirectory) =>
        DecideRuleset(gitignore, []).Decision(path, isDirectory);

    // The sink is not decoration: IgnoreRuleset drops an unsupported pattern only when it can warn about
    // it, and every caller in the index path passes one. Callers that don't (StaleFileDetector,
    // WorkspaceWatcher) keep the pattern and let it match literally — pinned as an obstacle on this task.
    private IgnoreRuleset DecideRuleset(string gitignore, List<string>? warnings = null)
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), gitignore + "\n");

        var config = SqueezrConfig.CreateDefault() with
        {
            Indexing = SqueezrConfig.CreateDefault().Indexing with
            {
                Ignore = [],
                RespectGitignore = true
            }
        };

        warnings ??= [];
        return IgnoreRuleset.For(_root, config, warnings.Add);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
