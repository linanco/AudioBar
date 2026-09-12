using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace AudioBar;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 单实例
        s_mutex = new Mutex(true, "AudioBar_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "程序已经在运行中，请勿重复打开。",
                "AudioBar", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new VisualizerForm());
    }

    private static Mutex? s_mutex; // 持有引用，避免被 GC 回收导致互斥失效
}

internal sealed class VisualizerForm : Form
{
    private const int BarCount = 64;
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExLayered = 0x80000;
    private const int WsExToolwindow = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private readonly System.Windows.Forms.Timer animationTimer = new() { Interval = 16 };
    private readonly float[] levels = new float[BarCount];
    private readonly float[] peaks = new float[BarCount];
    private readonly float[] spectrum = new float[BarCount];
    private readonly NotifyIcon trayIcon;
    private readonly AudioCapture audioCapture;
    private readonly LinearGradientBrush[,] barBrushes;  // [柱][能量档]：壁纸色纵向渐变
    private Color[] currentPalette = Array.Empty<Color>();   // 当前色板（随动画逼近目标）
    private Color[] targetPalette = Array.Empty<Color>();    // 最新采样目标色板
    private System.Windows.Forms.Timer? paletteAnimTimer;    // 色板切换动画定时器
    private bool showPeaks = false;
    private bool isPaused;
    private bool mousePassthrough = true;

    public VisualizerForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        TransparencyKey = Color.Black;   // 柱子弹之外的背景全透明，无毛玻璃
        DoubleBuffered = true;

        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Bounds = new Rectangle(screen.Left, screen.Bottom - 84, screen.Width, 84);

        barBrushes = new LinearGradientBrush[BarCount, 9];
        RebuildBrushes();

        // 每 3 秒重抓一次桌面色板，让渐变跟着壁纸走
        var refreshTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        refreshTimer.Tick += (_, _) => RebuildBrushes();
        refreshTimer.Start();

        trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "音频条",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        trayIcon.DoubleClick += (_, _) => TogglePassthrough();

        audioCapture = new AudioCapture(spectrum);
        audioCapture.Start();

        animationTimer.Tick += (_, _) =>
        {
            if (!isPaused)
            {
                UpdateLevels();
                Invalidate();
            }
        };
        animationTimer.Start();
        FormClosed += (_, _) =>
        {
            animationTimer.Stop();
            audioCapture.Dispose();
            trayIcon.Visible = false;
            trayIcon.Dispose();
        };
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var pauseItem = new ToolStripMenuItem("暂停动画");
        pauseItem.Click += (_, _) =>
        {
            isPaused = !isPaused;
            pauseItem.Text = isPaused ? "继续动画" : "暂停动画";
        };

        var passthroughItem = new ToolStripMenuItem("鼠标穿透")
        {
            Checked = true,
            CheckOnClick = true
        };
        passthroughItem.Click += (_, _) =>
        {
            mousePassthrough = passthroughItem.Checked;
            ApplyWindowStyle();
        };

        var peakItem = new ToolStripMenuItem("显示峰值方块")
        {
            Checked = showPeaks,
            CheckOnClick = true
        };
        peakItem.Click += (_, _) =>
        {
            showPeaks = peakItem.Checked;
            Invalidate();
        };

        var exitItem = new ToolStripMenuItem("退出程序");
        exitItem.Click += (_, _) => Close();

        menu.Items.Add(pauseItem);
        menu.Items.Add(passthroughItem);
        menu.Items.Add(peakItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        return menu;
    }

    private void TogglePassthrough()
    {
        mousePassthrough = !mousePassthrough;
        ApplyWindowStyle();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyWindowStyle();
    }

    // 让窗体背景变成系统级 Acrylic 毛玻璃模糊
    private void EnableAcrylic()
    {
        var accent = new AccentPolicy
        {
            AccentState = 4,            // ACCENT_ENABLE_ACRYLICBLURBEHIND
            AccentFlags = 0,
            GradientColor = 0x3E000000, // 轻深色染色（ABGR）
            AnimationId = 0
        };
        var data = new WindowCompositionAttributeData
        {
            Attribute = 19,            // WCA_ACCENT_POLICY
            SizeOfData = Marshal.SizeOf(accent),
            Data = Marshal.AllocHGlobal(Marshal.SizeOf(accent))
        };
        Marshal.StructureToPtr(accent, data.Data, false);
        SetWindowCompositionAttribute(Handle, ref data);
        Marshal.FreeHGlobal(data.Data);
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    private void ApplyWindowStyle()
    {
        var style = GetWindowLong(Handle, GwlExstyle);
        if (mousePassthrough)
            style |= WsExTransparent | WsExLayered | WsExToolwindow;
        else
            style &= ~WsExTransparent;
        SetWindowLong(Handle, GwlExstyle, style);
    }

    private void UpdateLevels()
    {
        for (var i = 0; i < BarCount; i++)
        {
            var target = spectrum[i];
            // 上升快、回落缓，形成鼓点"一拍冲起、缓缓回落"
            levels[i] += (target - levels[i]) * (target > levels[i] ? 0.55f : 0.13f);

            // 峰值保持：抓到高点后缓慢下落
            if (target > peaks[i])
                peaks[i] = target;
            else
                peaks[i] = Math.Max(0f, peaks[i] - 0.005f);
        }
    }

   
    private void RebuildBrushes()
    {
        var raw = GetWallpaperColors();
        var next = new Color[raw.Count];
        for (var i = 0; i < raw.Count; i++) next[i] = Brighten(raw[i]);   // 深色壁纸提亮，避免柱子弹死黑

        if (currentPalette.Length == 0)
        {
            currentPalette = (Color[])next.Clone();
            targetPalette = (Color[])next.Clone();
            ApplyPalette(currentPalette);
        }
        else
        {
            Array.Copy(next, targetPalette, next.Length);
            StartPaletteAnim();
        }
    }

    private void StartPaletteAnim()
    {
        if (paletteAnimTimer == null)
        {
            paletteAnimTimer = new System.Windows.Forms.Timer { Interval = 50 };
            paletteAnimTimer.Tick += (_, _) => StepPaletteAnim();
        }
        if (!paletteAnimTimer.Enabled) paletteAnimTimer.Start();
    }

    // 逐帧把当前色板向目标插值逼近，几乎到位后停止动画
    private void StepPaletteAnim()
    {
        var done = true;
        for (var i = 0; i < currentPalette.Length; i++)
        {
            var c = currentPalette[i];
            var t = targetPalette[i];
            var nr = c.R + (int)Math.Round((t.R - c.R) * 0.22f);
            var ng = c.G + (int)Math.Round((t.G - c.G) * 0.22f);
            var nb = c.B + (int)Math.Round((t.B - c.B) * 0.22f);
            currentPalette[i] = Color.FromArgb(nr, ng, nb);
            if (Math.Abs(t.R - nr) > 1 || Math.Abs(t.G - ng) > 1 || Math.Abs(t.B - nb) > 1) done = false;
        }
        ApplyPalette(currentPalette);
        Invalidate();
        if (done) paletteAnimTimer!.Stop();
    }

    // 用色板重建全部柱子弹渐变：沿频率取色，顶部较实、根部通透
    private void ApplyPalette(Color[] palette)
    {
        for (var i = 0; i < BarCount; i++)
        {
            var f = i / (float)(BarCount - 1);   // 0=低音 … 1=高音
            var c = SamplePalette(palette, f);
            for (var a = 0; a < 9; a++)
            {
                var alpha = 70 + (int)Math.Round(170f * a / 8f);   // alpha 70(很透)…240(较实)
                barBrushes[i, a] = MakeGradient(c, alpha);
            }
        }
    }

    // 亮度下限提升：低于 0.34 的深色按比例提亮，保证柱子弹可见
    private static Color Brighten(Color c)
    {
        var l = (c.R * 0.299f + c.G * 0.587f + c.B * 0.114f) / 255f;
        const float floor = 0.34f;
        if (l >= floor) return c;
        var k = floor / l;   // 恒 >= 1
        return Color.FromArgb(
            (int)Math.Min(255, c.R * k),
            (int)Math.Min(255, c.G * k),
            (int)Math.Min(255, c.B * k));
    }

    // 从桌面中部抓一条水平色带采样成约 16 个色板点，实现实时壁纸取色
    private List<Color> GetWallpaperColors()
    {
        var palette = new List<Color>();
        try
        {
            // 采样一条横跨屏幕中部、高 1px 的色带，避开底部音频条区域
            var bandY = Screen.PrimaryScreen!.Bounds.Height / 2;
            var w = Screen.PrimaryScreen!.Bounds.Width;
            using var band = new Bitmap(w, 1, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(band))
            {
                g.CopyFromScreen(0, bandY, 0, 0, band.Size);
            }

            // 沿色带按步长采样，每步取一段的平均色
            const int steps = 16;
            var step = Math.Max(1, w / steps);
            for (var s = 0; s < steps; s++)
            {
                var start = s * step;
                var end = Math.Min(w, start + step);
                long r = 0, g2 = 0, b = 0;
                var n = 0;
                for (var x = start; x < end; x++)
                {
                    var p = band.GetPixel(x, 0);
                    r += p.R; g2 += p.G; b += p.B; n++;
                }
                if (n > 0)
                    palette.Add(Color.FromArgb((int)(r / n), (int)(g2 / n), (int)(b / n)));
            }
        }
        catch { /* 抓屏失败则走回退 */ }
        if (palette.Count == 0)
        {
            palette.Add(Color.FromArgb(77, 107, 254));   // 回退为主色蓝 #4d6bfe
            palette.Add(Color.FromArgb(77, 107, 254));
        }
        return palette;
    }

    // 在壁纸色板上按位置插值取色（f∈[0,1] 低→高）
    private static Color SamplePalette(IReadOnlyList<Color> p, float f)
    {
        if (p.Count == 1) return p[0];
        var pos = f * (p.Count - 1);
        var idx = Math.Min((int)pos, p.Count - 2);
        var frac = pos - idx;
        var c0 = p[idx];
        var c1 = p[idx + 1];
        return Color.FromArgb(
            (int)(c0.R + (c1.R - c0.R) * frac),
            (int)(c0.G + (c1.G - c0.G) * frac),
            (int)(c0.B + (c1.B - c0.B) * frac));
    }

    // 纵向渐变画笔：顶部 alpha，根部渐淡（约为顶部四成）
    private static LinearGradientBrush MakeGradient(Color c, int alphaTop)
    {
        var top = Color.FromArgb(alphaTop, c);
        var bottom = Color.FromArgb(Math.Max(24, alphaTop * 2 / 5), c);
        return new LinearGradientBrush(new Rectangle(0, 0, 1, 100), top, bottom, 90f);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 不重绘背景，让 Acrylic 毛玻璃透出
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.Clear(Color.Transparent);

        const int gap = 4;   // 柱间距
        var availableWidth = ClientSize.Width;
        var barWidth = Math.Max(2, (availableWidth - gap * (BarCount - 1)) / BarCount);
        var maxHeight = ClientSize.Height - 6;

        using var capBrush = new SolidBrush(Color.FromArgb(180, 255, 255, 255));   // 峰值帽

        for (var i = 0; i < BarCount; i++)
        {
            var x = i * (barWidth + gap);
            // 反转：高音画在左侧，低音画在右侧
            var r = BarCount - 1 - i;
            var height = Math.Max(4, (int)(levels[r] * maxHeight));
            var y = ClientSize.Height - height;   // 底部贴齐成一条线

            var radius = 3;
            var ai = (int)Math.Clamp(levels[r] * 8f, 0f, 8f);   // 越活跃越不透明，随能量脉动
            e.Graphics.FillRoundedTop(barBrushes[r, ai], new Rectangle(x, y, barWidth, height), radius);

            // 峰值保持细线（托盘菜单可关）
            if (showPeaks)
            {
                var peakHeight = (int)(peaks[r] * maxHeight);
                var peakY = ClientSize.Height - peakHeight;
                e.Graphics.FillRectangle(capBrush, x, peakY - 6, barWidth, 3);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            audioCapture.Dispose();
            trayIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}

internal sealed class AudioCapture : IDisposable
{
    private readonly float[] spectrum;
    private readonly object sync = new();
    private WasapiLoopbackCapture? capture;
    private readonly System.Windows.Forms.Timer restartTimer; // 设备切换/停止后去抖自动重启
    private readonly System.Windows.Forms.Timer watchdogTimer; // 看门狗：检测静默停摆
    private MMDeviceEnumerator? deviceEnumerator;             // 通知客户端需保持存活
    private DefaultDeviceClient? deviceClient;                // 监听默认输出设备变化
    private bool disposed;                                    // 已释放则不再重启
    private DateTime lastData = DateTime.MinValue;            // 最近一次数据回调时间
    private readonly Analyzer analyzer;  // 频谱算法封装在 suanfa.cs 的 Analyzer 中

    public AudioCapture(float[] spectrum)
    {
        this.spectrum = spectrum;
        analyzer = new Analyzer(spectrum, spectrum.Length);

        restartTimer = new System.Windows.Forms.Timer { Interval = 500 };
        restartTimer.Tick += (_, _) => RecreateCapture();

        // 捕获活动但连续 5s 无数据回调，判定为设备切换导致的停摆，主动重建
        watchdogTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        watchdogTimer.Tick += (_, _) =>
        {
            if (disposed || capture is null)
                return;
            if (lastData != DateTime.MinValue && (DateTime.UtcNow - lastData).TotalSeconds > 5)
            {
                WriteDiag("看门狗：长时间无数据回调，疑似设备切换导致停摆，自动重启");
                RecreateCapture();
            }
        };
    }

    public void Start() => RecreateCapture();

    // 重建并启动 loopback 捕获：无论旧实例清理是否报错，都确保创建新的可用捕获
    private void RecreateCapture()
    {
        restartTimer.Stop();
        if (disposed)
            return;

        // 先停旧实例，避免事件重复绑定/句柄泄漏；清理失败不阻断重建
        var old = capture;
        capture = null;
        if (old != null)
        {
            try { old.StopRecording(); } catch { }
            try { old.DataAvailable -= OnDataAvailable; } catch { }
            try { old.RecordingStopped -= OnRecordingStopped; } catch { }
            try { old.Dispose(); } catch { }
        }

        WasapiLoopbackCapture c;
        try
        {
            c = new WasapiLoopbackCapture();
        }
        catch (Exception ex)
        {
            WriteDiag($"捕获创建失败，稍后重试：{ex.Message}");
            ScheduleRestart(1500);
            return;
        }

        // 绑定新实例，失败则清理并退避重试
        try
        {
            WriteDiag($"捕获格式：{c.WaveFormat.Encoding} {c.WaveFormat.SampleRate}Hz {c.WaveFormat.Channels}ch {c.WaveFormat.BitsPerSample}bit");
            c.DataAvailable += OnDataAvailable;
            c.RecordingStopped += OnRecordingStopped;
            c.StartRecording();
            capture = c;
            watchdogTimer.Start();

            // 主动监听默认输出设备变化，热点插拔的主要处理路径
            EnsureDefaultDeviceWatch();
        }
        catch (Exception ex)
        {
            WriteDiag($"捕获启动失败，稍后重试：{ex.Message}");
            try { c.Dispose(); } catch { }
            ScheduleRestart(1500);
        }
    }

    // 注册 IMMNotificationClient 监听默认输出设备切换（幂等）
    private void EnsureDefaultDeviceWatch()
    {
        if (deviceClient is not null)
            return;
        try
        {
            deviceEnumerator = new MMDeviceEnumerator();
            deviceClient = new DefaultDeviceClient(() => ScheduleRestart(300));
            deviceEnumerator.RegisterEndpointNotificationCallback(deviceClient);
        }
        catch (Exception ex)
        {
            WriteDiag($"默认设备监听注册失败：{ex.Message}");
            deviceClient = null;
            deviceEnumerator?.Dispose();
            deviceEnumerator = null;
        }
    }

    // 设备切换/异常导致捕获中断时，去抖后自动重启
    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (disposed)
            return;
        WriteDiag("捕获已停止（设备切换/异常），稍后自动重启");
        ScheduleRestart(500);
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
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "audiobar_rms.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {line}\n");
        }
        catch { }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lastData = DateTime.UtcNow;   // 有数据回调即认为捕获存活

        // 用触发回调的实例自身格式，规避重建瞬间共享字段的竞态
        var waveFormat = (sender as WasapiLoopbackCapture)?.WaveFormat ?? capture?.WaveFormat;
        if (waveFormat is null || e.BytesRecorded == 0)
            return;

        var channels = Math.Max(1, waveFormat.Channels);
        var bytesPerSample = waveFormat.BitsPerSample / 8;
        var frameBytes = channels * bytesPerSample;
        if (frameBytes == 0)
            return;

        lock (sync)
        {
            for (var offset = 0; offset + frameBytes <= e.BytesRecorded; offset += frameBytes)
            {
                var amplitude = 0f;
                for (var channel = 0; channel < channels; channel++)
                {
                    var sampleOffset = offset + channel * bytesPerSample;
                    amplitude += Analyzer.ReadSample(e.Buffer, sampleOffset, bytesPerSample, waveFormat.Encoding);
                }
                analyzer.Feed(amplitude / channels);
            }
        }
    }

    public void Dispose()
    {
        disposed = true;
        restartTimer.Stop();
        restartTimer.Dispose();
        watchdogTimer.Stop();
        watchdogTimer.Dispose();
        try { deviceEnumerator?.UnregisterEndpointNotificationCallback(deviceClient); } catch { }
        deviceEnumerator?.Dispose();
        deviceEnumerator = null;
        deviceClient = null;
        var c = capture;
        capture = null;
        if (c != null)
        {
            c.DataAvailable -= OnDataAvailable;
            c.RecordingStopped -= OnRecordingStopped;
            try { c.StopRecording(); } catch { }
            c.Dispose();
        }
    }
}

// 监听默认输出设备变化的 IMMNotificationClient 实现：设备插拔/切换时通知重建捕获
internal sealed class DefaultDeviceClient : NAudio.CoreAudioApi.Interfaces.IMMNotificationClient
{
    private readonly Action onDeviceChanged;
    public DefaultDeviceClient(Action onDeviceChanged) => this.onDeviceChanged = onDeviceChanged;

    public void OnDefaultDeviceChanged(NAudio.CoreAudioApi.DataFlow flow, NAudio.CoreAudioApi.Role role, string deviceId)
    {
        // 只关心渲染（输出）流的默认设备变化
        if (flow == NAudio.CoreAudioApi.DataFlow.Render)
            onDeviceChanged();
    }

    public void OnDeviceStateChanged(string deviceId, NAudio.CoreAudioApi.DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string pwstrDeviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, NAudio.CoreAudioApi.PropertyKey key) { }
}

internal static class GraphicsExtensions
{
    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle bounds, int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.DrawPath(pen, path);
    }

    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    // 只圆顶部两个角，底部保持直角
    public static void FillRoundedTop(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);           // 左上圆角
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90); // 右上圆角
        path.AddLine(bounds.Right, bounds.Y + radius, bounds.Right, bounds.Bottom);
        path.AddLine(bounds.Right, bounds.Bottom, bounds.Left, bounds.Bottom);  // 底部直角
        path.AddLine(bounds.Left, bounds.Bottom, bounds.Left, bounds.Y + radius);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
