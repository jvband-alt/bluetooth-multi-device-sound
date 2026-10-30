using Microsoft.Win32;

namespace MultiSound.App.Services;

/// <summary>Inicialização com o Windows via HKCU\...\Run (não requer administrador).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MultiSound";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Caminho do executável desconhecido.");
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("StartupRegistration", ex);
        }
    }
}
