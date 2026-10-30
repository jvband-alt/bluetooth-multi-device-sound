using System.Windows.Input;
using MultiSound.Audio;
using MultiSound.Audio.Devices;
using MultiSound.Core.Sync;

namespace MultiSound.App.ViewModels;

public enum StatusKind
{
    Neutral,
    Ok,
    Busy,
    Warning,
    Error,
}

public sealed class DeviceViewModel : ObservableObject
{
    public const int MaxDelayMs = 1000;

    private string _name;
    private bool _isSelected;
    private double _volumePercent = 100;
    private int _delayMs;
    private bool _isMuted;
    private bool _isActive;
    private bool _isBluetooth;
    private bool _isHandsFree;
    private bool _isWindowsDefault;
    private string _format = "";
    private string _statusText = "";
    private StatusKind _statusKind;
    private string _detail = "";
    private string _stats = "";
    private double _level;

    public DeviceViewModel(string id, string name)
    {
        Id = id;
        _name = name;
        DelayMinusCommand = new RelayCommand(() => DelayMs -= 5);
        DelayPlusCommand = new RelayCommand(() => DelayMs += 5);
        ToggleMuteCommand = new RelayCommand(() => IsMuted = !IsMuted);
    }

    public string Id { get; }

    /// <summary>Seleção mudou (precisa reconfigurar o motor).</summary>
    public event Action<DeviceViewModel>? SelectionChanged;

    /// <summary>Volume/atraso/mudo mudou (aplicação imediata).</summary>
    public event Action<DeviceViewModel>? ParametersChanged;

    public ICommand DelayMinusCommand { get; }
    public ICommand DelayPlusCommand { get; }
    public ICommand ToggleMuteCommand { get; }

    public string Name { get => _name; set => Set(ref _name, value); }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) SelectionChanged?.Invoke(this); }
    }

    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            if (Set(ref _volumePercent, Math.Round(Math.Clamp(value, 0, 100))))
            {
                OnPropertyChanged(nameof(VolumeText));
                ParametersChanged?.Invoke(this);
            }
        }
    }

    public string VolumeText => $"{VolumePercent:0}%";

    public int DelayMs
    {
        get => _delayMs;
        set
        {
            if (Set(ref _delayMs, Math.Clamp(value, 0, MaxDelayMs)))
            {
                OnPropertyChanged(nameof(DelayText));
                ParametersChanged?.Invoke(this);
            }
        }
    }

    /// <summary>Texto editável do atraso ("+120").</summary>
    public string DelayText
    {
        get => DelayMs == 0 ? "0" : $"+{DelayMs}";
        set
        {
            var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
            DelayMs = int.TryParse(digits, out int v) ? v : 0;
            OnPropertyChanged();
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set { if (Set(ref _isMuted, value)) ParametersChanged?.Invoke(this); }
    }

    public bool IsActive { get => _isActive; private set => Set(ref _isActive, value); }
    public bool IsBluetooth { get => _isBluetooth; private set => Set(ref _isBluetooth, value); }
    public bool IsHandsFree { get => _isHandsFree; private set => Set(ref _isHandsFree, value); }
    public bool IsWindowsDefault { get => _isWindowsDefault; private set => Set(ref _isWindowsDefault, value); }
    public string Format { get => _format; private set => Set(ref _format, value); }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public StatusKind StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public string Stats { get => _stats; private set => Set(ref _stats, value); }
    public double Level { get => _level; private set => Set(ref _level, value); }

    public OutputConfig ToConfig() => new(Id, Name, VolumePercent, DelayMs, IsMuted);

    /// <summary>Carrega valores salvos sem disparar eventos.</summary>
    public void LoadSaved(bool selected, double volume, int delay, bool muted)
    {
        _isSelected = selected;
        _volumePercent = Math.Clamp(volume, 0, 100);
        _delayMs = Math.Clamp(delay, 0, MaxDelayMs);
        _isMuted = muted;
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(VolumePercent));
        OnPropertyChanged(nameof(VolumeText));
        OnPropertyChanged(nameof(DelayMs));
        OnPropertyChanged(nameof(DelayText));
        OnPropertyChanged(nameof(IsMuted));
    }

    public void UpdateFrom(DeviceInfo? info)
    {
        if (info is null)
        {
            IsActive = false;
            IsWindowsDefault = false;
            return;
        }
        Name = info.Name;
        IsActive = info.IsActive;
        IsBluetooth = info.IsBluetooth;
        IsHandsFree = info.IsHandsFree;
        IsWindowsDefault = info.IsWindowsDefault;
        Format = info.MixFormat?.ToString() ?? "";
    }

    /// <summary>Status quando o motor está parado.</summary>
    public void ShowIdleStatus()
    {
        Level = 0;
        Stats = "";
        Detail = IsHandsFree
            ? "Perfil viva-voz (mono, baixa qualidade). Use o dispositivo \"Estéreo\"/\"Stereo\" da mesma caixa."
            : "";
        if (!IsActive)
        {
            StatusText = "Desconectado";
            StatusKind = StatusKind.Neutral;
        }
        else
        {
            StatusText = IsSelected ? "Conectado" : "Disponível";
            StatusKind = IsHandsFree ? StatusKind.Warning : StatusKind.Neutral;
        }
    }

    public void ShowRunningStatus(OutputStatus? status, bool captureIdle, Func<float, double> meter)
    {
        if (status is null)
        {
            ShowIdleStatus();
            return;
        }

        Detail = status.Message;
        Stats = status.Stats is { } s
            ? $"buffer {s.FillMs:0} ms · drift {s.CorrectionPpm:+0;-0;0} ppm · falhas {s.Underruns} · ressinc. {s.Resyncs}"
            : "";
        Level = status.Stats is { } st ? meter(st.PeakLevel) : 0;

        switch (status.State)
        {
            case OutputState.Playing when status.Stats?.State == PipelineState.Playing:
                StatusText = IsMuted ? "Reproduzindo (mudo)" : "Reproduzindo";
                StatusKind = StatusKind.Ok;
                break;
            case OutputState.Playing:
                StatusText = captureIdle ? "Aguardando áudio" : "Sincronizando…";
                StatusKind = StatusKind.Busy;
                break;
            case OutputState.Reconnecting:
                StatusText = "Reconectando…";
                StatusKind = StatusKind.Warning;
                break;
            case OutputState.Failed:
                StatusText = "Falha";
                StatusKind = StatusKind.Error;
                break;
            case OutputState.SkippedCaptureSource:
            case OutputState.SkippedWindowsDefault:
                StatusText = "Ignorado";
                StatusKind = StatusKind.Warning;
                break;
        }
    }
}
