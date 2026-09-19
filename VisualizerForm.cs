using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AudioBar;

internal sealed class VisualizerForm : Form
{
	private struct AccentPolicy
	{
		public int AccentState;

		public int AccentFlags;

		public int GradientColor;

		public int AnimationId;
	}

	private struct WindowCompositionAttributeData
	{
		public int Attribute;

		public nint Data;

		public int SizeOfData;
	}

	private const int BarCount = 64;

	private const int GwlExstyle = -20;

	private const int WsExTransparent = 32;

	private const int WsExLayered = 524288;

	private const int WsExToolwindow = 128;

	private readonly Timer animationTimer = new Timer
	{
		Interval = 16
	};

	private readonly float[] levels = new float[64];

	private readonly float[] peaks = new float[64];

	private readonly float[] spectrum = new float[64];

	private readonly NotifyIcon trayIcon;

	private readonly AudioCapture audioCapture;

	private readonly LinearGradientBrush[,] barBrushes;

	private Color[] currentPalette = Array.Empty<Color>();

	private Color[] targetPalette = Array.Empty<Color>();

	private Timer? paletteAnimTimer;

	private bool showPeaks;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool ShowPeaks { get => showPeaks; set { showPeaks = value; Invalidate(); } }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool IsPaused { get => isPaused; set => isPaused = value; }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool MousePassthrough { get => mousePassthrough; set { mousePassthrough = value; ApplyWindowStyle(); } }

	private bool isPaused;
    private float[] hoverFade = new float[BarCount];  // 每根柱独立 hover 收起量
    private readonly System.Windows.Forms.Timer hoverTimer = new() { Interval = 50 };
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

	private bool mousePassthrough = true;

        private readonly Color[,][] basePairs = new Color[64, 9][];

        private float highlightBlend;

        private float appliedHighlight = -1f;

        private long lastApplyMs;

	public VisualizerForm()
	{
		base.FormBorderStyle = FormBorderStyle.None;
		base.StartPosition = FormStartPosition.Manual;
		base.ShowInTaskbar = false;
		base.TopMost = true;
		BackColor = Color.Black;
		base.TransparencyKey = Color.Black;
		DoubleBuffered = true;
		Rectangle rectangle = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
		base.Bounds = new Rectangle(rectangle.Left, rectangle.Bottom - 84, rectangle.Width, 84);
		barBrushes = new LinearGradientBrush[64, 9];
		RebuildBrushes();
		Timer timer = new Timer();
		timer.Interval = 3000;
		timer.Tick += delegate
		{
			RebuildBrushes();
		};
		timer.Start();
		trayIcon = new NotifyIcon
		{
			Icon = SystemIcons.Application,
			Text = "音频条",
			Visible = true,
			ContextMenuStrip = BuildMenu()
		};
            trayIcon.DoubleClick += delegate { OpenSettings(); };
		audioCapture = new AudioCapture(spectrum);
            audioCapture.Apply(AudioCapture.LoadSettings());
            audioCapture.Start();
		animationTimer.Tick += delegate
		{
			if (!isPaused)
			{
				UpdateLevels();
				Invalidate();
			}
		};
		animationTimer.Start();
        hoverTimer.Tick += delegate { UpdateHoverFade(); };
        hoverTimer.Start();
		base.FormClosed += delegate
		{
			animationTimer.Stop();
                    audioCapture.Save();
			audioCapture.Dispose();
			trayIcon.Visible = false;
			trayIcon.Dispose();
		};
	}

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("打开主面板");
        open.Click += delegate { OpenSettings(); };
        var quit = new ToolStripMenuItem("退出程序");
        quit.Click += delegate { Close(); };
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quit);
        return menu;
    }

    private SettingsForm? _settingsForm;
    public void OpenSettings()
    {
        if (_settingsForm == null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(this, audioCapture);
            _settingsForm.FormClosed += (_, _) => _settingsForm = null;
            _settingsForm.Show(this);
        }
        else
        {
            _settingsForm.Activate();
        }
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

	private void EnableAcrylic()
	{
		AccentPolicy accentPolicy = default(AccentPolicy);
		accentPolicy.AccentState = 4;
		accentPolicy.AccentFlags = 0;
		accentPolicy.GradientColor = 1040187392;
		accentPolicy.AnimationId = 0;
		AccentPolicy structure = accentPolicy;
		WindowCompositionAttributeData windowCompositionAttributeData = default(WindowCompositionAttributeData);
		windowCompositionAttributeData.Attribute = 19;
		windowCompositionAttributeData.SizeOfData = Marshal.SizeOf(structure);
		windowCompositionAttributeData.Data = Marshal.AllocHGlobal(Marshal.SizeOf(structure));
		WindowCompositionAttributeData data = windowCompositionAttributeData;
		Marshal.StructureToPtr(structure, data.Data, fDeleteOld: false);
		SetWindowCompositionAttribute(base.Handle, ref data);
		Marshal.FreeHGlobal(data.Data);
	}

	[DllImport("user32.dll")]
	private static extern int SetWindowCompositionAttribute(nint hwnd, ref WindowCompositionAttributeData data);

	private void ApplyWindowStyle()
	{
		int windowLong = GetWindowLong(base.Handle, -20);
		SetWindowLong(dwNewLong: (!mousePassthrough) ? (windowLong & -33) : (windowLong | 0x800A0), hWnd: base.Handle, nIndex: -20);
	}

	private void UpdateLevels()
	{
		for (int i = 0; i < 64; i++)
		{
			float num = spectrum[i];
			levels[i] += (num - levels[i]) * ((num > levels[i]) ? 0.55f : 0.13f);
			if (num > peaks[i])
			{
				peaks[i] = num;
			}
			else
			{
				peaks[i] = Math.Max(0f, peaks[i] - 0.005f);
			}
		}
	}

	private void RebuildBrushes()
	{
		List<Color> wallpaperColors = GetWallpaperColors();
		Color[] array = new Color[wallpaperColors.Count];
		for (int i = 0; i < wallpaperColors.Count; i++)
		{
			array[i] = Brighten(wallpaperColors[i]);
		}
		if (currentPalette.Length == 0)
		{
			currentPalette = (Color[])array.Clone();
			targetPalette = (Color[])array.Clone();
			ApplyPalette(currentPalette);
		}
		else
		{
			Array.Copy(array, targetPalette, array.Length);
			StartPaletteAnim();
		}
	}

	private void StartPaletteAnim()
	{
		if (paletteAnimTimer == null)
		{
			paletteAnimTimer = new Timer
			{
				Interval = 50
			};
			paletteAnimTimer.Tick += delegate
			{
				StepPaletteAnim();
			};
		}
		if (!paletteAnimTimer.Enabled)
		{
			paletteAnimTimer.Start();
		}
	}

	private void StepPaletteAnim()
	{
		bool flag = true;
		for (int i = 0; i < currentPalette.Length; i++)
		{
			Color color = currentPalette[i];
			Color color2 = targetPalette[i];
			int num = color.R + (int)Math.Round((float)(color2.R - color.R) * 0.22f);
			int num2 = color.G + (int)Math.Round((float)(color2.G - color.G) * 0.22f);
			int num3 = color.B + (int)Math.Round((float)(color2.B - color.B) * 0.22f);
			currentPalette[i] = Color.FromArgb(num, num2, num3);
			if (Math.Abs(color2.R - num) > 1 || Math.Abs(color2.G - num2) > 1 || Math.Abs(color2.B - num3) > 1)
			{
				flag = false;
			}
		}
		ApplyPalette(currentPalette);
		Invalidate();
		if (flag)
		{
			paletteAnimTimer.Stop();
		}
	}

	private void ApplyPalette(Color[] palette)
	{
		for (int i = 0; i < 64; i++)
		{
			float f = (float)i / 63f;
			Color c = SamplePalette(palette, f);
			for (int j = 0; j < 9; j++)
			{
				int alphaTop = 70 + (int)Math.Round(170f * (float)j / 8f);
				Color g0 = Color.FromArgb(alphaTop, c);
					Color g1 = Color.FromArgb(Math.Max(24, alphaTop * 2 / 5), c);
					basePairs[i, j] = new Color[] { g0, g1 };
					Color w0 = LerpWhite(g0, highlightBlend);
					Color w1 = LerpWhite(g1, highlightBlend);
					barBrushes[i, j] = new LinearGradientBrush(new Rectangle(0, 0, 1, 100), w0, w1, 90f);
			}
		}
	}

	private static Color Brighten(Color c)
	{
		float num = ((float)(int)c.R * 0.299f + (float)(int)c.G * 0.587f + (float)(int)c.B * 0.114f) / 255f;
		if (num >= 0.34f)
		{
			return c;
		}
		float num2 = 0.34f / num;
		return Color.FromArgb((int)Math.Min(255f, (float)(int)c.R * num2), (int)Math.Min(255f, (float)(int)c.G * num2), (int)Math.Min(255f, (float)(int)c.B * num2));
	}

	private List<Color> GetWallpaperColors()
	{
		List<Color> list = new List<Color>();
		try
		{
			int sourceY = Screen.PrimaryScreen.Bounds.Height / 2;
			int width = Screen.PrimaryScreen.Bounds.Width;
			using Bitmap bitmap = new Bitmap(width, 1, PixelFormat.Format32bppArgb);
			using (Graphics graphics = Graphics.FromImage(bitmap))
			{
				graphics.CopyFromScreen(0, sourceY, 0, 0, bitmap.Size);
			}
			int num = Math.Max(1, width / 16);
			for (int i = 0; i < 16; i++)
			{
				int num2 = i * num;
				int num3 = Math.Min(width, num2 + num);
				long num4 = 0L;
				long num5 = 0L;
				long num6 = 0L;
				int num7 = 0;
				for (int j = num2; j < num3; j++)
				{
					Color pixel = bitmap.GetPixel(j, 0);
					num4 += pixel.R;
					num5 += pixel.G;
					num6 += pixel.B;
					num7++;
				}
				if (num7 > 0)
				{
					list.Add(Color.FromArgb((int)(num4 / num7), (int)(num5 / num7), (int)(num6 / num7)));
				}
			}
		}
		catch
		{
		}
		if (list.Count == 0)
		{
			list.Add(Color.FromArgb(77, 107, 254));
			list.Add(Color.FromArgb(77, 107, 254));
		}
		return list;
	}

	private static Color SamplePalette(IReadOnlyList<Color> p, float f)
	{
		if (p.Count == 1)
		{
			return p[0];
		}
		float num = f * (float)(p.Count - 1);
		int num2 = Math.Min((int)num, p.Count - 2);
		float num3 = num - (float)num2;
		Color color = p[num2];
		Color color2 = p[num2 + 1];
		return Color.FromArgb((int)((float)(int)color.R + (float)(color2.R - color.R) * num3), (int)((float)(int)color.G + (float)(color2.G - color.G) * num3), (int)((float)(int)color.B + (float)(color2.B - color.B) * num3));
	}

	private static LinearGradientBrush MakeGradient(Color c, int alphaTop)
	{
		Color color = Color.FromArgb(alphaTop, c);
		Color color2 = Color.FromArgb(Math.Max(24, alphaTop * 2 / 5), c);
		return new LinearGradientBrush(new Rectangle(0, 0, 1, 100), color, color2, 90f);
	}

	private void ApplyHighlight(float h)
		{
			for (int i = 0; i < 64; i++)
			{
				for (int j = 0; j < 9; j++)
				{
					Color[] b = basePairs[i, j];
					if (b == null) continue;
					barBrushes[i, j].LinearColors = new Color[] { LerpWhite(b[0], h), LerpWhite(b[1], h) };
				}
			}
		}

		private static Color LerpWhite(Color c, float h)
		{
			if (h <= 0f) return c;
                        float s = Math.Clamp(h, 0f, 1f);  // h 已是 AudioCapture 计算好的白化比例
			byte a = (byte)(c.A + (255 - c.A) * Math.Min(1f, h * 0.8f));
			return Color.FromArgb(a, (int)(c.R + (255 - c.R) * s), (int)(c.G + (255 - c.G) * s), (int)(c.B + (255 - c.B) * s));
		}

		    private void UpdateHoverFade()
    {
        if (!GetCursorPos(out var pt)) return;
        var screen = Screen.PrimaryScreen?.WorkingArea ?? Rectangle.Empty;
        var barArea = new Rectangle(screen.Left, screen.Bottom - Height, screen.Width, Height);

        // 鼠标是否在音频条 Y 范围内（贴底区域）
        if (pt.Y < barArea.Top - 4 || pt.Y > barArea.Bottom)
        {
            // 不在区域内，全部往 0 回
            for (var i = 0; i < BarCount; i++)
                hoverFade[i] += (0f - hoverFade[i]) * 0.1f;
            return;
        }

        // 算出鼠标 X 落在哪根柱
        var relX = pt.X - barArea.Left;
        var barW = barArea.Width / (float)BarCount;
        var mouseBar = relX / barW;  // 可能是小数，比如 32.7
        var mouseBarIdx = BarCount - 1 - mouseBar;  // 对齐 OnPaint 里 num3=63-i 的索引反转

        // 高斯权重：距离越近，target 越大（缩得越多）
        // sigma 控制影响半径（约 ±6 根柱）
        const float sigma = 3.5f;
        const float maxShrink = 0.85f;  // 最靠近的那根缩到 15%

        for (var i = 0; i < BarCount; i++)
        {
            var dist = Math.Abs(i - mouseBarIdx);
            if (dist > sigma * 3f) { hoverFade[i] += (0f - hoverFade[i]) * 0.12f; continue; }
            var weight = (float)Math.Exp(-(dist * dist) / (2f * sigma * sigma));
            var target = weight * maxShrink;
            // 靠近收得快，远的回得也快
            var close = weight > 0.3f;
            hoverFade[i] += (target - hoverFade[i]) * (close ? 0.25f : 0.1f);
            if (hoverFade[i] < 0.003f) hoverFade[i] = 0f;
        }
    }
protected override void OnPaintBackground(PaintEventArgs e)
	{
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		base.OnPaint(e);
		e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
		e.Graphics.Clear(Color.Transparent);
				float targetH = audioCapture.Highlight;
				highlightBlend += (targetH - highlightBlend) * 0.7f;
				long nowMs = Environment.TickCount64;
				if (Math.Abs(highlightBlend - appliedHighlight) > 0.02f && nowMs - lastApplyMs > 70)
				{
					appliedHighlight = highlightBlend;
					lastApplyMs = nowMs;
					ApplyHighlight(highlightBlend);
				}
		int width = base.ClientSize.Width;
		int num = Math.Max(2, (width - 252) / 64);
		int baseHeight = base.ClientSize.Height - 6;
		using SolidBrush brush = new SolidBrush(Color.FromArgb(180, 255, 255, 255));
		for (int i = 0; i < 64; i++)
		{
			int x = i * (num + 4);
			int num3 = 63 - i;
			int perBarMax = (int)(baseHeight * (1f - hoverFade[num3]));
			int num4 = Math.Max(4, (int)(levels[num3] * perBarMax));
			int y = base.ClientSize.Height - num4;
			int radius = 3;
			int num5 = (int)Math.Clamp(levels[num3] * 8f, 0f, 8f);
			e.Graphics.FillRoundedTop(barBrushes[num3, num5], new Rectangle(x, y, num, num4), radius);
			if (showPeaks)
			{
				int num6 = (int)(peaks[num3] * perBarMax);
				int num7 = base.ClientSize.Height - num6;
				e.Graphics.FillRectangle(brush, x, num7 - 6, num, 3);
			}
		}
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
                    audioCapture.Save(); // Dispose 路径兜底保存
                    audioCapture.Dispose();
			trayIcon.Dispose();
		}
		base.Dispose(disposing);
	}

	[DllImport("user32.dll")]
	private static extern int GetWindowLong(nint hWnd, int nIndex);

	[DllImport("user32.dll")]
	private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
}



