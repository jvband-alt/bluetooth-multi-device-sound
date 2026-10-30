using MultiSound.Core.Formats;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiSound.Audio.Capture;

/// <summary>Grava um microfone (mono float) para a medição automática de sincronização.</summary>
internal sealed class MicrophoneRecorder : IDisposable
{
    private readonly WasapiCapture _capture;
    private readonly MMDevice _device;
    private readonly AudioFormatInfo _format;
    private readonly List<float> _samples = new(48000 * 30);
    private readonly object _sync = new();
    private float[] _scratch = new float[4096];
    private long _frames;

    public MicrophoneRecorder(MMDevice device)
    {
        _device = device;
        Name = device.FriendlyName;
        _capture = new WasapiCapture(device, true, 50);
        _format = WaveFormatMapper.TryMap(_capture.WaveFormat)
                  ?? throw new NotSupportedException($"Formato do microfone não suportado: {_capture.WaveFormat}");
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) => { if (e.Exception is not null) Error = e.Exception; };
    }

    public string Name { get; }
    public int SampleRate => _format.SampleRate;
    public Exception? Error { get; private set; }
    public long FramesRecorded => Interlocked.Read(ref _frames);

    public void Start() => _capture.StartRecording();

    public float[] StopAndGetSamples()
    {
        try { _capture.StopRecording(); } catch { /* já parado */ }
        lock (_sync) return _samples.ToArray();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        int frames = e.BytesRecorded / _format.BlockAlign;
        if (frames <= 0) return;
        if (_scratch.Length < frames * 2) _scratch = new float[frames * 2];
        SampleConverter.ToStereoFloat(e.Buffer.AsSpan(0, frames * _format.BlockAlign), _format, _scratch);
        lock (_sync)
        {
            for (int i = 0; i < frames; i++) _samples.Add((_scratch[i * 2] + _scratch[i * 2 + 1]) * 0.5f);
        }
        Interlocked.Add(ref _frames, frames);
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnData;
        try { _capture.StopRecording(); } catch { /* idem */ }
        _capture.Dispose();
        _device.Dispose();
    }
}
