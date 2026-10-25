using System.Text;

namespace Squeezr.Core.Indexing.Query;

public static class SemanticSnippet
{
    public const int MaxChars = 200;

    public static string Build(string chunkText, string query)
    {
        var trimmed = chunkText.Trim();
        if (trimmed.Length <= MaxChars)
        {
            return trimmed;
        }

        var tokens = IdentifierTokenLeg.Tokenize(query);
        var lines = trimmed.Split('\n');
        var best = FirstNonBlank(lines);
        var bestScore = 0;
        for (var i = best; i < lines.Length; i++)
        {
            var line = lines[i].AsSpan().Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var score = ScoreLine(line, tokens);
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        return Window(lines, best);
    }

    private static int FirstNonBlank(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length > 0)
            {
                return i;
            }
        }

        return 0;
    }

    private static int ScoreLine(ReadOnlySpan<char> line, List<string> tokens)
    {
        var score = 0;
        foreach (var token in tokens)
        {
            if (line.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score++;
            }
        }

        return score;
    }

    private static string Window(string[] lines, int start)
    {
        var head = lines[start].Trim();
        if (head.Length > MaxChars)
        {
            return head[..MaxChars] + "…";
        }

        var builder = new StringBuilder(head);
        var length = head.Length;
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (length + 1 + line.Length > MaxChars)
            {
                var room = MaxChars - length - 1;
                if (room > 0)
                {
                    builder.Append(' ').Append(line.AsSpan(0, room));
                }

                builder.Append('…');
                break;
            }

            builder.Append(' ').Append(line);
            length += 1 + line.Length;
        }

        return builder.ToString();
    }
}
