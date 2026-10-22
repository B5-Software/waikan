using NAudio.Wave;
using Waikan.Services;

namespace Waikan.Tests;

public sealed class AudioPipelineTests
{
    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void Downmixes_Stereo_And_Resamples_To_16k_Mono(int sourceRate)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sourceRate, 2);
        var buffered = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };

        var seconds = 1.0;
        var frames = (int)(sourceRate * seconds);
        var interleaved = new float[frames * 2];
        for (var index = 0; index < frames; index++)
        {
            var sample = (float)(0.25 * Math.Sin(2 * Math.PI * 440 * index / sourceRate));
            interleaved[index * 2] = sample;
            interleaved[index * 2 + 1] = sample;
        }

        var bytes = new byte[interleaved.Length * sizeof(float)];
        Buffer.BlockCopy(interleaved, 0, bytes, 0, bytes.Length);
        buffered.AddSamples(bytes, 0, bytes.Length);

        var provider = AudioCaptureService.CreateMono16kProvider(buffered);
        Assert.Equal(16000, provider.WaveFormat.SampleRate);
        Assert.Equal(1, provider.WaveFormat.Channels);

        var output = new List<float>();
        var chunk = new float[1600];
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var read = provider.Read(chunk, 0, chunk.Length);
            if (read <= 0)
            {
                break;
            }

            output.AddRange(chunk.AsSpan(0, read).ToArray());
        }

        Assert.InRange(output.Count, 15500, 16500);
        Assert.True(output.Max() > 0.05);
        Assert.True(output.Min() < -0.05);
    }
}
