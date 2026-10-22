using System.Diagnostics;
using SherpaOnnx;

namespace Waikan.Services;

/// <summary>
/// Thin, single-threaded wrapper around sherpa-onnx's open-vocabulary keyword
/// spotter. The 3M model can detect an arbitrary Chinese keyword without
/// running a full speech recognizer, which keeps all-day microphone use cheap.
/// </summary>
public sealed class KeywordSpotterEngine : IDisposable
{
    private readonly KeywordSpotter _spotter;
    private readonly OnlineStream _stream;
    private bool _disposed;

    public KeywordSpotterEngine(string modelDirectory, string keywordsFile, int sensitivity, LogService log)
    {
        var encoder = Path.Combine(modelDirectory, "encoder.int8.onnx");
        var decoder = Path.Combine(modelDirectory, "decoder.onnx");
        var joiner = Path.Combine(modelDirectory, "joiner.int8.onnx");
        var tokens = Path.Combine(modelDirectory, "tokens.txt");

        foreach (var file in new[] { encoder, decoder, joiner, tokens, keywordsFile })
        {
            if (!File.Exists(file))
            {
                throw new FileNotFoundException($"缺少语音识别文件：{file}", file);
            }
        }

        var (score, threshold) = GetTuning(sensitivity);

        var config = new KeywordSpotterConfig
        {
            FeatConfig = { SampleRate = 16000, FeatureDim = 80 },
            KeywordsFile = keywordsFile,
            KeywordsScore = score,
            KeywordsThreshold = threshold,
            MaxActivePaths = 4,
        };
        config.ModelConfig.Transducer.Encoder = encoder;
        config.ModelConfig.Transducer.Decoder = decoder;
        config.ModelConfig.Transducer.Joiner = joiner;
        config.ModelConfig.Tokens = tokens;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.NumThreads = 1;
        config.ModelConfig.Debug = 0;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            _spotter = new KeywordSpotter(config);
            _stream = _spotter.CreateStream();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "无法加载本地关键词识别模型。请确认应用目录中的 models/kws 文件完整，并确认系统具备 Microsoft Visual C++ 运行库。",
                ex);
        }

        log.Info(
            $"关键词模型已加载：sensitivity={sensitivity}, score={score:F2}, threshold={threshold:F2}, " +
            $"elapsed={stopwatch.ElapsedMilliseconds}ms, threads=1");
    }

    /// <summary>Feed 16 kHz mono float PCM. Returns the keyword when triggered.</summary>
    public string? Process(float[] samples)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(KeywordSpotterEngine));
        }

        if (samples.Length == 0)
        {
            return null;
        }

        _stream.AcceptWaveform(16000, samples);
        while (_spotter.IsReady(_stream))
        {
            _spotter.Decode(_stream);
            var result = _spotter.GetResult(_stream);
            if (!string.IsNullOrEmpty(result.Keyword))
            {
                // The stream must be reset immediately after a trigger; it will
                // start accumulating acoustic context again on the next chunk.
                _spotter.Reset(_stream);
                return AppPaths.Keyword;
            }
        }

        return null;
    }

    public static (float Score, float Threshold) GetTuning(int sensitivity)
    {
        var normalized = Math.Clamp(sensitivity, 0, 100) / 100f;
        var threshold = 0.40f - (0.30f * normalized);
        var score = 1.0f + (1.0f * normalized);
        return (score, threshold);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
        _spotter.Dispose();
    }
}
