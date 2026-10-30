using MultiSound.Core.Measurement;

namespace MultiSound.Core.Tests;

public class SyncMeasurementTests
{
    /// <summary>
    /// Simula a gravação do microfone do notebook: cada caixa toca o chirp em seu horário, chega com a
    /// própria latência, atenuada, com eco da sala e ruído de fundo.
    /// </summary>
    private static float[] SimulateMic(SyncMeasurementPlan plan, int rate, double micOffsetSeconds, double[] latencies,
        double[] gains, double noise, int seed = 1)
    {
        var chirp = Chirp.Create(rate);
        int length = (int)((plan.TotalSeconds + micOffsetSeconds + 1) * rate);
        var mic = new float[length];
        var rng = new Random(seed);
        for (int i = 0; i < length; i++) mic[i] = (float)((rng.NextDouble() * 2 - 1) * noise);

        for (int r = 0; r < plan.Rounds; r++)
            for (int d = 0; d < plan.DeviceCount; d++)
            {
                if (gains[d] == 0) continue;
                double t = micOffsetSeconds + plan.ScheduledSeconds(r, d) + latencies[d];
                // Atraso fracionário aproximado por interpolação linear + uma reflexão 12 ms depois.
                foreach (var (delay, g) in new[] { (0.0, gains[d]), (0.012, gains[d] * 0.4) })
                {
                    double start = (t + delay) * rate;
                    int i0 = (int)Math.Floor(start);
                    double frac = start - i0;
                    for (int k = 0; k < chirp.Length; k++)
                    {
                        int idx = i0 + k;
                        if (idx + 1 >= length) break;
                        mic[idx] += (float)(chirp[k] * g * (1 - frac));
                        mic[idx + 1] += (float)(chirp[k] * g * frac);
                    }
                }
            }
        return mic;
    }

    [Theory]
    [InlineData(48000, 0.180, 0.310)]
    [InlineData(44100, 0.950, 0.140)]
    [InlineData(16000, 0.200, 0.200)]
    public void MeasuresEachDeviceLatency_WithinOneMillisecond(int micRate, double latA, double latB)
    {
        var plan = new SyncMeasurementPlan(2);
        double micOffset = 0.073; // latência do microfone desconhecida: precisa se cancelar
        var mic = SimulateMic(plan, micRate, micOffset, new[] { latA, latB }, new[] { 0.05, 0.02 }, noise: 0.01);
        var result = SyncAnalyzer.Analyze(mic, micRate, micIndexAtPlanStart: 0, plan, Chirp.Create(micRate));

        Assert.All(result, r => Assert.True(r.Detected));
        double diff = result[1].LatencySeconds - result[0].LatencySeconds;
        Assert.InRange(diff, latB - latA - 0.001, latB - latA + 0.001);
        Assert.InRange(result[0].LatencySeconds, latA + micOffset - 0.001, latA + micOffset + 0.001);
        Assert.All(result, r => Assert.True(r.SpreadSeconds < 0.001));
        Assert.All(result, r => Assert.True(r.Confidence > 3 * SyncAnalyzer.MinConfidence, $"confiança {r.Confidence:0}"));
    }

    [Fact]
    public void SilentDevice_IsReportedAsNotDetected()
    {
        var plan = new SyncMeasurementPlan(2);
        var mic = SimulateMic(plan, 48000, 0.05, new[] { 0.2, 0.3 }, new[] { 0.05, 0.0 }, noise: 0.01);
        var result = SyncAnalyzer.Analyze(mic, 48000, 0, plan, Chirp.Create(48000));
        Assert.True(result[0].Detected);
        Assert.False(result[1].Detected);
    }

    [Fact]
    public void PureNoise_DetectsNothing()
    {
        var plan = new SyncMeasurementPlan(2, rounds: 2);
        var mic = SimulateMic(plan, 48000, 0, new[] { 0.2, 0.3 }, new[] { 0.0, 0.0 }, noise: 0.05);
        var result = SyncAnalyzer.Analyze(mic, 48000, 0, plan, Chirp.Create(48000));
        Assert.All(result, r => Assert.False(r.Detected));
    }

    [Fact]
    public void ComputeDelays_AlignsToTheSlowestDevice()
    {
        // Harman chega em 180 ms, MIFA em 310 ms (com atrasos atuais 0 e 0): atrasar a Harman em 130 ms.
        var lat = new[] { new DeviceLatency(0, 0.180, 0, 50, 3, 1), new DeviceLatency(1, 0.310, 0, 50, 3, 1) };
        var d = SyncAnalyzer.ComputeDelays(new[] { 0, 0 }, lat);
        Assert.Equal(130, d[0]);
        Assert.Equal(0, d[1]);
    }

    [Fact]
    public void ComputeDelays_AccountsForCurrentDelays_AndRemovesCommonDelay()
    {
        // Atrasos atuais 0 e 683 ms (como no print): a MIFA chega muito depois e deve ter o atraso reduzido.
        var lat = new[] { new DeviceLatency(0, 0.200, 0, 50, 3, 1), new DeviceLatency(1, 0.900, 0, 50, 3, 1) };
        var d = SyncAnalyzer.ComputeDelays(new[] { 0, 683 }, lat);
        // Novo: Harman 0+700=700, MIFA 683 → menos o comum (683): Harman 17, MIFA 0.
        Assert.Equal(17, d[0]);
        Assert.Equal(0, d[1]);
    }

    [Fact]
    public void ComputeDelays_KeepsUndetectedDevicesUnchanged()
    {
        var lat = new[] { new DeviceLatency(0, 0.2, 0, 50, 3, 1), new DeviceLatency(1, double.NaN, double.NaN, 0, 0, 0) };
        var d = SyncAnalyzer.ComputeDelays(new[] { 40, 100 }, lat);
        Assert.Equal(40, d[0]);
        Assert.Null(d[1]);
    }

    [Fact]
    public void CrossCorrelator_FindsSubSampleOffset()
    {
        var chirp = Chirp.Create(48000);
        var signal = new float[48000];
        double pos = 12345.4;
        for (int k = 0; k < chirp.Length; k++)
        {
            signal[12345 + k] += chirp[k] * 0.6f;
            signal[12346 + k] += chirp[k] * 0.4f;
        }
        var peak = CrossCorrelator.FindPeak(signal, chirp);
        Assert.InRange(peak.Lag, pos - 0.25, pos + 0.25);
        Assert.True(peak.Confidence > 20);
    }
}
