namespace MultiSound.Core.Measurement;

/// <summary>
/// Plano da medição automática de sincronização com um microfone de referência (ex.: o do notebook).
/// Cada saída toca o chirp sozinha, em instantes previstos da linha do tempo; o microfone ouve todas.
/// Como a gravação é única, a latência do microfone é a mesma para todas as saídas e se cancela:
/// apenas as diferenças entre as saídas importam.
/// </summary>
public sealed class SyncMeasurementPlan
{
    public SyncMeasurementPlan(int deviceCount, int rounds = 3, double intervalSeconds = 2.5, double leadSeconds = 0.3)
    {
        if (deviceCount < 1) throw new ArgumentOutOfRangeException(nameof(deviceCount));
        DeviceCount = deviceCount;
        Rounds = Math.Max(1, rounds);
        IntervalSeconds = intervalSeconds;
        LeadSeconds = leadSeconds;
    }

    public int DeviceCount { get; }
    public int Rounds { get; }

    /// <summary>Distância entre chirps consecutivos. Deve exceder a maior diferença de latência esperada.</summary>
    public double IntervalSeconds { get; }

    /// <summary>Antecedência entre o início do plano e o primeiro chirp.</summary>
    public double LeadSeconds { get; }

    /// <summary>Janela de busca após o instante nominal: latência máxima total esperada (atraso + Bluetooth).</summary>
    /// <remarks>Com intervalo de 2,5 s e chirp de 0,3 s, as janelas de saídas vizinhas não se sobrepõem.</remarks>
    public double SearchAfterSeconds { get; init; } = 1.6;

    /// <summary>Margem antes do instante nominal (imprecisão do alinhamento microfone/linha do tempo).</summary>
    public double SearchBeforeSeconds { get; init; } = 0.2;

    /// <summary>Instante nominal (s, desde o início do plano) do chirp da saída <paramref name="device"/> na rodada <paramref name="round"/>.</summary>
    public double ScheduledSeconds(int round, int device) => LeadSeconds + (round * DeviceCount + device) * IntervalSeconds;

    /// <summary>Duração total, incluindo a janela do último chirp.</summary>
    public double TotalSeconds => ScheduledSeconds(Rounds - 1, DeviceCount - 1) + SearchAfterSeconds + 0.3;
}

public sealed record DeviceLatency(int Device, double LatencySeconds, double SpreadSeconds, double Confidence, int ValidRounds, double Level)
{
    public bool Detected => ValidRounds > 0 && !double.IsNaN(LatencySeconds);
}

public static class SyncAnalyzer
{
    public const double MinConfidence = 15.0;

    /// <summary>
    /// Analisa a gravação do microfone. <paramref name="micIndexAtPlanStart"/> é o índice da gravação
    /// correspondente ao início do plano. Retorna a latência (s) de cada saída relativa a esse início.
    /// </summary>
    public static IReadOnlyList<DeviceLatency> Analyze(ReadOnlySpan<float> mic, int micRate, double micIndexAtPlanStart,
        SyncMeasurementPlan plan, ReadOnlySpan<float> chirpAtMicRate)
    {
        var results = new List<DeviceLatency>();
        for (int d = 0; d < plan.DeviceCount; d++)
        {
            var lat = new List<double>();
            double confSum = 0, levelSum = 0;
            for (int r = 0; r < plan.Rounds; r++)
            {
                double nominal = micIndexAtPlanStart + plan.ScheduledSeconds(r, d) * micRate;
                int start = (int)Math.Max(0, nominal - plan.SearchBeforeSeconds * micRate);
                int end = (int)Math.Min(mic.Length, nominal + plan.SearchAfterSeconds * micRate + chirpAtMicRate.Length);
                if (end - start <= chirpAtMicRate.Length) continue;

                var peak = CrossCorrelator.FindPeak(mic[start..end], chirpAtMicRate);
                if (double.IsNaN(peak.Lag) || peak.Confidence < MinConfidence) continue;
                lat.Add((start + peak.Lag - nominal) / micRate);
                confSum += peak.Confidence;
                levelSum += peak.Amplitude;
            }

            if (lat.Count == 0)
            {
                results.Add(new DeviceLatency(d, double.NaN, double.NaN, 0, 0, 0));
                continue;
            }
            lat.Sort();
            double median = lat[lat.Count / 2];
            if (lat.Count % 2 == 0) median = (lat[lat.Count / 2 - 1] + lat[lat.Count / 2]) / 2;
            results.Add(new DeviceLatency(d, median, lat[^1] - lat[0], confSum / lat.Count, lat.Count, levelSum / lat.Count));
        }
        return results;
    }

    /// <summary>
    /// Calcula os novos atrasos (ms) para que todas as saídas soem juntas. As latências medidas já incluem
    /// os atrasos atuais; a saída mais lenta fica sem atraso extra e as demais são atrasadas até ela.
    /// Saídas não detectadas mantêm o atraso atual (null no resultado).
    /// </summary>
    public static IReadOnlyList<int?> ComputeDelays(IReadOnlyList<int> currentDelaysMs, IReadOnlyList<DeviceLatency> latencies, int maxDelayMs = 2000)
    {
        var raw = new double?[currentDelaysMs.Count];
        double? slowest = null;
        foreach (var l in latencies)
            if (l.Detected) slowest = slowest is null ? l.LatencySeconds : Math.Max(slowest.Value, l.LatencySeconds);

        for (int i = 0; i < currentDelaysMs.Count; i++)
        {
            var l = latencies.FirstOrDefault(x => x.Device == i);
            if (l is null || !l.Detected || slowest is null) continue;
            raw[i] = currentDelaysMs[i] + (slowest.Value - l.LatencySeconds) * 1000.0;
        }

        // Remove o atraso comum (o menor atraso vira 0) para não somar latência desnecessária.
        double min = raw.Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty(0).Min();
        if (raw.Count(x => x.HasValue) < currentDelaysMs.Count) min = Math.Min(min, 0); // mantém a referência das não medidas
        return raw.Select(x => x.HasValue ? (int?)Math.Clamp((int)Math.Round(x.Value - min), 0, maxDelayMs) : null).ToList();
    }
}
