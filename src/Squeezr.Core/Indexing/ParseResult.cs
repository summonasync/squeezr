namespace Squeezr.Core.Indexing;

public sealed record ParseResult(
    string Language,
    IReadOnlyList<Symbol> Symbols,
    IReadOnlyList<SymbolRef> Refs,
    IReadOnlyList<CodeChunk> Chunks);

public sealed record FileRef(string RepoName, string Path);

public sealed record CodeSpan(int StartLine, int EndLine);

public sealed record Symbol(
    string Name,
    SymbolKind Kind,
    string? Signature,
    CodeSpan Span,
    string? ContainerName,
    string? DocComment);

public enum SymbolKind
{
    Class,
    Method,
    Function,
    Property,
    Field,
    Interface,
    Other
}

public sealed record SymbolRef(
    string? ToSymbolId,
    string? ToName,
    RefKind Kind,
    int Line);

public enum RefKind
{
    Call,
    Inherit,
    Implement,
    Import,
    Reference
}

public sealed record CodeChunk(
    string? SymbolId,
    CodeSpan Span,
    string Text,
    int TokenCount,
    string ChunkHash);
