using System.Numerics;
using FaceBlood_WinUI3.Models;

namespace FaceBlood_WinUI3.Services
{
public sealed class RppgProcessor
{
    private const double MinHz = 0.7;
    private const double MaxHz = 3.5;

    private readonly List<RgbSample> _samples = new List<RgbSample>();
    private readonly double _windowMs;

    public RppgProcessor(double windowSeconds = 12)
    {
        _windowMs = windowSeconds * 1000.0;
    }

    public int Count => _samples.Count;

    public void Reset()
    {
        _samples.Clear();
    }

    public void Push(RgbSample sample)
    {
        _samples.Add(sample);
        var cutoff = sample.T - _windowMs;
        while (_samples.Count > 0 && _samples[0].T < cutoff)
        {
            _samples.RemoveAt(0);
        }
    }

    public RppgResult? Analyze()
    {
        var n = _samples.Count;
        if (n < 64)
        {
            return null;
        }

        var t0 = _samples[0].T;
        var tN = _samples[n - 1].T;
        var durationS = (tN - t0) / 1000.0;
        if (durationS < 3)
        {
            return null;
        }

        var fps = (n - 1) / durationS;
        var targetFs = Math.Clamp(fps, 15.0, 60.0);
        var m = (int)Math.Floor(durationS * targetFs);
        if (m < 64)
        {
            return null;
        }

        var rs = new double[m];
        var gs = new double[m];
        var bs = new double[m];
        var j = 0;
        for (var i = 0; i < m; i++)
        {
            var tt = t0 + ((double)i / targetFs * 1000.0);
            while (j < n - 2 && _samples[j + 1].T < tt)
            {
                j++;
            }

            var a = _samples[j];
            var b = _samples[Math.Min(j + 1, n - 1)];
            var span = Math.Max(1.0, b.T - a.T);
            var u = Math.Clamp((tt - a.T) / span, 0.0, 1.0);
            rs[i] = (a.R * (1.0 - u)) + (b.R * u);
            gs[i] = (a.G * (1.0 - u)) + (b.G * u);
            bs[i] = (a.B * (1.0 - u)) + (b.B * u);
        }

        var meanR = Mean(rs);
        var meanG = Mean(gs);
        var meanB = Mean(bs);

        var nr = new double[m];
        var ng = new double[m];
        var nb = new double[m];

        for (var i = 0; i < m; i++)
        {
            nr[i] = meanR > 0 ? (rs[i] / meanR) - 1.0 : 0;
            ng[i] = meanG > 0 ? (gs[i] / meanG) - 1.0 : 0;
            nb[i] = meanB > 0 ? (bs[i] / meanB) - 1.0 : 0;
        }

        var x = new double[m];
        var y = new double[m];
        for (var i = 0; i < m; i++)
        {
            x[i] = ng[i] - nb[i];
            y[i] = ng[i] + nb[i] - (2.0 * nr[i]);
        }

        var sX = Std(x);
        var sY = Std(y);
        var alpha = sY > 1e-9 ? sX / sY : 1.0;

        var s = new double[m];
        for (var i = 0; i < m; i++)
        {
            s[i] = x[i] + (alpha * y[i]);
        }

        DetrendInPlace(s, Math.Max(3, (int)Math.Floor(targetFs * 0.7)));

        var windowed = new double[m];
        for (var i = 0; i < m; i++)
        {
            windowed[i] = s[i] * 0.5 * (1.0 - Math.Cos((2.0 * Math.PI * i) / (m - 1)));
        }

        var nfft = NextPow2(Math.Max(256, m));
        var spectrum = new Complex[nfft];
        for (var i = 0; i < m; i++)
        {
            spectrum[i] = new Complex(windowed[i], 0);
        }

        FftInPlace(spectrum, inverse: false);

        var binHz = targetFs / nfft;
        var minBin = Math.Max(1, (int)Math.Floor(MinHz / binHz));
        var maxBin = Math.Min((nfft / 2) - 1, (int)Math.Ceiling(MaxHz / binHz));

        var peakBin = minBin;
        var peakMag = 0.0;
        var totalMag = 0.0;
        var inBandMag = 0.0;

        for (var k = 1; k < nfft / 2; k++)
        {
            var mag = spectrum[k].Magnitude;
            totalMag += mag;
            if (k < minBin || k > maxBin)
            {
                continue;
            }

            inBandMag += mag;
            if (mag > peakMag)
            {
                peakMag = mag;
                peakBin = k;
            }
        }

        var kHat = (double)peakBin;
        if (peakBin > 1 && peakBin < (nfft / 2) - 1)
        {
            var ym1 = spectrum[peakBin - 1].Magnitude;
            var y0 = peakMag;
            var yp1 = spectrum[peakBin + 1].Magnitude;
            var denom = ym1 - (2.0 * y0) + yp1;
            if (Math.Abs(denom) > 1e-9)
            {
                var delta = (0.5 * (ym1 - yp1)) / denom;
                kHat = peakBin + delta;
            }
        }

        var freqHz = kHat * binHz;
        var bpm = freqHz * 60.0;

        var noiseFloor = (inBandMag - peakMag) / Math.Max(1, maxBin - minBin);
        var snr = noiseFloor > 0 ? 20.0 * Math.Log10(peakMag / noiseFloor) : 0;
        var confidence = Math.Clamp(
            ((snr - 2.0) / 10.0)
            * Math.Min(1.0, durationS / 6.0)
            * (peakMag / Math.Max(1e-9, totalMag / (nfft / 4.0))),
            0,
            1);

        var filteredSpectrum = new Complex[nfft];
        var peakHalfBins = Math.Max(2, (int)Math.Floor(0.4 / binHz));
        for (var k = 0; k < nfft; k++)
        {
            var kk = k <= (nfft / 2) ? k : nfft - k;
            if (kk < minBin || kk > maxBin)
            {
                continue;
            }

            var gain = Math.Abs(kk - peakBin) <= peakHalfBins ? 1.6 : 1.0;
            filteredSpectrum[k] = spectrum[k] * gain;
        }

        FftInPlace(filteredSpectrum, inverse: true);

        var waveform = new double[m];
        var wMax = 1e-9;
        for (var i = 0; i < m; i++)
        {
            var value = filteredSpectrum[i].Real;
            waveform[i] = value;
            var abs = Math.Abs(value);
            if (abs > wMax)
            {
                wMax = abs;
            }
        }

        for (var i = 0; i < m; i++)
        {
            waveform[i] /= wMax;
        }

        var last = filteredSpectrum[m - 1];
        var phase = Math.Atan2(last.Imaginary, last.Real);
        if (phase < 0)
        {
            phase += 2.0 * Math.PI;
        }

        return new RppgResult
        {
            Bpm = bpm,
            Snr = snr,
            Confidence = confidence,
            Fps = targetFs,
            FreqHz = freqHz,
            Phase = phase,
            Waveform = waveform,
        };
    }

    private static double Mean(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sum = 0.0;
        for (var i = 0; i < values.Count; i++)
        {
            sum += values[i];
        }

        return sum / values.Count;
    }

    private static double Std(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var mean = Mean(values);
        var sum = 0.0;
        for (var i = 0; i < values.Count; i++)
        {
            var d = values[i] - mean;
            sum += d * d;
        }

        return Math.Sqrt(sum / values.Count);
    }

    private static void DetrendInPlace(double[] values, int win)
    {
        var n = values.Length;
        if (n == 0)
        {
            return;
        }

        var w = Math.Clamp(win, 1, n);
        var cumulative = new double[n + 1];
        for (var i = 0; i < n; i++)
        {
            cumulative[i + 1] = cumulative[i] + values[i];
        }

        for (var i = 0; i < n; i++)
        {
            var a = Math.Max(0, i - (w / 2));
            var b = Math.Min(n, i + (w / 2) + 1);
            var mean = (cumulative[b] - cumulative[a]) / (b - a);
            values[i] -= mean;
        }
    }

    private static int NextPow2(int n)
    {
        var p = 1;
        while (p < n)
        {
            p <<= 1;
        }

        return p;
    }

    private static void FftInPlace(Complex[] buffer, bool inverse)
    {
        var n = buffer.Length;

        var j = 0;
        for (var i = 1; i < n; i++)
        {
            var bit = n >> 1;
            while ((j & bit) != 0)
            {
                j ^= bit;
                bit >>= 1;
            }

            j ^= bit;
            if (i < j)
            {
                (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = (inverse ? 2.0 : -2.0) * Math.PI / len;
            var wLen = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var i = 0; i < n; i += len)
            {
                var w = Complex.One;
                var half = len / 2;
                for (var k = 0; k < half; k++)
                {
                    var u = buffer[i + k];
                    var v = buffer[i + k + half] * w;
                    buffer[i + k] = u + v;
                    buffer[i + k + half] = u - v;
                    w *= wLen;
                }
            }
        }

        if (!inverse)
        {
            return;
        }

        for (var i = 0; i < n; i++)
        {
            buffer[i] /= n;
        }
    }
}
}
