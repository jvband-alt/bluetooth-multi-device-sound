using MultiSound.Core.Formats;

namespace MultiSound.Core.Tests;

public class SampleConverterTests
{
    private static readonly float[] Stereo = { 0f, 0.5f, -0.5f, 0.25f, 0.999f, -1f, -0.123f, 0.75f };

    [Theory]
    [InlineData(SampleEncoding.Float32, 1e-7)]
    [InlineData(SampleEncoding.Pcm16, 1.0 / 32768)]
    [InlineData(SampleEncoding.Pcm24, 1.0 / 8388608)]
    [InlineData(SampleEncoding.Pcm32, 1e-6)]
    public void StereoRoundTrip(SampleEncoding enc, double tolerance)
    {
        var fmt = new AudioFormatInfo(48000, 2, enc);
        int frames = Stereo.Length / 2;
        var bytes = new byte[frames * fmt.BlockAlign];
        SampleConverter.FromStereoFloat(Stereo, frames, fmt, bytes);
        var back = new float[Stereo.Length];
        Assert.Equal(frames, SampleConverter.ToStereoFloat(bytes, fmt, back));
        for (int i = 0; i < Stereo.Length; i++)
            Assert.InRange(back[i] - Stereo[i], -tolerance, tolerance);
    }

    [Fact]
    public void Pcm16_KnownValues()
    {
        var fmt = new AudioFormatInfo(44100, 2, SampleEncoding.Pcm16);
        var bytes = new byte[4];
        SampleConverter.FromStereoFloat(new[] { 1.5f, -2f }, 1, fmt, bytes); // satura
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(bytes, 0));
        Assert.Equal(short.MinValue, BitConverter.ToInt16(bytes, 2));
    }

    [Fact]
    public void Pcm24_NegativeSignExtension()
    {
        var fmt = new AudioFormatInfo(48000, 1, SampleEncoding.Pcm24);
        byte[] bytes = { 0x00, 0x00, 0x80 }; // -8388608
        var dst = new float[2];
        SampleConverter.ToStereoFloat(bytes, fmt, dst);
        Assert.Equal(-1f, dst[0]);
        Assert.Equal(-1f, dst[1]);
    }

    [Fact]
    public void Mono_IsDuplicatedToBothChannels_And_DownmixedOnOutput()
    {
        var mono = new AudioFormatInfo(48000, 1, SampleEncoding.Float32);
        var src = new byte[8];
        BitConverter.GetBytes(0.3f).CopyTo(src, 0);
        BitConverter.GetBytes(-0.6f).CopyTo(src, 4);
        var st = new float[4];
        SampleConverter.ToStereoFloat(src, mono, st);
        Assert.Equal(new[] { 0.3f, 0.3f, -0.6f, -0.6f }, st);

        var outBytes = new byte[4];
        SampleConverter.FromStereoFloat(new[] { 0.2f, 0.6f }, 1, mono, outBytes);
        Assert.Equal(0.4f, BitConverter.ToSingle(outBytes, 0), 6);
    }

    [Fact]
    public void Surround51_DownmixKeepsLeftRightSeparation_AndNoClipping()
    {
        var fmt = new AudioFormatInfo(48000, 6, SampleEncoding.Float32);
        // FL=1, FR=0, FC=1, LFE=1, BL=1, BR=0
        float[] frame = { 1, 0, 1, 1, 1, 0 };
        var bytes = new byte[24];
        for (int i = 0; i < 6; i++) BitConverter.GetBytes(frame[i]).CopyTo(bytes, i * 4);
        var st = new float[2];
        SampleConverter.ToStereoFloat(bytes, fmt, st);
        Assert.InRange(st[0], 0.99f, 1.0001f); // L: FL + C + BL normalizados
        Assert.InRange(st[1], 0.2f, 0.4f);     // R: só o centro
    }

    [Fact]
    public void StereoToSurround_PutsSignalInFrontChannels()
    {
        var fmt = new AudioFormatInfo(48000, 6, SampleEncoding.Pcm16);
        var bytes = new byte[fmt.BlockAlign];
        SampleConverter.FromStereoFloat(new[] { 0.5f, -0.5f }, 1, fmt, bytes);
        Assert.Equal(16384, BitConverter.ToInt16(bytes, 0));
        Assert.Equal(-16384, BitConverter.ToInt16(bytes, 2));
        for (int c = 2; c < 6; c++) Assert.Equal(0, BitConverter.ToInt16(bytes, c * 2));
    }

    [Fact]
    public void BlockAlign_IsCorrect()
    {
        Assert.Equal(8, new AudioFormatInfo(48000, 2, SampleEncoding.Float32).BlockAlign);
        Assert.Equal(6, new AudioFormatInfo(48000, 2, SampleEncoding.Pcm24).BlockAlign);
        Assert.Equal(12, new AudioFormatInfo(48000, 6, SampleEncoding.Pcm16).BlockAlign);
    }
}
