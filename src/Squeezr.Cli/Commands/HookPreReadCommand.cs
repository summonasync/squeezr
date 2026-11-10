using System.Text.Json;
using Spectre.Console.Cli;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class HookPreReadCommand : Command<HookPreReadCommand.Settings>
{
    private const string ReadToolName = "Read";
    private const string PreToolUseEvent = "PreToolUse";

    private const long LargeFileBytes = 256 * 1024;
    private const int WideRangeLines = 400;
    private const int MaxStdinChars = 64 * 1024;

    public sealed class Settings : CommandSettings
    {
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var payload = ReadStdin();

        HookPreReadAdvice? advice = null;
        try
        {
            advice = Evaluate(payload);
        }
        catch (Exception)
        {
            // fail-open: the hook observes reads, it must never break one
        }

        RecordTelemetry(payload);

        if (advice is not null)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(advice));
        }

        return 0;
    }

    private static void RecordTelemetry(string payload)
    {
        try
        {
            var input = JsonSerializer.Deserialize<HookPreReadInput>(payload);
            if (input is null || !string.Equals(input.ToolName, ReadToolName, StringComparison.Ordinal))
            {
                return;
            }

            var filePath = input.ToolInput?.FilePath;
            if (string.IsNullOrWhiteSpace(filePath) ||
                WorkspaceRegistry.FindContainingPath(filePath) is not { } workspace)
            {
                return;
            }

            var redacted = HookSecretPaths.IsGuarded(Path.GetFullPath(filePath), workspace)
                ? QueryTelemetry.RedactedPlaceholder
                : filePath;
            QueryTelemetry.Record(
                workspace.RootPath,
                Guid.TryParse(input.SessionId, out var sessionId) ? sessionId : Guid.Empty,
                "pre-read",
                new { tool = input.ToolName, file = redacted },
                tokensUsed: 0,
                truncated: false,
                hits: 0,
                durationMs: 0,
                client: "hook",
                clientVersion: AppVersion.Current,
                // a pre-read nudge answers no budget question — it never read the file — so it records no state
                // at all: the mapped key stays absent instead of borrowing a "not mapped" it cannot mean
                mapped: null);
        }
        catch
        {
            // fail-open: the adoption signal is worth a line, never a broken hook
        }
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
            ? new HookPreReadAdvice(new HookPreReadAdviceOutput(PreToolUseEvent, RemedyText.BulkReadNudge))
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
