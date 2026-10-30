using MultiSound.Core.Formats;

namespace MultiSound.Audio.Devices;

public enum DeviceAvailability
{
    Active,
    Unplugged,
    Disabled,
    NotPresent,
}

/// <summary>Instantâneo imutável de um dispositivo de saída (seguro para a interface).</summary>
public sealed record DeviceInfo(
    string Id,
    string Name,
    DeviceAvailability Availability,
    bool IsBluetooth,
    bool IsHandsFree,
    bool IsWindowsDefault,
    AudioFormatInfo? MixFormat)
{
    public bool IsActive => Availability == DeviceAvailability.Active;
}
