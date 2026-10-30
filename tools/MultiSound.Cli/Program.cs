using System.Globalization;
using MultiSound.Audio;
using MultiSound.Audio.Devices;
using MultiSound.Core.Settings;

// Protótipo: multisound-cli listar
//            multisound-cli tocar 1 3 [--atraso 3=120] [--modo apps|loopback] [--origem N] [--buffer 60] [--teste]
Console.OutputEncoding = System.Text.Encoding.UTF8;

using var thread = new AudioThread();
using var catalog = await DeviceCatalog.CreateAsync(thread);
var devices = await catalog.GetDevicesAsync();

if (args.Length == 0 || args[0] is "listar" or "list")
{
    PrintDevices(devices);
    Console.WriteLine();
    Console.WriteLine("Uso: multisound-cli tocar <nº> <nº> [...] [--atraso <nº>=<ms>] [--modo apps|loopback] [--origem <nº>] [--buffer <ms>] [--teste]");
    return 0;
}

if (args[0] == "diag")
{
    // Diagnóstico sem caixas: verifica se a captura funciona neste Windows.
    PrintDevices(devices);
    Console.WriteLine($"Windows {Environment.OSVersion.Version}");
    TestCapture("Captura por processo (todos os apps exceto este)", () => new MultiSound.Audio.Capture.ProcessLoopbackCapture());
    return 0;
}

if (args[0] is not ("tocar" or "play"))
{
    Console.Error.WriteLine($"Comando desconhecido: {args[0]}");
    return 1;
}

var selected = new List<DeviceInfo>();
var delays = new Dictionary<int, int>();
var mode = CaptureMode.AllAppsExceptSelf;
string? sourceId = null;
int buffer = 60;
bool test = false;

for (int i = 1; i < args.Length; i++)
{
    string a = args[i];
    switch (a)
    {
        case "--atraso" when i + 1 < args.Length:
            var parts = args[++i].Split('=');
            delays[int.Parse(parts[0], CultureInfo.InvariantCulture)] = int.Parse(parts[1], CultureInfo.InvariantCulture);
            break;
        case "--modo" when i + 1 < args.Length:
            mode = args[++i] == "loopback" ? CaptureMode.EndpointLoopback : CaptureMode.AllAppsExceptSelf;
            break;
        case "--origem" when i + 1 < args.Length:
            sourceId = Pick(devices, args[++i])?.Id;
            mode = CaptureMode.EndpointLoopback;
            break;
        case "--buffer" when i + 1 < args.Length:
            buffer = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--teste":
            test = true;
            break;
        default:
            var d = Pick(devices, a);
            if (d is null) { Console.Error.WriteLine($"Dispositivo inválido: {a}"); return 1; }
            selected.Add(d);
            break;
    }
}

if (selected.Count == 0)
{
    Console.Error.WriteLine("Selecione ao menos um dispositivo (veja 'multisound-cli listar').");
    return 1;
}

using var engine = new AudioEngine(thread, catalog);
engine.Notice += m => Console.WriteLine($"[aviso] {m}");

var outputs = selected.Select(d =>
{
    int index = devices.ToList().IndexOf(d) + 1;
    return new OutputConfig(d.Id, d.Name, 100, delays.GetValueOrDefault(index), false);
}).ToList();

await engine.StartAsync(new EngineConfig(mode, sourceId, buffer, 40, outputs));
engine.SetTestSignal(test);

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
Console.WriteLine("Reproduzindo. Ctrl+C para parar.");

while (!stop.Task.IsCompleted)
{
    await Task.WhenAny(stop.Task, Task.Delay(1000));
    var s = engine.GetSnapshot();
    Console.WriteLine($"Captura: {s.CaptureDescription} {(s.CaptureFormat?.ToString() ?? "-")} {(s.CaptureIdle ? "(sem áudio)" : "")} pico={s.SourcePeak:0.00}");
    foreach (var o in s.Outputs)
    {
        string stats = o.Stats is { } st
            ? $"{st.State} buffer={st.FillMs:0}/{st.TargetMs:0} ms correção={st.CorrectionPpm:+0;-0} ppm underruns={st.Underruns} ressinc={st.Resyncs}"
            : "";
        Console.WriteLine($"  - {o.Name}: {o.State} {o.Message} {stats}");
    }
}

await engine.StopAsync();
Console.WriteLine("Parado. Recursos de áudio liberados.");
return 0;

static DeviceInfo? Pick(IReadOnlyList<DeviceInfo> list, string token)
{
    if (int.TryParse(token, out int n) && n >= 1 && n <= list.Count) return list[n - 1];
    return list.FirstOrDefault(d => string.Equals(d.Id, token, StringComparison.OrdinalIgnoreCase));
}

static void PrintDevices(IReadOnlyList<DeviceInfo> list)
{
    Console.WriteLine("Dispositivos de saída:");
    for (int i = 0; i < list.Count; i++)
    {
        var d = list[i];
        var tags = new List<string>();
        if (d.IsBluetooth) tags.Add("Bluetooth");
        if (d.IsHandsFree) tags.Add("VIVA-VOZ (evite)");
        if (d.IsWindowsDefault) tags.Add("padrão do Windows");
        if (!d.IsActive) tags.Add(d.Availability.ToString());
        Console.WriteLine($"  {i + 1,2}. {d.Name} [{string.Join(", ", tags)}] {d.MixFormat}");
    }
}

static void TestCapture(string name, Func<MultiSound.Audio.Capture.ICaptureSource> factory)
{
    Console.Write($"{name}: ");
    try
    {
        using var cap = factory();
        long bytes = 0;
        int packets = 0;
        cap.DataAvailable += d => { bytes += d.Length; packets++; };
        cap.Start();
        Thread.Sleep(2000);
        cap.Stop();
        Console.WriteLine($"OK — formato {cap.Format}, {packets} pacotes, {bytes} bytes em 2 s (0 é normal se nada estiver tocando).");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"INDISPONÍVEL — {ex.GetType().Name}: {ex.Message}");
    }
}
