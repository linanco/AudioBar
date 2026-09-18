namespace AudioBar;

public class AudioSettings
{
    public float BassBoost { get; set; } = 0.30f;
    public float VocalBoost { get; set; } = 0.85f;
    public float TrebleBoost { get; set; } = 0.25f;
    public float KickSensitivity { get; set; } = 1.0f;
    public float HighlightThreshold { get; set; } = 0.05f;
    public float HighlightGain { get; set; } = 2.4f;
    public float HighlightStrength { get; set; } = 1.0f;
    public float SoftSat { get; set; } = 1.05f;
    public float HeightScale { get; set; } = 1.0f;

    public AudioSettings() { }
    public AudioSettings(float bb, float vb, float tb, float ks, float ht, float hg, float hs, float ss, float hsc)
    { BassBoost=bb; VocalBoost=vb; TrebleBoost=tb; KickSensitivity=ks; HighlightThreshold=ht; HighlightGain=hg; HighlightStrength=hs; SoftSat=ss; HeightScale=hsc; }
}
