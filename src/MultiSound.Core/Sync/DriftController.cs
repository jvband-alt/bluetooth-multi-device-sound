namespace MultiSound.Core.Sync;

/// <summary>
/// Controlador PI que mantém o nível do buffer de uma saída no alvo, ajustando levemente a razão
/// de reamostragem. Compensa a diferença entre o relógio da captura e o relógio de cada dispositivo
/// (drift), de forma gradual e inaudível (limitado a <see cref="MaxCorrection"/>).
/// </summary>
public sealed class DriftController
{
    private readonly double _sampleRate;
    private double _ema;
    private double _integral;
    private double _bigErrorSeconds;
    private bool _initialized;

    public DriftController(int inputSampleRate)
    {
        if (inputSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputSampleRate));
        _sampleRate = inputSampleRate;
    }

    /// <summary>Ganho proporcional (1/s).</summary>
    public double Kp { get; init; } = 0.1;

    /// <summary>Ganho integral (1/s²).</summary>
    public double Ki { get; init; } = 0.005;

    /// <summary>Constante de tempo da média do nível do buffer (s).</summary>
    public double SmoothingSeconds { get; init; } = 1.0;

    /// <summary>Correção máxima da razão (0.002 = 2000 ppm ≈ 3,5 cents).</summary>
    public double MaxCorrection { get; init; } = 0.002;

    /// <summary>Erro a partir do qual a correção gradual é insuficiente e se pede um salto (s).</summary>
    public double HardResyncThresholdSeconds { get; init; } = 0.060;

    /// <summary>Tempo que o erro grande precisa persistir antes do salto (s).</summary>
    public double HardResyncHoldSeconds { get; init; } = 0.5;

    public double Correction { get; private set; } = 1.0;
    public double SmoothedFillFrames => _ema;
    public double ErrorSeconds { get; private set; }
    public bool HardResyncRequested { get; private set; }

    public void Reset(double fillFrames)
    {
        _ema = fillFrames;
        _bigErrorSeconds = 0;
        HardResyncRequested = false;
        ErrorSeconds = 0;
        _initialized = true;
        // A integral é mantida: ela representa o drift do relógio, que não muda após um salto.
        Correction = Math.Clamp(1.0 + Ki * _integral, 1.0 - MaxCorrection, 1.0 + MaxCorrection);
    }

    /// <summary>Atualiza o controlador. Retorna o fator de correção a aplicar no próximo bloco.</summary>
    public double Update(double fillFrames, double targetFrames, double dtSeconds)
    {
        if (!_initialized) Reset(fillFrames);
        if (dtSeconds <= 0) return Correction;

        double alpha = 1.0 - Math.Exp(-dtSeconds / SmoothingSeconds);
        _ema += (fillFrames - _ema) * alpha;

        double error = (_ema - targetFrames) / _sampleRate;
        ErrorSeconds = error;

        if (Math.Abs(error) > HardResyncThresholdSeconds)
        {
            _bigErrorSeconds += dtSeconds;
            if (_bigErrorSeconds >= HardResyncHoldSeconds) HardResyncRequested = true;
        }
        else
        {
            _bigErrorSeconds = 0;
        }

        double integralLimit = Ki > 0 ? MaxCorrection / Ki : 0;
        _integral = Math.Clamp(_integral + error * dtSeconds, -integralLimit, integralLimit);
        double c = Kp * error + Ki * _integral;
        Correction = 1.0 + Math.Clamp(c, -MaxCorrection, MaxCorrection);
        return Correction;
    }
}
