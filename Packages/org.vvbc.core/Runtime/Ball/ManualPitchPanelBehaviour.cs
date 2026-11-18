using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.Udon;

// World-space calibration panel for one mound. Display and buttons only: every decision,
// computation and save is in ManualPitchBehaviour, which calls OnManualPitchChanged.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class ManualPitchPanelBehaviour : TsukimiBehaviour
{
    public ManualPitchBehaviour pitcher;
    public Text statusLabel;
    [Tooltip("Text showing the last release's result: pitch or throw, speed and course miss.")]
    public Text resultLabel;
    [Tooltip("Text showing the last release's analysis: release position and motion, spin and SSW, aim.")]
    public Text detailLabel;
    [Tooltip("Text showing the allowed sync delay, whether the pitch data is out, and an invalid or cancelled pitch.")]
    public Text syncLabel;

    private bool _listening;

    private void Start()
    {
        Refresh();
    }

    public void OnManualPitchChanged()
    {
        Refresh();
    }

    public void OnPitchDelaySettingChanged()
    {
        Refresh();
    }

    // The shared allowed sync delay, 10 ms per click (display here, value in PitchDelaySettingBehaviour).
    public void DelayLimitDown() { if (Clicked()) pitcher.StepDelayThreshold(-1); }
    public void DelayLimitUp() { if (Clicked()) pitcher.StepDelayThreshold(1); }

    // Every click first cancels the trigger press that made it, so clicking a button never
    // records a calibration and undoes a course-lock toggle made by that press.
    public void StartCalibration() { if (Clicked()) pitcher.BeginCalibration(); }
    public void CancelCalibration() { if (Clicked()) pitcher.CancelCalibration(); }
    public void RecallBall() { if (Clicked()) pitcher.RecallBall(); }

    private bool Clicked()
    {
        if (pitcher == null) return false;
        pitcher.CancelTriggerPress();
        return true;
    }

    public void Refresh()
    {
        if (pitcher == null) return;
        if (!_listening) _listening = pitcher.ListenToDelaySetting(GetComponent<UdonBehaviour>());
        if (statusLabel != null) statusLabel.text = StatusText();
        if (resultLabel != null) resultLabel.text = pitcher.releaseResultText;
        if (detailLabel != null) detailLabel.text = pitcher.releaseDetailText;
        if (syncLabel != null) syncLabel.text = pitcher.PitchSyncText();
    }

    public string StatusText()
    {
        string calibration = pitcher.HasCalibration()
            ? (pitcher.CalibratedHand() == GloveBehaviour.HandLeft ? "saved (left hand)" : "saved (right hand)")
            : "none";
        string text = "4-Seam  |  " + ManualPitchMath.StateName(pitcher.state) + "  |  Calibration: " + calibration;
        if (pitcher.Calibrating()) text += "\nCALIBRATING";
        if (pitcher.statusText.Length > 0) text += "\n" + pitcher.statusText;
        return text;
    }
}
