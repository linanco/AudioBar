using System;
using System.Drawing;
using System.Windows.Forms;

namespace AudioBar;

internal sealed class SettingsForm : Form
{
    private readonly VisualizerForm _owner;
    private readonly AudioCapture _cap;

    public SettingsForm(VisualizerForm owner, AudioCapture cap)
    {
        _owner = owner;
        _cap = cap;

        Text = "音频条 设置";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(420, 520);
        MinimumSize = new Size(380, 460);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var tab = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };

        tab.TabPages.Add(BuildAudioTab());
        tab.TabPages.Add(BuildHighlightTab());
        tab.TabPages.Add(BuildLookTab());

        Controls.Add(tab);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
        var reset = new Button { Text = "恢复默认", Location = new Point(8, 8), Size = new Size(90, 28) };
        reset.Click += (_, _) => ResetDefaults();
        var save = new Button { Text = "保存并关闭", Location = new Point(Size.Width - 120, 8), Size = new Size(100, 28), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        save.Click += (_, _) => { _cap.Save(); Close(); };
this.FormClosing += (_, _) => { _cap.Save(); }; // 主面板关闭自动保存
        bottom.Controls.Add(reset);
        bottom.Controls.Add(save);
        Controls.Add(bottom);
    }

    private TabPage BuildAudioTab()
    {
        var p = new TabPage("音频");
        var presetLbl = new Label { Text = "预设", Location = new Point(12, 12), AutoSize = true };
        var presetCb = new ComboBox { Location = new Point(70, 8), Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
        presetCb.Items.AddRange(new object[] { "默认平衡", "重低音 PunchyBass", "电子 EDM", "人声清晰 VocalClear", "高音炫白 Sparkle" });
        presetCb.SelectedIndex = 0;
        presetCb.SelectedIndexChanged += (_, _) => {
            AudioCapture.Preset ps = presetCb.SelectedIndex switch { 1 => AudioCapture.Preset.PunchyBass, 2 => AudioCapture.Preset.EDM, 3 => AudioCapture.Preset.VocalClear, 4 => AudioCapture.Preset.Sparkle, _ => AudioCapture.Preset.Default };
            _cap.ApplyPreset(ps);
            // 刷新所有 TrackBar/TextBox
            foreach (Control c in p.Controls) if (c is Panel pa && pa.Tag != null && pa.Tag is Action<float> act && pa.Controls[1] is TrackBar tb) { tb.Value = (int)(act == null ? 0 : 0); }
            // 简化：直接重建控件——用递归找 TrackBar 刷新
            foreach (Control c in p.Controls) RefreshSliders(c, _cap);
        };
        p.Controls.Add(presetLbl);
        p.Controls.Add(presetCb);
        var y = 44;
        p.Controls.Add(MkSlider("低音增益", 0f, 1.5f, _cap.BassBoost, v => _cap.BassBoost = v, ref y));
        p.Controls.Add(MkSlider("人声增益", 0f, 1.5f, _cap.VocalBoost, v => _cap.VocalBoost = v, ref y));
        p.Controls.Add(MkSlider("高音增益", 0f, 1.5f, _cap.TrebleBoost, v => _cap.TrebleBoost = v, ref y));
        p.Controls.Add(MkSlider("鼓点灵敏度", 0f, 2.0f, _cap.KickSensitivity, v => _cap.KickSensitivity = v, ref y));
        p.Controls.Add(MkSlider("软饱和系数", 0.5f, 2.0f, _cap.SoftSat, v => _cap.SoftSat = v, ref y));
        return p;
    }

    private TabPage BuildHighlightTab()
    {
        var p = new TabPage("高音白化");
        var y = 44;
        p.Controls.Add(MkSlider("触发阈值", 0f, 0.3f, _cap.HighlightThreshold, v => _cap.HighlightThreshold = v, ref y, 2));
        p.Controls.Add(MkSlider("白化强度", 0f, 6.0f, _cap.HighlightGain, v => _cap.HighlightGain = v, ref y, 2));
        p.Controls.Add(MkSlider("总强度", 0f, 1.5f, _cap.HighlightStrength, v => _cap.HighlightStrength = v, ref y));
        return p;
    }

    private TabPage BuildLookTab()
    {
        var p = new TabPage("外观");
        var y = 44;
        p.Controls.Add(MkSlider("柱高缩放", 0.5f, 1.5f, _cap.HeightScale, v => _cap.HeightScale = v, ref y));

        var showPeaks = new CheckBox { Text = "显示峰值方块", Checked = _owner.ShowPeaks, Location = new Point(12, y + 6), AutoSize = true };
        showPeaks.CheckedChanged += (_, _) => { _owner.ShowPeaks = showPeaks.Checked; };
        p.Controls.Add(showPeaks); y += 30;

        var passthrough = new CheckBox { Text = "鼠标穿透", Checked = _owner.MousePassthrough, Location = new Point(12, y + 6), AutoSize = true };
        passthrough.CheckedChanged += (_, _) => { _owner.MousePassthrough = passthrough.Checked; };
        p.Controls.Add(passthrough); y += 30;

        var paused = new CheckBox { Text = "暂停动画", Checked = _owner.IsPaused, Location = new Point(12, y + 6), AutoSize = true };
        paused.CheckedChanged += (_, _) => { _owner.IsPaused = paused.Checked; };
        p.Controls.Add(paused);

        return p;
    }

    private static Panel MkSlider(string label, float min, float max, float value, Action<float> onChanged, ref int y, int decimals = 2)
    {
        var panel = new Panel { Location = new Point(12, y), Size = new Size(360, 44) };
        var lbl = new Label { Text = label, Location = new Point(0, 4), AutoSize = true };
        var tb = new TextBox { Width = 60, Location = new Point(300, 0), TextAlign = HorizontalAlignment.Right, ReadOnly = false };
        float scale = decimals == 2 ? 100f : 10f;
        var track = new TrackBar
        {
            Location = new Point(0, 22),
            Width = 360,
            Minimum = (int)(min * scale),
            Maximum = (int)(max * scale),
            Value = (int)(value * scale),
            TickFrequency = decimals == 2 ? 10 : 2
        };
        tb.Text = ((float)track.Value / scale).ToString("F" + decimals);
        track.ValueChanged += (_, _) =>
        {
            var v = (float)track.Value / scale;
            onChanged(v);
            tb.Text = v.ToString("F" + decimals);
        };
        tb.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter && float.TryParse(tb.Text, out var v))
            {
                v = Math.Clamp(v, min, max);
                track.Value = (int)(v * (int)scale);
            }
        };
        panel.Controls.Add(lbl);
        panel.Controls.Add(tb);
        panel.Controls.Add(track);
        y += 48;
        return panel;
    }

    private static void RefreshSliders(Control parent, AudioCapture cap)
    {
        foreach (Control c in parent.Controls)
        {
            if (c is TrackBar tb && tb.Tag is Tuple<string, int> tag)
            {
                float v = tag.Item1 switch
                {
                    "Bass" => cap.BassBoost, "Vocal" => cap.VocalBoost, "Treble" => cap.TrebleBoost,
                    "Kick" => cap.KickSensitivity, "Sat" => cap.SoftSat, "Scale" => cap.HeightScale,
                    "HiTh" => cap.HighlightThreshold, "HiGain" => cap.HighlightGain, "HiStr" => cap.HighlightStrength, _ => 0f
                };
                tb.ValueChanged -= null; tb.ValueChanged += null;
                tb.Value = (int)(v * tag.Item2);
            }
            RefreshSliders(c, cap);
        }
    }

    private void ResetDefaults()
    {
        _cap.Apply(new AudioSettings());
        _owner.ShowPeaks = false;
        _owner.MousePassthrough = true;
        _owner.IsPaused = false;
        Close();
    }
}

