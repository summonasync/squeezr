using Shouldly;
using Squeezr.Cli.Embeddings;

namespace Squeezr.Tests.Embeddings;

public class WordPieceTextEncoderTests
{
    private static string FixtureVocabPath =>
        Path.Combine(AppContext.BaseDirectory, "Embeddings", "Fixtures", "wordpiece-vocab", "vocab.txt");

    [Fact]
    public void Load_EncodesWordsByVocabIds()
    {
        var encoder = WordPieceTextEncoder.Load(FixtureVocabPath);

        encoder.EncodeToIds("hello world").ShouldBe([2, 5, 6, 3]);
    }

    [Fact]
    public void Load_WrapsSequenceWithClsAndSepTokenIds()
    {
        var encoder = WordPieceTextEncoder.Load(FixtureVocabPath);

        var ids = encoder.EncodeToIds("hello");

        ids.First().ShouldBe(2);
        ids.Last().ShouldBe(3);
    }

    [Fact]
    public void Load_LowercasesInputBeforeEncoding()
    {
        var encoder = WordPieceTextEncoder.Load(FixtureVocabPath);

        encoder.EncodeToIds("Hello WORLD").ShouldBe([2, 5, 6, 3]);
    }

    [Fact]
    public void Load_SplitsIntoSubwordsWithContinuationPrefix()
    {
        var encoder = WordPieceTextEncoder.Load(FixtureVocabPath);

        encoder.EncodeToIds("unaffable").ShouldBe([2, 10, 11, 12, 3]);
    }

    [Fact]
    public void Load_UnknownWordMapsToUnknownTokenId()
    {
        var encoder = WordPieceTextEncoder.Load(FixtureVocabPath);

        encoder.EncodeToIds("zzz").ShouldBe([2, 1, 3]);
        encoder.FallbackTokenId.ShouldBe(1);
    }

    [Fact]
    public void Load_MissingVocabFile_ThrowsFileNotFound()
    {
        var missing = Path.Combine(AppContext.BaseDirectory, "Embeddings", "Fixtures", "no-such-vocab.txt");

        Should.Throw<FileNotFoundException>(() => WordPieceTextEncoder.Load(missing));
    }
}
