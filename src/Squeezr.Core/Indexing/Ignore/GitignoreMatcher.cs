namespace Squeezr.Core.Indexing.Ignore;

public static class GitignoreMatcher
{
    public static IgnorePattern? Parse(string line, IgnoreSource source, string sourceFile, string baseDir)
    {
        var trimmed = line.Trim();

        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
            return null;

        var negate = false;
        var pattern = trimmed;

        if (pattern.StartsWith('!'))
        {
            negate = true;
            pattern = pattern.Substring(1);
        }

        var dirOnly = false;
        if (pattern.EndsWith('/'))
        {
            dirOnly = true;
            pattern = pattern.TrimEnd('/');
        }

        if (ContainsUnsupported(pattern))
        {
            return new IgnorePattern
            {
                Raw = trimmed,
                Source = source,
                SourceFile = sourceFile,
                BaseDir = baseDir,
                Negate = negate,
                DirOnly = dirOnly,
                Anchored = false,
                Unsupported = true,
                CompiledPattern = pattern
            };
        }

        var anchored = pattern.Contains('/');
        var compiledPattern = pattern;

        if (anchored && pattern.StartsWith('/'))
        {
            compiledPattern = pattern.Substring(1);
        }

        return new IgnorePattern
        {
            Raw = trimmed,
            Source = source,
            SourceFile = sourceFile,
            BaseDir = baseDir,
            Negate = negate,
            DirOnly = dirOnly,
            Anchored = anchored,
            Unsupported = false,
            CompiledPattern = compiledPattern
        };
    }

    private static bool ContainsUnsupported(string pattern)
    {
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '\\')
                return true;

            if (pattern[i] == '[')
                return true;
        }

        return false;
    }

    public static string FormatWarning(string rawPattern, string sourceFile) =>
        $"unsupported ignore pattern '{rawPattern}' in {sourceFile}; pattern skipped.";
}
