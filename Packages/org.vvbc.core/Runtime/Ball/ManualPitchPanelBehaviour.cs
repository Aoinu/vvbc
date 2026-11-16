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

    private void Start()
    {
        Refresh();
    }

    public void OnManualPitchChanged()
    {
        Refresh();
    }

    public void StartCalibration() { if (pitcher != null) pitcher.BeginCalibration(); }
    public void RecordBoth() { if (pitcher != null) pitcher.SetRecordMode(ManualPitchBehaviour.RecordBoth); }
    public void RecordPositionOnly() { if (pitcher != null) pitcher.SetRecordMode(ManualPitchBehaviour.RecordPosition); }
    public void RecordRotationOnly() { if (pitcher != null) pitcher.SetRecordMode(ManualPitchBehaviour.RecordRotation); }
    public void SaveCalibration() { if (pitcher != null) pitcher.SaveCalibration(); }
    public void CancelCalibration() { if (pitcher != null) pitcher.CancelCalibration(); }
    public void RecallBall() { if (pitcher != null) pitcher.RecallBall(); }

    public void Refresh()
    {
        if (statusLabel == null || pitcher == null) return;
        statusLabel.text = StatusText();
    }

    public string StatusText()
    {
        string calibration = pitcher.HasCalibration()
            ? (pitcher.CalibratedHand() == GloveBehaviour.HandLeft ? "saved (left hand)" : "saved (right hand)")
            : "none";
        string text = "4-Seam  |  " + ManualPitchMath.StateName(pitcher.state) + "  |  Calibration: " + calibration;
        if (pitcher.Calibrating()) text += "\nCALIBRATING  record: " + RecordModeName(pitcher.recordMode);
        if (pitcher.statusText.Length > 0) text += "\n" + pitcher.statusText;
        return text;
    }

    public static string RecordModeName(int mode)
    {
        if (mode == ManualPitchBehaviour.RecordPosition) return "position only";
        if (mode == ManualPitchBehaviour.RecordRotation) return "rotation only";
        return "position + rotation";
    }
}
