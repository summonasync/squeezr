using Spectre.Console;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Tui;

public static class TuiConfigEditor
{
    private static readonly string[] SectionItems = ["qdrant", "embeddings", "chunking", "indexing", "Back"];

    public static async Task RunAsync(IAnsiConsole console, string workspaceRoot, CancellationToken cancellationToken)
    {
        if (!WorkspaceManifestFile.Exists(workspaceRoot))
        {
            console.MarkupLine("[yellow]Workspace not initialized.[/] Run 'squeezr init' first.");
            return;
        }

        SqueezrConfig config;
        try
        {
            config = ConfigFile.Load(workspaceRoot);
        }
        catch (WorkspaceException ex)
        {
            console.MarkupLine($"[red]error:[/] {ex.Message.EscapeMarkup()}");
            return;
        }

        while (true)
        {
            var section = await new SelectionPrompt<string>()
                .Title("Config sections")
                .AddChoices(SectionItems)
                .ShowAsync(console, cancellationToken);
            if (section == "Back")
            {
                return;
            }

            var result = await RunSectionFormAsync(console, section, config, SectionFields(section), cancellationToken);
            if (!result.Save)
            {
                continue;
            }

            if (section == "embeddings"
                && EffectiveDimensions(config.Embeddings) is { } oldDimensions
                && EffectiveDimensions(result.Config.Embeddings) is { } newDimensions
                && oldDimensions != newDimensions)
            {
                console.MarkupLine(
                    $"[yellow]warning:[/] embedding dimensions will change {oldDimensions} → {newDimensions}; " +
                    "the existing vector collection is incompatible and 'squeezr index --reembed' will be required.");
                var proceed = await new SelectionPrompt<string>()
                    .Title("Apply anyway?")
                    .AddChoices("Apply", "Cancel")
                    .ShowAsync(console, cancellationToken);
                if (proceed != "Apply")
                {
                    console.MarkupLine("[grey]Changes discarded.[/]");
                    continue;
                }
            }

            config = result.Config;
            ConfigFile.Save(workspaceRoot, config);
            console.MarkupLine("[green]Saved.[/]");
            if (section == "embeddings")
            {
                console.MarkupLine(
                    "[yellow]Embeddings config changed.[/] Run 'squeezr index --reembed' to re-embed existing chunks.");
            }
        }
    }

    private static async Task<SectionResult> RunSectionFormAsync(
        IAnsiConsole console,
        string section,
        SqueezrConfig config,
        IReadOnlyList<FieldDef> fields,
        CancellationToken cancellationToken)
    {
        var working = config;
        var changes = new List<FieldChange>();
        foreach (var field in fields)
        {
            var current = field.Get(working);
            while (true)
            {
                var raw = await new TextPrompt<string>($"{field.Key} [grey]{current.EscapeMarkup()}[/] (empty to keep)")
                    .AllowEmpty()
                    .ShowAsync(console, cancellationToken);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    break;
                }

                var applied = field.Apply(working, raw.Trim());
                if (!applied.Ok)
                {
                    console.MarkupLine($"[red]invalid value:[/] {applied.Error.EscapeMarkup()}");
                    continue;
                }

                var next = applied.Config!;
                var updated = field.Get(next);
                if (!string.Equals(updated, current, StringComparison.Ordinal))
                {
                    changes.Add(new FieldChange(field.Key, current, updated));
                }

                working = next;
                break;
            }
        }

        if (changes.Count == 0)
        {
            console.MarkupLine("[grey]No changes.[/]");
            return new SectionResult(Save: false, Config: config);
        }

        console.MarkupLine($"[bold]{section}[/] changes:");
        foreach (var change in changes)
        {
            console.MarkupLine(
                $"  {change.Key.EscapeMarkup()}: {change.OldValue.EscapeMarkup()} → {change.NewValue.EscapeMarkup()}");
        }

        var choice = await new SelectionPrompt<string>()
            .Title("Save changes?")
            .AddChoices("Save changes", "Discard changes")
            .ShowAsync(console, cancellationToken);
        return new SectionResult(choice == "Save changes", working);
    }

    private static IReadOnlyList<FieldDef> SectionFields(string section) => section switch
    {
        "qdrant" =>
        [
            new FieldDef
            {
                Key = "endpoint",
                Get = c => c.Qdrant.Endpoint,
                Apply = (c, raw) => IsHttpUrl(raw, out var error)
                    ? FieldResult.Valid(c with { Qdrant = c.Qdrant with { Endpoint = raw } })
                    : FieldResult.Fail(error),
            },
        ],
        "embeddings" =>
        [
            new FieldDef
            {
                Key = "provider",
                Get = c => c.Embeddings.Provider,
                Apply = (c, raw) => NormalizeProvider(raw) is { } provider
                    ? FieldResult.Valid(c with { Embeddings = c.Embeddings with { Provider = provider } })
                    : FieldResult.Fail("must be 'onnx', 'ollama' or 'openai'"),
            },
            new FieldDef
            {
                Key = "onnx.modelPath",
                Get = c => c.Embeddings.Onnx.ModelPath,
                Apply = (c, raw) => FieldResult.Valid(c with
                {
                    Embeddings = c.Embeddings with { Onnx = c.Embeddings.Onnx with { ModelPath = raw } },
                }),
            },
            new FieldDef
            {
                Key = "onnx.tokenizerPath",
                Get = c => c.Embeddings.Onnx.TokenizerPath,
                Apply = (c, raw) => FieldResult.Valid(c with
                {
                    Embeddings = c.Embeddings with { Onnx = c.Embeddings.Onnx with { TokenizerPath = raw } },
                }),
            },
            new FieldDef
            {
                Key = "onnx.dimensions",
                Get = c => c.Embeddings.Onnx.EffectiveDimensions.ToString(),
                Apply = (c, raw) => IsPositiveInt(raw, out var value, out var error)
                    ? FieldResult.Valid(c with
                    {
                        Embeddings = c.Embeddings with { Onnx = c.Embeddings.Onnx with { Dimensions = value } },
                    })
                    : FieldResult.Fail(error),
            },
            new FieldDef
            {
                Key = "onnx.maxSequenceLength",
                Get = c => c.Embeddings.Onnx.EffectiveMaxSequenceLength.ToString(),
                Apply = (c, raw) => IsPositiveInt(raw, out var value, out var error)
                    ? FieldResult.Valid(c with
                    {
                        Embeddings = c.Embeddings with { Onnx = c.Embeddings.Onnx with { MaxSequenceLength = value } },
                    })
                    : FieldResult.Fail(error),
            },
            new FieldDef
            {
                Key = "onnx.batchSize",
                Get = c => c.Embeddings.Onnx.BatchSize.ToString(),
                Apply = (c, raw) => IsPositiveInt(raw, out var value, out var error)
                    ? FieldResult.Valid(c with
                    {
                        Embeddings = c.Embeddings with { Onnx = c.Embeddings.Onnx with { BatchSize = value } },
                    })
                    : FieldResult.Fail(error),
            },
            new FieldDef
            {
                Key = "openAi.endpoint",
                Get = c => c.Embeddings.OpenAiCompatible.Endpoint,
                Apply = (c, raw) => IsHttpUrl(raw, out var error)
                    ? FieldResult.Valid(c with
                    {
                        Embeddings = c.Embeddings with
                        {
                            OpenAiCompatible = c.Embeddings.OpenAiCompatible with { Endpoint = raw },
                        },
                    })
                    : FieldResult.Fail(error),
            },
            new FieldDef
            {
                Key = "openAi.apiKeyEnv",
                Get = c => c.Embeddings.OpenAiCompatible.ApiKeyEnv,
                Apply = (c, raw) => FieldResult.Valid(c with
                {
                    Embeddings = c.Embeddings with
                    {
                        OpenAiCompatible = c.Embeddings.OpenAiCompatible with { ApiKeyEnv = raw },
                    },
                }),
            },
            new FieldDef
            {
                Key = "openAi.model",
                Get = c => c.Embeddings.OpenAiCompatible.Model,
                Apply = (c, raw) => FieldResult.Valid(c with
                {
                    Embeddings = c.Embeddings with
                    {
                        OpenAiCompatible = c.Embeddings.OpenAiCompatible with { Model = raw },
                    },
                }),
            },
            new FieldDef
            {
                Key = "openAi.dimensions",
                Get = c => c.Embeddings.OpenAiCompatible.Dimensions.ToString(),
                Apply = (c, raw) => IsPositiveInt(raw, out var value, out var error)
                    ? FieldResult.Valid(c with
                    {
                        Embeddings = c.Embeddings with
                        {
                            OpenAiCompatible = c.Embeddings.OpenAiCompatible with { Dimensions = value },
                        },
                    })
                    : FieldResult.Fail(error),
            },
            new FieldDef
            {
                Key = "ollama.endpoint",
                Get = c => c.Embeddings.Ollama.Endpoint,
                Apply = (c, raw) => IsHttpUrl(raw, out var error)
                    ? FieldResult.Valid(c with
                    {
                        Embeddings = c.Embeddings with { Ollama = c.Embeddings.Ollama with { Endpoint = raw } },
                    })
                    : FieldResult.Fail(error),
            },
            new FieldDef
            {
                Key = "ollama.model",
                Get = c => c.Embeddings.Ollama.Model,
                Apply = (c, raw) => FieldResult.Valid(c with
                {
                    Embeddings = c.Embeddings with { Ollama = c.Embeddings.Ollama with { Model = raw } },
                }),
            },
            new FieldDef
            {
                Key = "ollama.dimensions",
                Get = c => c.Embeddings.Ollama.Dimensions.ToString(),
                Apply = (c, raw) => IsPositiveInt(raw, out var value, out var error)
                    ? FieldResult.Valid(c with
                    {
                        Embeddings = c.Embeddings with { Ollama = c.Embeddings.Ollama with { Dimensions = value } },
                    })
                    : FieldResult.Fail(error),
            },
            new FieldDef
            {
                Key = "ollama.modelProfile",
                Get = c => c.Embeddings.Ollama.ModelProfile ?? "null",
                Apply = (c, raw) => TryNormalizeOllamaModelProfile(raw, out var profile)
                    ? FieldResult.Valid(c with
                    {
                        Embeddings = c.Embeddings with
                        {
                            Ollama = c.Embeddings.Ollama with { ModelProfile = profile },
                        },
                    })
                    : FieldResult.Fail(
                        $"unknown model profile '{raw}'; known profiles: {string.Join(", ", EmbeddingModelProfiles.KnownIds)} (or 'null' for no profile)"),
            },
        ],
        "chunking" =>
        [
            new FieldDef
            {
                Key = "maxTokensPerChunk",
                Get = c => c.Chunking.MaxTokensPerChunk.ToString(),
                Apply = (c, raw) => IsPositiveInt(raw, out var value, out var error)
                    ? FieldResult.Valid(c with { Chunking = c.Chunking with { MaxTokensPerChunk = value } })
                    : FieldResult.Fail(error),
            },
            new FieldDef
            {
                Key = "fallbackWindowLines",
                Get = c => c.Chunking.FallbackWindowLines.ToString(),
                Apply = (c, raw) => IsPositiveInt(raw, out var value, out var error)
                    ? FieldResult.Valid(c with { Chunking = c.Chunking with { FallbackWindowLines = value } })
                    : FieldResult.Fail(error),
            },
            new FieldDef
            {
                Key = "fallbackOverlapLines",
                Get = c => c.Chunking.FallbackOverlapLines.ToString(),
                Apply = (c, raw) => IsPositiveInt(raw, out var value, out var error)
                    ? FieldResult.Valid(c with { Chunking = c.Chunking with { FallbackOverlapLines = value } })
                    : FieldResult.Fail(error),
            },
        ],
        _ =>
        [
            new FieldDef
            {
                Key = "maxFileSizeKb",
                Get = c => c.Indexing.MaxFileSizeKb.ToString(),
                Apply = (c, raw) => IsPositiveInt(raw, out var value, out var error)
                    ? FieldResult.Valid(c with { Indexing = c.Indexing with { MaxFileSizeKb = value } })
                    : FieldResult.Fail(error),
            },
            new FieldDef
            {
                Key = "noSemantic",
                Get = c => c.Indexing.NoSemantic.ToString().ToLowerInvariant(),
                Apply = (c, raw) => bool.TryParse(raw, out var value)
                    ? FieldResult.Valid(c with { Indexing = c.Indexing with { NoSemantic = value } })
                    : FieldResult.Fail("must be 'true' or 'false'"),
            },
            new FieldDef
            {
                Key = "ignore",
                Get = c => string.Join(", ", c.Indexing.Ignore),
                Apply = (c, raw) => FieldResult.Valid(c with
                {
                    Indexing = c.Indexing with
                    {
                        Ignore = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    },
                }),
            },
        ],
    };

    private static string? NormalizeProvider(string raw)
    {
        var value = raw.ToLowerInvariant();
        return value is "onnx" or "ollama" or "openai" ? value : null;
    }

    private static bool TryNormalizeOllamaModelProfile(string raw, out string? profile)
    {
        if (raw.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            profile = null;
            return true;
        }

        foreach (var id in EmbeddingModelProfiles.KnownIds)
        {
            if (raw.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                profile = id;
                return true;
            }
        }

        profile = null;
        return false;
    }

    private static int? EffectiveDimensions(EmbeddingsConfig embeddings)
        => embeddings.Provider.Trim().ToLowerInvariant() switch
        {
            "onnx" => embeddings.Onnx.EffectiveDimensions,
            "ollama" => embeddings.Ollama.Dimensions,
            "openai" => embeddings.OpenAiCompatible.Dimensions,
            _ => null,
        };

    private static bool IsHttpUrl(string raw, out string error)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "must be an absolute http(s) URL";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsPositiveInt(string raw, out int value, out string error)
    {
        if (!int.TryParse(raw, out value) || value <= 0)
        {
            error = "must be an integer greater than 0";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private sealed class FieldDef
    {
        public required string Key { get; init; }

        public required Func<SqueezrConfig, string> Get { get; init; }

        public required Func<SqueezrConfig, string, FieldResult> Apply { get; init; }
    }

    private readonly record struct FieldResult(bool Ok, SqueezrConfig? Config, string Error)
    {
        public static FieldResult Valid(SqueezrConfig config) => new(true, config, string.Empty);

        public static FieldResult Fail(string error) => new(false, null, error);
    }

    private readonly record struct SectionResult(bool Save, SqueezrConfig Config);

    private readonly record struct FieldChange(string Key, string OldValue, string NewValue);
}
