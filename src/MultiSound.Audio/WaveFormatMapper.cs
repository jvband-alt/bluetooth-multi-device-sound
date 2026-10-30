using MultiSound.Core.Formats;
using NAudio.Wave;

namespace MultiSound.Audio;

internal static class WaveFormatMapper
{
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public static AudioFormatInfo? TryMap(WaveFormat wf)
    {
        bool isFloat;
        switch (wf.Encoding)
        {
            case WaveFormatEncoding.IeeeFloat:
                isFloat = true;
                break;
            case WaveFormatEncoding.Pcm:
                isFloat = false;
                break;
            case WaveFormatEncoding.Extensible when wf is WaveFormatExtensible ext:
                if (ext.SubFormat == SubtypeFloat) isFloat = true;
                else if (ext.SubFormat == SubtypePcm) isFloat = false;
                else return null;
                break;
            default:
                return null;
        }

        SampleEncoding? enc = (isFloat, wf.BitsPerSample) switch
        {
            (true, 32) => SampleEncoding.Float32,
            (false, 16) => SampleEncoding.Pcm16,
            (false, 24) => SampleEncoding.Pcm24,
            (false, 32) => SampleEncoding.Pcm32,
            _ => null,
        };
        if (enc is null || wf.Channels <= 0 || wf.SampleRate <= 0) return null;
        var info = new AudioFormatInfo(wf.SampleRate, wf.Channels, enc.Value);
        return info.BlockAlign == wf.BlockAlign ? info : null;
    }
}
