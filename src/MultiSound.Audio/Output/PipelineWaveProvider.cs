using MultiSound.Core.Formats;
using MultiSound.Core.Sync;
using NAudio.Wave;

namespace MultiSound.Audio.Output;

/// <summary>
/// Adaptador NAudio: o WasapiOut pede bytes no formato de mixagem do dispositivo; geramos estéreo
/// float pelo <see cref="OutputPipeline"/> e convertemos. Nunca retorna menos que o pedido
/// (retornar 0 faria o NAudio encerrar a reprodução).
/// </summary>
internal sealed class PipelineWaveProvider : IWaveProvider
{
    private readonly OutputPipeline _pipeline;
    private readonly AudioFormatInfo _format;
    private float[] _scratch = new float[4096 * 2];

    public PipelineWaveProvider(OutputPipeline pipeline, WaveFormat deviceFormat, AudioFormatInfo format)
    {
        _pipeline = pipeline;
        WaveFormat = deviceFormat;
        _format = format;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Última exceção ocorrida no callback (para diagnóstico).</summary>
    public Exception? LastError { get; private set; }

    public int Read(byte[] buffer, int offset, int count)
    {
        int frames = count / _format.BlockAlign;
        int bytes = frames * _format.BlockAlign;
        try
        {
            if (_scratch.Length < frames * 2) _scratch = new float[frames * 2];
            _pipeline.Render(_scratch, frames);
            SampleConverter.FromStereoFloat(_scratch, frames, _format, buffer.AsSpan(offset, bytes));
        }
        catch (Exception ex)
        {
            LastError = ex;
            Array.Clear(buffer, offset, bytes);
        }
        if (bytes < count) Array.Clear(buffer, offset + bytes, count - bytes);
        return count;
    }
}
