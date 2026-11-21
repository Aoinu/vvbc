using UnityEngine;
using VRC.SDK3.Persistence;
using VRC.SDKBase;

// The 4-Seam release calibration (pose quality and the depth and height of P0; the 4-Seam spin
// axis is fixed by the hand, not calibrated): the throwing controller's position relative to the chest
// and its rotation, both in the pitching frame (chest position, horizontal direction to the
// plate, world up), and the controller's release point relative to the mound's ReleaseAnchor
// (toward the plate and up), which sets the depth and height of the fixed release point P0.
// The chest rotation is never read. Persisted in PlayerData for the local player; derived
// values (arm slot, spin axis) are recomputed, never stored.
public partial class ManualPitchBehaviour
{
    public const int CalibrationVersion = 1;
    private const string KeyVersion = "vvbc.pitching.calib.version";
    private const string KeyHand = "vvbc.pitching.calib.hand";
    private const string KeyPosition = "vvbc.pitching.calib.position";
    private const string KeyRotation = "vvbc.pitching.calib.rotation";
    // Added after version 1 without changing it: a calibration saved before has no release
    // offset and uses the ReleaseAnchor itself until it is calibrated again.
    private const string KeyReleaseSet = "vvbc.pitching.calib.releaseSet";
    private const string KeyReleaseForward = "vvbc.pitching.calib.releaseForward";
    private const string KeyReleaseUp = "vvbc.pitching.calib.releaseUp";
    // Added with the release range: toward the throwing arm's side from ReleaseAnchor. A
    // calibration saved before has none and reads as 0 (the anchor's own side, then limited).
    private const string KeyReleaseSide = "vvbc.pitching.calib.releaseSide";

    [HideInInspector] public string statusText = "";

    private bool _hasCalibration;
    private int _calHand;
    private Vector3 _calPosition;
    private Quaternion _calRotation = Quaternion.identity;
    // The calibrated release relative to ReleaseAnchor: x toward the plate, y up, z toward the
    // throwing arm's side (metres). P0 is this, limited to the release range.
    private bool _calHasRelease;
    private Vector3 _calReleaseOffset;
    private bool _restored;

    private bool _calibrating;
    private bool _draftHasPosition, _draftHasRotation;
    private Vector3 _draftPosition;
    private Quaternion _draftRotation = Quaternion.identity;
    private bool _draftHasRelease;
    private Vector3 _draftReleaseOffset;
    // The draft release lies outside the release range (it will be limited).
    private bool _draftReleaseLimited;
    private Vector3 _triggerHandPosition;

    public bool Calibrating() { return _calibrating; }
    public bool HasCalibration() { return _hasCalibration; }
    public int CalibratedHand() { return _calHand; }
    public Vector3 CalibratedPosition() { return _calPosition; }
    public Quaternion CalibratedRotation() { return _calRotation; }
    public bool HasCalibratedRelease() { return _calHasRelease; }
    public Vector3 CalibratedReleaseOffset() { return _calReleaseOffset; }

    // Only a calibration of the current throwing hand is used; the other hand's is never mirrored.
    public bool CalibrationUsable()
    {
        return _hasCalibration && _calHand == _activeHand;
    }

    // Opens calibration. Course locking is off until the pose is recorded or Cancel.
    public void BeginCalibration()
    {
        if (state == StateInactive)
        {
            statusText = "Stand on the mound in VR with the glove on.";
            Notify();
            return;
        }
        Unlock();
        // No crossing or auto release survives into calibration, and the plate is hidden.
        ClearCrossing();
        ClearReleaseFeedback();
        _calibrating = true;
        _draftHasPosition = false;
        _draftHasRotation = false;
        _draftHasRelease = false;
        statusText = "Put the ball down. Take your 4-Seam release pose with the empty throwing hand, then pull and release its trigger to record and save.";
        Notify();
    }

    // The throwing hand, chest and plate at the moment the glove-hand trigger goes down.
    private bool CaptureCalibrationPose()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player) || strikeZone == null) return false;
        VRCPlayerApi.TrackingData hand = player.GetTrackingData(HandTracking());
        Transform plate = strikeZone.PlateTransform();
        _triggerChest = player.GetBonePosition(HumanBodyBones.Chest);
        _triggerHandPosition = hand.position;
        _triggerHandRotation = hand.rotation;
        _triggerPlate = PlanePoint(plate, StrikeZoneGeometry.Forward(plate.forward));
        return true;
    }

    // Records position and rotation from the throwing hand pose, commits it and writes PlayerData.
    public bool FinishCalibrationAt(Vector3 chest, Vector3 handPosition, Quaternion handRotation, Vector3 platePoint)
    {
        if (!RecordCalibrationAt(chest, handPosition, handRotation, platePoint)) return false;
        return SaveCalibration();
    }

    // Records the draft (position, rotation and the release offset from this mound's anchor)
    // from explicit tracking data. An invalid chest, hand or plate records nothing. Without a
    // ReleaseAnchor no release offset is recorded (P0 stays on the anchor).
    public bool RecordCalibrationAt(Vector3 chest, Vector3 handPosition, Quaternion handRotation, Vector3 platePoint)
    {
        bool anchored = releaseAnchor != null;
        Transform centre = moundCenter != null ? moundCenter : transform;
        return RecordCalibrationWithAnchorAt(chest, handPosition, handRotation, platePoint, anchored,
            centre.position, anchored ? releaseAnchor.position : Vector3.zero);
    }

    public bool RecordCalibrationWithAnchorAt(Vector3 chest, Vector3 handPosition, Quaternion handRotation, Vector3 platePoint,
        bool anchored, Vector3 centre, Vector3 anchor)
    {
        if (!_calibrating) return false;
        Vector3 forward = ManualPitchMath.HorizontalDirection(chest, platePoint);
        if (!ManualPitchMath.ValidPoint(chest) || forward == Vector3.zero || !ManualPitchMath.FiniteVector(handPosition) ||
            !ManualPitchMath.ValidRotation(handRotation))
        {
            statusText = "Tracking or chest bone is not available; nothing recorded.";
            return false;
        }
        Quaternion frame = ManualPitchMath.Frame(forward);
        // Any finite position is recorded: no distance or angle limit. The pitch leaves from the
        // fixed P0: its depth and height come from this release (relative to the anchor), its
        // side from the throwing hand.
        Vector3 relative = ManualPitchMath.ToFrame(frame, chest, handPosition);
        if (!ManualPitchMath.ValidRelativePosition(relative))
        {
            statusText = "Tracking or chest bone is not available; nothing recorded.";
            return false;
        }
        _draftPosition = relative;
        _draftHasPosition = true;
        _draftRotation = Normalized(ManualPitchMath.Multiply(ManualPitchMath.Inverse(frame), handRotation));
        _draftHasRotation = true;
        _draftHasRelease = anchored && ManualPitchMath.HorizontalDirection(centre, platePoint) != Vector3.zero;
        _draftReleaseOffset = _draftHasRelease
            ? ManualPitchMath.ReleaseOffset(centre, anchor, platePoint, handPosition, _activeHand == GloveBehaviour.HandLeft) : Vector3.zero;
        _draftReleaseLimited = _draftHasRelease && !ManualPitchMath.InReleaseRange(_draftReleaseOffset, anchor.y - centre.y, ReleaseRange());
        statusText = PreviewText();
        return true;
    }

    // Arm slot, spin axis and stored release, shown before Save.
    public string PreviewText()
    {
        if (!_draftHasPosition) return "Position not recorded yet.";
        Vector3 slot = ManualPitchMath.ArmSlot(_draftPosition);
        float slotAngle = Mathf.Atan2(slot.y, Mathf.Abs(slot.x)) * Mathf.Rad2Deg;
        string form = slotAngle >= 45f ? "overhand" : slotAngle >= -20f ? "sidearm" : "underhand";
        // The 4-Seam axis is fixed by the throwing hand; the slot is shown for the form only.
        bool left = _activeHand == GloveBehaviour.HandLeft;
        Vector3 axis = ManualPitchMath.FixedFourSeamAxis(left);
        Vector3 magnus = Vector3.Cross(axis, Vector3.forward);
        return "Release " + _draftPosition.ToString("F2") + "  slot " + slotAngle.ToString("F0") + " deg (" + form + ")" +
            "\nSpin axis " + axis.ToString("F2") + " (fixed, " + (left ? "11-5" : "1-7") + " o'clock)  break dir " + magnus.ToString("F2") +
            (_draftHasRelease ? "\nRelease point: " + ReleaseOffsetText(_draftReleaseOffset) +
                (_draftReleaseLimited ? "\n  outside the release range: the plate is placed at the nearest point inside it" : "")
                : "\nRelease point: ReleaseAnchor (limited to the release range)") +
            (_draftHasRotation ? "" : "\nRotation not recorded yet.");
    }

    public static string ReleaseOffsetText(Vector3 offset)
    {
        return (offset.x >= 0f ? "+" : "") + offset.x.ToString("F2") + " m toward the plate, " +
            (offset.y >= 0f ? "+" : "") + offset.y.ToString("F2") + " m up, " +
            (offset.z >= 0f ? "+" : "") + offset.z.ToString("F2") + " m arm side from ReleaseAnchor";
    }

    // The calibrated release of the current hand lies outside the release range (P0 limited).
    public bool ReleaseLimited()
    {
        if (releaseAnchor == null) return false;
        Transform centre = moundCenter != null ? moundCenter : transform;
        Vector3 offset = CalibrationUsable() && _calHasRelease ? _calReleaseOffset : Vector3.zero;
        return !ManualPitchMath.InReleaseRange(offset, releaseAnchor.position.y - centre.position.y, ReleaseRange());
    }

    public bool SaveCalibration()
    {
        if (!CommitCalibration()) return false;
        PlayerData.SetInt(KeyVersion, CalibrationVersion);
        PlayerData.SetInt(KeyHand, _calHand);
        PlayerData.SetVector3(KeyPosition, _calPosition);
        PlayerData.SetQuaternion(KeyRotation, _calRotation);
        PlayerData.SetBool(KeyReleaseSet, _calHasRelease);
        PlayerData.SetFloat(KeyReleaseForward, _calReleaseOffset.x);
        PlayerData.SetFloat(KeyReleaseUp, _calReleaseOffset.y);
        PlayerData.SetFloat(KeyReleaseSide, _calReleaseOffset.z);
        return true;
    }

    // Validates the draft and makes it the calibration; PlayerData is written by the caller.
    public bool CommitCalibration()
    {
        if (!_calibrating) return false;
        if (!_draftHasPosition || !_draftHasRotation)
        {
            statusText = "Nothing recorded yet.";
            Notify();
            return false;
        }
        if (!_restored)
        {
            statusText = "Saved data is not loaded yet; try again in a moment.";
            Notify();
            return false;
        }
        _hasCalibration = true;
        _calHand = _activeHand;
        _calPosition = _draftPosition;
        _calRotation = _draftRotation;
        _calHasRelease = _draftHasRelease;
        _calReleaseOffset = _draftReleaseOffset;
        _calibrating = false;
        _markerPlaced = false;
        InvalidateDefaultCourse();
        statusText = "Calibration saved.\n" + PreviewText();
        if (showDebug) Debug.Log("ManualPitch: calibration saved, hand " + _calHand + ", release " + _calPosition.ToString("F2") +
            " (right, up, forward from the chest), rotation " + _calRotation.ToString("F3") +
            ", release point " + (_calHasRelease ? ReleaseOffsetText(_calReleaseOffset) : "ReleaseAnchor"));
        Notify();
        return true;
    }

    public void CancelCalibration()
    {
        if (!_calibrating) return;
        EndCalibration();
        statusText = "Calibration cancelled.";
        Notify();
    }

    private void EndCalibration()
    {
        _calibrating = false;
        _draftHasPosition = false;
        _draftHasRotation = false;
        _draftHasRelease = false;
    }

    // Read once for the local player; Save is refused until this has run.
    public override void OnPlayerRestored(VRCPlayerApi player)
    {
        if (!Utilities.IsValid(player) || !player.isLocal || _restored) return;
        _restored = true;
        if (PlayerData.TryGetInt(player, KeyVersion, out int version) &&
            PlayerData.TryGetInt(player, KeyHand, out int hand) &&
            PlayerData.TryGetVector3(player, KeyPosition, out Vector3 position) &&
            PlayerData.TryGetQuaternion(player, KeyRotation, out Quaternion rotation) &&
            !RestoreCalibration(version, hand, position, rotation))
        {
            // Only an unreadable value (wrong version or hand, a non-finite position, an invalid
            // quaternion) is refused; it stays stored until a new calibration overwrites it.
            Debug.LogWarning("ManualPitch: ignored an invalid stored calibration");
            statusText = "The saved calibration could not be read; calibrate again.";
        }
        else if (_hasCalibration && PlayerData.TryGetBool(player, KeyReleaseSet, out bool releaseSet) && releaseSet &&
            PlayerData.TryGetFloat(player, KeyReleaseForward, out float forward) &&
            PlayerData.TryGetFloat(player, KeyReleaseUp, out float up))
            RestoreReleaseOffset(forward, up, PlayerData.TryGetFloat(player, KeyReleaseSide, out float side) ? side : 0f);
        Notify();
    }

    // Accepts stored values only when every one is valid; anything else keeps no calibration.
    // The stored format is unchanged (version 1): every stored calibration with a finite position
    // and a unit quaternion is used, including ones taken under the former range limits.
    public bool RestoreCalibration(int version, int hand, Vector3 position, Quaternion rotation)
    {
        if (version != CalibrationVersion || (hand != GloveBehaviour.HandLeft && hand != GloveBehaviour.HandRight) ||
            !ManualPitchMath.ValidRelativePosition(position) || !ManualPitchMath.ValidRotation(rotation)) return false;
        _hasCalibration = true;
        _calHand = hand;
        _calPosition = position;
        _calRotation = Normalized(rotation);
        // A calibration saved before the release offset existed keeps P0 on the ReleaseAnchor.
        _calHasRelease = false;
        _calReleaseOffset = Vector3.zero;
        _markerPlaced = false;
        InvalidateDefaultCourse();
        return true;
    }

    // The stored release offset of the restored calibration (side 0 when it was saved before the
    // side existed); a non-finite one is ignored.
    public bool RestoreReleaseOffset(float forward, float up, float side)
    {
        if (!_hasCalibration || !ManualPitchMath.Finite(forward) || !ManualPitchMath.Finite(up) || !ManualPitchMath.Finite(side)) return false;
        _calHasRelease = true;
        _calReleaseOffset = new Vector3(forward, up, side);
        _markerPlaced = false;
        InvalidateDefaultCourse();
        return true;
    }

    private static Quaternion Normalized(Quaternion q)
    {
        float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        return n > 0.000001f ? new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n) : Quaternion.identity;
    }
}
