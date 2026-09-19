using System;
using System.Threading;
using System.Windows.Forms;

namespace AudioBar;

internal static class Program
{
	private static Mutex? s_mutex;

	[STAThread]
	private static void Main()
	{
		s_mutex = new Mutex(initiallyOwned: true, "AudioBar_SingleInstance", out var createdNew);
		if (!createdNew)
		{
			MessageBox.Show("程序已经在运行中，请勿重复打开。", "AudioBar", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
			return;
		}
		ApplicationConfiguration.Initialize();
		Application.Run(new VisualizerForm());
	}
}
