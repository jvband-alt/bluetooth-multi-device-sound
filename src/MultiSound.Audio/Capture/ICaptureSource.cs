using MultiSound.Core.Formats;

namespace MultiSound.Audio.Capture;

public delegate void CaptureDataHandler(ReadOnlySpan<byte> data);

/// <summary>Fonte de áudio capturado do sistema.</summary>
public interface ICaptureSource : IDisposable
{
    /// <summary>Formato dos bytes entregues em <see cref="DataAvailable"/>.</summary>
    AudioFormatInfo Format { get; }

    /// <summary>Descrição para a interface (ex.: "Todos os apps exceto o MultiSound").</summary>
    string Description { get; }

    /// <summary>Dados capturados (chamado na thread de captura; não bloquear).</summary>
    event CaptureDataHandler? DataAvailable;

    /// <summary>A captura parou (exceção = falha, ex.: dispositivo removido).</summary>
    event Action<Exception?>? Stopped;

    void Start();
    void Stop();
}
