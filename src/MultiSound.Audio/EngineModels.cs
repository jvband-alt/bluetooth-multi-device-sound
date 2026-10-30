using MultiSound.Core.Formats;
using MultiSound.Core.Settings;
using MultiSound.Core.Sync;

namespace MultiSound.Audio;

public enum OutputState
{
    Playing,
    Reconnecting,
    Failed,
    SkippedCaptureSource,
    SkippedWindowsDefault,
}

public sealed record OutputConfig(string Id, string Name, double VolumePercent, int DelayMs, bool Muted);

public sealed record EngineConfig(
    CaptureMode Mode,
    string? SourceDeviceId,
    int SafetyBufferMs,
    int OutputLatencyMs,
    IReadOnlyList<OutputConfig> Outputs);

public sealed record OutputStatus(
    string Id,
    string Name,
    OutputState State,
    string Message,
    PipelineStats? Stats,
    AudioFormatInfo? Format);

public sealed record EngineSnapshot(
    bool Running,
    bool CaptureActive,
    bool CaptureIdle,
    string CaptureDescription,
    AudioFormatInfo? CaptureFormat,
    CaptureMode? EffectiveMode,
    float SourcePeak,
    bool TestSignal,
    string? Error,
    IReadOnlyList<OutputStatus> Outputs)
{
    public static readonly EngineSnapshot Idle = new(false, false, false, "", null, null, 0f, false, null, Array.Empty<OutputStatus>());
}

public sealed record MeasuredOutput(string Id, string Name, Core.Measurement.DeviceLatency Latency);

public sealed record SyncMeasurementOutcome(
    bool Success,
    string Message,
    string? MicrophoneName,
    IReadOnlyList<MeasuredOutput> Outputs);
