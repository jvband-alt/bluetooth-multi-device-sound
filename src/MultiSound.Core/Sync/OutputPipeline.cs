using System.Diagnostics;
using MultiSound.Core.Buffers;
using MultiSound.Core.Dsp;

namespace MultiSound.Core.Sync;

/// <summary>
/// Caminho de áudio de UMA saída: buffer próprio → atraso ajustável → reamostragem com correção de drift
/// → volume/fades. <see cref="Write"/> é chamado pela thread de captura e <see cref="Render"/> pela thread
/// de reprodução do dispositivo; ajustes (volume, atraso, mudo) podem vir de qualquer thread.
/// </summary>
public sealed class OutputPipeline
{
    private readonly StereoRingBuffer _ring;
    private readonly StereoResampler _resampler;
    private readonly DriftController _controller;
    private readonly PipelineOptions _options;
    private readonly Func<double> _clockSeconds;
    private readonly StereoFrameSource _source;
    private readonly int _fadeFrames;

    private volatile int _delayMs;
    private volatile float _volume = 1f;
    private volatile bool _muted;

    // Estado da thread de reprodução.
    private PipelineState _state = PipelineState.Priming;
    private int _lastTargetFrames = -1;
    private double _correction = 1.0;
    private float _fadeGain;
    private float _currentVolume = -1f;

    // Estatísticas (escritas pelas threads de áudio, lidas pela UI).
    private long _underruns;
    private long _overflows;
    private long _resyncs;
    private double _lastWriteTime = double.NegativeInfinity;
    private volatile float _peak;
    private volatile float _lastFillMs;

    public OutputPipeline(int inputRate, int outputRate, PipelineOptions? options = null, Func<double>? clockSeconds = null)
    {
        _options = options ?? new PipelineOptions();
        InputRate = inputRate;
        OutputRate = outputRate;
        int capacityMs = _options.MaxDelayMs + _options.SafetyBufferMs + 1000;
        _ring = new StereoRingBuffer((int)((long)inputRate * capacityMs / 1000));
        _resampler = new StereoResampler(inputRate, outputRate);
        _controller = new DriftController(inputRate);
        _fadeFrames = Math.Max(1, (int)(outputRate * _options.FadeMs / 1000.0));
        _source = (dest, frames) => _ring.Read(dest, frames);
        if (clockSeconds is null)
        {
            var sw = Stopwatch.StartNew();
            clockSeconds = () => sw.Elapsed.TotalSeconds;
        }
        _clockSeconds = clockSeconds;
    }

    public int InputRate { get; }
    public int OutputRate { get; }

    /// <summary>Atraso adicional desta saída, em ms (0 até MaxDelayMs). Pode ser alterado durante a reprodução.</summary>
    public int DelayMs
    {
        get => _delayMs;
        set => _delayMs = Math.Clamp(value, 0, _options.MaxDelayMs);
    }

    /// <summary>Ganho linear (0 a 2).</summary>
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 2f);
    }

    public bool Muted
    {
        get => _muted;
        set => _muted = value;
    }

    public PipelineState State => _state;

    private int TargetFrames => (int)((long)InputRate * (_options.SafetyBufferMs + _delayMs) / 1000);

    /// <summary>Recebe quadros estéreo na taxa de entrada (thread de captura).</summary>
    public void Write(ReadOnlySpan<float> stereo)
    {
        int dropped = _ring.Write(stereo);
        if (dropped > 0) Interlocked.Increment(ref _overflows);
        Volatile.Write(ref _lastWriteTime, _clockSeconds());
    }

    /// <summary>Preenche <paramref name="frames"/> quadros estéreo na taxa de saída (thread de reprodução).</summary>
    public void Render(Span<float> output, int frames)
    {
        Span<float> dst = output[..(frames * 2)];
        int target = TargetFrames;

        if (target != _lastTargetFrames)
        {
            // Atraso alterado: faz uma rampa curta e salta para a nova posição (sem estalos).
            if (_lastTargetFrames >= 0 && _state == PipelineState.Playing) _state = PipelineState.FadingOut;
            _lastTargetFrames = target;
        }

        if (_state == PipelineState.Priming && !TryLeavePriming(target))
        {
            dst.Clear();
            UpdateFillStat();
            return;
        }

        int produced = _resampler.Process(dst, frames, _correction, _source);
        ApplyGain(dst, produced);
        if (produced < frames)
        {
            // Faltaram dados: completa com silêncio e volta a acumular até o alvo.
            dst[(produced * 2)..].Clear();
            FadeTail(dst, produced, Math.Min(produced, 64));
            bool idle = _clockSeconds() - Volatile.Read(ref _lastWriteTime) > _options.IdleThresholdMs / 1000.0;
            if (!idle && _state != PipelineState.Priming) Interlocked.Increment(ref _underruns);
            EnterPriming();
        }

        if (_state == PipelineState.Playing)
        {
            // Nível no meio do bloco: torna a medida independente do tamanho do período do dispositivo.
            double fill = _ring.Count + _resampler.BufferedInputFrames + frames * _resampler.NominalStep * 0.5;
            _correction = _controller.Update(fill, target, (double)frames / OutputRate);
            if (_controller.HardResyncRequested)
            {
                _state = PipelineState.FadingOut;
                Interlocked.Increment(ref _resyncs);
            }
        }
        UpdateFillStat();
    }

    public PipelineStats GetStats()
    {
        float peak = _peak;
        _peak = 0f;
        return new PipelineStats(
            _state,
            _lastFillMs,
            _options.SafetyBufferMs + _delayMs,
            (_correction - 1.0) * 1e6,
            Interlocked.Read(ref _underruns),
            Interlocked.Read(ref _overflows),
            Interlocked.Read(ref _resyncs),
            peak);
    }

    private bool TryLeavePriming(int target)
    {
        int count = _ring.Count;
        if (count < target) return false;
        int slack = (int)(InputRate * _options.PrimeSlackMs / 1000.0);
        if (count - target > slack) _ring.Skip(count - target);
        _resampler.Reset();
        _controller.Reset(target);
        _fadeGain = 0f;
        _state = PipelineState.Playing;
        return true;
    }

    private void EnterPriming()
    {
        _state = PipelineState.Priming;
        _resampler.Reset();
        _fadeGain = 0f;
    }

    private void ApplyGain(Span<float> dst, int frames)
    {
        float targetVolume = _muted ? 0f : _volume;
        if (_currentVolume < 0) _currentVolume = targetVolume;
        float volStep = frames > 0 ? (targetVolume - _currentVolume) / frames : 0f;
        float fadeStep = 1f / _fadeFrames;
        float vol = _currentVolume;
        float fade = _fadeGain;
        bool fadingOut = _state == PipelineState.FadingOut;
        float peak = _peak;

        for (int f = 0; f < frames; f++)
        {
            if (fadingOut)
            {
                fade -= fadeStep;
                if (fade <= 0f)
                {
                    // Fim da rampa: silencia o resto do bloco e reposiciona o buffer (salto).
                    dst[(f * 2)..(frames * 2)].Clear();
                    _currentVolume = targetVolume;
                    _peak = peak;
                    EnterPriming();
                    return;
                }
            }
            else if (fade < 1f)
            {
                fade = Math.Min(1f, fade + fadeStep);
            }

            vol += volStep;
            float g = vol * fade;
            float l = dst[f * 2] * g;
            float r = dst[f * 2 + 1] * g;
            dst[f * 2] = l;
            dst[f * 2 + 1] = r;
            float a = Math.Max(Math.Abs(l), Math.Abs(r));
            if (a > peak) peak = a;
        }

        _fadeGain = fade;
        _currentVolume = targetVolume;
        _peak = peak;
    }

    private static void FadeTail(Span<float> dst, int produced, int fadeLen)
    {
        if (fadeLen <= 0) return;
        int start = produced - fadeLen;
        for (int i = 0; i < fadeLen; i++)
        {
            float g = 1f - (float)(i + 1) / fadeLen;
            dst[(start + i) * 2] *= g;
            dst[(start + i) * 2 + 1] *= g;
        }
    }

    private void UpdateFillStat()
    {
        _lastFillMs = (float)((_ring.Count + _resampler.BufferedInputFrames) * 1000.0 / InputRate);
    }
}
