namespace MultiSound.Core.Sync;

/// <summary>
/// A captura loopback do WASAPI não entrega pacotes enquanto nada está tocando. Para manter um fluxo
/// contínuo (e o controle de drift estável), esta classe calcula quanto silêncio sintetizar quando a
/// captura fica muda por mais que <see cref="ThresholdSeconds"/>. Não é thread-safe: o chamador sincroniza.
/// </summary>
public sealed class CaptureGapFiller
{
    private readonly int _sampleRate;
    private double _lastData = double.NaN;
    private double _synthClock = double.NaN;

    public CaptureGapFiller(int sampleRate, double thresholdSeconds = 0.040)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
        ThresholdSeconds = thresholdSeconds;
    }

    public double ThresholdSeconds { get; }

    /// <summary>True enquanto silêncio sintético está sendo gerado (nenhum app tocando).</summary>
    public bool IsSynthesizing => !double.IsNaN(_synthClock);

    /// <summary>Informa a chegada de dados reais da captura.</summary>
    public void OnData(double nowSeconds)
    {
        _lastData = nowSeconds;
        _synthClock = double.NaN;
    }

    /// <summary>Inicia a contagem (antes do primeiro pacote).</summary>
    public void Start(double nowSeconds)
    {
        _lastData = nowSeconds;
        _synthClock = double.NaN;
    }

    /// <summary>Quadros de silêncio a injetar agora (0 se a captura está ativa).</summary>
    public int FramesToSynthesize(double nowSeconds)
    {
        if (double.IsNaN(_lastData)) return 0;
        if (double.IsNaN(_synthClock))
        {
            if (nowSeconds - _lastData < ThresholdSeconds) return 0;
            _synthClock = _lastData; // preenche todo o intervalo desde o último pacote
        }

        int frames = (int)Math.Floor((nowSeconds - _synthClock) * _sampleRate);
        if (frames <= 0) return 0;
        _synthClock += (double)frames / _sampleRate;
        return frames;
    }
}
