using System.Text.Json;
using Spectre.Console.Cli;
using Squeezr.Core.Storage;

namespace Squeezr.Cli.Commands;

public sealed class HookPreReadCommand : Command<HookPreReadCommand.Settings>
{
    private const string ReadToolName = "Read";
    private const string PreToolUseEvent = "PreToolUse";

    private const long LargeFileBytes = 256 * 1024;
    private const int WideRangeLines = 400;

    private const string NudgeText =
        "This workspace is indexed by squeezr. For a read this size, code_search or symbol_lookup can locate " +
        "the relevant lines, and expand_span(file, startLine, endLine) returns just that span from the live file. " +
        "If the file or repo is not in the index, a plain Read is correct.";

    public sealed class Settings : CommandSettings
    {
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        HookPreReadAdvice? advice = null;
        try
        {
            advice = Evaluate(Console.In.ReadToEnd());
        }
        catch (Exception)
        {
            // fail-open: the hook observes reads, it must never break one
        }

        if (advice is not null)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(advice));
        }

        return 0;
    }

    internal static HookPreReadAdvice? Evaluate(string payload)
    {
        HookPreReadInput? input;
        try
        {
            input = JsonSerializer.Deserialize<HookPreReadInput>(payload);
        }
        catch (JsonException)
        {
            return null;
        }

        if (!string.Equals(input?.ToolName, ReadToolName, StringComparison.Ordinal))
        {
            return null;
        }

        var filePath = input!.ToolInput?.FilePath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        return RequestsBulkRead(input.ToolInput!) && WorkspaceRegistry.FindContainingPath(filePath) is not null
            ? new HookPreReadAdvice(new HookPreReadAdviceOutput(PreToolUseEvent, NudgeText))
            : null;
    }

    private static bool RequestsBulkRead(HookPreReadToolInput toolInput)
    {
        if (toolInput.Limit is < WideRangeLines)
        {
            return false;
        }

        if (toolInput.Limit >= WideRangeLines)
        {
            return true;
        }

        var file = new FileInfo(toolInput.FilePath!);
        return file.Exists && file.Length >= LargeFileBytes;
    }
}
