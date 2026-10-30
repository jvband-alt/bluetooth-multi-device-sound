using System.Runtime.InteropServices;
using MultiSound.Core.Formats;
using static MultiSound.Audio.Interop.ProcessLoopbackInterop;

namespace MultiSound.Audio.Capture;

/// <summary>
/// Captura o áudio de TODOS os processos exceto a árvore do próprio MultiSound
/// (PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE). Como o áudio que o MultiSound envia às caixas
/// é excluído da captura, não existe caminho de realimentação, independentemente de qual saída esteja
/// selecionada. Também não depende do volume/mudo do dispositivo padrão do Windows.
/// </summary>
public sealed class ProcessLoopbackCapture : ICaptureSource
{
    private const int E_TIMEOUT = unchecked((int)0x800705B4);

    private readonly uint _excludedProcessId;
    private Thread? _thread;
    private volatile bool _stopRequested;
    private readonly ManualResetEventSlim _started = new(false);
    private Exception? _startError;

    public ProcessLoopbackCapture(int sampleRate = 48000, uint? excludedProcessId = null)
    {
        _excludedProcessId = excludedProcessId ?? (uint)Environment.ProcessId;
        // Formato solicitado ao mecanismo de áudio (ele converte a partir da mixagem).
        Format = new AudioFormatInfo(sampleRate, 2, SampleEncoding.Float32);
    }

    public AudioFormatInfo Format { get; private set; }
    public string Description => "Todos os aplicativos (exceto o MultiSound)";

    public event CaptureDataHandler? DataAvailable;
    public event Action<Exception?>? Stopped;

    /// <summary>Verificação rápida de versão (a ativação real é a prova definitiva).</summary>
    public static bool IsLikelySupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    /// <summary>Inicia a captura. Lança exceção se a ativação falhar (permitindo fallback).</summary>
    public void Start()
    {
        if (_thread is not null) return;
        _stopRequested = false;
        _started.Reset();
        _startError = null;
        _thread = new Thread(CaptureThread) { IsBackground = true, Name = "MultiSound.ProcessLoopback", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(6)))
        {
            _stopRequested = true;
            throw new TimeoutException("A ativação da captura por processo não respondeu.");
        }
        if (_startError is not null)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
            _thread = null;
            throw _startError;
        }
    }

    public void Stop()
    {
        _stopRequested = true;
        var t = _thread;
        if (t is not null && Thread.CurrentThread != t) t.Join(TimeSpan.FromSeconds(3));
        _thread = null;
    }

    public void Dispose() => Stop();

    private void CaptureThread()
    {
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        Exception? failure = null;
        using var dataReady = new AutoResetEvent(false);
        bool startedSignaled = false;
        IntPtr mmcss = AvSetMmThreadCharacteristicsW("Pro Audio", out _);

        try
        {
            (client, Format) = ActivateAndInitialize(Format.SampleRate);

            Check(client.SetEventHandle(dataReady.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            Guid iid = IID_IAudioCaptureClient;
            Check(client.GetService(ref iid, out object svc), "GetService");
            capture = (IAudioCaptureClient)svc;
            Check(client.Start(), "Start");

            startedSignaled = true;
            _started.Set();

            int blockAlign = Format.BlockAlign;
            byte[] buffer = new byte[Format.SampleRate * blockAlign]; // 1 s

            while (!_stopRequested)
            {
                dataReady.WaitOne(100);
                if (_stopRequested) break;

                Check(capture.GetNextPacketSize(out uint packet), "GetNextPacketSize");
                while (packet > 0 && !_stopRequested)
                {
                    Check(capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _), "GetBuffer");
                    int bytes = (int)frames * blockAlign;
                    if (bytes > buffer.Length) buffer = new byte[bytes];
                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0) Array.Clear(buffer, 0, bytes);
                    else Marshal.Copy(data, buffer, 0, bytes);
                    Check(capture.ReleaseBuffer(frames), "ReleaseBuffer");

                    if (bytes > 0) DataAvailable?.Invoke(buffer.AsSpan(0, bytes));
                    Check(capture.GetNextPacketSize(out packet), "GetNextPacketSize");
                }
            }
            client.Stop();
        }
        catch (Exception ex)
        {
            failure = ex;
            if (!startedSignaled)
            {
                _startError = ex;
                _started.Set();
            }
        }
        finally
        {
            if (capture is not null) Marshal.ReleaseComObject(capture);
            if (client is not null) Marshal.ReleaseComObject(client);
            if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
        }

        if (startedSignaled) Stopped?.Invoke(_stopRequested ? null : failure);
    }

    private (IAudioClient Client, AudioFormatInfo Format) ActivateAndInitialize(int sampleRate)
    {
        // Tenta float 32 (melhor qualidade); se o sistema recusar, usa PCM 16 bits como na amostra oficial.
        var candidates = new[]
        {
            new AudioFormatInfo(sampleRate, 2, SampleEncoding.Float32),
            new AudioFormatInfo(sampleRate, 2, SampleEncoding.Pcm16),
            new AudioFormatInfo(44100, 2, SampleEncoding.Pcm16),
        };

        Exception? last = null;
        foreach (var fmt in candidates)
        {
            IAudioClient client = Activate();
            IntPtr pFormat = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
            try
            {
                var wfx = new WaveFormatEx
                {
                    wFormatTag = (ushort)(fmt.Encoding == SampleEncoding.Float32 ? 3 : 1),
                    nChannels = (ushort)fmt.Channels,
                    nSamplesPerSec = (uint)fmt.SampleRate,
                    nAvgBytesPerSec = (uint)(fmt.SampleRate * fmt.BlockAlign),
                    nBlockAlign = (ushort)fmt.BlockAlign,
                    wBitsPerSample = (ushort)(fmt.BytesPerSample * 8),
                    cbSize = 0,
                };
                Marshal.StructureToPtr(wfx, pFormat, false);
                uint flags = AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
                             AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
                // 200 ms de buffer no mecanismo (somente teto; os pacotes chegam a cada ~10 ms).
                int hr = client.Initialize(0 /* SHARED */, flags, 2_000_000, 0, pFormat, IntPtr.Zero);
                if (hr >= 0) return (client, fmt);
                last = Marshal.GetExceptionForHR(hr);
            }
            finally
            {
                Marshal.FreeHGlobal(pFormat);
            }
            Marshal.ReleaseComObject(client);
        }
        throw new InvalidOperationException("O Windows recusou todos os formatos para a captura por processo.", last);
    }

    private IAudioClient Activate()
    {
        var parameters = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            TargetProcessId = _excludedProcessId,
            ProcessLoopbackMode = ProcessLoopbackMode.ExcludeTargetProcessTree,
        };

        IntPtr pParams = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        IntPtr pVariant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        var handler = new ActivationCompletionHandler();
        try
        {
            Marshal.StructureToPtr(parameters, pParams, false);
            var variant = new PropVariantBlob
            {
                vt = VT_BLOB,
                cbSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                pBlobData = pParams,
            };
            Marshal.StructureToPtr(variant, pVariant, false);

            ActivateAudioInterfaceAsync(VirtualAudioDeviceProcessLoopback, IID_IAudioClient, pVariant, handler, out var operation);
            try
            {
                if (!handler.Completed.Wait(TimeSpan.FromSeconds(5)))
                    throw new COMException("Tempo esgotado ao ativar a captura por processo.", E_TIMEOUT);
            }
            finally
            {
                Marshal.ReleaseComObject(operation);
            }

            if (handler.ActivateResult < 0 || handler.ActivatedInterface is null)
                throw Marshal.GetExceptionForHR(handler.ActivateResult) ?? new COMException("Falha na ativação.", handler.ActivateResult);

            return (IAudioClient)handler.ActivatedInterface;
        }
        finally
        {
            Marshal.FreeHGlobal(pVariant);
            Marshal.FreeHGlobal(pParams);
        }
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, out uint taskIndex);

    [DllImport("avrt.dll", ExactSpelling = true)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} falhou (0x{hr:X8}).", hr);
    }
}
