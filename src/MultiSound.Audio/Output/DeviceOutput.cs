using MultiSound.Core.Formats;
using MultiSound.Core.Sync;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiSound.Audio.Output;

/// <summary>Uma saída de áudio real: WasapiOut (modo compartilhado, por eventos) alimentado por um pipeline.</summary>
internal sealed class DeviceOutput : IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiOut _out;
    private readonly PipelineWaveProvider _provider;
    private bool _disposed;

    private DeviceOutput(MMDevice device, WasapiOut output, PipelineWaveProvider provider, OutputPipeline pipeline, AudioFormatInfo format)
    {
        _device = device;
        _out = output;
        _provider = provider;
        Pipeline = pipeline;
        Format = format;
        _out.PlaybackStopped += OnPlaybackStopped;
    }

    public string Id => _device.ID;
    public OutputPipeline Pipeline { get; }
    public AudioFormatInfo Format { get; }

    /// <summary>A reprodução parou sozinha (ex.: dispositivo desconectado). Chamado na thread do NAudio.</summary>
    public event Action<DeviceOutput, Exception?>? Faulted;

    /// <summary>Abre e inicia a saída. Assume a posse de <paramref name="device"/>.</summary>
    public static DeviceOutput Open(MMDevice device, int inputSampleRate, PipelineOptions options, int latencyMs)
    {
        WasapiOut? output = null;
        try
        {
            WaveFormat mix;
            using (var client = device.AudioClient) mix = client.MixFormat;
            var format = WaveFormatMapper.TryMap(mix)
                         ?? throw new NotSupportedException($"Formato do dispositivo não suportado: {mix}");

            var pipeline = new OutputPipeline(inputSampleRate, format.SampleRate, options);
            var provider = new PipelineWaveProvider(pipeline, mix, format);
            output = new WasapiOut(device, AudioClientShareMode.Shared, true, latencyMs);
            output.Init(provider);
            var result = new DeviceOutput(device, output, provider, pipeline, format);
            output.Play();
            return result;
        }
        catch
        {
            output?.Dispose();
            device.Dispose();
            throw;
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_disposed) return;
        Faulted?.Invoke(this, e.Exception ?? _provider.LastError ?? new InvalidOperationException("A reprodução parou inesperadamente."));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _out.PlaybackStopped -= OnPlaybackStopped;
        try { _out.Stop(); } catch { /* dispositivo já removido */ }
        try { _out.Dispose(); } catch { /* idem */ }
        _device.Dispose();
    }
}
