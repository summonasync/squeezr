using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Infra;

namespace Squeezr.Tests.Cli;

// The rendering half of the SessionStart hook, measured in-process against the verb's wire shape. The pin that
// matters here is the escape contract: Qwen puts this text into the system prompt after replacing '<' and '>',
// so anything the builder passes through has to read correctly once that has happened.
public sealed class HookSessionStartContextTests
{
    [Fact]
    public void Build_RegisteredReport_AnswersWithTheNewStoreAndTheBranch()
    {
        var advice = HookSessionStartContext.Build(Run(Report("Registered", """
            "storeKey": "lane-a1b2c3d4",
            "branch": "feat/116-hook",
            "collection": "squeezr-a1b2c3d4e5f6"
            """)));

        advice.ShouldNotBeNull();
        var context = advice!.HookSpecificOutput.AdditionalContext;
        context.ShouldContain("feat/116-hook");
        context.ShouldContain("lane-a1b2c3d4");
        context.ShouldContain("squeezr-a1b2c3d4e5f6");
        context.ShouldContain("got its own index just now");
        context.ShouldContain("code_search");
        advice.HookSpecificOutput.HookEventName.ShouldBe("SessionStart");
    }

    [Fact]
    public void Build_AlreadyRegisteredReport_SaysNothingWasCreatedThisSession()
    {
        var context = ContextOf(Report("AlreadyRegistered", """
            "storeKey": "lane-a1b2c3d4",
            "branch": "feat/116-hook"
            """));

        context.ShouldContain("already has its own index");
        context.ShouldContain("nothing was created this session");
        // the collection line is the first session's news; repeating it every start would claim a pass that ran
        context.ShouldNotContain("collection");
    }

    [Fact]
    public void Build_StatesThatCreatedNothing_AnswerSilently()
    {
        foreach (var state in new[] { "ParentNotIndexed", "NoParentRootToName" })
        {
            HookSessionStartContext.Build(Run($$"""{"state":"{{state}}","rootPath":"/tmp/lane"}"""))
                .ShouldBeNull($"a session in a repo nobody indexed is not a session to lecture about {state}");
        }
    }

    [Fact]
    public void Build_ReportCarryingAngleBrackets_EmitsNone()
    {
        // 'cd <path> && squeezr serve' is the shape remedies take in this product; escaped it would read
        // '&lt;path&gt;' inside a first-turn instruction, so the builder replaces the brackets before emitting.
        var advice = HookSessionStartContext.Build(Run(Report("Registered", """
            "storeKey": "lane-a1b2c3d4",
            "branch": "feat/<angled>/116",
            "collection": "squeezr-[a1b2c3d4e5f6]"
            """)));

        var context = advice!.HookSpecificOutput.AdditionalContext;
        context.ShouldNotContain("<");
        context.ShouldNotContain(">");
        context.ShouldContain("feat/[angled]/116");
    }

    [Fact]
    public void Build_PendingReconcile_TellsTheSessionItsCoordinatesAreNotThisBranchsYet()
    {
        var context = ContextOf(Report("AlreadyRegistered", """
            "storeKey": "lane-a1b2c3d4",
            "branch": "feat/116-hook",
            "reconcilePending": true,
            "reconcile": { "state": "Pending", "reason": "2 chunk(s) here have never been embedded" }
            """));

        context.ShouldContain("not reconciled to this branch yet");
        context.ShouldContain("2 chunk(s) here have never been embedded");
        context.ShouldContain("squeezr status");
    }

    [Fact]
    public void Build_DeferredReconcile_SaysNoPassRanWithoutPromisingWork()
    {
        var context = ContextOf(Report("Registered", """
            "storeKey": "lane-a1b2c3d4",
            "branch": "feat/116-hook",
            "reconcile": { "state": "Deferred", "reason": "this pass created no store" }
            """));

        context.ShouldContain("No reconcile pass ran here");
        context.ShouldContain("this pass created no store");
    }

    [Fact]
    public void Build_StderrWarnings_TravelIntoTheContextOnceEach()
    {
        var context = ContextOf(Report("Registered", """
            "storeKey": "lane-a1b2c3d4",
            "branch": "feat/116-hook"
            """), """
            warning: the parent's index.lock looks stale — taken over
            warning: 482 rows have no vector this pass
            warning: second session of the same lane — nothing created
            """);

        context.ShouldContain("index.lock looks stale");
        context.ShouldContain("482 rows have no vector");
        context.ShouldContain("second session of the same lane");
    }

    [Fact]
    public void Build_WarningAlreadyToldByTheReconcileNotice_IsNotRepeated()
    {
        var reason = "2 chunk(s) here have never been embedded";
        var context = ContextOf(
            Report("AlreadyRegistered", $$"""
                "storeKey": "lane-a1b2c3d4",
                "branch": "feat/116-hook",
                "reconcile": { "state": "Pending", "reason": "{{reason}}" }
                """),
            $"warning: {reason}\n");

        CountOf(context, reason).ShouldBe(1, "the same sentence twice in a first-turn instruction is noise");
    }

    [Fact]
    public void Build_FourthWarningLine_IsNotAppended()
    {
        var context = ContextOf(Report("Registered", """
            "storeKey": "lane-a1b2c3d4",
            "branch": "feat/116-hook"
            """), """
            warning: first notice
            warning: second notice
            warning: third notice
            warning: fourth notice
            """);

        context.ShouldContain("third notice");
        context.ShouldNotContain("fourth notice");
    }

    [Fact]
    public void Build_NonJsonStdout_AnswersSilently()
    {
        HookSessionStartContext.Build(Run("index: nothing\n")).ShouldBeNull();
        HookSessionStartContext.Build(Run("{\"error\":")).ShouldBeNull();
    }

    [Fact]
    public void Build_ErrorReport_AnswersSilently()
    {
        HookSessionStartContext.Build(new ProcessRunResult(
            1,
            """{"error":{"code":"workspace_error","message":"not a git worktree"}}""",
            "error: not a git worktree")).ShouldBeNull();
    }

    [Fact]
    public void Build_OverlongNotices_AreCutAndTheWholeBlockStaysBounded()
    {
        var context = ContextOf(Report("Registered", $$"""
            "storeKey": "lane-{{new string('k', 300)}}",
            "branch": "feat/{{new string('x', 300)}}-116",
            "collection": "squeezr-{{new string('c', 300)}}"
            """), """
            warning: FIRST
            warning: SECOND
            warning: THIRD
            """.Replace("FIRST", new string('a', 300)).Replace("SECOND", new string('b', 300)).Replace("THIRD", new string('c', 300)));

        context.Length.ShouldBeLessThanOrEqualTo(1200);
        context.ShouldEndWith("...");
    }

    private static string ContextOf(string stdout, string stderr = "") =>
        HookSessionStartContext.Build(Run(stdout, stderr))!.HookSpecificOutput.AdditionalContext;

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal);
             at >= 0;
             at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string Report(string state, string fields) =>
        $$"""{"state":"{{state}}","rootPath":"/tmp/lane","workspaceName":"lane",{{fields}}}""";

    private static ProcessRunResult Run(string stdout, string stderr = "") => new(0, stdout, stderr);

    [Fact]
    public void Build_ReportIsReadThroughTheVerbWireNames()
    {
        // the builder deserializes the verb's own record: a renamed wire field would leave the hook silent,
        // so one test reads the report the way 'worktree init --json' really writes it
        using var document = JsonDocument.Parse(Report("Registered", """
            "storeKey": "lane-a1b2c3d4",
            "branch": "feat/116-hook"
            """));
        document.RootElement.GetProperty("state").GetString().ShouldBe("Registered");
        ContextOf(Report("Registered", """
            "storeKey": "lane-a1b2c3d4",
            "branch": "feat/116-hook"
            """)).ShouldContain("lane-a1b2c3d4");
    }
}
