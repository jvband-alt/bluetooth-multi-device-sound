using MultiSound.Core.Devices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MultiSound.Audio.Devices;

/// <summary>
/// Enumeração de dispositivos de saída e notificações de conexão/desconexão (IMMNotificationClient).
/// Todos os acessos COM acontecem na <see cref="AudioThread"/>.
/// </summary>
public sealed class DeviceCatalog : IDisposable
{
    private readonly AudioThread _thread;
    private MMDeviceEnumerator? _enumerator;
    private NotificationClient? _client;

    private DeviceCatalog(AudioThread thread) => _thread = thread;

    /// <summary>Lista de dispositivos mudou (adicionado, removido, estado). Chamado fora da thread da UI.</summary>
    public event Action? DevicesChanged;

    /// <summary>Saída padrão do Windows mudou (id novo ou nulo).</summary>
    public event Action<string?>? DefaultDeviceChanged;

    /// <summary>Um dispositivo mudou de estado (id, ativo?).</summary>
    public event Action<string, bool>? DeviceStateChanged;

    public static Task<DeviceCatalog> CreateAsync(AudioThread thread) => thread.InvokeAsync(() =>
    {
        var catalog = new DeviceCatalog(thread);
        catalog._enumerator = new MMDeviceEnumerator();
        catalog._client = new NotificationClient(catalog);
        catalog._enumerator.RegisterEndpointNotificationCallback(catalog._client);
        return catalog;
    });

    internal MMDeviceEnumerator Enumerator => _enumerator ?? throw new ObjectDisposedException(nameof(DeviceCatalog));

    public Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync() => _thread.InvokeAsync(GetDevices);

    public Task<string?> GetDefaultRenderIdAsync() => _thread.InvokeAsync(GetDefaultRenderId);

    internal IReadOnlyList<DeviceInfo> GetDevices()
    {
        var list = new List<DeviceInfo>();
        string? defaultId = GetDefaultRenderId();
        var states = DeviceState.Active | DeviceState.Unplugged | DeviceState.Disabled;
        foreach (var device in Enumerator.EnumerateAudioEndPoints(DataFlow.Render, states))
        {
            try
            {
                list.Add(Describe(device, defaultId));
            }
            catch
            {
                // Dispositivo sumiu durante a enumeração ou propriedades inacessíveis: ignora.
            }
            finally
            {
                device.Dispose();
            }
        }
        return list
            .OrderByDescending(d => d.IsActive)
            .ThenByDescending(d => d.IsBluetooth)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    internal string? GetDefaultRenderId()
    {
        try
        {
            if (!Enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
            using var d = Enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return d.ID;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Retorna o dispositivo se ele existir e estiver ativo (o chamador deve descartá-lo).</summary>
    internal MMDevice? TryGetActiveDevice(string id)
    {
        try
        {
            var d = Enumerator.GetDevice(id);
            if (d.State == DeviceState.Active) return d;
            d.Dispose();
        }
        catch
        {
            // Id desconhecido.
        }
        return null;
    }

    /// <summary>
    /// Escolhe um microfone para a medição de sincronização. Microfones Bluetooth são recusados: abri-los
    /// muda as caixas para o perfil viva-voz (HFP) e invalidaria a medição do perfil estéreo (A2DP).
    /// Preferência: o microfone padrão do Windows, se não for Bluetooth; senão, o primeiro ativo não Bluetooth.
    /// </summary>
    internal MMDevice? PickMeasurementMicrophone()
    {
        string? defaultId = null;
        try
        {
            if (Enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
            {
                using var d = Enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                defaultId = d.ID;
            }
        }
        catch
        {
            // Sem microfone padrão.
        }

        MMDevice? chosen = null;
        foreach (var device in Enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            string name = SafeGet(() => device.FriendlyName) ?? "";
            bool bluetooth = DeviceClassifier.IsBluetooth(FindBluetoothMarker(device), name);
            bool isDefault = string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase);
            if (!bluetooth && (chosen is null || isDefault))
            {
                chosen?.Dispose();
                chosen = device;
                if (isDefault) break;
                continue;
            }
            device.Dispose();
        }
        return chosen;
    }

    private static DeviceInfo Describe(MMDevice device, string? defaultId)
    {
        string name = SafeGet(() => device.FriendlyName) ?? device.ID;
        var availability = device.State switch
        {
            DeviceState.Active => DeviceAvailability.Active,
            DeviceState.Unplugged => DeviceAvailability.Unplugged,
            DeviceState.Disabled => DeviceAvailability.Disabled,
            _ => DeviceAvailability.NotPresent,
        };

        bool bluetooth = DeviceClassifier.IsBluetooth(FindBluetoothMarker(device), name);

        Core.Formats.AudioFormatInfo? format = null;
        if (availability == DeviceAvailability.Active)
        {
            format = SafeGet(() =>
            {
                using var client = device.AudioClient;
                return WaveFormatMapper.TryMap(client.MixFormat);
            });
        }

        bool handsFree = DeviceClassifier.IsHandsFree(name, format?.SampleRate ?? 0, format?.Channels ?? 0, bluetooth);
        bool isDefault = defaultId is not null && string.Equals(defaultId, device.ID, StringComparison.OrdinalIgnoreCase);
        return new DeviceInfo(device.ID, name, availability, bluetooth, handsFree, isDefault, format);
    }

    /// <summary>
    /// Procura no repositório de propriedades do endpoint alguma referência ao enumerador Bluetooth
    /// (BTHENUM / BTHHFENUM / BTHLE). Retorna a string encontrada ou nulo.
    /// </summary>
    private static string? FindBluetoothMarker(MMDevice device)
    {
        try
        {
            var props = device.Properties;
            for (int i = 0; i < props.Count; i++)
            {
                try
                {
                    if (props[i].Value is string s && s.Contains("BTH", StringComparison.OrdinalIgnoreCase))
                        return s;
                }
                catch
                {
                    // Tipo de propriedade não suportado pelo NAudio: ignora.
                }
            }
        }
        catch
        {
            // Repositório inacessível.
        }
        return null;
    }

    private static T? SafeGet<T>(Func<T> f)
    {
        try { return f(); } catch { return default; }
    }

    public void Dispose()
    {
        _thread.InvokeAsync(() =>
        {
            if (_enumerator is not null && _client is not null)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(_client); } catch { /* encerrando */ }
            }
            _enumerator?.Dispose();
            _enumerator = null;
        }).Wait(TimeSpan.FromSeconds(3));
    }

    /// <summary>Callbacks chegam em threads do sistema: apenas repassa, sem bloquear.</summary>
    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly DeviceCatalog _owner;
        public NotificationClient(DeviceCatalog owner) => _owner = owner;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            _owner.DeviceStateChanged?.Invoke(deviceId, newState == DeviceState.Active);
            _owner.DevicesChanged?.Invoke();
        }

        public void OnDeviceAdded(string pwstrDeviceId) => _owner.DevicesChanged?.Invoke();

        public void OnDeviceRemoved(string deviceId)
        {
            _owner.DeviceStateChanged?.Invoke(deviceId, false);
            _owner.DevicesChanged?.Invoke();
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow != DataFlow.Render || role != Role.Multimedia) return;
            _owner.DefaultDeviceChanged?.Invoke(string.IsNullOrEmpty(defaultDeviceId) ? null : defaultDeviceId);
            _owner.DevicesChanged?.Invoke();
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // Muito frequente (volume, etc.) — ignorado de propósito.
        }
    }
}
