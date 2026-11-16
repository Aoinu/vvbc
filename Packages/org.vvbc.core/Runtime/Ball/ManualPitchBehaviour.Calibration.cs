using UnityEngine;
using VRC.SDK3.Persistence;
using VRC.SDKBase;

// The 4-Seam release calibration: the throwing controller's position relative to the chest
// and its rotation, both in the pitching frame (chest position, horizontal direction to the
// plate, world up). The chest rotation is never read. Persisted in PlayerData for the local
// player; derived values (arm slot, spin axis) are recomputed, never stored.
public partial class ManualPitchBehaviour
{
    public const int RecordBoth = 0;
    public const int RecordPosition = 1;
    public const int RecordRotation = 2;
    public const int CalibrationVersion = 1;
    private const string KeyVersion = "vvbc.pitching.calib.version";
    private const string KeyHand = "vvbc.pitching.calib.hand";
    private const string KeyPosition = "vvbc.pitching.calib.position";
    private const string KeyRotation = "vvbc.pitching.calib.rotation";

    [HideInInspector] public int recordMode = RecordBoth;
    [HideInInspector] public string statusText = "";

    private bool _hasCalibration;
    private int _calHand;
    private Vector3 _calPosition;
    private Quaternion _calRotation = Quaternion.identity;
    private bool _restored;

    private bool _calibrating;
    private bool _draftHasPosition, _draftHasRotation;
    private Vector3 _draftPosition;
    private Quaternion _draftRotation = Quaternion.identity;

    public bool Calibrating() { return _calibrating; }
    public bool HasCalibration() { return _hasCalibration; }
    public int CalibratedHand() { return _calHand; }
    public Vector3 CalibratedPosition() { return _calPosition; }
    public Quaternion CalibratedRotation() { return _calRotation; }

    // Only a calibration of the current throwing hand is used; the other hand's is never mirrored.
    public bool CalibrationUsable()
    {
        return _hasCalibration && _calHand == _activeHand;
    }

    // Opens calibration. Course locking is off until Save or Cancel.
    public void BeginCalibration()
    {
        if (state == StateInactive)
        {
            statusText = "Stand on the mound in VR with the glove on.";
            Notify();
            return;
        }
        Unlock();
        _calibrating = true;
        bool usable = CalibrationUsable();
        _draftHasPosition = usable;
        _draftHasRotation = usable;
        _draftPosition = usable ? _calPosition : Vector3.zero;
        _draftRotation = usable ? _calRotation : Quaternion.identity;
        statusText = "Hold the ball, take your 4-Seam release pose and pull the glove-hand trigger.";
        Notify();
    }

    public void SetRecordMode(int mode)
    {
        recordMode = mode == RecordPosition || mode == RecordRotation ? mode : RecordBoth;
        Notify();
    }

    public void RecordCalibration()
    {
        if (!_calibrating) return;
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player) || strikeZone == null) return;
        VRCPlayerApi.TrackingData hand = player.GetTrackingData(HandTracking());
        Transform plate = strikeZone.PlateTransform();
        Vector3 forward = StrikeZoneGeometry.Forward(plate.forward);
        RecordCalibrationAt(player.GetBonePosition(HumanBodyBones.Chest), hand.position, hand.rotation, PlanePoint(plate, forward));
        Notify();
    }

    // Records the draft from explicit tracking data; position and rotation separately
    // according to recordMode. An invalid chest, hand or plate records nothing.
    public bool RecordCalibrationAt(Vector3 chest, Vector3 handPosition, Quaternion handRotation, Vector3 platePoint)
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
        Vector3 relative = ManualPitchMath.ToFrame(frame, chest, handPosition);
        if (recordMode != RecordRotation)
        {
            if (!ManualPitchMath.ValidRelativePosition(relative))
            {
                statusText = "The hand is too close to or too far from the chest; nothing recorded.";
                return false;
            }
            _draftPosition = relative;
            _draftHasPosition = true;
        }
        if (recordMode != RecordPosition)
        {
            _draftRotation = Normalized(ManualPitchMath.Multiply(ManualPitchMath.Inverse(frame), handRotation));
            _draftHasRotation = true;
        }
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
        Vector3 axis = ManualPitchMath.FourSeamAxis(_draftPosition);
        Vector3 magnus = Vector3.Cross(axis, Vector3.forward);
        return "Release " + _draftPosition.ToString("F2") + "  slot " + slotAngle.ToString("F0") + " deg (" + form + ")" +
            "\nSpin axis " + axis.ToString("F2") + "  break dir " + magnus.ToString("F2") +
            (_draftHasRotation ? "" : "\nRotation not recorded yet.");
    }

    public bool SaveCalibration()
    {
        if (!CommitCalibration()) return false;
        PlayerData.SetInt(KeyVersion, CalibrationVersion);
        PlayerData.SetInt(KeyHand, _calHand);
        PlayerData.SetVector3(KeyPosition, _calPosition);
        PlayerData.SetQuaternion(KeyRotation, _calRotation);
        return true;
    }

    // Validates the draft and makes it the calibration; PlayerData is written by the caller.
    public bool CommitCalibration()
    {
        if (!_calibrating) return false;
        if (!_draftHasPosition || !_draftHasRotation)
        {
            statusText = "Record both position and rotation before saving.";
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
        _calibrating = false;
        statusText = "Calibration saved.";
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
            Debug.LogWarning("ManualPitch: ignored an invalid stored calibration");
        Notify();
    }

    // Accepts stored values only when every one is valid; anything else keeps no calibration.
    public bool RestoreCalibration(int version, int hand, Vector3 position, Quaternion rotation)
    {
        if (version != CalibrationVersion || (hand != GloveBehaviour.HandLeft && hand != GloveBehaviour.HandRight) ||
            !ManualPitchMath.ValidRelativePosition(position) || !ManualPitchMath.ValidRotation(rotation)) return false;
        _hasCalibration = true;
        _calHand = hand;
        _calPosition = position;
        _calRotation = Normalized(rotation);
        return true;
    }

    private static Quaternion Normalized(Quaternion q)
    {
        float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        return n > 0.000001f ? new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n) : Quaternion.identity;
    }
}
