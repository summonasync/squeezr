using System.Text.Json;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Configuration;

public static class ConfigFile
{
    public static SqueezrConfig Load(string root)
    {
        var path = SqueezrPaths.ConfigPath(root);
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkspaceException(
                $"Config file at '{path}' could not be read: {ex.Message}",
                WorkspaceException.StorageUnavailableCode,
                ex);
        }

        SqueezrConfig config;
        try
        {
            config = JsonSerializer.Deserialize<SqueezrConfig>(json, SqueezrJson.FileOptions)
                     ?? throw new WorkspaceException($"Config file at '{path}' is corrupt.");
        }
        catch (JsonException ex)
        {
            throw new WorkspaceException($"Config file at '{path}' is not valid JSON: {ex.Message}");
        }

        config = NormalizeNullSections(config);

        if (config.Embeddings.Provider.Trim().ToLowerInvariant() == "onnx")
        {
            throw new WorkspaceException(
                "Embeddings provider 'onnx' is no longer supported. Switch embeddings.provider to 'ollama' or 'openai' and run a full reindex ('squeezr index --reembed'): vectors produced by the ONNX model are incompatible with every other embedding model.",
                WorkspaceException.OnnxProviderCode);
        }

        try
        {
            _ = config.Embeddings.Ollama.ResolvedProfile;
            _ = config.Embeddings.OpenAiCompatible.ResolvedProfile;
        }
        catch (ArgumentException ex)
        {
            throw new WorkspaceException($"Config file at '{path}': {ex.Message}");
        }

        return config;
    }

    public static void Save(string root, SqueezrConfig config)
    {
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        var json = JsonSerializer.Serialize(config, SqueezrJson.FileOptions);
        File.WriteAllText(SqueezrPaths.ConfigPath(root), json);
    }

    // A section set to literal null is configuration data, not a corrupt file: whoever wrote it
    // discarded that section wholesale, so Load substitutes the default section instance per
    // section. No field-level merging; a truncated file with absent required sections still
    // fails as invalid JSON above.
    private static SqueezrConfig NormalizeNullSections(SqueezrConfig config)
    {
        var defaults = SqueezrConfig.CreateDefault();
        return new SqueezrConfig
        {
            Embeddings = config.Embeddings ?? defaults.Embeddings,
            Chunking = config.Chunking ?? defaults.Chunking,
            Indexing = config.Indexing ?? defaults.Indexing,
            Qdrant = config.Qdrant ?? defaults.Qdrant,
            Watch = config.Watch ?? defaults.Watch
        };
    }
}
