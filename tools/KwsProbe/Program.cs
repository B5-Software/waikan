using NAudio.Wave;
using SherpaOnnx;

if (args.Length < 1)
{
    Console.WriteLine("Usage: KwsProbe <wav> [wav...] [--threshold 0.25] [--score 1.5]");
    return 2;
}

float threshold = 0.25f;
float score = 1.5f;
var files = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--threshold" && i + 1 < args.Length)
    {
        threshold = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    }
    else if (args[i] == "--score" && i + 1 < args.Length)
    {
        score = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    }
    else
    {
        files.Add(args[i]);
    }
}

var root = FindProjectRoot();
var modelDir = Path.Combine(root, "models", "kws");
var keywordFile = Path.Combine(root, "models", "kws", "keywords.txt");
if (!File.Exists(keywordFile))
{
    File.WriteAllText(keywordFile, "w ài k ān @外刊\n");
}

Console.WriteLine($"threshold={threshold}, score={score}, keywordFile={keywordFile}");
foreach (var line in File.ReadAllLines(keywordFile))
{
    Console.WriteLine($"keyword: {line}");
}

var config = new KeywordSpotterConfig();
config.FeatConfig.SampleRate = 16000;
config.FeatConfig.FeatureDim = 80;
config.ModelConfig.Transducer.Encoder = Path.Combine(modelDir, "encoder.int8.onnx");
config.ModelConfig.Transducer.Decoder = Path.Combine(modelDir, "decoder.onnx");
config.ModelConfig.Transducer.Joiner = Path.Combine(modelDir, "joiner.int8.onnx");
config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
config.ModelConfig.Provider = "cpu";
config.ModelConfig.NumThreads = 1;
config.KeywordsFile = keywordFile;
config.KeywordsScore = score;
config.KeywordsThreshold = threshold;
config.MaxActivePaths = 4;

using var kws = new KeywordSpotter(config);
Console.WriteLine("KWS ready");

foreach (var file in files)
{
    using var reader = new AudioFileReader(file);
    var samples = new List<float>();
    var buf = new float[reader.WaveFormat.SampleRate * 2];
    int n;
    while ((n = reader.Read(buf, 0, buf.Length)) > 0)
    {
        samples.AddRange(buf.AsSpan(0, n).ToArray());
    }

    var sampleRate = reader.WaveFormat.SampleRate;
    var stream = kws.CreateStream();
    stream.AcceptWaveform(sampleRate, samples.ToArray());
    stream.AcceptWaveform(sampleRate, new float[(int)(sampleRate * 0.66)]);
    stream.InputFinished();
    int hits = 0;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (kws.IsReady(stream))
    {
        kws.Decode(stream);
        var result = kws.GetResult(stream);
        if (result.Keyword != string.Empty)
        {
            hits++;
            Console.WriteLine($"HIT {Path.GetFileName(file)}: {result.Keyword}");
            kws.Reset(stream);
        }
    }
    Console.WriteLine($"RESULT {Path.GetFileName(file)}: hits={hits}, elapsed={sw.ElapsedMilliseconds}ms, audio={samples.Count / (double)sampleRate:F1}s");
}

return 0;

static string FindProjectRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "models", "kws")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    return Directory.GetCurrentDirectory();
}
