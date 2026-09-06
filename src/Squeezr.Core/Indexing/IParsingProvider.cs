namespace Squeezr.Core.Indexing;

public interface IParsingProvider
{
    bool CanHandle(string path, string? languageHint);

    ParseResult Parse(FileRef file, ReadOnlySpan<byte> content);
}
