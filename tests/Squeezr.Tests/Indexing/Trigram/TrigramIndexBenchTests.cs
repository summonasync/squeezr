using System.Diagnostics;
using Shouldly;
using Xunit.Abstractions;
using Tg = Squeezr.Core.Indexing.Trigram;

namespace Squeezr.Tests.Indexing.Trigram;

public class TrigramIndexBenchTests
{
    private const int DocumentCount = 100_000;
    private const int Lookups = 200;

    private readonly ITestOutputHelper _output;

    public TrigramIndexBenchTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Lookup_On100kSyntheticDocs_StaysSubFiveMilliseconds()
    {
        var dir = TestDirs.NewTempDir();
        try
        {
            RunBench(dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private void RunBench(string dir)
    {
        var path = Path.Combine(dir, "trigram.idx");
        var stopwatch = Stopwatch.StartNew();
        BuildIndex(path);
        var buildMs = stopwatch.ElapsedMilliseconds;
        var fileKb = new FileInfo(path).Length / 1024;

        var reader = new Tg.TrigramIndexReader(path);
        var target = Tg.TrigramHasher.ComputeHash('a', 'b', 'c');
        var postings = reader.Find(target);
        postings.ShouldNotBeNull();

        var timings = new double[Lookups];
        for (var i = 0; i < Lookups; i++)
        {
            stopwatch.Restart();
            reader.Find(target);
            timings[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        var avg = timings.Average();
        var max = timings.Max();
        _output.WriteLine(
            $"docs={DocumentCount} build={buildMs}ms file={fileKb}KiB listDocs={postings!.DocumentCount} " +
            $"lookup avg={avg:F3}ms max={max:F3}ms over {Lookups} runs");

        avg.ShouldBeLessThan(5.0);
    }

    private static void BuildIndex(string path)
    {
        const string alphabet = "abcdefgh";
        var random = new Random(42);
        var writer = new Tg.TrigramIndexWriter();

        for (var i = 0; i < DocumentCount; i++)
        {
            var chars = new char[32];
            for (var c = 0; c < chars.Length; c++)
            {
                chars[c] = alphabet[random.Next(alphabet.Length)];
            }

            var postings = Tg.TrigramExtractor.ExtractPostings(new string(chars));
            writer.AddDocument(new Tg.TrigramDocument((uint)(i % 4), (uint)i, postings.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray())));
        }

        writer.Write(path);
    }
}
