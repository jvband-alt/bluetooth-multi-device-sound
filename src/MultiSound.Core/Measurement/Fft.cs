namespace MultiSound.Core.Measurement;

/// <summary>FFT complexa radix-2 iterativa (in-place).</summary>
internal static class Fft
{
    public static int NextPowerOfTwo(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    public static void Transform(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        if (n != im.Length || (n & (n - 1)) != 0) throw new ArgumentException("Tamanho deve ser potência de 2.");

        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            int half = len >> 1;
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < half; k++)
                {
                    int a = i + k, b = a + half;
                    double xr = re[b] * cr - im[b] * ci;
                    double xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr;
                    im[b] = im[a] - xi;
                    re[a] += xr;
                    im[a] += xi;
                    double t = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = t;
                }
            }
        }

        if (inverse)
            for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
    }
}
