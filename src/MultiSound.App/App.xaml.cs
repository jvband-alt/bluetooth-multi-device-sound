using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using MultiSound.App.Services;
using MultiSound.App.ViewModels;
using MultiSound.Core.Settings;

namespace MultiSound.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private TrayIconService? _tray;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var culture = new CultureInfo("pt-BR");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Não tratado", (Exception)args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Task", args.Exception); args.SetObserved(); };

        _instance = SingleInstance.TryAcquire(() => Dispatcher.BeginInvoke(ShowMainWindow));
        if (_instance is null)
        {
            // Já existe um MultiSound aberto: ele foi trazido para frente.
            Shutdown();
            return;
        }

        var store = new SettingsStore(SettingsStore.DefaultPath);
        var settings = store.Load();
        if (settings.StartWithWindows) StartupRegistration.Apply(true); // atualiza o caminho se o .exe mudou

        _vm = new MainViewModel(store, settings, Dispatcher);
        _window = new MainWindow { DataContext = _vm };
        _window.Closing += OnWindowClosing;

        _tray = new TrayIconService(
            open: ShowMainWindow,
            togglePlayback: async () => await _vm.TogglePlaybackAsync(),
            exit: ExitApplication);
        _vm.PlaybackStateChanged += () => _tray.Update(_vm.IsRunning, _vm.StatusText);
        _vm.ImportantNotice += msg =>
        {
            if (_window is { IsVisible: false }) _tray.ShowNotice("MultiSound", msg);
        };

        int smokeIndex = Array.FindIndex(e.Args, a => a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase));
        if (smokeIndex >= 0)
        {
            await SmokeTest.RunAsync(this, _window, _vm, e.Args.Skip(smokeIndex + 1).FirstOrDefault());
            return;
        }

        bool startHidden = settings.StartMinimized || e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (!startHidden) _window.Show();

        await _vm.InitializeAsync();
    }

    /// <summary>Usado pelo teste de fumaça: encerra sem diálogos.</summary>
    internal void ExitForSmokeTest(int code)
    {
        _exiting = true;
        _vm?.Dispose();
        _tray?.Dispose();
        _instance?.Dispose();
        Shutdown(code);
    }

    internal static bool IsSmokeTest { get; set; }

    private void ShowMainWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting || _vm is null) return;
        if (_vm.MinimizeToTrayOnClose)
        {
            // Continua tocando em segundo plano.
            e.Cancel = true;
            _window!.Hide();
            _tray?.ShowMinimizedHint();
        }
        else
        {
            e.Cancel = true;
            ExitApplication();
        }
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            _window?.Close();
            _vm?.Dispose();
        }
        finally
        {
            _tray?.Dispose();
            _instance?.Dispose();
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Garantia extra caso o Windows encerre a sessão sem passar pelo menu.
        if (!_exiting)
        {
            _exiting = true;
            _vm?.Dispose();
            _tray?.Dispose();
            _instance?.Dispose();
        }
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI", e.Exception);
        if (IsSmokeTest)
        {
            Console.Error.WriteLine("SMOKE FAIL: " + e.Exception);
            e.Handled = true;
            ExitForSmokeTest(1);
            return;
        }
        MessageBox.Show($"Ocorreu um erro inesperado:\n\n{e.Exception.Message}\n\nDetalhes em {Log.FileLocation}",
            "MultiSound", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
