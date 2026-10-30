namespace MultiSound.Core.Signals;

/// <summary>
/// Sinal de teste de sincronização: um bipe curto (tom com envelope Hann) a cada intervalo.
/// O mesmo sinal é enviado a todas as saídas; o usuário ajusta os atrasos até ouvir um único bipe.
/// </summary>
public sealed class TestSignalGenerator
{
    private readonly int _sampleRate;
    private readonly int _periodFrames;
    private readonly int _beepFrames;
    private readonly double _phaseIncrement;
    private long _position;

    public TestSignalGenerator(int sampleRate, double intervalSeconds = 1.0, double beepSeconds = 0.030,
        double frequencyHz = 1500, float amplitude = 0.5f)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
        _periodFrames = Math.Max(1, (int)(sampleRate * intervalSeconds));
        _beepFrames = Math.Clamp((int)(sampleRate * beepSeconds), 1, _periodFrames);
        _phaseIncrement = 2 * Math.PI * frequencyHz / sampleRate;
        Amplitude = amplitude;
    }

    public float Amplitude { get; }
    public int PeriodFrames => _periodFrames;
    public int BeepFrames => _beepFrames;

    public void Reset() => _position = 0;

    /// <summary>Gera <paramref name="frames"/> quadros estéreo.</summary>
    public void Generate(Span<float> stereo, int frames)
    {
        for (int f = 0; f < frames; f++)
        {
            int inPeriod = (int)(_position % _periodFrames);
            float v = 0f;
            if (inPeriod < _beepFrames)
            {
                double env = 0.5 - 0.5 * Math.Cos(2 * Math.PI * inPeriod / _beepFrames);
                v = (float)(Amplitude * env * Math.Sin(_phaseIncrement * inPeriod));
            }
            stereo[f * 2] = v;
            stereo[f * 2 + 1] = v;
            _position++;
        }
    }
}
