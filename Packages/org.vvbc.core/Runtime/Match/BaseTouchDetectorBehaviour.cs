using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// Base contact of the local player (docs/development/player-event-collector.md). Each client
// detects only its own player; nobody scans other players' bones for bases.
//
// Five regions, combined with OR per base: the VRChat player capsule (the base zones' player
// trigger events) and a sphere on each hand and foot (Humanoid bone positions in PostLateUpdate,
// one fixed radius for everyone, no avatar scaling, tested against the base's oriented box with
// math, no extra colliders). A base is touched while any region touches it; BaseTouchEnter when
// the first region touches, BaseTouchExit when the last one leaves. Regions changing while the
// base stays touched send nothing. Each base is independent. A missing bone disables only its
// sphere. A capsule left inside after a missed exit (respawn, teleport) is cleared when the
// player is clearly away from the box, or at a respawn; a capsule inside whose enter was missed is
// filled in by the zone's trigger stay.
//
// Cost: nothing per frame while the player is away from every base. A poll every pollSeconds
// checks the distance and enables the per-frame sampling near a base (or while touching one).
// Events and the contact bits go to the local PlayerObject's collector; touching never moves a
// runner's awarded base or changes the match.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class BaseTouchDetectorBehaviour : TsukimiBehaviour
{
    [Header("Bases")]
    [Tooltip("Zones of Home, 1B, 2B, 3B (index = base).")]
    public BaseZoneBehaviour[] zones = new BaseZoneBehaviour[ContactRules.BaseCount];

    [Header("Hand and foot spheres")]
    [Tooltip("Radius of each hand and foot sphere, in metres (diameter 10 cm). Default 0.05.")]
    public float sphereRadius = 0.05f;
    [Tooltip("Offset of each sphere from its bone, in the bone's space, in metres. Default zero.")]
    public Vector3 sphereOffset = Vector3.zero;

    [Header("Activation")]
    [Tooltip("Horizontal distance from a base within which the hands and feet are sampled every frame, in metres. Default 4.")]
    public float activationDistance = 4f;
    [Tooltip("Seconds between distance checks while away from every base. Default 0.2.")]
    public float pollSeconds = 0.2f;
    [Tooltip("A capsule still reported inside is cleared once the player is this far outside the box horizontally, in metres. Default 1.")]
    public float capsuleReleaseMargin = 1f;

    private readonly Vector3[] _center = new Vector3[ContactRules.BaseCount];
    private readonly Quaternion[] _rotation = new Quaternion[ContactRules.BaseCount];
    private readonly Vector3[] _half = new Vector3[ContactRules.BaseCount];
    private readonly bool[] _hasBase = new bool[ContactRules.BaseCount];
    private readonly bool[] _capsule = new bool[ContactRules.BaseCount];
    private readonly int[] _parts = new int[ContactRules.BaseCount];
    private readonly Vector3[] _spheres = new Vector3[ContactRules.SphereCount];
    private readonly bool[] _sphereValid = new bool[ContactRules.SphereCount];
    private int _touchMask;
    private int _publishedMask;
    // Transitions of the last Evaluate, handed to the collector after it (Flush).
    private const int MaxQueued = ContactRules.BaseCount * 2;
    private readonly int[] _queuedType = new int[MaxQueued];
    private readonly int[] _queuedBase = new int[MaxQueued];
    private readonly int[] _queuedParts = new int[MaxQueued];
    private readonly Vector3[] _queuedPosition = new Vector3[MaxQueued];
    private int _queued;
    private PlayerEventCollectorBehaviour _collector;
    private float _nextCollectorSearch;
    private bool _geometryCached;

    // Diagnostics.
    private readonly int[] _enters = new int[ContactRules.BaseCount];
    private readonly int[] _exits = new int[ContactRules.BaseCount];
    private int _lastEventType;
    private int _lastEventBase = ContactRules.NoBase;
    private int _lastEventParts;
    private Vector3 _lastEventPosition;
    private int _unrecorded;
    private int _missedCapsuleExits;
    private int _samples;

    void Start()
    {
        CacheGeometry();
        enabled = false;
        SendCustomEventDelayedSeconds(nameof(_Poll), Mathf.Max(0.05f, pollSeconds));
    }

    public int GetTouchMask() { return _touchMask; }
    public int GetParts(int baseIndex) { return ContactRules.ValidBase(baseIndex) ? _parts[baseIndex] : 0; }
    public bool GetCapsule(int baseIndex) { return ContactRules.ValidBase(baseIndex) && _capsule[baseIndex]; }
    public int GetEnterCount(int baseIndex) { return ContactRules.ValidBase(baseIndex) ? _enters[baseIndex] : 0; }
    public int GetExitCount(int baseIndex) { return ContactRules.ValidBase(baseIndex) ? _exits[baseIndex] : 0; }
    public int GetLastEventType() { return _lastEventType; }
    public int GetLastEventBase() { return _lastEventBase; }
    public int GetLastEventParts() { return _lastEventParts; }
    public Vector3 GetLastEventPosition() { return _lastEventPosition; }
    public int GetUnrecordedCount() { return _unrecorded; }
    public int GetMissedCapsuleExitCount() { return _missedCapsuleExits; }
    public int GetSampleCount() { return _samples; }

    // Bases do not move during a match: their boxes are read once (and again on request).
    public void CacheGeometry()
    {
        _geometryCached = true;
        for (int b = 0; b < ContactRules.BaseCount; b++)
        {
            BaseZoneBehaviour zone = zones != null && b < zones.Length ? zones[b] : null;
            if (zone == null)
            {
                _hasBase[b] = false;
                continue;
            }
            SetBaseGeometry(b, zone.GetBoxCenter(), zone.GetBoxRotation(), zone.GetBoxHalfExtents());
        }
    }

    public void SetBaseGeometry(int baseIndex, Vector3 center, Quaternion rotation, Vector3 halfExtents)
    {
        if (!ContactRules.ValidBase(baseIndex)) return;
        _center[baseIndex] = center;
        _rotation[baseIndex] = rotation;
        _half[baseIndex] = halfExtents;
        _hasBase[baseIndex] = halfExtents.x > 0f && halfExtents.y > 0f && halfExtents.z > 0f;
    }

    // ---- Capsule (base zones; local player only) ----

    public void SetCapsule(int baseIndex, bool inside)
    {
        if (SetCapsuleFlag(baseIndex, inside) && inside) enabled = true;
    }

    public bool SetCapsuleFlag(int baseIndex, bool inside)
    {
        if (!ContactRules.ValidBase(baseIndex)) return false;
        _capsule[baseIndex] = inside;
        return true;
    }

    public override void OnPlayerRespawn(VRCPlayerApi player)
    {
        if (!Utilities.IsValid(player) || !player.isLocal) return;
        for (int b = 0; b < ContactRules.BaseCount; b++) _capsule[b] = false;
        enabled = true;
    }

    // ---- Activation ----

    public void _Poll()
    {
        SendCustomEventDelayedSeconds(nameof(_Poll), Mathf.Max(0.05f, pollSeconds));
        if (enabled) return;
        VRCPlayerApi local = Networking.LocalPlayer;
        if (!Utilities.IsValid(local)) return;
        if (ShouldTrack(local.GetPosition())) enabled = true;
    }

    public bool ShouldTrack(Vector3 playerPosition)
    {
        if (_touchMask != 0) return true;
        for (int b = 0; b < ContactRules.BaseCount; b++)
        {
            if (_capsule[b]) return true;
            if (!_hasBase[b]) continue;
            float dx = playerPosition.x - _center[b].x;
            float dz = playerPosition.z - _center[b].z;
            if (dx * dx + dz * dz <= activationDistance * activationDistance) return true;
        }
        return false;
    }

    // ---- Per frame while near a base ----

    public override void PostLateUpdate()
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        if (!Utilities.IsValid(local)) return;
        if (!_geometryCached) CacheGeometry();
        _samples++;
        for (int i = 0; i < ContactRules.SphereCount; i++)
        {
            HumanBodyBones bone = ContactRules.SphereBone(i);
            Vector3 p = local.GetBonePosition(bone);
            bool valid = ContactRules.ValidPoint(p);
            if (valid && sphereOffset != Vector3.zero) p += local.GetBoneRotation(bone) * sphereOffset;
            _spheres[i] = p;
            _sphereValid[i] = valid;
        }
        Vector3 position = local.GetPosition();
        Evaluate(position);
        Flush();
        if (_touchMask == 0 && !ShouldTrack(position)) enabled = false;
    }

    public void SetSphere(int sphere, Vector3 position, bool valid)
    {
        if (sphere < 0 || sphere >= ContactRules.SphereCount) return;
        _spheres[sphere] = position;
        _sphereValid[sphere] = valid;
    }

    // Region masks of every base from the current spheres and capsule flags; enter and exit on the
    // OR of them. Returns the new contact bits.
    public int Evaluate(Vector3 playerPosition)
    {
        int mask = 0;
        for (int b = 0; b < ContactRules.BaseCount; b++)
        {
            int parts = 0;
            Vector3 cause = playerPosition;
            if (_hasBase[b])
            {
                if (_capsule[b] && AwayFromBox(b, playerPosition))
                {
                    _capsule[b] = false;
                    _missedCapsuleExits++;
                }
                if (_capsule[b]) parts |= ContactRules.PartCapsule;
                for (int i = ContactRules.SphereCount - 1; i >= 0; i--)
                {
                    if (!_sphereValid[i]) continue;
                    if (!ContactRules.SphereTouchesBox(_spheres[i], sphereRadius, _center[b], _rotation[b], _half[b])) continue;
                    parts |= ContactRules.SpherePart(i);
                    cause = _spheres[i];
                }
            }
            bool was = _parts[b] != 0;
            _parts[b] = parts;
            if (parts != 0) mask |= 1 << b;
            if (!was && parts != 0) Transition(ContactRules.TypeBaseTouchEnter, b, parts, cause);
            else if (was && parts == 0) Transition(ContactRules.TypeBaseTouchExit, b, 0, playerPosition);
        }
        _touchMask = mask;
        return mask;
    }

    private bool AwayFromBox(int b, Vector3 playerPosition)
    {
        Vector3 half = _half[b];
        float reach = Mathf.Sqrt(half.x * half.x + half.z * half.z) + capsuleReleaseMargin;
        float dx = playerPosition.x - _center[b].x;
        float dz = playerPosition.z - _center[b].z;
        return dx * dx + dz * dz > reach * reach;
    }

    private void Transition(int type, int baseIndex, int parts, Vector3 position)
    {
        if (type == ContactRules.TypeBaseTouchEnter) _enters[baseIndex]++;
        else _exits[baseIndex]++;
        _lastEventType = type;
        _lastEventBase = baseIndex;
        _lastEventParts = parts;
        _lastEventPosition = position;
        if (_queued >= MaxQueued) return;
        _queuedType[_queued] = type;
        _queuedBase[_queued] = baseIndex;
        _queuedParts[_queued] = parts;
        _queuedPosition[_queued] = position;
        _queued++;
    }

    public int GetQueuedCount() { return _queued; }

    // ---- Collector (thin wrappers; the local PlayerObject) ----

    // The queued transitions in detection order, then the contact bits when they changed.
    private void Flush()
    {
        if (_queued == 0 && _touchMask == _publishedMask) return;
        PlayerEventCollectorBehaviour collector = LocalCollector();
        if (collector == null)
        {
            _unrecorded += _queued;
            _queued = 0;
            return;
        }
        for (int i = 0; i < _queued; i++)
            if (collector.Record(_queuedType[i], 0, _queuedBase[i], _queuedParts[i], _queuedPosition[i], ContactRules.HoldFree, 0, 0) < 0) _unrecorded++;
        _queued = 0;
        if (_touchMask != _publishedMask)
        {
            _publishedMask = _touchMask;
            collector.SetTouchMask(_touchMask);
        }
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
