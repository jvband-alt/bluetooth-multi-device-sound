using MultiSound.Core.Devices;
using MultiSound.Core.Dsp;
using MultiSound.Core.Settings;
using MultiSound.Core.Signals;
using MultiSound.Core.Sync;

namespace MultiSound.Core.Tests;

public class CaptureGapFillerTests
{
    [Fact]
    public void NoSynthesisWhileDataArrives()
    {
        var g = new CaptureGapFiller(48000, 0.04);
        g.Start(0);
        for (double t = 0; t < 1; t += 0.01)
        {
            g.OnData(t);
            Assert.Equal(0, g.FramesToSynthesize(t + 0.005));
        }
        Assert.False(g.IsSynthesizing);
    }

    [Fact]
    public void FillsTheWholeGap_AfterThreshold()
    {
        var g = new CaptureGapFiller(48000, 0.04);
        g.OnData(1.0);
        Assert.Equal(0, g.FramesToSynthesize(1.03));
        int total = 0;
        for (double t = 1.05; t <= 2.0 + 1e-9; t += 0.01) total += g.FramesToSynthesize(t);
        Assert.True(g.IsSynthesizing);
        Assert.InRange(total, 48000 - 2, 48000); // 1 s de silêncio desde o último pacote
        g.OnData(2.01);
        Assert.False(g.IsSynthesizing);
    }
}

public class TestSignalGeneratorTests
{
    [Fact]
    public void BeepOncePerPeriod_WithSilenceBetween()
    {
        var gen = new TestSignalGenerator(48000);
        var buf = new float[48000 * 2 * 2];
        gen.Generate(buf, 96000);
        float peakBeep = 0, peakSilence = 0;
        for (int i = 0; i < 96000; i++)
        {
            int inPeriod = i % 48000;
            float a = Math.Abs(buf[i * 2]);
            if (inPeriod < gen.BeepFrames) peakBeep = Math.Max(peakBeep, a);
            else peakSilence = Math.Max(peakSilence, a);
            Assert.Equal(buf[i * 2], buf[i * 2 + 1]);
        }
        Assert.InRange(peakBeep, 0.4f, 0.5f);
        Assert.Equal(0f, peakSilence);
        // Começa e termina em zero (envelope Hann, sem estalo).
        Assert.Equal(0f, buf[0]);
    }
}

public class LevelMathTests
{
    [Fact]
    public void PercentToGain_Curve()
    {
        Assert.Equal(0f, LevelMath.PercentToGain(0));
        Assert.Equal(1f, LevelMath.PercentToGain(100));
        Assert.Equal(0.25f, LevelMath.PercentToGain(50));
        Assert.Equal(1f, LevelMath.PercentToGain(150));
    }

    [Fact]
    public void Meter()
    {
        Assert.Equal(0, LevelMath.PeakToMeter(0));
        Assert.Equal(1, LevelMath.PeakToMeter(1), 6);
        Assert.InRange(LevelMath.PeakToMeter(0.1f), 0.66, 0.67);
        Assert.Equal(0.9f, LevelMath.Peak(new[] { 0.1f, -0.9f, 0.5f }));
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void RoundTrip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ms-test-" + Guid.NewGuid());
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        var s = new AppSettings { CaptureMode = CaptureMode.EndpointLoopback, SourceDeviceId = "{src}", SafetyBufferMs = 80, StartMinimized = true };
        var d = s.GetOrAddDevice("{0.0.0.00000000}.{aaa}", "Harman Kardon Go + Play 3");
        d.Selected = true; d.DelayMs = 120; d.VolumePercent = 70;
        store.Save(s);

        var loaded = store.Load();
        Assert.Equal(CaptureMode.EndpointLoopback, loaded.CaptureMode);
        Assert.Equal("{src}", loaded.SourceDeviceId);
        Assert.Equal(80, loaded.SafetyBufferMs);
        Assert.True(loaded.StartMinimized);
        var ld = Assert.Single(loaded.Devices);
        Assert.Equal("Harman Kardon Go + Play 3", ld.Name);
        Assert.True(ld.Selected);
        Assert.Equal(120, ld.DelayMs);
        Assert.Equal(70, ld.VolumePercent);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void CorruptFile_ReturnsDefaults_AndKeepsBackup()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ms-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ isto não é json");
        var loaded = new SettingsStore(path).Load();
        Assert.Empty(loaded.Devices);
        Assert.True(File.Exists(path + ".corrompido"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Normalize_ClampsAndDeduplicates()
    {
        var s = new AppSettings { SafetyBufferMs = 5, OutputLatencyMs = 5000 };
        s.Devices.Add(new DeviceSettings { Id = "A", DelayMs = 99999, VolumePercent = -3 });
        s.Devices.Add(new DeviceSettings { Id = "a", DelayMs = 1 });
        s.Devices.Add(new DeviceSettings { Id = "" });
        s.Normalize();
        Assert.Equal(20, s.SafetyBufferMs);
        Assert.Equal(200, s.OutputLatencyMs);
        var d = Assert.Single(s.Devices);
        Assert.Equal(2000, d.DelayMs);
        Assert.Equal(0, d.VolumePercent);
    }
}

public class DeviceTests
{
    [Theory]
    [InlineData("Fone de ouvido (MIFA A90 Hands-Free AG Audio)", 16000, 1, true, true)]
    [InlineData("Headset (WH-1000XM4 Hands-Free)", 16000, 1, true, true)]
    [InlineData("Alto-falantes (MIFA A90 Stereo)", 48000, 2, true, false)]
    [InlineData("Speakers (Harman Kardon Go + Play 3)", 44100, 2, true, false)]
    [InlineData("Headset (USB Audio)", 48000, 2, false, false)]
    [InlineData("Alto-falantes (Realtek(R) Audio)", 48000, 2, false, false)]
    [InlineData("Caixa BT genérica", 8000, 1, true, true)]
    public void HandsFreeDetection(string name, int rate, int ch, bool bt, bool expected)
        => Assert.Equal(expected, DeviceClassifier.IsHandsFree(name, rate, ch, bt));

    [Fact]
    public void BluetoothDetection()
    {
        Assert.True(DeviceClassifier.IsBluetooth("BTHENUM", "Speakers"));
        Assert.True(DeviceClassifier.IsBluetooth(@"BTHHFENUM\{0000111e}", "x"));
        Assert.False(DeviceClassifier.IsBluetooth("HDAUDIO", "Speakers (Realtek)"));
        Assert.True(DeviceClassifier.IsBluetooth(null, "Bluetooth Speaker"));
    }

    [Fact]
    public void Routing_PreventsFeedbackAndDuplicates()
    {
        var devices = new[]
        {
            new RouteInput("hk", true, true),
            new RouteInput("mifa", true, true),
            new RouteInput("realtek", true, true),
            new RouteInput("HK", true, true), // duplicata
            new RouteInput("off", true, false),
            new RouteInput("unsel", false, true),
        };

        var loop = RoutingPlanner.Plan(CaptureMode.EndpointLoopback, "realtek", "realtek", devices);
        Assert.Equal(RouteDecision.Play, loop["hk"]);
        Assert.Equal(RouteDecision.Play, loop["mifa"]);
        Assert.Equal(RouteDecision.SkipIsCaptureSource, loop["realtek"]);
        Assert.Equal(RouteDecision.Unavailable, loop["off"]);
        Assert.False(loop.ContainsKey("unsel"));
        Assert.Equal(4, loop.Count);

        var apps = RoutingPlanner.Plan(CaptureMode.AllAppsExceptSelf, null, "hk", devices);
        Assert.Equal(RouteDecision.SkipIsWindowsDefault, apps["hk"]);
        Assert.Equal(RouteDecision.Play, apps["realtek"]);
    }
}
