using MultiSound.Core.Buffers;

namespace MultiSound.Core.Tests;

public class StereoRingBufferTests
{
    private static float[] Frames(int start, int count)
    {
        var a = new float[count * 2];
        for (int i = 0; i < count; i++) { a[i * 2] = start + i; a[i * 2 + 1] = -(start + i); }
        return a;
    }

    [Fact]
    public void WriteThenRead_PreservesOrderAcrossWrap()
    {
        var rb = new StereoRingBuffer(10);
        rb.Write(Frames(0, 7));
        var tmp = new float[20];
        Assert.Equal(5, rb.Read(tmp, 5));
        rb.Write(Frames(7, 6)); // dá a volta no buffer
        Assert.Equal(8, rb.Count);
        Assert.Equal(8, rb.Read(tmp, 10));
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(5 + i, tmp[i * 2]);
            Assert.Equal(-(5 + i), tmp[i * 2 + 1]);
        }
        Assert.Equal(0, rb.Count);
    }

    [Fact]
    public void Overflow_DropsOldestFrames()
    {
        var rb = new StereoRingBuffer(8);
        rb.Write(Frames(0, 6));
        int dropped = rb.Write(Frames(6, 5));
        Assert.Equal(3, dropped);
        Assert.Equal(8, rb.Count);
        var tmp = new float[16];
        rb.Read(tmp, 8);
        Assert.Equal(3, tmp[0]); // os 3 mais antigos foram descartados
        Assert.Equal(10, tmp[14]);
    }

    [Fact]
    public void WriteLargerThanCapacity_KeepsMostRecent()
    {
        var rb = new StereoRingBuffer(4);
        int dropped = rb.Write(Frames(0, 10));
        Assert.Equal(6, dropped);
        var tmp = new float[8];
        Assert.Equal(4, rb.Read(tmp, 4));
        Assert.Equal(6, tmp[0]);
        Assert.Equal(9, tmp[6]);
    }

    [Fact]
    public void Skip_And_Clear()
    {
        var rb = new StereoRingBuffer(16);
        rb.Write(Frames(0, 10));
        Assert.Equal(4, rb.Skip(4));
        var tmp = new float[2];
        rb.Read(tmp, 1);
        Assert.Equal(4, tmp[0]);
        Assert.Equal(5, rb.Skip(100));
        rb.Write(Frames(0, 3));
        rb.Clear();
        Assert.Equal(0, rb.Count);
    }

    [Fact]
    public async Task ConcurrentProducerConsumer_NoCorruption()
    {
        var rb = new StereoRingBuffer(4096);
        const int total = 200_000;
        long sumRead = 0;
        int readCount = 0;
        var producer = Task.Run(() =>
        {
            int next = 0;
            while (next < total)
            {
                int n = Math.Min(64, total - next);
                while (rb.Count > 4096 - n) Thread.Yield();
                rb.Write(Frames(next, n));
                next += n;
            }
        });
        var buf = new float[256];
        int expected = 0;
        bool ordered = true;
        while (readCount < total)
        {
            int got = rb.Read(buf, 128);
            for (int i = 0; i < got; i++)
            {
                if (buf[i * 2] != expected || buf[i * 2 + 1] != -expected) ordered = false;
                sumRead += (long)buf[i * 2];
                expected++;
            }
            readCount += got;
            if (got == 0) Thread.Yield();
        }
        await producer;
        Assert.True(ordered);
        Assert.Equal((long)total * (total - 1) / 2, sumRead);
    }
}
