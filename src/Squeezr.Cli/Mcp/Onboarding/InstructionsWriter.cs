using System.Text;

namespace Squeezr.Cli.Mcp.Onboarding;

public static class InstructionsWriter
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    public static void Upsert(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            Write(path, SqueezrInstructions.Block + "\n", hadBom: false);
            return;
        }

        var (content, hadBom) = Read(path);
        var start = content.IndexOf(SqueezrInstructions.StartMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            Write(path, InsertFresh(content), hadBom);
            return;
        }

        var end = content.IndexOf(SqueezrInstructions.EndMarker, start, StringComparison.Ordinal);
        var blockEnd = end < 0 ? content.Length : end + SqueezrInstructions.EndMarker.Length;
        Write(path, content[..start] + SqueezrInstructions.Block + content[blockEnd..], hadBom);
    }

    public static void Remove(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            return;
        }

        var (content, hadBom) = Read(path);
        var start = content.IndexOf(SqueezrInstructions.StartMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return;
        }

        var end = content.IndexOf(SqueezrInstructions.EndMarker, start, StringComparison.Ordinal);
        if (end < 0)
        {
            return;
        }

        var lineStart = content.LastIndexOf('\n', start) + 1;
        var cutEnd = content.Length;
        var newline = content.IndexOf('\n', end + SqueezrInstructions.EndMarker.Length);
        if (newline >= 0)
        {
            cutEnd = newline + 1;
        }

        var prefix = TrimTrailingBlankLines(content[..lineStart]);
        var suffix = content[cutEnd..].TrimStart('\r', '\n');

        if (prefix.Length == 0 && suffix.Length == 0)
        {
            File.Delete(path);
            return;
        }

        var result = prefix.Length == 0
            ? suffix
            : suffix.Length == 0
                ? prefix
                : prefix + (content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n") + suffix;
        Write(path, result, hadBom);
    }

    private static string InsertFresh(string content)
    {
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var headingEnd = EndOfFirstHeading(content);
        if (headingEnd < 0)
        {
            var prefix = content.Length == 0
                ? string.Empty
                : content.EndsWith("\n", StringComparison.Ordinal) ? content + "\n" : content + "\n\n";
            return prefix + SqueezrInstructions.Block + "\n";
        }

        var head = content[..headingEnd];
        if (!head.EndsWith("\n", StringComparison.Ordinal))
        {
            head += newline;
        }
        var tail = content[headingEnd..].TrimStart('\r', '\n');
        return tail.Length == 0
            ? head + newline + SqueezrInstructions.Block + newline
            : head + newline + SqueezrInstructions.Block + newline + newline + tail;
    }

    private static int EndOfFirstHeading(string content)
    {
        var lineStart = 0;
        while (lineStart < content.Length)
        {
            var lineEnd = content.IndexOf('\n', lineStart);
            var exclusiveEnd = lineEnd < 0 ? content.Length : lineEnd;
            if (exclusiveEnd > lineStart && content[exclusiveEnd - 1] == '\r')
            {
                exclusiveEnd--;
            }
            if (IsAtxHeading(content, lineStart, exclusiveEnd))
            {
                return lineEnd < 0 ? content.Length : lineEnd + 1;
            }
            if (lineEnd < 0)
            {
                break;
            }
            lineStart = lineEnd + 1;
        }
        return -1;
    }

    private static bool IsAtxHeading(string content, int start, int exclusiveEnd)
    {
        var hashes = 0;
        var i = start;
        while (i < exclusiveEnd && content[i] == '#' && hashes < 6)
        {
            hashes++;
            i++;
        }
        if (hashes == 0)
        {
            return false;
        }
        return i == exclusiveEnd || content[i] is ' ' or '\t';
    }

    private static (string Content, bool HadBom) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hadBom = bytes.AsSpan().StartsWith(Bom);
        return (Encoding.UTF8.GetString(hadBom ? bytes[Bom.Length..] : bytes), hadBom);
    }

    private static string TrimTrailingBlankLines(string value)
    {
        var trimmed = value.TrimEnd('\r', '\n');
        if (trimmed.Length == value.Length)
        {
            return value;
        }
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }
        var newline = value.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return trimmed + newline;
    }

    private static void Write(string path, string content, bool hadBom)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(path, content, hadBom ? Encoding.UTF8 : new UTF8Encoding(false));
    }
}
