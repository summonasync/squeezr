using System.Text.RegularExpressions;

namespace Squeezr.Core.Indexing;

// Value masking for covered config files. Runs on the raw text an index pass reads, before any chunk,
// posting or snippet is built from it, so no stored surface ever sees an unmasked secret value. A line
// whose key names a secret loses its value to [redacted]; a value that merely carries a secret-shaped
// assignment inside it loses it to [redacted?] — the two markers let an owner audit the borderline
// cases without re-reading the file. Keys, structure and non-secret values are never touched, and the
// masking never moves a line boundary, so line numbers keep meaning the same thing on disk and in the
// store.
internal static class SecretSnippetRedactor
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    internal const string Masked = "[redacted]";
    internal const string MaskedUncertain = "[redacted?]";

    private static readonly string[] KeyWords =
    [
        "password",
        "passwd",
        "pwd",
        "secret",
        "apikey",
        "token",
        "credential",
        "privatekey",
        "connectionstring",
    ];

    private static readonly Regex JsonPair = new(
        """^\s*"(?<key>[^"]+)"\s*:\s*(?<val>.*)$""",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex EnvYamlPair = new(
        """^\s*(?:-\s+)?(?:export\s+)?["']?(?<key>[\w.\-/]+)["']?\s*[:=]\s*(?<val>.*)$""",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex XmlElement = new(
        """^\s*<(?<key>[\w.\-:]+)>\s*(?<val>[^<]*)(</)""",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex XmlKeyValueAttrs = new(
        @"(?:key|name)\s*=\s*""(?<key>[^""]*)""[^>]*?\bvalue\s*=\s*""(?<val>[^""]*)""",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex InlinePair = new(
        @"""(?<key>[^""]+)""\s*:\s*""(?<val>[^""]*)""",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex FlowPair = new(
        """(?<key>[\w.\-]+)\s*[:=]\s*(?<val>[^,}\]\n]+)""",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex XmlAttribute = new(
        """(?<head>(?<name>[\w.\-:]+)\s*=\s*")(?<val>[^"]*)(")""",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex EmbeddedAssignment = new(
        """(password|passwd|pwd|secret|api[_\- ]?key|token|credential|private[_\- ]?key|connection[_\- ]?string)\s*[=:]\s*\S|\w+://[^\s:/@]+:[^\s@/]+@|-----BEGIN [A-Z ]*PRIVATE KEY-----""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    internal static string Redact(string relativePath, string text) =>
        SecretSnippetCoverage.IsCovered(relativePath) ? RedactText(text) : text;

    private static string RedactText(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var carriage = lines[i].EndsWith('\r') ? "\r" : string.Empty;
            lines[i] = RedactLine(lines[i].TrimEnd('\r')) + carriage;
        }

        return string.Join('\n', lines);
    }

    private static string RedactLine(string line)
    {
        return RedactInlinePairs(line) is { } inlined ? inlined
            : TryPair(JsonPair.Match(line), line)
            ?? TryPair(EnvYamlPair.Match(line), line)
            ?? TryPair(XmlElement.Match(line), line)
            ?? RedactXmlAttributes(line);
    }

    private static string? RedactInlinePairs(string line)
    {
        var matches = InlinePair.Matches(line);
        if (matches.Count == 0)
        {
            return null;
        }

        var changed = false;
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var value = matches[i].Groups["val"];
            var masked = IsSecretKey(matches[i].Groups["key"].Value) ? Masked
                : EmbeddedAssignment.IsMatch(value.Value) ? MaskedUncertain
                : null;
            if (masked is null)
            {
                continue;
            }

            line = Splice(line, value.Index, value.Length, masked);
            changed = true;
        }

        return changed ? line : null;
    }

    private static string? TryPair(Match match, string line)
    {
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups["val"];
        var trimmed = value.Value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return RedactStructuralValue(value, line);
        }

        var masked = IsSecretKey(match.Groups["key"].Value) ? Masked
            : EmbeddedAssignment.IsMatch(value.Value) ? MaskedUncertain
            : null;
        return masked is null
            ? null
            : Splice(line, value.Index, value.Length, masked);
    }

    private static string? RedactStructuralValue(Group value, string line)
    {
        var span = value.Value;
        var matches = FlowPair.Matches(span);
        var changed = false;
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var entry = matches[i];
            var masked = IsSecretKey(entry.Groups["key"].Value) ? Masked
                : EmbeddedAssignment.IsMatch(entry.Groups["val"].Value) ? MaskedUncertain
                : null;
            if (masked is null)
            {
                continue;
            }

            span = Splice(span, entry.Groups["val"].Index, entry.Groups["val"].Length, masked);
            changed = true;
        }

        return changed ? Splice(line, value.Index, value.Length, span) : null;
    }

    private static string RedactXmlAttributes(string line)
    {
        var pair = XmlKeyValueAttrs.Match(line);
        if (pair.Success && IsSecretKey(pair.Groups["key"].Value))
        {
            var value = pair.Groups["val"];
            return Splice(line, value.Index, value.Length, Masked);
        }

        var matches = XmlAttribute.Matches(line);
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            if (!IsSecretKey(matches[i].Groups["name"].Value))
            {
                continue;
            }

            var value = matches[i].Groups["val"];
            line = Splice(line, value.Index, value.Length, Masked);
        }

        return line;
    }

    private static string Splice(string line, int start, int length, string replacement) =>
        string.Concat(line.AsSpan(0, start), replacement, line.AsSpan(start + length));

    private static bool IsSecretKey(string key)
    {
        var normalized = new string(key.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return KeyWords.Any(normalized.Contains);
    }
}
