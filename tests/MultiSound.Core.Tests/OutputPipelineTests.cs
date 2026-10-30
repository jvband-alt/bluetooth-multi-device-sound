using MultiSound.Core.Sync;

namespace MultiSound.Core.Tests;

/// <summary>
/// Simula captura e reprodução com relógios independentes (incluindo drift) para validar
/// o comportamento do pipeline sem hardware.
/// </summary>
public class OutputPipelineTests
{
    private sealed class Sim
    {
        public double Time;
        public readonly List<(OutputPipeline Pipe, double RenderClockFactor, List<float> Output)> Outputs = new();
        public int InputRate = 48000;
        public double CaptureChunkSeconds = 0.010;
        public double RenderChunkSeconds = 0.010;
        public Func<long, float> Signal = _ => 0f;
        private long _inputFrame;
        private double _nextCapture;
        private readonly Dictionary<int, double> _nextRender = new();
        private readonly Dictionary<int, double> _renderAcc = new();
        public bool CaptureEnabled = true;

        public OutputPipeline Add(int outputRate, double clockPpm, PipelineOptions? opt = null)
        {
            var p = new OutputPipeline(InputRate, outputRate, opt, () => Time);
            Outputs.Add((p, 1 + clockPpm * 1e-6, new List<float>()));
            return p;
        }

        public void Run(double seconds, bool record = false)
        {
            double end = Time + seconds;
            const double tick = 0.0005;
            var buf = new float[96000];
            while (Time < end)
            {
                Time += tick;
                if (Time >= _nextCapture)
                {
                    int n = (int)(InputRate * CaptureChunkSeconds);
                    if (CaptureEnabled)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            float v = Signal(_inputFrame + i);
                            buf[i * 2] = v;
                            buf[i * 2 + 1] = v;
                        }
                        foreach (var o in Outputs) o.Pipe.Write(buf.AsSpan(0, n * 2));
                    }
                    _inputFrame += n;
                    _nextCapture += CaptureChunkSeconds;
                }

                for (int k = 0; k < Outputs.Count; k++)
                {
                    var (pipe, factor, list) = Outputs[k];
                    _nextRender.TryGetValue(k, out double next);
                    if (Time < next) continue;
                    _renderAcc.TryGetValue(k, out double acc);
                    acc += pipe.OutputRate * RenderChunkSeconds * factor;
                    int frames = (int)acc;
                    _renderAcc[k] = acc - frames;
                    var outBuf = new float[frames * 2];
                    pipe.Render(outBuf, frames);
                    if (record) for (int i = 0; i < frames; i++) list.Add(outBuf[i * 2]);
                    _nextRender[k] = next + RenderChunkSeconds;
                }
            }
        }
    }

    [Theory]
    [InlineData(48000, 0)]
    [InlineData(48000, 250)]
    [InlineData(44100, -400)]
    [InlineData(44100, 800)]
    public void BufferStaysAtTarget_WithClockDrift_AndNoUnderruns(int outRate, double ppm)
    {
        var sim = new Sim { Signal = i => (float)(0.3 * Math.Sin(i * 0.05)) };
        var pipe = sim.Add(outRate, ppm);
        pipe.DelayMs = 100;
        sim.Run(240);
        var s = pipe.GetStats();
        Assert.Equal(PipelineState.Playing, s.State);
        Assert.Equal(0, s.Underruns);
        Assert.Equal(0, s.Overflows);
        Assert.Equal(0, s.Resyncs);
        Assert.InRange(s.FillMs, s.TargetMs - 12, s.TargetMs + 12); // inclui o dente de serra da captura em blocos de 10 ms
        // Relógio do dispositivo mais rápido (+ppm) pede mais quadros: a entrada deve ser consumida
        // proporcionalmente mais devagar por quadro de saída, ou seja, correção ≈ -ppm.
        Assert.InRange(s.CorrectionPpm, -ppm - 60, -ppm + 60);
    }

    [Fact]
    public void RelativeDelayBetweenOutputs_MatchesConfiguredDelay()
    {
        // Um clique na entrada deve sair 120 ms mais tarde na saída B do que na A.
        long clickAt = 48000 * 3;
        var sim = new Sim { Signal = i => i >= clickAt && i < clickAt + 48 ? 0.9f : 0f };
        var a = sim.Add(48000, 0);
        var b = sim.Add(44100, 150);
        a.DelayMs = 0;
        b.DelayMs = 120;
        sim.Run(5, record: true);

        double tA = FirstAbove(sim.Outputs[0].Output, 0.3f) / 48000.0;
        double tB = FirstAbove(sim.Outputs[1].Output, 0.3f) / 44100.0;
        Assert.True(tA > 0 && tB > 0);
        double diffMs = (tB - tA) * 1000;
        Assert.InRange(diffMs, 120 - 6, 120 + 6);
    }

    [Fact]
    public void DelayChangeDuringPlayback_IsAppliedWithoutUnderruns()
    {
        var sim = new Sim { Signal = i => (float)(0.3 * Math.Sin(i * 0.05)) };
        var p = sim.Add(48000, 0);
        p.DelayMs = 0;
        sim.Run(3);
        Assert.Equal(PipelineState.Playing, p.State);

        p.DelayMs = 200;
        sim.Run(3);
        var s = p.GetStats();
        Assert.Equal(PipelineState.Playing, s.State);
        Assert.InRange(s.FillMs, 260 - 12, 260 + 12);

        p.DelayMs = 50;
        sim.Run(3);
        s = p.GetStats();
        Assert.InRange(s.FillMs, 110 - 12, 110 + 12);
        Assert.Equal(0, s.Underruns);
    }

    [Fact]
    public void JumpUsesFade_NoLargeSampleDiscontinuity()
    {
        var sim = new Sim { Signal = i => (float)(0.8 * Math.Sin(i * 0.02)) };
        var p = sim.Add(48000, 0);
        sim.Run(2);
        p.DelayMs = 300;
        sim.Run(2, record: true);
        var o = sim.Outputs[0].Output;
        float maxStep = 0;
        for (int i = 1; i < o.Count; i++) maxStep = Math.Max(maxStep, Math.Abs(o[i] - o[i - 1]));
        // Senoide de 0,8 com 0,02 rad/amostra varia no máximo 0,016 por amostra; fades devem manter algo próximo disso.
        Assert.True(maxStep < 0.05f, $"Descontinuidade de {maxStep}");
    }

    [Fact]
    public void CaptureSilence_IsNotCountedAsUnderrun_AndPlaybackResumes()
    {
        var sim = new Sim { Signal = i => 0.2f };
        var p = sim.Add(48000, 0);
        sim.Run(2);
        sim.CaptureEnabled = false;
        sim.Run(2);
        Assert.Equal(PipelineState.Priming, p.State);
        Assert.Equal(1, p.GetStats().Underruns); // a primeira falta (ainda "recente") conta uma vez
        sim.Run(1);
        Assert.Equal(1, p.GetStats().Underruns); // ficar ocioso não acumula
        sim.CaptureEnabled = true;
        sim.Run(1);
        Assert.Equal(PipelineState.Playing, p.State);
    }

    [Fact]
    public void Volume_And_Mute_AreApplied()
    {
        var sim = new Sim { Signal = _ => 0.5f };
        var p = sim.Add(48000, 0);
        p.Volume = 0.5f;
        sim.Run(2);
        sim.Outputs[0].Output.Clear();
        sim.Run(0.5, record: true);
        Assert.InRange(sim.Outputs[0].Output.Last(), 0.249f, 0.251f);

        p.Muted = true;
        sim.Run(0.2);
        sim.Outputs[0].Output.Clear();
        sim.Run(0.2, record: true);
        Assert.All(sim.Outputs[0].Output, v => Assert.Equal(0f, v));
    }

    private static int FirstAbove(List<float> data, float threshold)
    {
        for (int i = 0; i < data.Count; i++) if (data[i] > threshold) return i;
        return -1;
    }
}
