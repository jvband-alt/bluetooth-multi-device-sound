using System.Diagnostics;
using System.Runtime.InteropServices;
using MultiSound.Audio.Capture;
using MultiSound.Audio.Devices;
using MultiSound.Audio.Output;
using MultiSound.Core.Devices;
using MultiSound.Core.Dsp;
using MultiSound.Core.Formats;
using MultiSound.Core.Settings;
using MultiSound.Core.Signals;
using MultiSound.Core.Sync;

namespace MultiSound.Audio;

/// <summary>
/// Orquestra a captura do áudio do sistema e a distribuição para várias saídas, cada uma com
/// buffer, atraso e correção de drift próprios. Operações de controle rodam na <see cref="AudioThread"/>;
/// o caminho de dados roda nas threads de captura e de reprodução.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const int ReconnectIntervalMs = 3000;
    private const int GapCheckIntervalMs = 15;

    private readonly AudioThread _thread;
    private readonly DeviceCatalog _catalog;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _slotsLock = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _feedLock = new();

    private EngineConfig? _config;
    private ICaptureSource? _capture;
    private CaptureMode? _effectiveMode;
    private string? _captureSourceId;
    private string? _windowsDefaultId;
    private string? _error;
    private volatile bool _captureReady;

    // Caminho de dados (protegido por _feedLock).
    private float[] _scratch = new float[48000 * 2];
    private CaptureGapFiller? _gap;
    private TestSignalGenerator? _test;
    private volatile bool _testOn;
    private volatile OutputPipeline[] _feeds = Array.Empty<OutputPipeline>();
    private float _sourcePeak;

    private Timer? _reconnectTimer;
    private Timer? _gapTimer;
    private bool _disposed;

    public AudioEngine(AudioThread thread, DeviceCatalog catalog)
    {
        _thread = thread;
        _catalog = catalog;
        _catalog.DefaultDeviceChanged += id => _thread.Post(() => OnDefaultDeviceChanged(id));
        _catalog.DeviceStateChanged += (id, active) => _thread.Post(() => OnDeviceStateChanged(id, active));
    }

    /// <summary>Mensagens importantes para o usuário (falhas, fallback, reconexões).</summary>
    public event Action<string>? Notice;

    public bool IsRunning { get; private set; }

    public Task StartAsync(EngineConfig config) => _thread.InvokeAsync(() => StartCore(config));

    public Task StopAsync() => _thread.InvokeAsync(StopCore);

    /// <summary>
    /// Aplica uma nova configuração. Se só mudaram as saídas, adiciona/remove sem interromper as demais;
    /// se mudou a captura ou o buffer, reinicia tudo.
    /// </summary>
    public Task ApplyConfigAsync(EngineConfig config) => _thread.InvokeAsync(() =>
    {
        if (!IsRunning || _config is null) return;
        bool restart = config.Mode != _config.Mode
                       || !string.Equals(config.SourceDeviceId, _config.SourceDeviceId, StringComparison.OrdinalIgnoreCase)
                       || config.SafetyBufferMs != _config.SafetyBufferMs
                       || config.OutputLatencyMs != _config.OutputLatencyMs;
        if (restart)
        {
            StartCore(config);
            return;
        }

        _config = config;
        var wanted = config.Outputs.GroupBy(o => o.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        lock (_slotsLock)
        {
            foreach (var id in _slots.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
            {
                CloseOutput(_slots[id]);
                _slots.Remove(id);
            }
            foreach (var o in wanted.Values)
            {
                if (_slots.TryGetValue(o.Id, out var existing)) ApplyParameters(existing, o);
                else _slots[o.Id] = new Slot(o);
            }
        }
        Replan();
    });

    /// <summary>Atualiza volume/atraso/mudo imediatamente (pode ser chamado de qualquer thread).</summary>
    public void UpdateOutput(OutputConfig config)
    {
        lock (_slotsLock)
        {
            if (_slots.TryGetValue(config.Id, out var slot)) ApplyParameters(slot, config);
        }
    }

    /// <summary>Substitui o áudio capturado por bipes periódicos em todas as saídas.</summary>
    public void SetTestSignal(bool enabled)
    {
        lock (_feedLock)
        {
            _test?.Reset();
            _testOn = enabled;
        }
    }

    public EngineSnapshot GetSnapshot()
    {
        float peak;
        bool idle;
        lock (_feedLock)
        {
            peak = _sourcePeak;
            _sourcePeak = 0f;
            idle = _gap?.IsSynthesizing ?? false;
        }

        List<OutputStatus> outputs;
        lock (_slotsLock)
        {
            outputs = _slots.Values.Select(s => new OutputStatus(
                s.Config.Id, s.Config.Name, s.State, s.Message,
                s.Output?.Pipeline.GetStats(), s.Output?.Format)).ToList();
        }

        var cap = _capture;
        return new EngineSnapshot(IsRunning, cap is not null && _captureReady, idle, cap?.Description ?? "",
            cap is not null ? cap.Format : null, _effectiveMode, peak, _testOn, _error, outputs);
    }

    // ───────────────────────────── controle (AudioThread) ─────────────────────────────

    private void StartCore(EngineConfig config)
    {
        StopCore();
        _config = config;
        _error = null;
        _windowsDefaultId = _catalog.GetDefaultRenderId();

        lock (_slotsLock)
        {
            foreach (var o in config.Outputs)
                if (!_slots.ContainsKey(o.Id)) _slots[o.Id] = new Slot(o); // ignora duplicatas
        }

        IsRunning = true;
        if (!TryStartCapture())
        {
            // Sem captura não adianta abrir saídas; a reconexão tenta de novo periodicamente.
            ScheduleTimers();
            return;
        }
        Replan();
        ScheduleTimers();
    }

    private void StopCore()
    {
        IsRunning = false;
        _reconnectTimer?.Dispose();
        _reconnectTimer = null;
        _gapTimer?.Dispose();
        _gapTimer = null;

        StopCapture();
        lock (_slotsLock)
        {
            foreach (var s in _slots.Values) CloseOutput(s);
            _slots.Clear();
        }
        _feeds = Array.Empty<OutputPipeline>();
        _config = null;
        _effectiveMode = null;
        _captureSourceId = null;
        _testOn = false;
    }

    private void ScheduleTimers()
    {
        _reconnectTimer = new Timer(_ => _thread.Post(ReconnectTick), null, ReconnectIntervalMs, ReconnectIntervalMs);
        _gapTimer = new Timer(_ => GapTick(), null, GapCheckIntervalMs, GapCheckIntervalMs);
    }

    private bool TryStartCapture()
    {
        var config = _config!;
        ICaptureSource? capture = null;
        CaptureMode mode = config.Mode;

        if (mode == CaptureMode.AllAppsExceptSelf)
        {
            try
            {
                capture = new ProcessLoopbackCapture();
                Hook(capture);
                capture.Start();
            }
            catch (Exception ex)
            {
                capture?.Dispose();
                capture = null;
                mode = CaptureMode.EndpointLoopback;
                Notice?.Invoke("A captura \"todos os aplicativos\" não está disponível neste Windows " +
                               $"({Describe(ex)}). Usando o loopback da saída padrão — veja o README.");
            }
        }

        if (mode == CaptureMode.EndpointLoopback)
        {
            string? id = config.Mode == CaptureMode.EndpointLoopback ? config.SourceDeviceId ?? _windowsDefaultId : _windowsDefaultId;
            var device = id is null ? null : _catalog.TryGetActiveDevice(id);
            if (device is null)
            {
                _error = "O dispositivo de origem da captura não está disponível.";
                Notice?.Invoke(_error);
                return false;
            }
            try
            {
                capture = new EndpointLoopbackCapture(device);
                Hook(capture);
                capture.Start();
                _captureSourceId = id;
            }
            catch (Exception ex)
            {
                capture?.Dispose();
                _error = $"Não foi possível iniciar a captura: {Describe(ex)}";
                Notice?.Invoke(_error);
                return false;
            }
        }

        var format = capture!.Format;
        lock (_feedLock)
        {
            _scratch = new float[format.SampleRate * 2];
            _gap = new CaptureGapFiller(format.SampleRate);
            _gap.Start(Now);
            _test = new TestSignalGenerator(format.SampleRate);
        }
        _capture = capture;
        _effectiveMode = mode;
        _error = null;
        _captureReady = true;
        return true;
    }

    private void Hook(ICaptureSource capture)
    {
        capture.DataAvailable += data => OnCaptureData(capture, data);
        capture.Stopped += ex => _thread.Post(() => OnCaptureStopped(capture, ex));
    }

    private void StopCapture()
    {
        _captureReady = false;
        var cap = _capture;
        _capture = null;
        if (cap is not null)
        {
            try { cap.Dispose(); } catch { /* melhor esforço */ }
        }
        lock (_feedLock) _gap = null;
    }

    private void RestartCapture()
    {
        int? oldRate = _capture?.Format.SampleRate;
        StopCapture();
        if (!TryStartCapture()) return;
        if (oldRate != _capture!.Format.SampleRate)
        {
            // A taxa de entrada mudou: os pipelines precisam ser recriados.
            lock (_slotsLock)
                foreach (var s in _slots.Values) CloseOutput(s);
        }
        Replan();
    }

    /// <summary>Decide para quais saídas tocar e abre/fecha conforme necessário.</summary>
    private void Replan()
    {
        if (!IsRunning || _capture is null || _effectiveMode is null) return;
        lock (_slotsLock)
        {
            var plan = RoutingPlanner.Plan(_effectiveMode.Value, _captureSourceId, _windowsDefaultId,
                _slots.Values.Select(s => new RouteInput(s.Config.Id, true, true)));
            foreach (var slot in _slots.Values)
            {
                switch (plan[slot.Config.Id])
                {
                    case RouteDecision.SkipIsCaptureSource:
                        CloseOutput(slot);
                        slot.State = OutputState.SkippedCaptureSource;
                        slot.Message = "É a origem da captura — tocar aqui causaria eco/realimentação.";
                        break;
                    case RouteDecision.SkipIsWindowsDefault:
                        CloseOutput(slot);
                        slot.State = OutputState.SkippedWindowsDefault;
                        slot.Message = "É a saída padrão do Windows: os apps já tocam aqui diretamente (sem atraso ajustável).";
                        break;
                    default:
                        if (slot.Output is null) TryOpen(slot);
                        break;
                }
            }
        }
    }

    private void TryOpen(Slot slot)
    {
        var capture = _capture;
        if (capture is null || _config is null) return;
        var device = _catalog.TryGetActiveDevice(slot.Config.Id);
        if (device is null)
        {
            slot.State = OutputState.Reconnecting;
            slot.Message = "Desconectado — aguardando o dispositivo voltar.";
            return;
        }

        try
        {
            var options = new PipelineOptions { SafetyBufferMs = _config.SafetyBufferMs };
            var output = DeviceOutput.Open(device, capture.Format.SampleRate, options, _config.OutputLatencyMs);
            slot.Output = output;
            ApplyParameters(slot, slot.Config);
            output.Faulted += (o, ex) => _thread.Post(() => OnOutputFaulted(slot, o, ex));
            bool wasReconnecting = slot.State == OutputState.Reconnecting && slot.EverPlayed;
            slot.State = OutputState.Playing;
            slot.Message = "";
            slot.EverPlayed = true;
            RebuildFeeds();
            if (wasReconnecting) Notice?.Invoke($"{slot.Config.Name}: reconectado.");
        }
        catch (Exception ex)
        {
            slot.Output = null;
            int hr = ex.HResult;
            if (hr == unchecked((int)0x8889000A)) // AUDCLNT_E_DEVICE_IN_USE
            {
                slot.State = OutputState.Failed;
                slot.Message = "Dispositivo em uso exclusivo por outro programa. Desative o \"modo exclusivo\" nas propriedades de som.";
            }
            else if (hr == unchecked((int)0x88890004)) // AUDCLNT_E_DEVICE_INVALIDATED
            {
                slot.State = OutputState.Reconnecting;
                slot.Message = "Dispositivo indisponível — tentando novamente.";
            }
            else
            {
                slot.State = OutputState.Failed;
                slot.Message = $"Falha ao abrir: {Describe(ex)}. Nova tentativa em instantes.";
            }
        }
    }

    private void CloseOutput(Slot slot)
    {
        var o = slot.Output;
        if (o is null) return;
        slot.Output = null;
        RebuildFeeds();
        try { o.Dispose(); } catch { /* melhor esforço */ }
    }

    private void RebuildFeeds()
    {
        lock (_slotsLock)
            _feeds = _slots.Values.Where(s => s.Output is not null).Select(s => s.Output!.Pipeline).ToArray();
    }

    private static void ApplyParameters(Slot slot, OutputConfig config)
    {
        slot.Config = config;
        var p = slot.Output?.Pipeline;
        if (p is null) return;
        p.Volume = LevelMath.PercentToGain(config.VolumePercent);
        p.DelayMs = config.DelayMs;
        p.Muted = config.Muted;
    }

    private void OnOutputFaulted(Slot slot, DeviceOutput output, Exception? ex)
    {
        lock (_slotsLock)
        {
            if (!ReferenceEquals(slot.Output, output)) return;
            CloseOutput(slot);
            slot.State = OutputState.Reconnecting;
            slot.Message = "Conexão perdida — tentando reconectar…";
        }
        Notice?.Invoke($"{slot.Config.Name}: conexão perdida ({Describe(ex)}). Tentando reconectar.");
    }

    private void OnCaptureStopped(ICaptureSource capture, Exception? ex)
    {
        if (!ReferenceEquals(capture, _capture) || !IsRunning) return;
        StopCapture();
        _error = $"A captura foi interrompida ({Describe(ex)}). Tentando retomar…";
        Notice?.Invoke(_error);
    }

    private void ReconnectTick()
    {
        if (!IsRunning) return;
        if (_capture is null)
        {
            if (TryStartCapture())
            {
                lock (_slotsLock)
                    foreach (var s in _slots.Values) CloseOutput(s);
                Replan();
            }
            return;
        }
        lock (_slotsLock)
        {
            foreach (var slot in _slots.Values)
                if (slot.Output is null && slot.State is OutputState.Reconnecting or OutputState.Failed)
                    TryOpen(slot);
        }
    }

    private void OnDefaultDeviceChanged(string? id)
    {
        _windowsDefaultId = id;
        if (!IsRunning || _config is null) return;
        bool followsDefault = _effectiveMode == CaptureMode.EndpointLoopback &&
                              (_config.Mode == CaptureMode.AllAppsExceptSelf || _config.SourceDeviceId is null);
        if (followsDefault) RestartCapture();
        else Replan();
    }

    private void OnDeviceStateChanged(string id, bool active)
    {
        if (!IsRunning || !active) return;
        lock (_slotsLock)
        {
            if (_slots.TryGetValue(id, out var slot) && slot.Output is null &&
                slot.State is OutputState.Reconnecting or OutputState.Failed)
                TryOpen(slot);
        }
    }

    // ───────────────────────────── caminho de dados ─────────────────────────────

    private double Now => _clock.Elapsed.TotalSeconds;

    private void OnCaptureData(ICaptureSource capture, ReadOnlySpan<byte> data)
    {
        if (!_captureReady || !ReferenceEquals(capture, _capture)) return;
        var format = capture.Format;
        lock (_feedLock)
        {
            if (_gap is null) return;
            int frames = data.Length / format.BlockAlign;
            if (_scratch.Length < frames * 2) _scratch = new float[frames * 2];
            SampleConverter.ToStereoFloat(data[..(frames * format.BlockAlign)], format, _scratch);
            _gap.OnData(Now);
            Feed(frames);
        }
    }

    private void GapTick()
    {
        lock (_feedLock)
        {
            if (_gap is null || !_captureReady) return;
            int n = _gap.FramesToSynthesize(Now);
            int chunk = _scratch.Length / 2;
            while (n > 0)
            {
                int c = Math.Min(n, chunk);
                Array.Clear(_scratch, 0, c * 2);
                Feed(c);
                n -= c;
            }
        }
    }

    private void Feed(int frames)
    {
        var span = _scratch.AsSpan(0, frames * 2);
        if (_testOn) _test?.Generate(span, frames);
        float peak = LevelMath.Peak(span);
        if (peak > _sourcePeak) _sourcePeak = peak;
        foreach (var p in _feeds) p.Write(span);
    }

    private static string Describe(Exception? ex) => ex switch
    {
        null => "motivo desconhecido",
        COMException com => $"{com.Message.Trim()} [0x{com.HResult:X8}]",
        _ => ex.Message,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _thread.InvokeAsync(StopCore).Wait(TimeSpan.FromSeconds(5)); } catch { /* encerrando */ }
    }

    private sealed class Slot
    {
        public Slot(OutputConfig config) => Config = config;
        public OutputConfig Config;
        public DeviceOutput? Output;
        public OutputState State = OutputState.Reconnecting;
        public string Message = "Iniciando…";
        public bool EverPlayed;
    }
}
