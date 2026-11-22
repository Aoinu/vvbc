using Tsukimi;
using UnityEngine;
using UnityEngine.UI;

// World-space calibration panel for one mound. Display and buttons only: every decision,
// computation and save is in ManualPitchBehaviour, which calls OnManualPitchChanged.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class ManualPitchPanelBehaviour : TsukimiBehaviour
{
    public ManualPitchBehaviour pitcher;
    public Text statusLabel;
    [Tooltip("Text showing the last release's result: pitch or throw, speed and course miss.")]
    public Text resultLabel;
    [Tooltip("Text showing the last release's analysis: arm motion and form, launch point, speed, spin and SSW, aim.")]
    public Text detailLabel;

    private void Start()
    {
        Refresh();
    }

    public void OnManualPitchChanged()
    {
        Refresh();
    }

    // Every click first cancels the trigger press that made it, so clicking a button never
    // records a calibration and undoes a course-lock toggle made by that press.
    public void StartCalibration() { if (Clicked()) pitcher.BeginCalibration(); }
    public void CancelCalibration() { if (Clicked()) pitcher.CancelCalibration(); }
    public void RecallBall() { if (Clicked()) pitcher.RecallBall(); }
    // Records the throwing hand's twist as the selected type's ideal after a short countdown.
    public void RecordPose() { if (Clicked()) pitcher.RecordIdealTwist(); }

    private bool Clicked()
    {
        if (pitcher == null) return false;
        pitcher.CancelTriggerPress();
        return true;
    }

    public void Refresh()
    {
        if (pitcher == null) return;
        if (statusLabel != null) statusLabel.text = StatusText();
        if (resultLabel != null) resultLabel.text = pitcher.releaseResultText;
        if (detailLabel != null) detailLabel.text = pitcher.releaseDetailText;
    }

    public string StatusText()
    {
        string calibration = pitcher.HasCalibration()
            ? (pitcher.CalibratedHand() == GloveBehaviour.HandLeft ? "saved (left hand)" : "saved (right hand)")
            : "none";
        string text = pitcher.SelectedPitchTypeName() + "  |  " + ManualPitchMath.StateName(pitcher.state) + "  |  Calibration: " + calibration;
        if (pitcher.Calibrating()) text += "\nCALIBRATING";
        if (pitcher.statusText.Length > 0) text += "\n" + pitcher.statusText;
        return text;
    }
}
