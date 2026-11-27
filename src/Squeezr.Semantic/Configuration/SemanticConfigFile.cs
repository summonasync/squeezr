using System.Text.Json;
using Squeezr.Core.Configuration;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Semantic.Configuration;

public static class SemanticConfigFile
{
    public static SemanticConfig Load(string root)
    {
        var core = ConfigFile.Load(root);
        var config = SemanticConfig.Bind(core);
        Validate(SqueezrPaths.ConfigPath(root), config.Embeddings);
        return config;
    }

    public static void Save(string root, SemanticConfig config) =>
        ConfigFile.Save(root, Attach(config.Core, config));

    // The typed sections are written back over whatever raw form they arrived in, so a save after
    // an edit cannot lose them the way an unknown property would: the section keys this assembly
    // owns are exactly the two it binds.
    public static SqueezrConfig Attach(SqueezrConfig core, SemanticConfig config)
    {
        var sections = new Dictionary<string, JsonElement>(
            core.ExtensionData ?? new Dictionary<string, JsonElement>(), StringComparer.Ordinal);
        sections["embeddings"] = JsonSerializer.SerializeToElement(config.Embeddings, SqueezrJson.FileOptions);
        sections["qdrant"] = JsonSerializer.SerializeToElement(config.Qdrant, SqueezrJson.FileOptions);
        return core with { ExtensionData = sections };
    }

    private static void Validate(string path, EmbeddingsConfig embeddings)
    {
        if (embeddings.Provider.Trim().ToLowerInvariant() == "onnx")
        {
            throw new WorkspaceException(
                "Embeddings provider 'onnx' is no longer supported. Switch embeddings.provider to 'ollama' or 'openai' and run a full reindex ('squeezr index --reembed'): vectors produced by the ONNX model are incompatible with every other embedding model.",
                WorkspaceException.OnnxProviderCode);
        }

        try
        {
            _ = embeddings.Ollama.ResolvedProfile;
            _ = embeddings.OpenAiCompatible.ResolvedProfile;
        }
        catch (ArgumentException ex)
        {
            throw new WorkspaceException($"Config file at '{path}': {ex.Message}");
        }
    }
}
