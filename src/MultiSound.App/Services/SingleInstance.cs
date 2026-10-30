namespace MultiSound.App.Services;

/// <summary>
/// Garante uma única instância (evita duas capturas/saídas duplicadas). Uma segunda execução apenas
/// pede à primeira que mostre a janela.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\MultiSound.SingleInstance.v1";
    private const string EventName = @"Local\MultiSound.Show.v1";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle showEvent, Action onShowRequested)
    {
        _mutex = mutex;
        _showEvent = showEvent;
        _registration = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => onShowRequested(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Retorna null se já existe outra instância (que foi sinalizada para aparecer).</summary>
    public static SingleInstance? TryAcquire(Action onShowRequested)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool created);
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        if (!created)
        {
            ev.Set();
            ev.Dispose();
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex, ev, onShowRequested);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _showEvent.Dispose();
        try { _mutex.ReleaseMutex(); } catch { /* já liberado */ }
        _mutex.Dispose();
    }
}
