using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace MultiSound.Core.Formats;

/// <summary>
/// Conversão entre bytes no formato do dispositivo e o formato interno (estéreo float intercalado).
/// Ordem de canais assumida: a padrão do WAVEFORMATEXTENSIBLE (FL, FR, FC, LFE, BL, BR, SL, SR).
/// </summary>
public static class SampleConverter
{
    private const float Minus3dB = 0.70710678f;

    /// <summary>Converte <paramref name="src"/> para estéreo float. Retorna o número de quadros escritos.</summary>
    public static int ToStereoFloat(ReadOnlySpan<byte> src, AudioFormatInfo format, Span<float> dst)
    {
        int channels = format.Channels;
        if (channels <= 0) throw new ArgumentException("Formato sem canais.", nameof(format));
        int frames = src.Length / format.BlockAlign;
        if (dst.Length < frames * 2) throw new ArgumentException("Destino pequeno demais.", nameof(dst));

        int bps = format.BytesPerSample;
        Span<float> frame = stackalloc float[Math.Min(channels, 32)];
        int usable = frame.Length;

        for (int f = 0; f < frames; f++)
        {
            int baseOffset = f * format.BlockAlign;
            for (int c = 0; c < usable; c++)
                frame[c] = ReadSample(src.Slice(baseOffset + c * bps, bps), format.Encoding);
            Downmix(frame, channels, out dst[f * 2], out dst[f * 2 + 1]);
        }
        return frames;
    }

    /// <summary>Converte quadros estéreo float para bytes no formato do dispositivo (com saturação).</summary>
    public static void FromStereoFloat(ReadOnlySpan<float> stereo, int frames, AudioFormatInfo format, Span<byte> dst)
    {
        if (stereo.Length < frames * 2) throw new ArgumentException("Origem pequena demais.", nameof(stereo));
        if (dst.Length < frames * format.BlockAlign) throw new ArgumentException("Destino pequeno demais.", nameof(dst));

        if (format.Encoding == SampleEncoding.Float32 && format.Channels == 2)
        {
            // Caminho rápido para o formato de mixagem mais comum do Windows.
            MemoryMarshal.AsBytes(stereo[..(frames * 2)]).CopyTo(dst);
            return;
        }

        int channels = format.Channels;
        int bps = format.BytesPerSample;
        for (int f = 0; f < frames; f++)
        {
            float l = stereo[f * 2];
            float r = stereo[f * 2 + 1];
            int baseOffset = f * format.BlockAlign;
            for (int c = 0; c < channels; c++)
            {
                float v = channels == 1 ? (l + r) * 0.5f : c switch
                {
                    0 => l,
                    1 => r,
                    _ => 0f,
                };
                WriteSample(dst.Slice(baseOffset + c * bps, bps), format.Encoding, v);
            }
        }
    }

    /// <summary>Reduz um quadro multicanal para estéreo, normalizando para evitar saturação.</summary>
    internal static void Downmix(ReadOnlySpan<float> frame, int channels, out float left, out float right)
    {
        switch (channels)
        {
            case 1:
                left = right = frame[0];
                return;
            case 2:
                left = frame[0];
                right = frame[1];
                return;
            case 3: // FL FR FC
            {
                const float n = 1f / (1f + Minus3dB);
                float c = frame[2] * Minus3dB;
                left = (frame[0] + c) * n;
                right = (frame[1] + c) * n;
                return;
            }
            case 4: // FL FR BL BR (quad)
            {
                const float n = 1f / (1f + Minus3dB);
                left = (frame[0] + frame[2] * Minus3dB) * n;
                right = (frame[1] + frame[3] * Minus3dB) * n;
                return;
            }
            case 6: // 5.1: FL FR FC LFE BL BR
            {
                const float n = 1f / (1f + 2f * Minus3dB);
                float c = frame[2] * Minus3dB;
                left = (frame[0] + c + frame[4] * Minus3dB) * n;
                right = (frame[1] + c + frame[5] * Minus3dB) * n;
                return;
            }
            case 8: // 7.1: FL FR FC LFE BL BR SL SR
            {
                const float n = 1f / (1f + 3f * Minus3dB);
                float c = frame[2] * Minus3dB;
                left = (frame[0] + c + (frame[4] + frame[6]) * Minus3dB) * n;
                right = (frame[1] + c + (frame[5] + frame[7]) * Minus3dB) * n;
                return;
            }
            default:
                left = frame[0];
                right = frame.Length > 1 ? frame[1] : frame[0];
                return;
        }
    }

    internal static float ReadSample(ReadOnlySpan<byte> b, SampleEncoding enc) => enc switch
    {
        SampleEncoding.Float32 => BinaryPrimitives.ReadSingleLittleEndian(b),
        SampleEncoding.Pcm16 => BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f,
        SampleEncoding.Pcm24 => ((b[0] | (b[1] << 8) | (b[2] << 16)) << 8 >> 8) / 8388608f,
        SampleEncoding.Pcm32 => (float)(BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648.0),
        _ => 0f,
    };

    internal static void WriteSample(Span<byte> b, SampleEncoding enc, float v)
    {
        if (float.IsNaN(v)) v = 0f;
        switch (enc)
        {
            case SampleEncoding.Float32:
                BinaryPrimitives.WriteSingleLittleEndian(b, Math.Clamp(v, -1f, 1f));
                break;
            case SampleEncoding.Pcm16:
                BinaryPrimitives.WriteInt16LittleEndian(b, (short)Math.Clamp(MathF.Round(v * 32768f), short.MinValue, short.MaxValue));
                break;
            case SampleEncoding.Pcm24:
            {
                int s = (int)Math.Clamp(MathF.Round(v * 8388608f), -8388608f, 8388607f);
                b[0] = (byte)s;
                b[1] = (byte)(s >> 8);
                b[2] = (byte)(s >> 16);
                break;
            }
            case SampleEncoding.Pcm32:
            {
                double d = Math.Clamp(Math.Round(v * 2147483648.0), int.MinValue, int.MaxValue);
                BinaryPrimitives.WriteInt32LittleEndian(b, (int)d);
                break;
            }
        }
    }
}
