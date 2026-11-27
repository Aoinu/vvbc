using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

// Locker UI for the local player's glove. Buttons call these public events through
// UdonBehaviour.SendCustomEvent. All state lives on the glove; this only displays it.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class GloveLockerBehaviour : TsukimiBehaviour
{
    [Tooltip("Status text showing the fitting and the calibration state.")]
    public Text statusText;
    [Tooltip("Label of the equip button.")]
    public Text equipLabel;

    private GloveBehaviour _glove;
    private bool _saveRejected;

    private void Start()
    {
        Refresh();
    }

    public void ToggleEquip()
    {
        if (!Bind()) return;
        // During a match the roster decides (the glove refuses manual equipping then).
        if (!_glove.IsCalibrating() && !_glove.IsAutomaticEquipment()) _glove.SetEquipped(!_glove.equipped);
        Refresh();
    }

    public void BeginAdjust()
    {
        if (!Bind()) return;
        _saveRejected = false;
        _glove.BeginCalibration();
        Refresh();
    }

    public void ToggleThrowHand()
    {
        if (!BeginIfNeeded()) return;
        _glove.SetDraftThrowHand(_glove.DraftThrowHand() == GloveBehaviour.HandLeft
            ? GloveBehaviour.HandRight : GloveBehaviour.HandLeft);
        Refresh();
    }

    public void ScaleDown()
    {
        if (!BeginIfNeeded()) return;
        _glove.StepDraftScale(-1);
        Refresh();
    }

    public void ScaleUp()
    {
        if (!BeginIfNeeded()) return;
        _glove.StepDraftScale(1);
        Refresh();
    }

    public void ConfirmAdjust()
    {
        if (!Bind()) return;
        _saveRejected = _glove.IsCalibrating() && !_glove.ConfirmCalibration();
        Refresh();
    }

    public void CancelAdjust()
    {
        if (!Bind()) return;
        _saveRejected = false;
        _glove.CancelCalibration();
        Refresh();
    }

    // Called by the glove when its state changes while this locker is its listener.
    public void OnGloveChanged()
    {
        Refresh();
    }

    private bool BeginIfNeeded()
    {
        if (!Bind()) return false;
        _saveRejected = false;
        return _glove.IsCalibrating() || _glove.BeginCalibration();
    }

    // The local player's glove: exactly one locally owned GloveBehaviour among the
    // local PlayerObjects.
    private bool Bind()
    {
        if (_glove != null) return true;
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player)) return false;
        GameObject[] objects = Networking.GetPlayerObjects(player);
        if (objects == null) return false;
        GloveBehaviour found = null;
        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] == null) continue;
            GloveBehaviour candidate = objects[i].GetComponentInChildren<GloveBehaviour>(true);
            if (candidate == null) continue;
            if (found != null) return false;
            found = candidate;
        }
        if (found == null || !found.IsLocalOwner()) return false;
        _glove = found;
        _glove.SetListener(this);
        return true;
    }

    private static string ReasonName(int reason)
    {
        switch (reason)
        {
            case GloveBehaviour.AutoReasonDefense: return "Defense";
            case GloveBehaviour.AutoReasonOffense: return "Offense";
            case GloveBehaviour.AutoReasonDesignatedHitter: return "DH";
            case GloveBehaviour.AutoReasonNotRegistered: return "Not registered";
        }
        return "-";
    }

    private void Refresh()
    {
        if (statusText == null && equipLabel == null) return;
        if (_glove == null && !Bind())
        {
            if (statusText != null) statusText.text = "Glove not available";
            return;
        }
        bool calibrating = _glove.IsCalibrating();
        bool automatic = _glove.IsAutomaticEquipment();
        if (equipLabel != null) equipLabel.text = automatic ? "Automatic" : _glove.equipped ? "Unequip" : "Equip";
        if (statusText == null) return;
        int hand = calibrating ? _glove.DraftThrowHand() : _glove.throwHand;
        float scale = calibrating ? _glove.DraftScale() : _glove.gloveScale;
        float distance = calibrating ? _glove.DraftOffsetDistance() : _glove.offsetPosition.magnitude;
        string text = (_glove.equipped ? "Glove: Equipped" : "Glove: Not equipped") +
            "\nEquipment: " + (automatic ? "Automatic (" + ReasonName(_glove.AutomaticReason()) + ")" : "Manual") +
            "\nThrowing hand: " + (hand == GloveBehaviour.HandLeft ? "Left (glove on right)" : "Right (glove on left)") +
            "\nScale: " + scale.ToString("F2") +
            "\nOffset: " + distance.ToString("F2") + " m (max " + _glove.maxOffsetDistance.ToString("F2") + ")";
        if (calibrating)
        {
            text += _glove.IsHandleHeld()
                ? "\n\nAdjusting: release the glove where it fits."
                : "\n\nAdjusting: grab the glove with your throwing hand, then Save.";
            if (!_glove.DraftValid()) text += "\nOutside the limits; Save is disabled.";
        }
        if (_saveRejected) text += "\nNot saved: the fitting is outside the limits.";
        if (_glove.HoldsBall()) text += "\nRelease the ball to change the glove.";
        if (_glove.IsAutomaticPending()) text += "\n" + (_glove.AutomaticEquip() ? "Equipping" : "Unequipping") + " once the ball is released or adjusting ends.";
        if (automatic && !_glove.IsCalibrationAllowed()) text += "\nAdjusting: only for a fielder while the ball is dead.";
        statusText.text = text;
    }
}
