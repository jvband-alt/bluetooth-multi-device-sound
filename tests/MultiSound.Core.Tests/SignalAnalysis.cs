namespace MultiSound.Core.Tests;

internal static class SignalAnalysis
{
    /// <summary>Ajusta A·sin + B·cos na frequência dada; retorna amplitude e SNR (dB) do resíduo.</summary>
    public static (double Amplitude, double SnrDb) FitSine(ReadOnlySpan<float> x, int stride, int offset, double freq, double rate)
    {
        int n = (x.Length - offset) / stride;
        double ss = 0, sc = 0, cc = 0, xs = 0, xc = 0;
        for (int i = 0; i < n; i++)
        {
            double w = 2 * Math.PI * freq * i / rate;
            double s = Math.Sin(w), c = Math.Cos(w), v = x[offset + i * stride];
            ss += s * s; sc += s * c; cc += c * c; xs += v * s; xc += v * c;
        }
        double det = ss * cc - sc * sc;
        double a = (xs * cc - xc * sc) / det;
        double b = (xc * ss - xs * sc) / det;
        double residual = 0, signal = 0;
        for (int i = 0; i < n; i++)
        {
            double w = 2 * Math.PI * freq * i / rate;
            double fit = a * Math.Sin(w) + b * Math.Cos(w);
            double v = x[offset + i * stride];
            residual += (v - fit) * (v - fit);
            signal += fit * fit;
        }
        return (Math.Sqrt(a * a + b * b), 10 * Math.Log10(signal / Math.Max(residual, 1e-30)));
    }

    public static float[] Sine(int frames, double freq, double rate, float amp = 0.5f)
    {
        var a = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(amp * Math.Sin(2 * Math.PI * freq * i / rate));
            a[i * 2] = v;
            a[i * 2 + 1] = -v;
        }
        return a;
    }
}
