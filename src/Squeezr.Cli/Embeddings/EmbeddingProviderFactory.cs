using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Embeddings;

public static class EmbeddingProviderFactory
{
    public static IEmbeddingProvider Create(EmbeddingsConfig config, string workspaceRoot, HttpMessageHandler? httpHandler = null)
    {
        try
        {
            return CreateCore(config, workspaceRoot, httpHandler);
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

    private static IEmbeddingProvider CreateCore(EmbeddingsConfig config, string workspaceRoot, HttpMessageHandler? httpHandler)
    {
        switch (config.Provider.Trim().ToLowerInvariant())
        {
            case "onnx":
                return CreateOnnx(config.Onnx, workspaceRoot);
            case "openai":
                return new OpenAiCompatibleEmbeddingProvider(config.OpenAiCompatible);
            case "ollama":
                OllamaModelEnsurer.Ensure(config.Ollama, httpHandler);
                return new OllamaEmbeddingProvider(config.Ollama, httpHandler);
            default:
                throw new WorkspaceException($"Unknown embeddings provider '{config.Provider}'. Supported providers: onnx, ollama, openai.");
        }
    }

    private static IEmbeddingProvider CreateOnnx(OnnxEmbeddingOptions options, string workspaceRoot)
    {
        var modelPath = ResolveOnnxModelPath(options, workspaceRoot);
        var tokenizerDir = ResolveTokenizerDir(options.TokenizerPath, workspaceRoot);
        return new OnnxEmbeddingProvider(modelPath, tokenizerDir, options.ResolvedProfile, options.Dimensions, options.EffectiveMaxSequenceLength, options.BatchSize);
    }

    internal static string ResolveOnnxModelPath(OnnxEmbeddingOptions options, string workspaceRoot)
    {
        var modelPath = ResolveFile(options.ModelPath, workspaceRoot);
        if (File.Exists(modelPath))
        {
            return modelPath;
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "Models", Path.GetFileName(options.ModelPath));
        if (File.Exists(bundled))
        {
            return bundled;
        }

        throw new WorkspaceException($"Embedding model not found at '{modelPath}' and no bundled copy exists. Run scripts/fetch-model.sh (Linux/macOS) or scripts/fetch-model.ps1 (Windows) to download it.");
    }

    internal static string? DescribeOnnxModelProblem(OnnxEmbeddingOptions options, string workspaceRoot)
    {
        try
        {
            ResolveOnnxModelPath(options, workspaceRoot);
            return null;
        }
        catch (WorkspaceException ex)
        {
            return ex.Message;
        }
    }

    private static string ResolveTokenizerDir(string tokenizerPath, string workspaceRoot)
    {
        var resolved = ResolveFile(tokenizerPath, workspaceRoot);
        var dir = Path.GetDirectoryName(resolved)!;
        if (HasTokenizerFiles(dir))
            return dir;

        var anchorName = Path.GetFileName(dir);
        var bundled = Path.Combine(AppContext.BaseDirectory, "Models", anchorName);
        if (HasTokenizerFiles(bundled))
            return bundled;

        throw new WorkspaceException($"Tokenizer files not found at '{dir}' and no bundled copy exists. Run scripts/fetch-model.sh (Linux/macOS) or scripts/fetch-model.ps1 (Windows) to download them.");
    }

    private static bool HasTokenizerFiles(string dir)
        => File.Exists(Path.Combine(dir, "vocab.json")) || File.Exists(Path.Combine(dir, "vocab.txt"));

    private static string ResolveFile(string path, string workspaceRoot)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded == "~")
            return UserHome.Path ?? expanded;
        if (UserHome.Path is { } userHome &&
            (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal)))
            expanded = Path.Combine(userHome, expanded[2..]);
        if (!Path.IsPathRooted(expanded))
            expanded = Path.GetFullPath(expanded, workspaceRoot);
        return expanded;
    }
}
