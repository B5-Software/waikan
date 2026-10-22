using NAudio.Wave;
using Waikan.Services;

namespace Waikan.Tests;

public sealed class KeywordSpotterIntegrationTests
{
    [Fact]
    public void Detects_Two_Occurrences_Of_Keyword_And_Ignores_Negative_Sample()
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        var positive = Path.Combine(assets, "positive.wav");
        var negative = Path.Combine(assets, "negative.wav");
        Assert.True(File.Exists(positive), positive);
        Assert.True(File.Exists(negative), negative);

        var repoRoot = TestInfrastructure.FindRepositoryRoot();
        var modelDirectory = Path.Combine(repoRoot, "models", "kws");
        var temp = TestInfrastructure.CreateTempDirectory();
        try
        {
            var keywordsFile = Path.Combine(temp, "keywords.txt");
            File.WriteAllText(keywordsFile, "w ài k ān @外刊\n");
            var log = TestInfrastructure.CreateLog(temp);

            using var engine = new KeywordSpotterEngine(modelDirectory, keywordsFile, sensitivity: 50, log);
            var positiveHits = CountHits(engine, positive);
            var negativeHits = CountHits(engine, negative);

            Assert.Equal(2, positiveHits);
            Assert.Equal(0, negativeHits);
        }
        finally
        {
            TestInfrastructure.TryDeleteDirectory(temp);
        }
    }

    [Theory]
    [InlineData(0, 1.0f, 0.40f)]
    [InlineData(50, 1.5f, 0.25f)]
    [InlineData(100, 2.0f, 0.10f)]
    public void Tuning_Is_Calibrated(int sensitivity, float expectedScore, float expectedThreshold)
    {
        var (score, threshold) = KeywordSpotterEngine.GetTuning(sensitivity);
        Assert.Equal(expectedScore, score, 2);
        Assert.Equal(expectedThreshold, threshold, 2);
    }

    private static int CountHits(KeywordSpotterEngine engine, string wavePath)
    {
        using var reader = new AudioFileReader(wavePath);
        Assert.Equal(16000, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);

        var all = new List<float>();
        var buffer = new float[reader.WaveFormat.SampleRate];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            all.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        const int chunkSize = 320;
        var hits = 0;
        for (var offset = 0; offset < all.Count; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, all.Count - offset);
            var chunk = new float[length];
            all.CopyTo(offset, chunk, 0, length);
            if (engine.Process(chunk) is not null)
            {
                hits++;
            }
        }

        for (var index = 0; index < 20; index++)
        {
            if (engine.Process(new float[chunkSize]) is not null)
            {
                hits++;
            }
        }

        return hits;
    }
}
