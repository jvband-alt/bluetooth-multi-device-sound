using MultiSound.Core.Dsp;

namespace MultiSound.Core.Tests;

public class StereoResamplerTests
{
    private static StereoFrameSource FromArray(float[] data)
    {
        int pos = 0;
        return (dest, frames) =>
        {
            int n = Math.Min(frames, data.Length / 2 - pos);
            data.AsSpan(pos * 2, n * 2).CopyTo(dest);
            pos += n;
            return n;
        };
    }

    [Fact]
    public void SameRate_IsTransparentPassthrough()
    {
        var input = SignalAnalysis.Sine(4000, 997, 48000);
        var rs = new StereoResampler(48000, 48000);
        var output = new float[3000 * 2];
        int produced = 0;
        var src = FromArray(input);
        while (produced < 3000)
            produced += rs.Process(output.AsSpan(produced * 2), Math.Min(256, 3000 - produced), 1.0, src);
        // A saída 0 está centrada no quadro de entrada 0 (o silêncio inicial fica ANTES dele).
        for (int i = 0; i < 3000 * 2; i++) Assert.Equal(input[i], output[i], 6);
    }

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(48000, 44100)]
    [InlineData(96000, 48000)]
    [InlineData(48000, 96000)]
    public void SineIsPreserved(int inRate, int outRate)
    {
        const double freq = 1000;
        int inFrames = inRate; // 1 s
        var input = SignalAnalysis.Sine(inFrames, freq, inRate);
        var rs = new StereoResampler(inRate, outRate);
        int outFrames = (int)(outRate * 0.9);
        var output = new float[outFrames * 2];
        var src = FromArray(input);
        int produced = 0;
        while (produced < outFrames)
        {
            int got = rs.Process(output.AsSpan(produced * 2), Math.Min(441, outFrames - produced), 1.0, src);
            Assert.True(got > 0);
            produced += got;
        }
        // Ignora o transitório inicial do filtro.
        var steady = output.AsSpan(200 * 2);
        var (ampL, snrL) = SignalAnalysis.FitSine(steady, 2, 0, freq, outRate);
        var (ampR, snrR) = SignalAnalysis.FitSine(steady, 2, 1, freq, outRate);
        Assert.InRange(ampL, 0.49, 0.51);
        Assert.InRange(ampR, 0.49, 0.51);
        Assert.True(snrL > 80, $"SNR L = {snrL:0.0} dB");
        Assert.True(snrR > 80, $"SNR R = {snrR:0.0} dB");
    }

    [Fact]
    public void HighFrequencyAt48kTo44k_StaysClean()
    {
        var input = SignalAnalysis.Sine(48000, 15000, 48000);
        var rs = new StereoResampler(48000, 44100);
        var output = new float[40000 * 2];
        var src = FromArray(input);
        int produced = 0;
        while (produced < 40000) produced += rs.Process(output.AsSpan(produced * 2), 40000 - produced, 1.0, src);
        var (amp, snr) = SignalAnalysis.FitSine(output.AsSpan(400), 2, 0, 15000, 44100);
        Assert.InRange(amp, 0.47, 0.51);
        Assert.True(snr > 60, $"SNR = {snr:0.0} dB");
    }

    [Theory]
    [InlineData(1.002)]
    [InlineData(0.998)]
    public void Correction_ChangesInputConsumptionRate(double correction)
    {
        var input = new float[200_000 * 2];
        int consumed = 0;
        StereoFrameSource src = (dest, frames) => { consumed += frames; return frames; };
        var rs = new StereoResampler(48000, 48000);
        var output = new float[1024 * 2];
        int total = 0;
        for (int i = 0; i < 100; i++) total += rs.Process(output, 1024, correction, src);
        double actualRatio = (consumed - rs.BufferedInputFrames) / total;
        Assert.InRange(actualRatio, correction - 0.0005, correction + 0.0005);
    }

    [Fact]
    public void Underrun_ReturnsFewerFrames()
    {
        var rs = new StereoResampler(48000, 48000);
        var src = FromArray(new float[100 * 2]);
        var output = new float[1000 * 2];
        int got = rs.Process(output, 1000, 1.0, src);
        Assert.True(got < 100);
        Assert.True(got > 50);
    }
}
