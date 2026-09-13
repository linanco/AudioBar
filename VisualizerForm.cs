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

	private bool isPaused;

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
		trayIcon.DoubleClick += delegate
		{
			TogglePassthrough();
		};
		audioCapture = new AudioCapture(spectrum);
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
		base.FormClosed += delegate
		{
			animationTimer.Stop();
			audioCapture.Dispose();
			trayIcon.Visible = false;
			trayIcon.Dispose();
		};
	}

	private ContextMenuStrip BuildMenu()
	{
		ContextMenuStrip contextMenuStrip = new ContextMenuStrip();
		ToolStripMenuItem pauseItem = new ToolStripMenuItem("暂停动画");
		pauseItem.Click += delegate
		{
			isPaused = !isPaused;
			pauseItem.Text = (isPaused ? "继续动画" : "暂停动画");
		};
		ToolStripMenuItem passthroughItem = new ToolStripMenuItem("鼠标穿透")
		{
			Checked = true,
			CheckOnClick = true
		};
		passthroughItem.Click += delegate
		{
			mousePassthrough = passthroughItem.Checked;
			ApplyWindowStyle();
		};
		ToolStripMenuItem peakItem = new ToolStripMenuItem("显示峰值方块")
		{
			Checked = showPeaks,
			CheckOnClick = true
		};
		peakItem.Click += delegate
		{
			showPeaks = peakItem.Checked;
			Invalidate();
		};
		ToolStripMenuItem toolStripMenuItem = new ToolStripMenuItem("退出程序");
		toolStripMenuItem.Click += delegate
		{
			Close();
		};
		contextMenuStrip.Items.Add(pauseItem);
		contextMenuStrip.Items.Add(passthroughItem);
		contextMenuStrip.Items.Add(peakItem);
		contextMenuStrip.Items.Add(new ToolStripSeparator());
		contextMenuStrip.Items.Add(toolStripMenuItem);
		return contextMenuStrip;
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
			float s = Math.Min(1f, h * 2.2f);
			byte a = (byte)(c.A + (255 - c.A) * Math.Min(1f, h * 0.8f));
			return Color.FromArgb(a, (int)(c.R + (255 - c.R) * s), (int)(c.G + (255 - c.G) * s), (int)(c.B + (255 - c.B) * s));
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
		int num2 = base.ClientSize.Height - 6;
		using SolidBrush brush = new SolidBrush(Color.FromArgb(180, 255, 255, 255));
		for (int i = 0; i < 64; i++)
		{
			int x = i * (num + 4);
			int num3 = 63 - i;
			int num4 = Math.Max(4, (int)(levels[num3] * (float)num2));
			int y = base.ClientSize.Height - num4;
			int radius = 3;
			int num5 = (int)Math.Clamp(levels[num3] * 8f, 0f, 8f);
			e.Graphics.FillRoundedTop(barBrushes[num3, num5], new Rectangle(x, y, num, num4), radius);
			if (showPeaks)
			{
				int num6 = (int)(peaks[num3] * (float)num2);
				int num7 = base.ClientSize.Height - num6;
				e.Graphics.FillRectangle(brush, x, num7 - 6, num, 3);
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
	private static extern int GetWindowLong(nint hWnd, int nIndex);

	[DllImport("user32.dll")]
	private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
}



