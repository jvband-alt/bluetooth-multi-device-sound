namespace MultiSound.Core.Measurement;

/// <summary>
/// Varredura linear de frequência (chirp) com bordas suavizadas. Sua autocorrelação tem um pico
/// estreito, o que permite localizar com precisão o instante em que ela chega ao microfone.
/// </summary>
public static class Chirp
{
    public const double DefaultDurationSeconds = 0.30;
    public const double DefaultStartHz = 800;
    public const double DefaultEndHz = 6000;

    public static float[] Create(int sampleRate, double durationSeconds = DefaultDurationSeconds,
        double startHz = DefaultStartHz, double endHz = DefaultEndHz, float amplitude = 0.6f)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        int n = Math.Max(1, (int)(sampleRate * durationSeconds));
        var s = new float[n];
        double k = (endHz - startHz) / durationSeconds;
        int fade = Math.Max(1, n / 20); // 5 % de rampa em cada borda (sem estalo)
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / sampleRate;
            double phase = 2 * Math.PI * (startHz * t + 0.5 * k * t * t);
            double env = 1.0;
            if (i < fade) env = 0.5 - 0.5 * Math.Cos(Math.PI * i / fade);
            else if (i >= n - fade) env = 0.5 - 0.5 * Math.Cos(Math.PI * (n - 1 - i) / fade);
            s[i] = (float)(amplitude * env * Math.Sin(phase));
        }
        return s;
    }
}
