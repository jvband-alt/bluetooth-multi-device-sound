namespace MultiSound.Core.Measurement;

public readonly record struct CorrelationPeak(double Lag, double Confidence, double Amplitude);

/// <summary>Localiza uma referência conhecida dentro de um sinal por correlação cruzada (FFT).</summary>
public static class CrossCorrelator
{
    /// <summary>
    /// Retorna o deslocamento (em amostras, com interpolação sub-amostra) em que <paramref name="reference"/>
    /// melhor se alinha a <paramref name="signal"/>. A confiança é a razão entre o pico e o nível típico
    /// (mediana) da correlação: valores altos indicam detecção clara.
    /// </summary>
    public static CorrelationPeak FindPeak(ReadOnlySpan<float> signal, ReadOnlySpan<float> reference)
    {
        if (signal.Length < reference.Length || reference.Length == 0)
            return new CorrelationPeak(double.NaN, 0, 0);

        int n = Fft.NextPowerOfTwo(signal.Length + reference.Length);
        var sr = new double[n]; var si = new double[n];
        var rr = new double[n]; var ri = new double[n];
        for (int i = 0; i < signal.Length; i++) sr[i] = signal[i];
        for (int i = 0; i < reference.Length; i++) rr[i] = reference[i];
        Fft.Transform(sr, si, false);
        Fft.Transform(rr, ri, false);
        // S · conj(R)
        for (int i = 0; i < n; i++)
        {
            double a = sr[i], b = si[i], c = rr[i], d = -ri[i];
            sr[i] = a * c - b * d;
            si[i] = a * d + b * c;
        }
        Fft.Transform(sr, si, true);

        int maxLag = signal.Length - reference.Length;
        int best = 0;
        double bestVal = double.MinValue;
        var mags = new double[maxLag + 1];
        for (int lag = 0; lag <= maxLag; lag++)
        {
            double v = Math.Abs(sr[lag]);
            mags[lag] = v;
            if (v > bestVal) { bestVal = v; best = lag; }
        }

        // Interpolação parabólica em torno do pico.
        double offset = 0;
        if (best > 0 && best < maxLag)
        {
            double y0 = mags[best - 1], y1 = mags[best], y2 = mags[best + 1];
            double den = y0 - 2 * y1 + y2;
            if (Math.Abs(den) > 1e-20) offset = Math.Clamp(0.5 * (y0 - y2) / den, -0.5, 0.5);
        }

        Array.Sort(mags);
        double median = mags[mags.Length / 2];
        double confidence = median > 1e-20 ? bestVal / median : (bestVal > 0 ? double.PositiveInfinity : 0);

        double refEnergy = 0;
        foreach (float x in reference) refEnergy += x * (double)x;
        return new CorrelationPeak(best + offset, confidence, refEnergy > 0 ? bestVal / refEnergy : 0);
    }
}
