using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

// Tag contact of the local fielder (docs/development/player-event-collector.md). It records that
// the ball, held by a fielder of the defense, touched an active runner; it never decides an out.
//
// Armed only while: a play is live, the ball's confirmed hold (glove or hand, not a local catch or
// pickup still waiting for ownership) is the local player's, the local player is a fielder of the
// defense in the play's roster snapshot (not the DH, the offense or a spectator), and the play has
// an active runner. Then, every frame, the tag region of that hold (the glove's TagSensor for a
// glove hold, the ball for a hand hold: MatchBallQueryBehaviour) is swept from its previous to its
// current position and tested against each runner's five regions on this client: the standing
// capsule (feet to eye height, approximated from the player's position) and the hand and foot
// spheres at the remote player's Humanoid bones. Nothing relies on remote players' colliders.
//
// One continuous contact gives one TagContactEnter (several parts touching at once included); it
// counts as released only when no part is within the reach plus releaseMargin, so a new contact
// after a full release is a new event. No timer repeats it. The fielder sees the effect at once;
// everyone else when the MatchManager Owner has accepted the event.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class TagContactDetectorBehaviour : TsukimiBehaviour
{
    [Header("References")]
    public MatchControllerBehaviour match;
    [Tooltip("MatchManager/BallQuery: confirmed hold and the local tag region.")]
    public MatchBallQueryBehaviour ballQuery;
    [Tooltip("Tag contact effect and sound (played here at once).")]
    public TagContactFeedbackBehaviour feedback;

    [Header("Runner regions")]
    [Tooltip("Radius of each runner hand and foot sphere, in metres. Default 0.05.")]
    public float runnerSphereRadius = 0.05f;
    [Tooltip("Radius of the runner's standing capsule, in metres. Default 0.2.")]
    public float runnerCapsuleRadius = 0.2f;
    [Tooltip("Capsule height above the avatar's eye height, in metres. Default 0.1.")]
    public float runnerCapsuleHeadMargin = 0.1f;

    [Header("Contact")]
    [Tooltip("A contact is released only once every part is this much farther than touching, in metres. Default 0.03.")]
    public float releaseMargin = 0.03f;
    [Tooltip("A tag region that moved farther than this in one frame (a teleport) is tested at its new position only, in metres. Default 1.5.")]
    public float maxSweepDistance = 1.5f;
    [Tooltip("Seconds between checks of the hold and the runners while live. Default 0.1.")]
    public float pollSeconds = 0.1f;

    public const int Targets = RunnerRules.SlotCount;
    public const int SpheresPerTarget = ContactRules.SphereCount;

    private readonly int[] _targets = new int[Targets];
    private readonly bool[] _inContact = new bool[Targets];
    private readonly bool[] _targetValid = new bool[Targets];
    private readonly Vector3[] _bottom = new Vector3[Targets];
    private readonly Vector3[] _top = new Vector3[Targets];
    private readonly Vector3[] _spheres = new Vector3[Targets * SpheresPerTarget];
    private readonly bool[] _sphereValid = new bool[Targets * SpheresPerTarget];
    private readonly VRCPlayerApi[] _players = new VRCPlayerApi[Targets];
    private readonly Vector3[] _scratchSpheres = new Vector3[SpheresPerTarget];
    private readonly bool[] _scratchValid = new bool[SpheresPerTarget];
    private Vector3 _sensorPrevious;
    private bool _sensorPreviousValid;
    private bool _armed;
    private bool _polling;
    private int _holdMode;
    private int _holdSeq;

    private const int MaxQueued = Targets;
    private readonly int[] _queuedTarget = new int[MaxQueued];
    private readonly int[] _queuedParts = new int[MaxQueued];
    private readonly Vector3[] _queuedPosition = new Vector3[MaxQueued];
    private int _queued;
    private PlayerEventCollectorBehaviour _collector;
    private float _nextCollectorSearch;

    // Diagnostics.
    private int _contacts;
    private int _releases;
    private int _unrecorded;
    private int _lastTarget;
    private int _lastParts;
    private Vector3 _lastPosition;
    private int _samples;

    void Start()
    {
        enabled = false;
        if (match != null) match.AddChangeListener((UdonBehaviour)GetComponent(typeof(UdonBehaviour)));
        _OnMatchChanged();
    }

    public bool IsArmed() { return _armed; }
    public int GetTarget(int slot) { return slot >= 0 && slot < Targets ? _targets[slot] : 0; }
    public bool IsInContact(int slot) { return slot >= 0 && slot < Targets && _inContact[slot]; }
    public int GetContactCount() { return _contacts; }
    public int GetReleaseCount() { return _releases; }
    public int GetUnrecordedCount() { return _unrecorded; }
    public int GetLastTarget() { return _lastTarget; }
    public int GetLastParts() { return _lastParts; }
    public Vector3 GetLastPosition() { return _lastPosition; }
    public int GetQueuedCount() { return _queued; }
    public int GetSampleCount() { return _samples; }

    // ---- Arming (poll while live) ----

    public void _OnMatchChanged()
    {
        if (_polling || match == null || !match.IsLive()) return;
        _polling = true;
        SendCustomEventDelayedSeconds(nameof(_Poll), 0.05f);
    }

    public void _Poll()
    {
        if (match == null || !match.IsLive())
        {
            _polling = false;
            Disarm();
            return;
        }
        SendCustomEventDelayedSeconds(nameof(_Poll), Mathf.Max(0.05f, pollSeconds));
        VRCPlayerApi local = Networking.LocalPlayer;
        int localId = Utilities.IsValid(local) ? local.playerId : 0;
        bool holding = ballQuery != null && ballQuery.HasConfirmedHold() && ballQuery.ConfirmedHolderId() == localId;
        int roleSlot = match.GetSnapshotRoleSlot(localId);
        int defense = match.GetDefenseTeam();
        for (int s = 0; s < Targets; s++)
            SetTarget(s, match.GetRunnerStatus(s) == RunnerRules.StatusActive ? match.GetRunnerPlayer(s) : 0, localId);
        int mode = holding ? ballQuery.ConfirmedHoldMode() : ContactRules.HoldFree;
        int seq = holding ? ballQuery.ConfirmedHoldSequence() : 0;
        if (Arm(localId, roleSlot, defense, mode, seq)) enabled = true;
        else Disarm();
    }

    // The arming rule (tests call it with the answers passed in). True when armed.
    public bool Arm(int localId, int roleSlot, int defenseTeam, int holdMode, int holdSeq)
    {
        bool fielder = roleSlot >= 0 && roleSlot / RosterRules.RoleCount == defenseTeam && roleSlot % RosterRules.RoleCount != RosterRules.RoleDH;
        bool hold = (holdMode == ContactRules.HoldGlove || holdMode == ContactRules.HoldHand) && holdSeq > 0;
        bool anyTarget = false;
        for (int s = 0; s < Targets; s++) if (_targets[s] > 0) anyTarget = true;
        if (localId <= 0 || !fielder || !hold || !anyTarget) return false;
        if (_armed && (holdMode != _holdMode || holdSeq != _holdSeq)) _sensorPreviousValid = false;
        _armed = true;
        _holdMode = holdMode;
        _holdSeq = holdSeq;
        return true;
    }

    public void Disarm()
    {
        ResetContacts();
        enabled = false;
    }

    // Not armed: every contact ends (a new hold starts from no contact).
    public void ResetContacts()
    {
        _armed = false;
        _sensorPreviousValid = false;
        for (int s = 0; s < Targets; s++) _inContact[s] = false;
    }

    // A runner slot's player (0 = none; the local player is never a target). A new player in the
    // slot starts out of contact.
    public void SetTarget(int slot, int playerId, int localId)
    {
        if (slot < 0 || slot >= Targets) return;
        if (playerId == localId) playerId = 0;
        if (_targets[slot] == playerId) return;
        _targets[slot] = playerId;
        _inContact[slot] = false;
        _targetValid[slot] = false;
        _players[slot] = null;
    }

    // ---- Per frame while armed ----

    public override void PostLateUpdate()
    {
        if (!_armed || ballQuery == null) return;
        if (!ballQuery.LocalTagSensorReady())
        {
            _sensorPreviousValid = false;
            return;
        }
        Vector3 center = ballQuery.LocalTagSensorCenter();
        float radius = ballQuery.LocalTagSensorRadius();
        _samples++;
        for (int s = 0; s < Targets; s++) SampleRunner(s);
        Step(center, radius);
        Flush();
    }

    private void SampleRunner(int slot)
    {
        _targetValid[slot] = false;
        if (_targets[slot] <= 0) return;
        VRCPlayerApi player = _players[slot];
        if (!Utilities.IsValid(player))
        {
            player = VRCPlayerApi.GetPlayerById(_targets[slot]);
            _players[slot] = player;
            if (!Utilities.IsValid(player)) return;
        }
        Vector3 feet = player.GetPosition();
        if (!ContactRules.ValidPoint(feet)) return;
        float eye = player.GetAvatarEyeHeightAsMeters();
        for (int i = 0; i < SpheresPerTarget; i++)
        {
            _scratchSpheres[i] = player.GetBonePosition(ContactRules.SphereBone(i));
            _scratchValid[i] = ContactRules.ValidPoint(_scratchSpheres[i]);
        }
        SetRunnerGeometry(slot, feet, eye, _scratchSpheres, _scratchValid);
    }

    public void SetRunnerGeometry(int slot, Vector3 feet, float eyeHeight, Vector3[] spheres, bool[] valid)
    {
        if (slot < 0 || slot >= Targets) return;
        _bottom[slot] = ContactRules.CapsuleBottom(feet, runnerCapsuleRadius);
        _top[slot] = ContactRules.CapsuleTop(feet, eyeHeight, runnerCapsuleHeadMargin, runnerCapsuleRadius);
        for (int i = 0; i < SpheresPerTarget; i++)
        {
            _spheres[slot * SpheresPerTarget + i] = spheres[i];
            _sphereValid[slot * SpheresPerTarget + i] = valid[i];
        }
        _targetValid[slot] = true;
    }

    // The swept region against every target: a new contact is queued, a full release clears it.
    public void Step(Vector3 center, float radius)
    {
        Vector3 from = _sensorPreviousValid ? _sensorPrevious : center;
        for (int s = 0; s < Targets; s++)
        {
            if (_targets[s] <= 0 || !_targetValid[s]) continue;
            if (!_inContact[s])
            {
                int parts = TouchParts(s, from, center, radius, 0f);
                if (parts == 0) continue;
                _inContact[s] = true;
                Contact(s, parts, center);
            }
            else if (TouchParts(s, center, center, radius, releaseMargin) == 0)
            {
                _inContact[s] = false;
                _releases++;
            }
        }
        _sensorPrevious = center;
        _sensorPreviousValid = true;
    }

    // Parts of target slot within reach of the region swept from 'from' to 'to'.
    public int TouchParts(int slot, Vector3 from, Vector3 to, float radius, float margin)
    {
        float r = radius + margin;
        int parts = 0;
        if (ContactRules.SweptSphereTouchesCapsule(from, to, r, _bottom[slot], _top[slot], runnerCapsuleRadius, maxSweepDistance))
            parts |= ContactRules.PartCapsule;
        for (int i = 0; i < SpheresPerTarget; i++)
        {
            int at = slot * SpheresPerTarget + i;
            if (!_sphereValid[at]) continue;
            if (ContactRules.SweptSphereTouchesSphere(from, to, r, _spheres[at], runnerSphereRadius, maxSweepDistance))
                parts |= ContactRules.SpherePart(i);
        }
        return parts;
    }

    private void Contact(int slot, int parts, Vector3 position)
    {
        _contacts++;
        _lastTarget = _targets[slot];
        _lastParts = parts;
        _lastPosition = position;
        if (_queued >= MaxQueued) return;
        _queuedTarget[_queued] = _targets[slot];
        _queuedParts[_queued] = parts;
        _queuedPosition[_queued] = position;
        _queued++;
    }

    // ---- Collector and the local effect (thin wrappers) ----

    private void Flush()
    {
        if (_queued == 0) return;
        PlayerEventCollectorBehaviour collector = LocalCollector();
        for (int i = 0; i < _queued; i++) RecordTag(collector, _queuedTarget[i], _queuedParts[i], _queuedPosition[i]);
        _queued = 0;
    }

    private void RecordTag(PlayerEventCollectorBehaviour collector, int target, int parts, Vector3 position)
    {
        int slot = collector != null ? collector.Record(ContactRules.TypeTagContactEnter, target, ContactRules.NoBase, parts, position, _holdMode, _holdSeq) : -1;
        if (slot < 0)
        {
            _unrecorded++;
            return;
        }
        if (feedback != null)
            feedback.PlayLocal(collector.GetRecordField(slot, ContactRules.RecPlay), collector.GetSourceId(), collector.GetRecordField(slot, ContactRules.RecSeq), position);
    }

    private PlayerEventCollectorBehaviour LocalCollector()
    {
        if (_collector != null) return _collector;
        if (Time.time < _nextCollectorSearch) return null;
        _nextCollectorSearch = Time.time + 2f;
        _collector = ContactLookup.LocalCollector();
        return _collector;
    }
}
