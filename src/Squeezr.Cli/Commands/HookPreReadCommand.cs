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
    private const int MaxStdinChars = 64 * 1024;

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
            advice = Evaluate(ReadStdin());
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

    private static string ReadStdin()
    {
        var buffer = new char[MaxStdinChars + 1];
        var read = 0;
        int chunk;
        while (read < buffer.Length && (chunk = Console.In.ReadBlock(buffer, read, buffer.Length - read)) > 0)
        {
            read += chunk;
        }

        // Oversized input is treated as unreadable rather than truncated: the hook
        // stays advisory and oversized payloads degrade to the fail-open empty path.
        if (read <= MaxStdinChars)
        {
            return new string(buffer, 0, read);
        }

        // Drain the rest into a fixed sink so a large writer never blocks on a full
        // pipe; allocation stays bounded by the buffers above.
        var sink = new char[4096];
        while (Console.In.ReadBlock(sink, 0, sink.Length) > 0)
        {
        }

        return string.Empty;
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

        if (WorkspaceRegistry.FindContainingPath(filePath) is not { } workspace)
        {
            return null;
        }

        return RequestsBulkRead(input.ToolInput!, filePath, workspace)
            ? new HookPreReadAdvice(new HookPreReadAdviceOutput(PreToolUseEvent, NudgeText))
            : null;
    }

    private static bool RequestsBulkRead(
        HookPreReadToolInput toolInput, string filePath, WorkspaceRegistryEntry workspace)
    {
        if (toolInput.Limit is < WideRangeLines)
        {
            return false;
        }

        if (toolInput.Limit >= WideRangeLines)
        {
            return true;
        }

        // the size branch is the one that proves a path exists, so the guard answers before the disk does
        if (HookSecretPaths.IsGuarded(Path.GetFullPath(filePath), workspace))
        {
            return false;
        }

        var file = new FileInfo(filePath);
        return file.Exists && file.Length >= LargeFileBytes;
    }
}
