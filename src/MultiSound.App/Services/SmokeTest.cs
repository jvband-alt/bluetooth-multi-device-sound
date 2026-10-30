using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MultiSound.App.ViewModels;

namespace MultiSound.App.Services;

/// <summary>
/// Teste de fumaça para CI (Windows sem caixas de som): abre a janela, inicializa o áudio, força a
/// renderização de todos os estilos/templates, salva uma captura de tela e encerra com código 0.
/// Qualquer exceção de XAML/recursos encerra com código 1. Não reproduz áudio.
/// </summary>
internal static class SmokeTest
{
    public static async Task RunAsync(App app, MainWindow window, MainViewModel vm, string? screenshotPath)
    {
        App.IsSmokeTest = true;
        try
        {
            window.Show();
            await vm.InitializeAsync();
            // Exemplos visuais SOMENTE para a captura de tela do teste (não são dispositivos reais).
            if (vm.Devices.Count == 0) vm.AddDemoDevicesForScreenshot();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await Task.Delay(500);
            window.UpdateLayout();

            if (!string.IsNullOrWhiteSpace(screenshotPath))
            {
                var dpi = VisualTreeHelper.GetDpi(window);
                var content = (FrameworkElement)window.Content;
                var bmp = new RenderTargetBitmap(
                    (int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
                    96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                var dv = new DrawingVisual();
                using (var ctx = dv.RenderOpen())
                {
                    ctx.DrawRectangle((Brush)app.FindResource("Bg"), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                    ctx.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                }
                bmp.Render(dv);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshotPath))!);
                using var fs = File.Create(screenshotPath);
                encoder.Save(fs);
            }

            Console.WriteLine($"SMOKE OK: {vm.Devices.Count} dispositivo(s), status: {vm.StatusText}");
            app.ExitForSmokeTest(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SMOKE FAIL: " + ex);
            Log.Error("SmokeTest", ex);
            app.ExitForSmokeTest(1);
        }
    }
}
