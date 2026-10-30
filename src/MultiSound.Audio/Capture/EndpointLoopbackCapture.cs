using MultiSound.Core.Formats;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiSound.Audio.Capture;

/// <summary>
/// Loopback WASAPI clássico de um dispositivo de saída: captura a mixagem que o Windows envia para ele.
/// Observação: a mixagem capturada é afetada pelo volume/mudo desse dispositivo no Windows.
/// </summary>
public sealed class EndpointLoopbackCapture : ICaptureSource
{
    private readonly LoopbackWasapiCapture _capture;
    private readonly MMDevice _device;

    public EndpointLoopbackCapture(MMDevice device, int bufferMs = 40)
    {
        _device = device;
        _capture = new LoopbackWasapiCapture(device, bufferMs);
        Format = WaveFormatMapper.TryMap(_capture.WaveFormat)
                 ?? throw new NotSupportedException($"Formato de mixagem não suportado: {_capture.WaveFormat}");
        Description = $"Loopback de \"{device.FriendlyName}\"";
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) => Stopped?.Invoke(e.Exception);
    }

    public AudioFormatInfo Format { get; }
    public string Description { get; }
    public string DeviceId => _device.ID;

    public event CaptureDataHandler? DataAvailable;
    public event Action<Exception?>? Stopped;

    public void Start() => _capture.StartRecording();
    public void Stop() => _capture.StopRecording();

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded > 0) DataAvailable?.Invoke(e.Buffer.AsSpan(0, e.BytesRecorded));
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnData;
        try { _capture.StopRecording(); } catch { /* já parado */ }
        _capture.Dispose();
        _device.Dispose();
    }

    /// <summary>WasapiCapture em modo loopback com buffer menor que o padrão do NAudio (100 ms).</summary>
    private sealed class LoopbackWasapiCapture : WasapiCapture
    {
        public LoopbackWasapiCapture(MMDevice device, int bufferMs) : base(device, false, bufferMs) { }

        protected override AudioClientStreamFlags GetAudioClientStreamFlags() => AudioClientStreamFlags.Loopback;
    }
}
