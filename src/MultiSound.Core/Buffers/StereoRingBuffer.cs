namespace MultiSound.Core.Buffers;

/// <summary>
/// Buffer circular de quadros estéreo float (L,R intercalados), seguro para um produtor e um consumidor.
/// As seções críticas são curtas (apenas cópias de memória), então um lock simples é adequado
/// para threads de áudio e evita os riscos de uma estrutura lock-free quando há descarte de dados antigos.
/// </summary>
public sealed class StereoRingBuffer
{
    private readonly float[] _data;
    private readonly object _sync = new();
    private int _readFrame;
    private int _count;

    public StereoRingBuffer(int capacityFrames)
    {
        if (capacityFrames <= 0) throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        CapacityFrames = capacityFrames;
        _data = new float[capacityFrames * 2];
    }

    public int CapacityFrames { get; }

    /// <summary>Quadros disponíveis para leitura.</summary>
    public int Count
    {
        get { lock (_sync) return _count; }
    }

    /// <summary>
    /// Escreve quadros. Se não houver espaço, descarta os quadros MAIS ANTIGOS (mantém a latência limitada).
    /// Retorna quantos quadros foram descartados (overflow).
    /// </summary>
    public int Write(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / 2;
        if (frames == 0) return 0;
        int dropped = 0;

        lock (_sync)
        {
            if (frames >= CapacityFrames)
            {
                dropped = _count + frames - CapacityFrames;
                interleaved = interleaved[((frames - CapacityFrames) * 2)..];
                frames = CapacityFrames;
                _readFrame = 0;
                _count = 0;
            }
            else if (_count + frames > CapacityFrames)
            {
                dropped = _count + frames - CapacityFrames;
                _readFrame = (_readFrame + dropped) % CapacityFrames;
                _count -= dropped;
            }

            int writeFrame = (_readFrame + _count) % CapacityFrames;
            int first = Math.Min(frames, CapacityFrames - writeFrame);
            interleaved[..(first * 2)].CopyTo(_data.AsSpan(writeFrame * 2));
            if (first < frames)
                interleaved[(first * 2)..(frames * 2)].CopyTo(_data.AsSpan(0));
            _count += frames;
        }
        return dropped;
    }

    /// <summary>Lê até <paramref name="frames"/> quadros. Retorna os quadros efetivamente lidos.</summary>
    public int Read(Span<float> dest, int frames)
    {
        frames = Math.Min(frames, dest.Length / 2);
        lock (_sync)
        {
            int n = Math.Min(frames, _count);
            int first = Math.Min(n, CapacityFrames - _readFrame);
            _data.AsSpan(_readFrame * 2, first * 2).CopyTo(dest);
            if (first < n)
                _data.AsSpan(0, (n - first) * 2).CopyTo(dest[(first * 2)..]);
            _readFrame = (_readFrame + n) % CapacityFrames;
            _count -= n;
            return n;
        }
    }

    /// <summary>Descarta até <paramref name="frames"/> quadros. Retorna quantos foram descartados.</summary>
    public int Skip(int frames)
    {
        if (frames <= 0) return 0;
        lock (_sync)
        {
            int n = Math.Min(frames, _count);
            _readFrame = (_readFrame + n) % CapacityFrames;
            _count -= n;
            return n;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _readFrame = 0;
            _count = 0;
        }
    }
}
