using System.IO;

namespace MultiSound.App.Services;

/// <summary>Registro simples em %APPDATA%\MultiSound\multisound.log (útil nos testes físicos).</summary>
public static class Log
{
    private static readonly object Sync = new();
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiSound", "multisound.log");

    public static string FileLocation => FilePath;

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 512 * 1024)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Registro nunca deve derrubar o app.
        }
    }

    public static void Error(string context, Exception ex) => Write($"ERRO {context}: {ex}");
}
