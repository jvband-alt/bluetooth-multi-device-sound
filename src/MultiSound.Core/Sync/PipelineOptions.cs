namespace MultiSound.Core.Sync;

public sealed record PipelineOptions
{
    /// <summary>Buffer de segurança comum a todas as saídas (absorve o jitter da captura).</summary>
    public int SafetyBufferMs { get; init; } = 60;

    /// <summary>Atraso máximo ajustável por saída.</summary>
    public int MaxDelayMs { get; init; } = 2000;

    /// <summary>Duração das rampas de fade usadas em saltos e partidas (evita estalos).</summary>
    public double FadeMs { get; init; } = 8;

    /// <summary>Tolerância acima do alvo aceita ao sair do estado de preenchimento.</summary>
    public double PrimeSlackMs { get; init; } = 5;

    /// <summary>Se não houver escrita há mais que isso, falta de dados não é contada como underrun.</summary>
    public double IdleThresholdMs { get; init; } = 250;
}
