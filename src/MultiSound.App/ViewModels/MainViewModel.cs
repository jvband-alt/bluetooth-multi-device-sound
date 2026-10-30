using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using MultiSound.App.Services;
using MultiSound.Audio;
using MultiSound.Audio.Capture;
using MultiSound.Audio.Devices;
using MultiSound.Core.Dsp;
using MultiSound.Core.Settings;
using CaptureMode = MultiSound.Core.Settings.CaptureMode;

namespace MultiSound.App.ViewModels;

public sealed record SourceOption(string? Id, string Name);

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _refreshTimer;

    private AudioThread? _thread;
    private DeviceCatalog? _catalog;
    private AudioEngine? _engine;
    private IReadOnlyList<DeviceInfo> _lastDevices = Array.Empty<DeviceInfo>();

    private bool _isRunning;
    private bool _isBusy;
    private bool _isTesting;
    private string _statusText = "Pronto.";
    private StatusKind _statusKind = StatusKind.Neutral;
    private string _hint = "";
    private string _notice = "";
    private double _sourceLevel;
    private string _captureInfo = "";
    private int _captureModeIndex;
    private SourceOption? _selectedSource;
    private bool _suppressSourceChange;

    public MainViewModel(SettingsStore store, AppSettings settings, Dispatcher dispatcher)
    {
        _store = store;
        _settings = settings;
        _dispatcher = dispatcher;
        _captureModeIndex = settings.CaptureMode == CaptureMode.AllAppsExceptSelf ? 0 : 1;

        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => RefreshStatus(), dispatcher);
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, (_, _) => SaveNow(), dispatcher);
        _saveTimer.Stop();
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, async (_, _) =>
        {
            _refreshTimer!.Stop();
            await RefreshDevicesAsync();
        }, dispatcher);
        _refreshTimer.Stop();

        StartCommand = new RelayCommand(async () => await StartAsync(), () => !IsRunning && !_isBusy);
        StopCommand = new RelayCommand(async () => await StopAsync(), () => IsRunning && !_isBusy);
        ToggleTestCommand = new RelayCommand(ToggleTest, () => IsRunning);
        RefreshCommand = new RelayCommand(async () => await RefreshDevicesAsync());
        OpenSoundSettingsCommand = new RelayCommand(OpenSoundSettings);
        OpenLogCommand = new RelayCommand(OpenLogFolder);
        DismissNoticeCommand = new RelayCommand(() => Notice = "");
        ApplyBufferCommand = new RelayCommand(async () => await ApplyConfigIfRunningAsync(), () => IsRunning);
    }

    /// <summary>Estado de reprodução mudou (para a bandeja).</summary>
    public event Action? PlaybackStateChanged;

    /// <summary>Aviso importante (para balão na bandeja quando a janela está oculta).</summary>
    public event Action<string>? ImportantNotice;

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();
    public ObservableCollection<SourceOption> SourceOptions { get; } = new();

    public IReadOnlyList<string> CaptureModes { get; } = new[]
    {
        "Todos os aplicativos (recomendado)",
        "Loopback de um dispositivo de saída",
    };

    public IReadOnlyList<int> LatencyOptions { get; } = new[] { 20, 30, 40, 60, 80, 100, 150 };

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ToggleTestCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenSoundSettingsCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand DismissNoticeCommand { get; }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsStopped));
                PlaybackStateChanged?.Invoke();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsStopped => !IsRunning;

    public bool IsTesting
    {
        get => _isTesting;
        private set { if (Set(ref _isTesting, value)) OnPropertyChanged(nameof(TestButtonText)); }
    }

    public string TestButtonText => IsTesting ? "ENCERRAR TESTE" : "TESTAR SINCRONIZAÇÃO";
    public string StatusText { get => _statusText; private set { if (Set(ref _statusText, value)) PlaybackStateChanged?.Invoke(); } }
    public StatusKind StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }
    public string Hint { get => _hint; private set => Set(ref _hint, value); }
    public string Notice { get => _notice; private set { if (Set(ref _notice, value)) OnPropertyChanged(nameof(HasNotice)); } }
    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public double SourceLevel { get => _sourceLevel; private set => Set(ref _sourceLevel, value); }
    public string CaptureInfo { get => _captureInfo; private set => Set(ref _captureInfo, value); }

    public int CaptureModeIndex
    {
        get => _captureModeIndex;
        set
        {
            if (!Set(ref _captureModeIndex, value)) return;
            _settings.CaptureMode = value == 0 ? CaptureMode.AllAppsExceptSelf : CaptureMode.EndpointLoopback;
            OnPropertyChanged(nameof(IsLoopbackMode));
            UpdateHint();
            ScheduleSave();
            _ = ApplyConfigIfRunningAsync();
        }
    }

    public bool IsLoopbackMode => CaptureModeIndex == 1;

    public SourceOption? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!Set(ref _selectedSource, value) || _suppressSourceChange || value is null) return;
            _settings.SourceDeviceId = value.Id;
            UpdateHint();
            ScheduleSave();
            _ = ApplyConfigIfRunningAsync();
        }
    }

    public bool StartMinimized
    {
        get => _settings.StartMinimized;
        set { _settings.StartMinimized = value; OnPropertyChanged(); ScheduleSave(); }
    }

    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            _settings.StartWithWindows = value;
            StartupRegistration.Apply(value);
            OnPropertyChanged();
            ScheduleSave();
        }
    }

    public bool MinimizeToTrayOnClose
    {
        get => _settings.MinimizeToTrayOnClose;
        set { _settings.MinimizeToTrayOnClose = value; OnPropertyChanged(); ScheduleSave(); }
    }

    public bool AutoStartPlayback
    {
        get => _settings.AutoStartPlayback;
        set { _settings.AutoStartPlayback = value; OnPropertyChanged(); ScheduleSave(); }
    }

    public int SafetyBufferMs
    {
        get => _settings.SafetyBufferMs;
        set
        {
            int v = Math.Clamp((int)Math.Round(value / 10.0) * 10, 20, 500);
            if (_settings.SafetyBufferMs == v) return;
            _settings.SafetyBufferMs = v;
            OnPropertyChanged();
            ScheduleSave();
        }
    }

    public int OutputLatencyMs
    {
        get => _settings.OutputLatencyMs;
        set { _settings.OutputLatencyMs = Math.Clamp(value, 10, 200); OnPropertyChanged(); ScheduleSave(); }
    }

    /// <summary>Aplica buffer/latência alterados (reinicia a reprodução se ativa).</summary>
    public ICommand ApplyBufferCommand { get; }

    public bool SelectedCountIsZero => !Devices.Any(d => d.IsSelected);

    // ───────────────────────────── inicialização ─────────────────────────────

    public async Task InitializeAsync()
    {
        try
        {
            _thread = new AudioThread();
            _thread.UnhandledError += ex => Log.Error("AudioThread", ex);
            _catalog = await DeviceCatalog.CreateAsync(_thread);
            _catalog.DevicesChanged += () => _dispatcher.BeginInvoke(() => { _refreshTimer.Stop(); _refreshTimer.Start(); });
            _engine = new AudioEngine(_thread, _catalog);
            _engine.Notice += msg => _dispatcher.BeginInvoke(() => ShowNotice(msg));
        }
        catch (Exception ex)
        {
            Log.Error("Inicialização do áudio", ex);
            SetStatus($"Não foi possível acessar o sistema de áudio do Windows: {ex.Message}", StatusKind.Error);
            return;
        }

        // Dispositivos salvos aparecem mesmo desconectados (reconhecimento automático quando voltarem).
        foreach (var saved in _settings.Devices)
        {
            var vm = CreateDeviceVm(saved.Id, saved.Name);
            vm.LoadSaved(saved.Selected, saved.VolumePercent, saved.DelayMs, saved.Muted);
            Devices.Add(vm);
        }

        await RefreshDevicesAsync();
        Log.Write($"MultiSound iniciado. Windows {Environment.OSVersion.Version}. Captura por app suportada (versão): {ProcessLoopbackCapture.IsLikelySupported}");

        if (_settings.AutoStartPlayback && Devices.Any(d => d.IsSelected))
            await StartAsync();
    }

    /// <summary>Somente para a captura de tela do teste de fumaça em CI (máquina sem áudio).</summary>
    internal void AddDemoDevicesForScreenshot()
    {
        var hk = CreateDeviceVm("demo-1", "Harman Kardon Go + Play 3");
        hk.UpdateFrom(new DeviceInfo("demo-1", "Harman Kardon Go + Play 3", DeviceAvailability.Active, true, false, false,
            new Core.Formats.AudioFormatInfo(48000, 2, Core.Formats.SampleEncoding.Float32)));
        hk.LoadSaved(true, 100, 0, false);
        var mifa = CreateDeviceVm("demo-2", "MIFA A90");
        mifa.UpdateFrom(new DeviceInfo("demo-2", "MIFA A90", DeviceAvailability.Active, true, false, false,
            new Core.Formats.AudioFormatInfo(44100, 2, Core.Formats.SampleEncoding.Float32)));
        mifa.LoadSaved(true, 80, 120, false);
        var rt = CreateDeviceVm("demo-3", "Alto-falantes (Realtek(R) Audio)");
        rt.UpdateFrom(new DeviceInfo("demo-3", "Alto-falantes (Realtek(R) Audio)", DeviceAvailability.Active, false, false, true, null));
        foreach (var d in new[] { hk, mifa, rt })
        {
            d.ShowIdleStatus();
            Devices.Add(d);
        }
    }

    private DeviceViewModel CreateDeviceVm(string id, string name)
    {
        var vm = new DeviceViewModel(id, name);
        vm.SelectionChanged += OnDeviceSelectionChanged;
        vm.ParametersChanged += OnDeviceParametersChanged;
        return vm;
    }

    public async Task RefreshDevicesAsync()
    {
        if (_catalog is null) return;
        try
        {
            _lastDevices = await _catalog.GetDevicesAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Enumeração", ex);
            return;
        }

        foreach (var info in _lastDevices)
        {
            var vm = Devices.FirstOrDefault(d => string.Equals(d.Id, info.Id, StringComparison.OrdinalIgnoreCase));
            if (vm is null)
            {
                // Dispositivos desativados/desconectados que nunca foram usados não poluem a lista.
                if (!info.IsActive) continue;
                vm = CreateDeviceVm(info.Id, info.Name);
                Devices.Add(vm);
            }
            vm.UpdateFrom(info);
        }
        foreach (var vm in Devices)
            if (!_lastDevices.Any(d => string.Equals(d.Id, vm.Id, StringComparison.OrdinalIgnoreCase)))
                vm.UpdateFrom(null);

        // Remove itens que não estão ativos, nem selecionados, nem salvos com ajustes.
        foreach (var vm in Devices.Where(d => !d.IsActive && !d.IsSelected && !_settings.Devices.Any(s => s.Id == d.Id)).ToList())
            Devices.Remove(vm);

        SortDevices();
        RebuildSourceOptions();
        if (!IsRunning) foreach (var vm in Devices) vm.ShowIdleStatus();
        UpdateHint();
    }

    private void SortDevices()
    {
        var ordered = Devices
            .OrderByDescending(d => d.IsSelected)
            .ThenByDescending(d => d.IsActive)
            .ThenByDescending(d => d.IsBluetooth && !d.IsHandsFree)
            .ThenBy(d => d.IsHandsFree)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            int old = Devices.IndexOf(ordered[i]);
            if (old != i) Devices.Move(old, i);
        }
    }

    private void RebuildSourceOptions()
    {
        _suppressSourceChange = true;
        try
        {
            var def = _lastDevices.FirstOrDefault(d => d.IsWindowsDefault);
            var options = new List<SourceOption>
            {
                new(null, def is null ? "Saída padrão do Windows" : $"Saída padrão do Windows ({def.Name})"),
            };
            options.AddRange(_lastDevices.Where(d => d.IsActive).Select(d => new SourceOption(d.Id, d.Name)));

            SourceOptions.Clear();
            foreach (var o in options) SourceOptions.Add(o);
            SelectedSource = SourceOptions.FirstOrDefault(o => string.Equals(o.Id, _settings.SourceDeviceId, StringComparison.OrdinalIgnoreCase))
                             ?? SourceOptions[0];
        }
        finally
        {
            _suppressSourceChange = false;
        }
    }

    // ───────────────────────────── reprodução ─────────────────────────────

    private EngineConfig BuildConfig() => new(
        _settings.CaptureMode,
        _settings.SourceDeviceId,
        _settings.SafetyBufferMs,
        _settings.OutputLatencyMs,
        Devices.Where(d => d.IsSelected).Select(d => d.ToConfig()).ToList());

    public async Task StartAsync()
    {
        if (_engine is null || IsRunning || _isBusy) return;
        var config = BuildConfig();
        if (config.Outputs.Count == 0)
        {
            SetStatus("Selecione pelo menos um dispositivo de saída.", StatusKind.Warning);
            return;
        }

        _isBusy = true;
        SetStatus("Iniciando…", StatusKind.Busy);
        try
        {
            await _engine.StartAsync(config);
            IsRunning = _engine.IsRunning;
            Log.Write($"Reprodução iniciada: modo={config.Mode}, saídas={string.Join(", ", config.Outputs.Select(o => $"{o.Name}(+{o.DelayMs}ms)"))}");
            _statusTimer.Start();
            RefreshStatus();
        }
        catch (Exception ex)
        {
            Log.Error("Iniciar", ex);
            SetStatus($"Falha ao iniciar: {ex.Message}", StatusKind.Error);
        }
        finally
        {
            _isBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public async Task StopAsync()
    {
        if (_engine is null || !IsRunning) return;
        _isBusy = true;
        try
        {
            _engine.SetTestSignal(false);
            IsTesting = false;
            await _engine.StopAsync();
            Log.Write("Reprodução parada.");
        }
        catch (Exception ex)
        {
            Log.Error("Parar", ex);
        }
        finally
        {
            _isBusy = false;
            IsRunning = false;
            _statusTimer.Stop();
            SourceLevel = 0;
            CaptureInfo = "";
            foreach (var d in Devices) d.ShowIdleStatus();
            SetStatus("Parado.", StatusKind.Neutral);
        }
    }

    public Task TogglePlaybackAsync() => IsRunning ? StopAsync() : StartAsync();

    private void ToggleTest()
    {
        if (_engine is null || !IsRunning) return;
        IsTesting = !IsTesting;
        _engine.SetTestSignal(IsTesting);
    }

    private async Task ApplyConfigIfRunningAsync()
    {
        if (_engine is null || !IsRunning) return;
        try
        {
            await _engine.ApplyConfigAsync(BuildConfig());
            if (IsTesting) _engine.SetTestSignal(true);
        }
        catch (Exception ex)
        {
            Log.Error("Aplicar configuração", ex);
        }
    }

    private void OnDeviceSelectionChanged(DeviceViewModel vm)
    {
        Persist(vm);
        if (!IsRunning) vm.ShowIdleStatus();
        UpdateHint();
        _ = ApplyConfigIfRunningAsync();
    }

    private void OnDeviceParametersChanged(DeviceViewModel vm)
    {
        Persist(vm);
        _engine?.UpdateOutput(vm.ToConfig());
    }

    private void Persist(DeviceViewModel vm)
    {
        var d = _settings.GetOrAddDevice(vm.Id, vm.Name);
        d.Selected = vm.IsSelected;
        d.VolumePercent = vm.VolumePercent;
        d.DelayMs = vm.DelayMs;
        d.Muted = vm.IsMuted;
        ScheduleSave();
    }

    // ───────────────────────────── status ─────────────────────────────

    private void RefreshStatus()
    {
        if (_engine is null) return;
        var snap = _engine.GetSnapshot();
        if (!snap.Running)
        {
            _statusTimer.Stop();
            IsRunning = false;
            return;
        }

        SourceLevel = LevelMath.PeakToMeter(snap.SourcePeak);
        CaptureInfo = snap.CaptureActive
            ? $"{snap.CaptureDescription} · {snap.CaptureFormat}"
            : "Captura inativa";

        foreach (var vm in Devices)
        {
            var status = vm.IsSelected
                ? snap.Outputs.FirstOrDefault(o => string.Equals(o.Id, vm.Id, StringComparison.OrdinalIgnoreCase))
                : null;
            vm.ShowRunningStatus(status, snap.CaptureIdle, LevelMath.PeakToMeter);
        }

        int playing = snap.Outputs.Count(o => o.State == OutputState.Playing);
        int problems = snap.Outputs.Count(o => o.State is OutputState.Reconnecting or OutputState.Failed);
        if (snap.Error is not null && !snap.CaptureActive)
            SetStatus(snap.Error, StatusKind.Error);
        else if (playing == 0)
            SetStatus("Nenhuma saída ativa. Verifique as conexões.", StatusKind.Warning);
        else if (IsTesting)
            SetStatus($"Teste de sincronização em {playing} dispositivo{(playing > 1 ? "s" : "")}.", StatusKind.Busy);
        else
        {
            string text = $"Reproduzindo em {playing} dispositivo{(playing > 1 ? "s" : "")}.";
            if (problems > 0) text += $" {problems} aguardando reconexão.";
            if (snap.CaptureIdle) text += " Nenhum aplicativo tocando no momento.";
            SetStatus(text, problems > 0 ? StatusKind.Warning : StatusKind.Ok);
        }
    }

    private void SetStatus(string text, StatusKind kind)
    {
        StatusText = text;
        StatusKind = kind;
    }

    private void ShowNotice(string msg)
    {
        Log.Write("AVISO " + msg);
        Notice = msg;
        ImportantNotice?.Invoke(msg);
    }

    /// <summary>Orientação contextual sobre a saída padrão do Windows (evita eco e duplicação).</summary>
    private void UpdateHint()
    {
        var def = _lastDevices.FirstOrDefault(d => d.IsWindowsDefault);
        var selected = Devices.Where(d => d.IsSelected).ToList();
        var defaultSelected = def is null ? null : selected.FirstOrDefault(d => d.Id == def.Id);

        if (selected.Any(d => d.IsHandsFree))
        {
            Hint = "Há um dispositivo \"viva-voz\" (Hands-Free) selecionado. Ele reduz a qualidade para mono/8–16 kHz. " +
                   "Prefira o dispositivo \"Estéreo\" da mesma caixa.";
            return;
        }

        if (!IsLoopbackMode)
        {
            Hint = defaultSelected is not null
                ? $"\"{def!.Name}\" é a saída padrão do Windows: os apps já tocam nela diretamente e o MultiSound não a duplica " +
                  "(o atraso dela não poderia ser ajustado). Para sincronizar todas as caixas, defina como saída padrão " +
                  "outro dispositivo que não esteja na lista (ex.: alto-falantes do notebook) e deixe-o no mudo."
                : "Os aplicativos tocam na saída padrão do Windows" + (def is null ? "" : $" ({def.Name})") +
                  "; o MultiSound copia esse áudio para as caixas selecionadas. Se não quiser ouvir a saída padrão, deixe-a no mudo " +
                  "e confirme no medidor \"Origem\" que o áudio continua chegando.";
        }
        else
        {
            string srcName = SelectedSource?.Id is null ? def?.Name ?? "saída padrão" : SelectedSource.Name;
            Hint = $"Capturando a mixagem de \"{srcName}\". Ela não pode receber o áudio (seria realimentação) e " +
                   "o volume/mudo dela afeta a captura: mantenha-a com volume alto, num dispositivo sem caixas ligadas " +
                   "(ex.: saída HDMI de monitor sem som). Prefira o modo \"Todos os aplicativos\" se o seu Windows suportar.";
        }
    }

    // ───────────────────────────── utilidades ─────────────────────────────

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        if (App.IsSmokeTest) return;
        try
        {
            foreach (var vm in Devices)
            {
                var d = _settings.GetOrAddDevice(vm.Id, vm.Name);
                d.Selected = vm.IsSelected;
                d.VolumePercent = vm.VolumePercent;
                d.DelayMs = vm.DelayMs;
                d.Muted = vm.IsMuted;
            }
            _store.Save(_settings);
        }
        catch (Exception ex)
        {
            Log.Error("Salvar configurações", ex);
        }
    }

    private static void OpenSoundSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); }
        catch
        {
            try { Process.Start(new ProcessStartInfo("mmsys.cpl") { UseShellExecute = true }); } catch { /* sem alternativa */ }
        }
    }

    private static void OpenLogFolder()
    {
        try
        {
            string dir = Path.GetDirectoryName(Log.FileLocation)!;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Abrir pasta", ex);
        }
    }

    public void Dispose()
    {
        _statusTimer.Stop();
        SaveNow();
        // Encerramento limpo: para saídas e captura, libera COM e a thread de controle.
        try { _engine?.Dispose(); } catch (Exception ex) { Log.Error("Encerrar motor", ex); }
        try { _catalog?.Dispose(); } catch (Exception ex) { Log.Error("Encerrar catálogo", ex); }
        try { _thread?.Dispose(); } catch (Exception ex) { Log.Error("Encerrar thread", ex); }
        Log.Write("MultiSound encerrado.");
    }
}
