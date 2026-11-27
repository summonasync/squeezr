using System.Text.Json.Serialization;
using Squeezr.Core.Workspace;
using Squeezr.Lite.Mcp;

namespace Squeezr.Lite.Output;

// The one wording of "which tree this answer describes", shared by the lite verbs and the MCP envelope.
internal static class AnswerSubjectNotice
{
    internal static AnswerSubjectBody? BodyOf(AnswerSubject? subject) =>
        subject is null
            ? null
            : new(
                SessionLocation.Display(subject.RootPath),
                subject.Branch,
                subject.StoreKey);

    internal static string? CrossTreeWarningOf(AnswerSubject? subject) =>
        subject is { CrossTree: true, CallerRootPath: { } caller } cross
            ? $"cross-tree: this query reads "
              + $"'{SessionLocation.Display(cross.RootPath)}', not '{SessionLocation.Display(caller)}', "
              + "the checkout this query was asked in — paths and line numbers it returns are the read store's"
            : null;

    internal static void Report(AnswerSubject? subject)
    {
        if (subject is null)
        {
            return;
        }

        Console.Error.WriteLine(
            $"subject: {SessionLocation.Display(subject.RootPath)}"
            + (subject.Branch is { } branch ? $" (branch {branch})" : " (branch unknown)")
            + (subject.StoreKey is { } key ? $" store {key}" : " no store key"));

        if (CrossTreeWarningOf(subject) is { } warning)
        {
            Console.Error.WriteLine($"warning: {warning}");
        }
    }
}

public sealed record AnswerSubjectBody(
    [property: JsonPropertyName("rootPath")] string RootPath,
    [property: JsonPropertyName("branch"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Branch,
    [property: JsonPropertyName("storeKey"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? StoreKey);
