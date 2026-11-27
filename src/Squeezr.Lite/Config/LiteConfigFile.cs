using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Lite.Config;

public static class LiteConfigFile
{
    private static readonly string[] SemanticSections = ["embeddings", "qdrant"];

    public static LiteConfig Load(string root)
    {
        var config = ConfigFile.Load(root);
        return new LiteConfig(config, IgnoredSectionWarnings(config));
    }

    public static LiteConfig TryLoad(string root)
    {
        if (!File.Exists(SqueezrPaths.ConfigPath(root)))
        {
            return LiteConfig.Missing;
        }

        try
        {
            return Load(root);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            return new LiteConfig(null, [$"config file could not be read: {ex.Message}"]);
        }
    }

    // The semantic profile's sections ride Core's extension data; a lite workspace never binds them,
    // and the warning is the structured record of what was skipped rather than a silent drop.
    private static IReadOnlyList<string> IgnoredSectionWarnings(SqueezrConfig config)
    {
        if (config.ExtensionData is not { Count: > 0 } sections)
        {
            return [];
        }

        return [.. sections.Keys
            .Order(StringComparer.Ordinal)
            .Select(SectionWarning)];
    }

    private static string SectionWarning(string section) =>
        SemanticSections.Contains(section, StringComparer.Ordinal)
            ? $"config section '{section}' belongs to the semantic profile; sqzrlite ignores it and leaves it in the file"
            : $"config section '{section}' is not part of the lite schema; ignored";
}
