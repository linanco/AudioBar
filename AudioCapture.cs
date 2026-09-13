using System;
using System.IO;
using System.Numerics;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioBar;

internal sealed class AudioCapture : IDisposable
{
	private const int HopLen = 1024;

	private readonly float[] spectrum;

	private readonly object sync = new object();

	private WasapiLoopbackCapture? capture;

	private readonly Timer restartTimer;

	private readonly Timer watchdogTimer;

	private MMDeviceEnumerator? deviceEnumerator;

	private DefaultDeviceClient? deviceClient;

	private bool disposed;

	private DateTime lastData = DateTime.MinValue;

	private readonly float[] samples = new float[4096];

	private readonly Complex[] fftBuf;

	private readonly float[] tempBuf;

	private readonly float[] perBarPeak = new float[64];

	// 人声存在度：来自 2~6kHz（人声泛音/清晰度）与中频旋律段的独立能量
	private float voiceEnv;

	private float voicePeak;

        private float highEnv;
        private float highPeak;
        private float highlight;

	private int sampleCount;

	private int breathPhase;

	private int diagCount;

	public float LastRms { get; private set; }

        public float Highlight
        {
            get
            {
                lock (sync)
                {
                    return highlight;
                }
            }
        }

	public AudioCapture(float[] spectrum)
	{
		this.spectrum = spectrum;
		fftBuf = new Complex[samples.Length];
		tempBuf = new float[spectrum.Length];
		restartTimer = new Timer
		{
			Interval = 500
		};
		restartTimer.Tick += delegate
		{
			RecreateCapture();
		};
		watchdogTimer = new Timer
		{
			Interval = 2000
		};
		watchdogTimer.Tick += delegate
		{
			if (!disposed && capture != null && lastData != DateTime.MinValue && (DateTime.UtcNow - lastData).TotalSeconds > 5.0)
			{
				WriteDiag("看门狗：长时间无数据回调，疑似设备切换导致停摆，自动重启");
				RecreateCapture();
			}
		};
	}

	public void Start()
	{
		RecreateCapture();
	}

	private void RecreateCapture()
	{
		restartTimer.Stop();
		if (disposed)
		{
			return;
		}
		WasapiLoopbackCapture wasapiLoopbackCapture = capture;
		capture = null;
		if (wasapiLoopbackCapture != null)
		{
			try
			{
				wasapiLoopbackCapture.StopRecording();
			}
			catch
			{
			}
			try
			{
				wasapiLoopbackCapture.DataAvailable -= OnDataAvailable;
			}
			catch
			{
			}
			try
			{
				wasapiLoopbackCapture.RecordingStopped -= OnRecordingStopped;
			}
			catch
			{
			}
			try
			{
				wasapiLoopbackCapture.Dispose();
			}
			catch
			{
			}
		}
		WasapiLoopbackCapture wasapiLoopbackCapture2;
		try
		{
			wasapiLoopbackCapture2 = new WasapiLoopbackCapture();
		}
		catch (Exception ex)
		{
			WriteDiag("捕获创建失败，稍后重试：" + ex.Message);
			ScheduleRestart(1500);
			return;
		}
		try
		{
			WriteDiag($"捕获格式：{wasapiLoopbackCapture2.WaveFormat.Encoding} {wasapiLoopbackCapture2.WaveFormat.SampleRate}Hz {wasapiLoopbackCapture2.WaveFormat.Channels}ch {wasapiLoopbackCapture2.WaveFormat.BitsPerSample}bit");
			wasapiLoopbackCapture2.DataAvailable += OnDataAvailable;
			wasapiLoopbackCapture2.RecordingStopped += OnRecordingStopped;
			wasapiLoopbackCapture2.StartRecording();
			capture = wasapiLoopbackCapture2;
			watchdogTimer.Start();
			EnsureDefaultDeviceWatch();
		}
		catch (Exception ex2)
		{
			WriteDiag("捕获启动失败，稍后重试：" + ex2.Message);
			try
			{
				wasapiLoopbackCapture2.Dispose();
			}
			catch
			{
			}
			ScheduleRestart(1500);
		}
	}

	private void EnsureDefaultDeviceWatch()
	{
		if (deviceClient != null)
		{
			return;
		}
		try
		{
			deviceEnumerator = new MMDeviceEnumerator();
			deviceClient = new DefaultDeviceClient(delegate
			{
				ScheduleRestart(300);
			});
			deviceEnumerator.RegisterEndpointNotificationCallback(deviceClient);
		}
		catch (Exception ex)
		{
			WriteDiag("默认设备监听注册失败：" + ex.Message);
			deviceClient = null;
			deviceEnumerator?.Dispose();
			deviceEnumerator = null;
		}
	}

	private void OnRecordingStopped(object? sender, StoppedEventArgs e)
	{
		if (!disposed)
		{
			WriteDiag("捕获已停止（设备切换/异常），稍后自动重启");
			ScheduleRestart(500);
		}
	}

	private void ScheduleRestart(int delayMs)
	{
		restartTimer.Interval = delayMs;
		restartTimer.Start();
	}

	private static void WriteDiag(string line)
	{
		try
		{
			File.AppendAllText(Path.Combine(Path.GetTempPath(), "audiobar_rms.log"), $"{DateTime.Now:HH:mm:ss.fff} {line}\n");
		}
		catch
		{
		}
	}

	private void OnDataAvailable(object? sender, WaveInEventArgs e)
	{
		lastData = DateTime.UtcNow;
		WaveFormat waveFormat = (sender as WasapiLoopbackCapture)?.WaveFormat ?? capture?.WaveFormat;
		if (waveFormat == null || e.BytesRecorded == 0)
		{
			return;
		}
		int num = Math.Max(1, waveFormat.Channels);
		int num2 = waveFormat.BitsPerSample / 8;
		int num3 = num * num2;
		if (num3 == 0)
		{
			return;
		}
		lock (sync)
		{
			for (int i = 0; i + num3 <= e.BytesRecorded; i += num3)
			{
				float num4 = 0f;
				for (int j = 0; j < num; j++)
				{
					int offset = i + j * num2;
					num4 += ReadSample(e.Buffer, offset, num2, waveFormat.Encoding);
				}
				samples[sampleCount++] = num4 / (float)num;
				if (sampleCount >= samples.Length)
				{
					CalculateSpectrum();
					Array.Copy(samples, samples.Length - 1024, samples, 0, 1024);
					sampleCount = 1024;
				}
			}
		}
	}

	private void CalculateSpectrum()
	{
		int num = samples.Length;
		Complex[] array = fftBuf;
		double num2 = 0.0;
		for (int i = 0; i < num; i++)
		{
			num2 += (double)samples[i];
		}
		num2 /= (double)num;
		for (int j = 0; j < num; j++)
		{
			double num3 = 0.5 * (1.0 - Math.Cos(Math.PI * 2.0 * (double)j / (double)(num - 1)));
			array[j] = new Complex(((double)samples[j] - num2) * num3, 0.0);
		}
		for (int num4 = 2; num4 <= num; num4 <<= 1)
		{
			double num5 = Math.PI * -2.0 / (double)num4;
			Complex complex = new Complex(Math.Cos(num5), Math.Sin(num5));
			for (int k = 0; k < num; k += num4)
			{
				Complex one = Complex.One;
				for (int l = 0; l < num4 / 2; l++)
				{
					Complex complex2 = array[k + l];
					Complex complex3 = one * array[k + l + num4 / 2];
					array[k + l] = complex2 + complex3;
					array[k + l + num4 / 2] = complex2 - complex3;
					one *= complex;
				}
			}
		}
		double num6 = 0.0;
		for (int m = 0; m < num; m++)
		{
			num6 += (double)(samples[m] * samples[m]);
		}
		double num7 = Math.Sqrt(num6 / (double)num);
		LastRms = (float)num7;
		if (++diagCount % 10 == 1)
		{
			WriteDiag($"rms={num7:F4}  (门控0.004，高于它才会动)");
		}
		for (int n = 0; n < spectrum.Length; n++)
		{
			int num8 = Math.Min(num / 2 - 1, (int)Math.Pow(2.0, (float)n * 0.1746f) + 1);
			int num9 = Math.Min(num / 2 - 1, (int)Math.Pow(2.0, (float)(n + 1) * 0.1746f) + 2);
			float num10 = 0f;
			for (int num11 = num8; num11 <= num9; num11++)
			{
				num10 += (float)array[num11].Magnitude;
			}
			num10 /= (float)Math.Max(1, num9 - num8 + 1);
			spectrum[n] = num10;
		}

		// ===== 映射增强（大厂做法）：三段独立自动增益 + 人声存在度检测 =====
		int M = spectrum.Length;
		float[] bar = tempBuf;

		// 1) 每柱相对自身峰值归一化（保留原版张缩手感）
		for (int i = 0; i < M; i++)
		{
			float raw = spectrum[i];
			float pk = perBarPeak[i];
			pk = raw > pk ? pk + (raw - pk) * 0.45f : pk + (raw - pk) * 0.17f;
			pk = Math.Max(pk, 1E-06f);
			perBarPeak[i] = pk;
			bar[i] = raw / pk;
		}

		// 2) 频段独立能量（bars 是 2 的对数分布，最高约 24kHz）
		//    低音 ~bars0-5  旋律/人声 ~bars20-44(≈300Hz-3kHz)  人声泛音/齿音 ~bars45-53(2-6kHz)  高音 ~bars54-62
		float bassE = BandMean(bar, 0, 5);
		float vocalE = BandMean(bar, 20, 44);
		float formantE = BandMean(bar, 45, 53);
		float hiE = BandMean(bar, 54, 62);
			float trebleE = BandMean(bar, 45, 62); // 上中频(齿音)+高频一起算，高音素材更足更易触发

		// 人声存在度：取"旋律+齿音"能量的包络，慢速自动增益得到 0..1 活跃度
		float vp = Math.Max(vocalE, formantE);
		voiceEnv = vp > voiceEnv ? voiceEnv + (vp - voiceEnv) * 0.6f : voiceEnv + (vp - voiceEnv) * 0.35f;
		if (vp > voicePeak) voicePeak = vp;
		else voicePeak += (0.9f - voicePeak) * 0.12f; // 基准缓慢回落，避免人声一停就顶满
		voicePeak = Math.Max(voicePeak, 1E-06f);
		float voiceP = Math.Min(1f, voiceEnv / voicePeak);

            // 高音活跃度：高音频段活跃度经慢速自动增益，驱动柱子变白
            highEnv = trebleE > highEnv ? highEnv + (trebleE - highEnv) * 0.8f : highEnv + (trebleE - highEnv) * 0.3f;
            highlight = Math.Min(1f, Math.Max(0f, (highEnv - 0.05f) * 2.4f)); // 阈值+增益：上中/高频一现就明显闪白 // 高音能量直接放大驱动，一有高音就明显变白

		// 3) 按所属频段加权：人声出现时中频段明显抬起，低音/高音独立起伏，互不抢戏
		for (int i = 0; i < M; i++)
		{
			float f = (float)i / (float)(M - 1);
			float dim = 1f - 0.10f * f; // 高音略收（沿用原版防常年钉顶）
			float boost = 1f;
			if (i <= 5)
			{
				boost = 1f + 0.30f * bassE;
			}
			else if (i >= 20 && i <= 53)
			{
				// 人声/旋律段：齿音(45+)给 0.7 权重，主体给满权重
				boost = 1f + 0.85f * voiceP * (i >= 45 ? 0.7f : 1f);
			}
			else if (i >= 54)
			{
				boost = 1f + 0.25f * hiE;
			}
			float v = Math.Max(0f, bar[i] * dim * boost);
			// 软饱和曲线：持续音停在中等高度，只有鼓点/瞬态才能冲顶，杜绝一条直线
			v = 1f - (float)Math.Exp(-1.05f * v);
			bar[i] = Math.Max(0.01f, Math.Min(0.99f, v));
		}

		// 4) 相邻平滑 + 写回
		for (int i = 1; i < M - 1; i++)
		{
			bar[i] = (bar[i - 1] + 2f * bar[i] + bar[i + 1]) * 0.25f;
		}
		Array.Copy(bar, spectrum, M);

		if (num7 < 0.00039999998989515007)
		{
			fixedSpectrumMin();
			sampleCount = 0;
		}
		else
		{
			sampleCount = 0;
		}
		void fixedSpectrumMin()
		{
			int num21 = breathPhase++ % 300;
			for (int num22 = 0; num22 < spectrum.Length; num22++)
			{
				spectrum[num22] = 0.05f + 0.04f * (float)(Math.Sin((double)num22 * 0.4 + (double)num21 * 0.12) + 1.0) / 2f;
			}
		}
	}

	private static float BandMean(float[] arr, int a, int b)
	{
		float s = 0f;
		int c = 0;
		for (int i = a; i <= b && i < arr.Length; i++)
		{
			s += arr[i];
			c++;
		}
		return c == 0 ? 0f : s / (float)c;
	}

	private static float ReadSample(byte[] buffer, int offset, int bytesPerSample, WaveFormatEncoding encoding)
	{
		if (encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample >= 4)
		{
			return BitConverter.ToSingle(buffer, offset);
		}
		if (bytesPerSample >= 2)
		{
			return (float)BitConverter.ToInt16(buffer, offset) / 32768f;
		}
		return (float)(buffer[offset] - 128) / 128f;
	}

	public void Dispose()
	{
		disposed = true;
		restartTimer.Stop();
		restartTimer.Dispose();
		watchdogTimer.Stop();
		watchdogTimer.Dispose();
		try
		{
			deviceEnumerator?.UnregisterEndpointNotificationCallback(deviceClient);
		}
		catch
		{
		}
		deviceEnumerator?.Dispose();
		deviceEnumerator = null;
		deviceClient = null;
		WasapiLoopbackCapture wasapiLoopbackCapture = capture;
		capture = null;
		if (wasapiLoopbackCapture != null)
		{
			wasapiLoopbackCapture.DataAvailable -= OnDataAvailable;
			wasapiLoopbackCapture.RecordingStopped -= OnRecordingStopped;
			try
			{
				wasapiLoopbackCapture.StopRecording();
			}
			catch
			{
			}
			wasapiLoopbackCapture.Dispose();
		}
	}
}





