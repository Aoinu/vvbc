using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

// Edits are drafted locally; a coalesced commit asks the pitcher to take Ownership and sync them.
public partial class PitchSettingsBehaviour : TsukimiBehaviour
{
    public BattingPitcherBehaviour pitcher;
    public Slider speedSlider;
    public Slider spinSlider;
    public Slider resetSlider;
    public Text speedText;
    public Text spinText;
    public Text resetText;
    public Text ownerText;
    public Image[] courseIndicators = new Image[0];
    public Image[] pitchIndicators = new Image[0];
    public float minSpeedKmh = 60f;
    public float maxSpeedKmh = 160f;
    public float minSpinRpm = 0f;
    public float maxSpinRpm = 3000f;
    public float minResetSeconds = 3f;
    public float maxResetSeconds = 15f;
    [Tooltip("Quiet time after the last edit before the settings are committed, in seconds.")]
    public float commitDelaySeconds = 0.5f;
    public Color selectedColor = new Color(0.18f, 0.65f, 0.95f, 1f);
    public Color unselectedColor = new Color(0.19f, 0.22f, 0.27f, 1f);
    private bool applying;
    private bool commitScheduled;
    private float lastEditTime;
    private int draftMask;
    private int draftType;

    private void Start()
    {
        if (pitcher == null) return;
        applying = true;
        if (speedSlider != null) { speedSlider.minValue = minSpeedKmh; speedSlider.maxValue = maxSpeedKmh; }
        if (spinSlider != null) { spinSlider.minValue = minSpinRpm; spinSlider.maxValue = maxSpinRpm; }
        if (resetSlider != null) { resetSlider.minValue = minResetSeconds; resetSlider.maxValue = maxResetSeconds; }
        applying = false;
        pitcher.RegisterSettingsListener(GetComponent<UdonBehaviour>());
        _OnPitcherStateChanged();
    }

    // The pitcher's synced settings or Owner changed: show them without re-triggering a commit.
    public void _OnPitcherStateChanged()
    {
        if (pitcher == null) return;
        RefreshOwner();
        if (commitScheduled) return;
        applying = true;
        if (speedSlider != null) speedSlider.value = pitcher.speedKmh;
        if (spinSlider != null) spinSlider.value = pitcher.spinRpm;
        if (resetSlider != null) resetSlider.value = pitcher.resetSeconds;
        applying = false;
        draftMask = pitcher.courseMask;
        draftType = pitcher.pitchType;
        Refresh();
    }

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        RefreshOwner();
    }

    // Slider changes come from drag, click and keyboard alike; the commit waits for the edits to settle.
    public void _SpeedChanged() { Edited(); }
    public void _SpinChanged() { Edited(); }
    public void _ResetChanged() { Edited(); }

    private void Edited()
    {
        if (applying) return;
        Refresh();
        lastEditTime = Time.time;
        if (commitScheduled) return;
        commitScheduled = true;
        SendCustomEventDelayedSeconds(nameof(_Commit), Mathf.Max(0.05f, commitDelaySeconds));
    }

    // Seconds still to wait so that the commit comes commitDelaySeconds after the last edit.
    private float CommitRemaining(float now)
    {
        return Mathf.Max(0.05f, commitDelaySeconds) - (now - lastEditTime);
    }

    public void _Commit()
    {
        if (!commitScheduled) return;
        float remaining = CommitRemaining(Time.time);
        if (remaining > 0.01f)
        {
            SendCustomEventDelayedSeconds(nameof(_Commit), remaining);
            return;
        }
        commitScheduled = false;
        if (pitcher == null) return;
        pitcher.SubmitSettings(speedSlider != null ? speedSlider.value : pitcher.speedKmh,
            spinSlider != null ? spinSlider.value : pitcher.spinRpm,
            resetSlider != null ? resetSlider.value : pitcher.resetSeconds, draftMask, draftType);
    }

    private void ToggleCourse(int index) { draftMask = draftMask ^ (1 << index); Edited(); }
    private void SelectPitch(int index) { draftType = index; Edited(); }
    public void _Course0() { ToggleCourse(0); } public void _Course1() { ToggleCourse(1); } public void _Course2() { ToggleCourse(2); }
    public void _Course3() { ToggleCourse(3); } public void _Course4() { ToggleCourse(4); } public void _Course5() { ToggleCourse(5); }
    public void _Course6() { ToggleCourse(6); } public void _Course7() { ToggleCourse(7); } public void _Course8() { ToggleCourse(8); }
    public void _Pitch0() { SelectPitch(0); } public void _Pitch1() { SelectPitch(1); } public void _Pitch2() { SelectPitch(2); }
    public void _Pitch3() { SelectPitch(3); } public void _Pitch4() { SelectPitch(4); } public void _Pitch5() { SelectPitch(5); }
    public void _Pitch6() { SelectPitch(6); }

    public void RefreshOwner()
    {
        if (ownerText == null || pitcher == null) return;
        VRCPlayerApi owner = Networking.GetOwner(pitcher.gameObject);
        ownerText.text = "Owner: " + (Utilities.IsValid(owner) ? owner.displayName : "-");
    }

    public void Refresh()
    {
        if (pitcher == null) return;
        if (speedText != null && speedSlider != null) speedText.text = Mathf.RoundToInt(speedSlider.value) + " km/h";
        if (spinText != null && spinSlider != null) spinText.text = Mathf.RoundToInt(spinSlider.value) + " rpm";
        if (resetText != null && resetSlider != null) resetText.text = resetSlider.value.ToString("F1") + " s";
        for (int i = 0; i < courseIndicators.Length && i < 9; i++)
            if (courseIndicators[i] != null) courseIndicators[i].color = (draftMask & (1 << i)) != 0 ? selectedColor : unselectedColor;
        for (int i = 0; i < pitchIndicators.Length && i < 7; i++)
            if (pitchIndicators[i] != null) pitchIndicators[i].color = draftType == i ? selectedColor : unselectedColor;
        RefreshOwner();
    }
}
