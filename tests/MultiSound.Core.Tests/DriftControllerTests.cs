using MultiSound.Core.Sync;

namespace MultiSound.Core.Tests;

public class DriftControllerTests
{
    [Theory]
    [InlineData(300)]
    [InlineData(-500)]
    [InlineData(1200)]
    public void ConvergesToClockDrift(double driftPpm)
    {
        // Modelo: o buffer ganha (drift - correção) segundos por segundo.
        const int rate = 48000;
        var c = new DriftController(rate);
        double target = rate * 0.060;
        double fill = target;
        c.Reset(fill);
        double dt = 0.010;
        for (double t = 0; t < 300; t += dt)
        {
            double corr = c.Update(fill, target, dt);
            fill += (driftPpm * 1e-6 - (corr - 1.0)) * rate * dt;
        }
        Assert.InRange((c.Correction - 1.0) * 1e6, driftPpm - 20, driftPpm + 20);
        Assert.InRange((fill - target) / rate * 1000, -1.0, 1.0); // erro < 1 ms
        Assert.False(c.HardResyncRequested);
    }

    [Fact]
    public void CorrectionIsBounded()
    {
        var c = new DriftController(48000) { HardResyncThresholdSeconds = 10 };
        c.Reset(0);
        for (int i = 0; i < 1000; i++) c.Update(48000, 0, 0.01); // 1 s acima do alvo
        Assert.InRange(c.Correction, 1.0 - c.MaxCorrection - 1e-12, 1.0 + c.MaxCorrection + 1e-12);
    }

    [Fact]
    public void LargeSustainedError_RequestsHardResync()
    {
        var c = new DriftController(48000);
        c.Reset(48000 * 0.06);
        for (int i = 0; i < 300; i++) c.Update(48000 * 0.30, 48000 * 0.06, 0.01);
        Assert.True(c.HardResyncRequested);
        c.Reset(48000 * 0.06);
        Assert.False(c.HardResyncRequested);
    }
}
