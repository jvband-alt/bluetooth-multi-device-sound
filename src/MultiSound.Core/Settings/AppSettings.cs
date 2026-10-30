namespace MultiSound.Core.Settings;

public enum CaptureMode
{
    /// <summary>
    /// Captura o áudio de todos os aplicativos, exceto o próprio MultiSound (Windows 10 2004+).
    /// Elimina a realimentação por construção.
    /// </summary>
    AllAppsExceptSelf,

    /// <summary>Loopback clássico de um dispositivo de saída (o padrão do Windows ou um escolhido).</summary>
    EndpointLoopback,
}

public sealed class DeviceSettings
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Selected { get; set; }
    public double VolumePercent { get; set; } = 100;
    public int DelayMs { get; set; }
    public bool Muted { get; set; }
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public CaptureMode CaptureMode { get; set; } = CaptureMode.AllAppsExceptSelf;

    /// <summary>Dispositivo de origem no modo loopback. Nulo = saída padrão do Windows.</summary>
    public string? SourceDeviceId { get; set; }

    public int SafetyBufferMs { get; set; } = 60;
    public int OutputLatencyMs { get; set; } = 40;

    public bool StartMinimized { get; set; }
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>Inicia a reprodução automaticamente ao abrir, se houver dispositivos salvos.</summary>
    public bool AutoStartPlayback { get; set; }

    public List<DeviceSettings> Devices { get; set; } = new();

    public DeviceSettings GetOrAddDevice(string id, string name)
    {
        var d = Devices.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (d is null)
        {
            d = new DeviceSettings { Id = id, Name = name };
            Devices.Add(d);
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            d.Name = name;
        }
        return d;
    }

    /// <summary>Corrige valores fora de faixa (arquivo editado à mão ou de versão antiga).</summary>
    public void Normalize()
    {
        SafetyBufferMs = Math.Clamp(SafetyBufferMs, 20, 500);
        OutputLatencyMs = Math.Clamp(OutputLatencyMs, 10, 200);
        Devices ??= new();
        Devices.RemoveAll(d => d is null || string.IsNullOrWhiteSpace(d.Id));
        Devices = Devices
            .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        foreach (var d in Devices)
        {
            d.VolumePercent = Math.Clamp(d.VolumePercent, 0, 100);
            d.DelayMs = Math.Clamp(d.DelayMs, 0, 2000);
            d.Name ??= "";
        }
        if (!Enum.IsDefined(CaptureMode)) CaptureMode = CaptureMode.AllAppsExceptSelf;
    }
}
