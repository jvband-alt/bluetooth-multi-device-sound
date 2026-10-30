namespace MultiSound.Core.Dsp;

/// <summary>Fonte de quadros estéreo intercalados para o reamostrador.</summary>
public delegate int StereoFrameSource(Span<float> dest, int frames);

/// <summary>
/// Reamostrador estéreo de razão variável (sinc janelada com Kaiser, tabela polifásica interpolada).
/// Converte a taxa da captura para a taxa do dispositivo e permite variações finas da razão
/// (correção de drift de relógio) sem descontinuidades.
/// </summary>
public sealed class StereoResampler
{
    public const int Taps = 32;
    private const int Half = Taps / 2;
    private const int Phases = 512;
    private const int HistoryCapacity = 16384;

    private readonly float[] _table;
    private readonly float[] _hist = new float[HistoryCapacity * 2];
    private int _histFrames;
    private double _pos;

    public StereoResampler(int inputRate, int outputRate)
    {
        if (inputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        if (outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(outputRate));
        InputRate = inputRate;
        OutputRate = outputRate;
        NominalStep = (double)inputRate / outputRate;
        Cutoff = ComputeCutoff(NominalStep);
        _table = BuildTable(Cutoff);
        Reset();
    }

    public int InputRate { get; }
    public int OutputRate { get; }

    /// <summary>Avanço nominal na entrada por quadro de saída (taxa de entrada / taxa de saída).</summary>
    public double NominalStep { get; }

    /// <summary>Frequência de corte relativa ao Nyquist da entrada.</summary>
    public double Cutoff { get; }

    /// <summary>Quadros de entrada já retirados da fonte mas ainda não consumidos (fazem parte da latência).</summary>
    public double BufferedInputFrames => Math.Max(0, _histFrames - _pos);

    /// <summary>Descarta o histórico interno (usar após saltos/ressincronização).</summary>
    public void Reset()
    {
        // Pré-carrega Half-1 quadros de silêncio para que o primeiro quadro real fique no centro do filtro.
        Array.Clear(_hist);
        _histFrames = Half - 1;
        _pos = Half - 1;
    }

    /// <summary>
    /// Gera até <paramref name="frames"/> quadros de saída. <paramref name="correction"/> multiplica a razão
    /// nominal (ex.: 1.0002 consome a entrada 200 ppm mais rápido). Retorna os quadros gerados;
    /// um valor menor que o pedido indica falta de dados na fonte (underrun).
    /// </summary>
    public int Process(Span<float> output, int frames, double correction, StereoFrameSource source)
    {
        double step = NominalStep * correction;
        int produced = 0;
        var table = _table;
        var hist = _hist;

        while (produced < frames)
        {
            int i = (int)_pos;
            int needed = i + Half + 1;
            if (needed > _histFrames)
            {
                int remainingOut = frames - produced;
                int want = (int)Math.Ceiling(remainingOut * step) + Half + 2 - (_histFrames - i);
                if (!Refill(Math.Max(want, 1), source))
                    break;
                i = (int)_pos; // a compactação pode ter deslocado a posição
                if (i + Half + 1 > _histFrames)
                    continue;
            }

            double frac = _pos - i;
            double phase = frac * Phases;
            int p = (int)phase;
            float t = (float)(phase - p);
            int row0 = p * Taps;
            int row1 = row0 + Taps;
            int start = (i - Half + 1) * 2;

            float l = 0f, r = 0f;
            for (int k = 0; k < Taps; k++)
            {
                float c0 = table[row0 + k];
                float c = c0 + t * (table[row1 + k] - c0);
                int idx = start + k * 2;
                l += hist[idx] * c;
                r += hist[idx + 1] * c;
            }

            output[produced * 2] = l;
            output[produced * 2 + 1] = r;
            produced++;
            _pos += step;
        }
        return produced;
    }

    private bool Refill(int want, StereoFrameSource source)
    {
        // Compacta: mantém apenas Half-1 quadros antes da posição atual.
        int keepFrom = (int)_pos - (Half - 1);
        if (keepFrom > 0 && (_histFrames + want > HistoryCapacity || keepFrom > HistoryCapacity / 2))
        {
            int keep = _histFrames - keepFrom;
            Array.Copy(_hist, keepFrom * 2, _hist, 0, keep * 2);
            _histFrames = keep;
            _pos -= keepFrom;
        }

        int free = HistoryCapacity - _histFrames;
        int n = Math.Min(want, free);
        if (n <= 0) return false;
        int got = source(_hist.AsSpan(_histFrames * 2, n * 2), n);
        _histFrames += got;
        return got > 0;
    }

    private static double ComputeCutoff(double step)
    {
        if (Math.Abs(step - 1.0) < 0.01) return 1.0; // mesma taxa: passa-tudo exato quando a fase é 0
        return Math.Min(1.0, 1.0 / step) * 0.94;
    }

    private static float[] BuildTable(double cutoff)
    {
        const double beta = 8.0;
        var table = new float[(Phases + 1) * Taps];
        double i0Beta = BesselI0(beta);
        for (int p = 0; p <= Phases; p++)
        {
            double frac = (double)p / Phases;
            double sum = 0;
            int row = p * Taps;
            for (int k = 0; k < Taps; k++)
            {
                // Distância (em amostras de entrada) entre a amostra k e o ponto interpolado.
                double x = k - (Half - 1) - frac;
                double sinc = Math.Abs(x) < 1e-12 ? 1.0 : Math.Sin(Math.PI * cutoff * x) / (Math.PI * cutoff * x);
                double w = x / Half;
                double window = Math.Abs(w) >= 1 ? 0 : BesselI0(beta * Math.Sqrt(1 - w * w)) / i0Beta;
                double v = cutoff * sinc * window;
                table[row + k] = (float)v;
                sum += v;
            }
            // Ganho DC unitário em todas as fases (evita modulação de amplitude).
            for (int k = 0; k < Taps; k++) table[row + k] = (float)(table[row + k] / sum);
        }
        return table;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, half = x / 2;
        for (int k = 1; k < 50; k++)
        {
            term *= (half / k) * (half / k);
            sum += term;
            if (term < 1e-12 * sum) break;
        }
        return sum;
    }
}
