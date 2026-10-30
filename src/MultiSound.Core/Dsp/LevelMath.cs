namespace MultiSound.Core.Dsp;

public static class LevelMath
{
    /// <summary>Pico absoluto de um bloco intercalado.</summary>
    public static float Peak(ReadOnlySpan<float> samples)
    {
        float peak = 0f;
        foreach (float s in samples)
        {
            float a = Math.Abs(s);
            if (a > peak) peak = a;
        }
        return peak;
    }

    /// <summary>Converte o volume da interface (0–100%) em ganho linear com curva perceptual.</summary>
    public static float PercentToGain(double percent)
    {
        double p = Math.Clamp(percent, 0, 100) / 100.0;
        return (float)(p * p);
    }

    /// <summary>Converte pico linear para uma escala 0–1 em dB (-60 dB a 0 dB) para medidores.</summary>
    public static double PeakToMeter(float peak)
    {
        if (peak <= 0.001f) return 0;
        double db = 20 * Math.Log10(peak);
        return Math.Clamp((db + 60) / 60, 0, 1);
    }
}
