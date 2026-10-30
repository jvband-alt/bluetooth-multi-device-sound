namespace MultiSound.Core.Devices;

public static class DeviceClassifier
{
    private static readonly string[] HandsFreeMarkers =
    {
        "hands-free", "handsfree", "hands free", "viva-voz", "viva voz", "headset", "fone de ouvido com microfone",
    };

    /// <summary>
    /// Endpoints Bluetooth do perfil HFP/HSP ("Hands-Free"): mono, 8–16 kHz. Abri-los força o dispositivo
    /// a sair do A2DP (estéreo), derrubando a qualidade. O correto é usar o endpoint "Estéreo"/"Stereo".
    /// </summary>
    public static bool IsHandsFree(string friendlyName, int sampleRate, int channels, bool isBluetooth)
    {
        string n = friendlyName.ToLowerInvariant();
        if (HandsFreeMarkers.Any(n.Contains)) return isBluetooth || n.Contains("hands");
        return isBluetooth && channels == 1 && sampleRate > 0 && sampleRate <= 16000;
    }

    /// <summary>Identifica Bluetooth pelo enumerador/ID de instância do dispositivo (BTHENUM, BTHHFENUM, BTHLE...).</summary>
    public static bool IsBluetooth(string? enumeratorOrInstanceId, string friendlyName)
    {
        if (!string.IsNullOrEmpty(enumeratorOrInstanceId) &&
            enumeratorOrInstanceId.Contains("BTH", StringComparison.OrdinalIgnoreCase))
            return true;
        return friendlyName.Contains("bluetooth", StringComparison.OrdinalIgnoreCase);
    }
}
