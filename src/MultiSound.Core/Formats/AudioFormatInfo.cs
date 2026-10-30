namespace MultiSound.Core.Formats;

/// <summary>Codificação das amostras de um fluxo PCM intercalado.</summary>
public enum SampleEncoding
{
    Float32,
    Pcm16,
    Pcm24,
    /// <summary>Inteiro de 32 bits (inclui 24 bits válidos em contêiner de 32).</summary>
    Pcm32,
}

/// <summary>Descrição mínima de um formato de áudio intercalado (independente de NAudio).</summary>
public readonly record struct AudioFormatInfo(int SampleRate, int Channels, SampleEncoding Encoding)
{
    public int BytesPerSample => Encoding switch
    {
        SampleEncoding.Pcm16 => 2,
        SampleEncoding.Pcm24 => 3,
        _ => 4,
    };

    public int BlockAlign => BytesPerSample * Channels;

    public override string ToString()
    {
        string enc = Encoding switch
        {
            SampleEncoding.Float32 => "32-bit float",
            SampleEncoding.Pcm16 => "16-bit",
            SampleEncoding.Pcm24 => "24-bit",
            _ => "32-bit",
        };
        return $"{SampleRate / 1000.0:0.#} kHz · {Channels} canais · {enc}";
    }
}
