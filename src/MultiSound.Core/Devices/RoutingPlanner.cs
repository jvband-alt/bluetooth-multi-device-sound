using MultiSound.Core.Settings;

namespace MultiSound.Core.Devices;

public enum RouteDecision
{
    Play,
    /// <summary>É o próprio dispositivo de onde o áudio é capturado: tocar nele causaria realimentação/eco.</summary>
    SkipIsCaptureSource,
    /// <summary>É a saída padrão do Windows no modo "todos os apps": os apps já tocam nele diretamente.</summary>
    SkipIsWindowsDefault,
    /// <summary>Selecionado, mas não está conectado/ativo agora.</summary>
    Unavailable,
}

public readonly record struct RouteInput(string Id, bool Selected, bool Active);

/// <summary>
/// Decide, de forma verificável, para quais dispositivos o áudio pode ser enviado sem duplicação
/// nem realimentação.
/// </summary>
public static class RoutingPlanner
{
    public static IReadOnlyDictionary<string, RouteDecision> Plan(
        CaptureMode mode, string? captureSourceId, string? windowsDefaultId, IEnumerable<RouteInput> devices)
    {
        var result = new Dictionary<string, RouteDecision>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices)
        {
            if (!d.Selected || result.ContainsKey(d.Id)) continue; // ignora duplicatas
            RouteDecision decision;
            if (mode == CaptureMode.EndpointLoopback && Same(d.Id, captureSourceId))
                decision = RouteDecision.SkipIsCaptureSource;
            else if (mode == CaptureMode.AllAppsExceptSelf && Same(d.Id, windowsDefaultId))
                decision = RouteDecision.SkipIsWindowsDefault;
            else if (!d.Active)
                decision = RouteDecision.Unavailable;
            else
                decision = RouteDecision.Play;
            result[d.Id] = decision;
        }
        return result;
    }

    private static bool Same(string a, string? b) => b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
