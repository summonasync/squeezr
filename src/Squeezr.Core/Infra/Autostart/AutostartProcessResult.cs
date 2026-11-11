namespace Squeezr.Core.Infra.Autostart;

public sealed record AutostartProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public void EnsureSuccess(string fileName, string action)
    {
        if (ExitCode == 0)
        {
            return;
        }

        var detail = FirstLine(StandardError) ?? FirstLine(StandardOutput);
        throw new AutostartException(detail is { } text
            ? $"'{fileName}' failed to {action} (exit {ExitCode}): {text}"
            : $"'{fileName}' failed to {action} (exit {ExitCode}).");
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);

    // The tool's own words, for a caller that has to report an answer it could not act on: a probe
    // reads the response instead of throwing on it, so the text is the only thing left to carry.
    public static string AnswerLine(string text, string whenSilent) =>
        FirstLine(text) ?? whenSilent;
}
