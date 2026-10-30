namespace MultiSound.Core.Sync;

public enum PipelineState
{
    /// <summary>Acumulando dados até atingir o atraso alvo.</summary>
    Priming,
    Playing,
    /// <summary>Rampa de saída antes de um salto (ajuste de atraso ou ressincronização).</summary>
    FadingOut,
}

public readonly record struct PipelineStats(
    PipelineState State,
    double FillMs,
    double TargetMs,
    double CorrectionPpm,
    long Underruns,
    long Overflows,
    long Resyncs,
    float PeakLevel);
