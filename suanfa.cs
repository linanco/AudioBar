using System.Numerics;
using NAudio.Wave;

namespace AudioBar;

internal sealed class Analyzer
{
    public const int FftSize = 4096;
    public const int HopLen = 1024;             // 重叠步长，越小帧率越高

    private const float RMSGateThreshold = 0.0004f;

    private readonly float[] levels;            // 输出柱高 0..1
    private readonly float[] samples;
    private readonly Complex[] fft;
    private readonly float[] temp;
    private readonly float[] bandDb;            // 分频段 EQ 的 dB 偏移
    private readonly float[] smooth;            // 输出时间平滑
    private float autoGain;
    private int[] binLo = null!;                // 每柱覆盖的 FFT bin 起止
    private int[] binHi = null!;
    private int sampleCount;
    private int breathPhase;
    private int diagCount;

    public Analyzer(float[] levels, int barCount)
    {
        this.levels = levels;
        samples = new float[FftSize];
        fft = new Complex[FftSize];
        temp = new float[barCount];
        bandDb = new float[barCount];
        smooth = new float[barCount];
        BuildBinMap(barCount, FftSize);
        for (var bar = 0; bar < barCount; bar++)
            bandDb[bar] = (float)(20.0 * Math.Log10(Math.Max(BandGain(bar / (float)(barCount - 1)), 1e-3)));
    }

    public float Rms { get; private set; }

    public void Feed(float sample)
    {
        samples[sampleCount++] = sample;
        if (sampleCount >= FftSize)
            Analyze();
    }

    public static float ReadSample(byte[] buffer, int offset, int bytesPerSample, WaveFormatEncoding encoding)
    {
        if (encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample >= 4)
            return BitConverter.ToSingle(buffer, offset);
        if (bytesPerSample >= 2)
            return BitConverter.ToInt16(buffer, offset) / 32768f;
        return (buffer[offset] - 128) / 128f;
    }

    private void Analyze()
    {
        // 去直流
        var dc = 0.0;
        for (var i = 0; i < FftSize; i++)
            dc += samples[i];
        dc /= FftSize;

        // 汉宁窗
        for (var i = 0; i < FftSize; i++)
        {
            var window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)));
            fft[i] = new Complex((samples[i] - dc) * window, 0);
        }

        // 迭代 radix-2 FFT
        for (var length = 2; length <= FftSize; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var root = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var start = 0; start < FftSize; start += length)
            {
                var factor = Complex.One;
                for (var j = 0; j < length / 2; j++)
                {
                    var even = fft[start + j];
                    var odd = factor * fft[start + j + length / 2];
                    fft[start + j] = even + odd;
                    fft[start + j + length / 2] = even - odd;
                    factor *= root;
                }
            }
        }

        var sumSq = 0.0;
        for (var i = 0; i < FftSize; i++)
            sumSq += samples[i] * samples[i];
        Rms = (float)Math.Sqrt(sumSq / FftSize);
        if (++diagCount % 10 == 1)
            WriteDiag($"rms={Rms:F4}");

        // 各频带取平均幅度
        var count = levels.Length;
        for (var bar = 0; bar < count; bar++)
        {
            var energy = 0f;
            for (var bin = binLo[bar]; bin <= binHi[bar]; bin++)
                energy += (float)fft[bin].Magnitude;
            levels[bar] = energy / Math.Max(1, binHi[bar] - binLo[bar] + 1);
        }

        // 慢速自动增益，只随整体音量小幅调节
        var rmsDb = 20.0 * Math.Log10(Math.Max(Rms, 1e-6));
        var gainTarget = (float)Math.Clamp(-rmsDb + 6.0, -8.0, 24.0);
        autoGain += (gainTarget - autoGain) * (gainTarget > autoGain ? 0.6f : 0.045f);

        // dB 标尺映射 + EQ
        const float DbFloor = -58f, DbCeil = -4f;
        for (var bar = 0; bar < count; bar++)
        {
            var db = 20.0 * Math.Log10(Math.Max(levels[bar], 1e-7)) + autoGain + bandDb[bar];
            temp[bar] = Math.Max(0.01f, (float)Math.Clamp((db - DbFloor) / (DbCeil - DbFloor), 0.0, 1.0));
        }

        // 相邻平滑
        for (var bar = 1; bar < count - 1; bar++)
            temp[bar] = (temp[bar - 1] + 2 * temp[bar] + temp[bar + 1]) * 0.25f;
        // 时间平滑 + 弱信号死区；高频端阻尼更强，避免顶上的柱子被噪声带着跳
        for (var bar = 0; bar < count; bar++)
        {
            var f = bar / (float)(count - 1);
            var coef = f > 0.93f ? 0.12f : (f > 0.85f ? 0.22f : (f > 0.60f ? 0.45f : 0.55f));
            smooth[bar] += (temp[bar] - smooth[bar]) * coef;
            var dead = f > 0.93f ? 0.12f : 0.06f;
            levels[bar] = Math.Max(0.01f, temp[bar] < dead ? 0.02f : smooth[bar]);
        }

        // 几乎无声时底部轻微呼吸
        if (Rms < RMSGateThreshold)
        {
            var t = (breathPhase++) % 300;
            for (var i = 0; i < count; i++)
                levels[i] = 0.05f + 0.04f * (float)(Math.Sin(i * 0.4 + t * 0.12) + 1f) / 2f;
            sampleCount = 0;
            return;
        }

        // 重叠窗口滑动
        Array.Copy(samples, FftSize - HopLen, samples, 0, HopLen);
        sampleCount = HopLen;
    }

    // 频段增益：低音重、人声略收、高音亮
    private static readonly (float f, float g)[] BandNodes =
    {
        (0.00f, 1.30f),
        (0.10f, 1.26f),
        (0.16f, 1.12f),
        (0.28f, 1.10f),
        (0.34f, 0.98f),
        (0.42f, 1.06f),
        (0.50f, 0.99f),
        (0.58f, 1.07f),
        (0.66f, 1.13f),
        (0.74f, 1.22f),
        (0.84f, 1.32f),
        (1.00f, 1.42f),
    };

    private static float BandGain(float f)
    {
        f = Math.Clamp(f, 0f, 1f);
        for (var i = 0; i < BandNodes.Length - 1; i++)
        {
            var (f0, g0) = BandNodes[i];
            var (f1, g1) = BandNodes[i + 1];
            if (f >= f0 && f <= f1)
            {
                var t = f1 == f0 ? 0f : (f - f0) / (f1 - f0);
                return g0 + (g1 - g0) * t;
            }
        }
        return BandNodes[^1].g;
    }

    // 把柱分到不同频段：人声带分最多柱，最高到约18kHz（避开24kHz的高频噪声）
    private void BuildBinMap(int barCount, int fftSize)
    {
        binLo = new int[barCount];
        binHi = new int[barCount];
        var binMax = fftSize / 2;
        var max = Math.Min(binMax - 1, 1536);
        int nLow = 12, nVocal = 36;                    // 低频 / 人声带
        var nHigh = barCount - nLow - nVocal;
        FillBinSection(0, 1, 18, nLow);                // ~200Hz
        FillBinSection(nLow, 18, 341, nVocal);         // ~4kHz
        FillBinSection(nLow + nVocal, 341, max, nHigh);
    }

    private void FillBinSection(int startBar, int bStart, int bEnd, int n)
    {
        for (var k = 0; k < n; k++)
        {
            var lo = MapLog(bStart, bEnd, (float)k / n);
            var hi = MapLog(bStart, bEnd, (float)(k + 1) / n);
            binLo[startBar] = (int)Math.Round(lo);
            binHi[startBar] = Math.Max(binLo[startBar] + 1, (int)Math.Round(hi));
            startBar++;
        }
    }

    private static float MapLog(int a, int b, float t)
    {
        var la = (float)Math.Log(a + 1);
        var lb = (float)Math.Log(b + 1);
        return (float)Math.Exp(la + (lb - la) * t) - 1;
    }

    private static void WriteDiag(string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "audiobar_rms.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {line}\n");
        }
        catch { }
    }
}
