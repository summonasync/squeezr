using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Embeddings;

public static class EmbeddingProviderFactory
{
    public static IEmbeddingProvider Create(EmbeddingsConfig config, string workspaceRoot)
    {
        try
        {
            return CreateCore(config, workspaceRoot);
        }
        catch (WorkspaceException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            throw new WorkspaceException($"Failed to initialize embeddings provider '{config.Provider}': {ex.Message}");
        }
    }

    private static IEmbeddingProvider CreateCore(EmbeddingsConfig config, string workspaceRoot)
    {
        switch (config.Provider.Trim().ToLowerInvariant())
        {
            case "onnx":
                return CreateOnnx(config.Onnx, workspaceRoot);
            case "openai":
                return new OpenAiCompatibleEmbeddingProvider(config.OpenAiCompatible);
            default:
                throw new WorkspaceException($"Unknown embeddings provider '{config.Provider}'. Supported providers: onnx, openai.");
        }
    }

    private static IEmbeddingProvider CreateOnnx(OnnxEmbeddingOptions options, string workspaceRoot)
    {
        var modelPath = ResolveFile(options.ModelPath, workspaceRoot);
        if (!File.Exists(modelPath))
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "Models", Path.GetFileName(options.ModelPath));
            if (File.Exists(bundled))
                modelPath = bundled;
            else
                throw new WorkspaceException($"Embedding model not found at '{modelPath}' and no bundled copy exists. Run scripts/fetch-model.sh (Linux/macOS) or scripts/fetch-model.ps1 (Windows) to download it.");
        }

        var tokenizerDir = ResolveTokenizerDir(options.TokenizerPath, workspaceRoot);
        return new OnnxEmbeddingProvider(modelPath, tokenizerDir, options.Dimensions, options.MaxSequenceLength, options.BatchSize);
    }

    private static string ResolveTokenizerDir(string tokenizerPath, string workspaceRoot)
    {
        var resolved = ResolveFile(tokenizerPath, workspaceRoot);
        var dir = Path.GetDirectoryName(resolved)!;
        if (File.Exists(Path.Combine(dir, "vocab.json")))
            return dir;

        var anchorName = Path.GetFileName(dir);
        var bundled = Path.Combine(AppContext.BaseDirectory, "Models", anchorName);
        if (File.Exists(Path.Combine(bundled, "vocab.json")))
            return bundled;

        throw new WorkspaceException($"Tokenizer files not found at '{dir}' and no bundled copy exists. Run scripts/fetch-model.sh (Linux/macOS) or scripts/fetch-model.ps1 (Windows) to download them.");
    }

    private static string ResolveFile(string path, string workspaceRoot)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
            expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), expanded[2..]);
        if (!Path.IsPathRooted(expanded))
            expanded = Path.GetFullPath(expanded, workspaceRoot);
        return expanded;
    }
}
