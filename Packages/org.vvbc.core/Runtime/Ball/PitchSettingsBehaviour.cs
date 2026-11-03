using Tsukimi;
using UnityEngine;
using UnityEngine.UI;

// The UI only changes next-cycle settings on the local pitcher.
public partial class PitchSettingsBehaviour : TsukimiBehaviour
{
    public BattingPitcherBehaviour pitcher;
    public Slider speedSlider;
    public Slider spinSlider;
    public Slider resetSlider;
    public Text speedText;
    public Text spinText;
    public Text resetText;
    public Image[] courseIndicators = new Image[0];
    public Image[] pitchIndicators = new Image[0];
    public float minSpeedKmh = 60f;
    public float maxSpeedKmh = 160f;
    public float minSpinRpm = 0f;
    public float maxSpinRpm = 4000f;
    public float minResetSeconds = 3f;
    public float maxResetSeconds = 15f;
    public Color selectedColor = new Color(0.18f, 0.65f, 0.95f, 1f);
    public Color unselectedColor = new Color(0.19f, 0.22f, 0.27f, 1f);
    private bool initializing;

    private void Start()
    {
        if (pitcher == null) return;
        initializing = true;
        if (speedSlider != null) { speedSlider.minValue = minSpeedKmh; speedSlider.maxValue = maxSpeedKmh; speedSlider.value = pitcher.speedKmh; }
        if (spinSlider != null) { spinSlider.minValue = minSpinRpm; spinSlider.maxValue = maxSpinRpm; spinSlider.value = pitcher.spinRpm; }
        if (resetSlider != null) { resetSlider.minValue = minResetSeconds; resetSlider.maxValue = maxResetSeconds; resetSlider.value = pitcher.resetSeconds; }
        initializing = false;
        Refresh();
    }

    public void _SpeedChanged() { if (!initializing && pitcher != null && speedSlider != null) { pitcher.speedKmh = speedSlider.value; Refresh(); } }
    public void _SpinChanged() { if (!initializing && pitcher != null && spinSlider != null) { pitcher.spinRpm = spinSlider.value; Refresh(); } }
    public void _ResetChanged() { if (!initializing && pitcher != null && resetSlider != null) { pitcher.resetSeconds = resetSlider.value; Refresh(); } }

    private void ToggleCourse(int index)
    {
        if (pitcher == null) return;
        pitcher.courseMask = pitcher.courseMask ^ (1 << index);
        Refresh();
    }
    private void SelectPitch(int index) { if (pitcher != null) { pitcher.pitchType = index; Refresh(); } }
    public void _Course0() { ToggleCourse(0); } public void _Course1() { ToggleCourse(1); } public void _Course2() { ToggleCourse(2); }
    public void _Course3() { ToggleCourse(3); } public void _Course4() { ToggleCourse(4); } public void _Course5() { ToggleCourse(5); }
    public void _Course6() { ToggleCourse(6); } public void _Course7() { ToggleCourse(7); } public void _Course8() { ToggleCourse(8); }
    public void _Pitch0() { SelectPitch(0); } public void _Pitch1() { SelectPitch(1); } public void _Pitch2() { SelectPitch(2); }
    public void _Pitch3() { SelectPitch(3); } public void _Pitch4() { SelectPitch(4); } public void _Pitch5() { SelectPitch(5); }
    public void _Pitch6() { SelectPitch(6); }

    public void Refresh()
    {
        if (pitcher == null) return;
        if (speedText != null) speedText.text = Mathf.RoundToInt(pitcher.speedKmh) + " km/h";
        if (spinText != null) spinText.text = Mathf.RoundToInt(pitcher.spinRpm) + " rpm";
        if (resetText != null) resetText.text = pitcher.resetSeconds.ToString("F1") + " s";
        for (int i = 0; i < courseIndicators.Length && i < 9; i++)
            if (courseIndicators[i] != null) courseIndicators[i].color = (pitcher.courseMask & (1 << i)) != 0 ? selectedColor : unselectedColor;
        for (int i = 0; i < pitchIndicators.Length && i < 7; i++)
            if (pitchIndicators[i] != null) pitchIndicators[i].color = pitcher.pitchType == i ? selectedColor : unselectedColor;
    }
}
