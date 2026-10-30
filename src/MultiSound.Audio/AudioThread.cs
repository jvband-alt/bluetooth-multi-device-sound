using System.Collections.Concurrent;

namespace MultiSound.Audio;

/// <summary>
/// Thread dedicada (apartamento MTA) onde vivem todos os objetos COM do Core Audio (enumerador,
/// dispositivos, clientes de áudio). Evita problemas de marshaling entre a thread STA da interface
/// WPF e as threads de áudio, e serializa as operações de controle (iniciar, parar, reconectar).
/// </summary>
public sealed class AudioThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private volatile bool _disposed;

    public AudioThread()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "MultiSound.Control" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Erros não tratados em ações postadas.</summary>
    public event Action<Exception>? UnhandledError;

    public bool IsCurrentThread => Thread.CurrentThread == _thread;

    public void Post(Action action)
    {
        if (_disposed) return;
        try { _queue.Add(action); } catch (InvalidOperationException) { /* encerrando */ }
    }

    public Task InvokeAsync(Action action) => InvokeAsync(() => { action(); return true; });

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsCurrentThread)
        {
            try { tcs.SetResult(func()); } catch (Exception ex) { tcs.SetException(ex); }
            return tcs.Task;
        }
        if (_disposed)
        {
            tcs.SetException(new ObjectDisposedException(nameof(AudioThread)));
            return tcs.Task;
        }
        Post(() =>
        {
            try { tcs.SetResult(func()); } catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    private void Run()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception ex) { UnhandledError?.Invoke(ex); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        if (!IsCurrentThread) _thread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }
}
