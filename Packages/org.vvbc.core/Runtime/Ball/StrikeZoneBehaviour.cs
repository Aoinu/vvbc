using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

// The origin is the rear tip of the plate; forward points toward the pitcher.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class StrikeZoneBehaviour : TsukimiBehaviour
{
    // Legacy standalone prefabs can retain their original origin; HomePlate uses its root.
    [HideInInspector] public Transform plateOrigin;
    [Header("Home Plate")]
    [Tooltip("The physical Ball assigned to this HomePlate.")]
    public BallBehaviour ball;
    [Tooltip("Optional strike zone outline refreshed when the effective zone may change.")]
    public StrikeZoneFrameBehaviour frame;
    [Tooltip("Plate depth in metres, from the rear tip toward the pitcher.")]
    public float plateDepth = 0.4318f;
    [Header("Strike Zone")]
    [Tooltip("Zone width in metres.")]
    public float width = 0.4318f;
    // MLB's 2026 ABS proportions for a 1.80 m batter: 27% to 53.5% of height.
    [Tooltip("Height of the bottom edge above the plate origin, in metres.")] public float bottom = 0.486f;
    [Tooltip("Height of the top edge above the plate origin, in metres.")] public float top = 0.963f;
    [Tooltip("Horizontal and vertical offset in metres, in HomePlate coordinates.")]
    public Vector2 zoneCenterOffset;

    [Header("Batter Occupancy")]
    public bool useBatterOccupancy;
    public UdonBehaviour contactListener;
    [Header("Occupant Only Objects")]
    public GameObject[] occupantOnlyObjects;
    [Header("Batter Height")]
    public float eyeToHeightOffset = 0.10f;
    public float bottomHeightRatio = 0.270f;
    public float topHeightRatio = 0.535f;
    public float defaultBatterHeight = 1.80f;
    [UdonSynced] public int occupantPlayerId = -1;
    [UdonSynced] public float syncedZoneBottom;
    [UdonSynced] public float syncedZoneTop;

    // These values are copied at release, so a later adjustment does not change a pitch.
    public Vector3 snapshotOrigin;
    public Vector3 snapshotForward;
    public Vector3 snapshotRight;
    public float snapshotDepth;
    public float snapshotWidth;
    public float snapshotBottom;
    public float snapshotTop;

    private bool _insideLeft;
    private bool _insideRight;
    private bool _claimPending;
    private bool _ownershipLost;
    private bool _confirmedLocal;

    public void Start()
    {
        UpdateLocalAccess();
    }

    public bool IsLocalOccupant()
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        return useBatterOccupancy && !_ownershipLost && Utilities.IsValid(local) && (_insideLeft || _insideRight) &&
            occupantPlayerId == local.playerId && Networking.IsOwner(gameObject);
    }

    // Called only for the local player's trigger events by the two boxes.
    public void SetBatterBoxInside(bool left, bool inside)
    {
        if (left) _insideLeft = inside; else _insideRight = inside;
        VRCPlayerApi local = Networking.LocalPlayer;
        if (!Utilities.IsValid(local)) return;
        if (inside)
        {
            if (occupantPlayerId < 0)
            {
                _claimPending = true;
                if (!Networking.IsOwner(gameObject)) Networking.SetOwner(local, gameObject);
                if (Networking.IsOwner(gameObject)) CompleteClaim();
            }
        }
        else if (!_insideLeft && !_insideRight)
        {
            _claimPending = false;
            if (Networking.IsOwner(gameObject) && occupantPlayerId == local.playerId) ClearOccupant();
        }
        UpdateLocalAccess();
    }

    private void CompleteClaim()
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        if (!_claimPending || _ownershipLost || !Utilities.IsValid(local) ||
            !Networking.IsOwner(gameObject) || !(_insideLeft || _insideRight)) return;
        _claimPending = false;
        _confirmedLocal = true;
        occupantPlayerId = local.playerId;
        if (!SetEyeHeight(local.GetAvatarEyeHeightAsMeters()) && !SetHeight(defaultBatterHeight))
        { syncedZoneBottom = 0f; syncedZoneTop = 0f; }
        RequestSerialization();
        UpdateLocalAccess();
    }

    private void ClearOccupant()
    {
        if (_ownershipLost || !Networking.IsOwner(gameObject)) return;
        _confirmedLocal = false;
        occupantPlayerId = -1;
        if (!SetHeight(defaultBatterHeight)) { syncedZoneBottom = 0f; syncedZoneTop = 0f; }
        RequestSerialization();
        UpdateLocalAccess();
    }

    public override bool OnOwnershipRequest(VRCPlayerApi requestingPlayer, VRCPlayerApi requestedOwner)
    {
        return occupantPlayerId < 0 || (Utilities.IsValid(requestingPlayer) && Utilities.IsValid(requestedOwner) &&
            requestingPlayer.playerId == occupantPlayerId && requestedOwner.playerId == occupantPlayerId);
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        _ownershipLost = !Utilities.IsValid(player) || !player.isLocal;
        // Keep an entry request until the player exits. Competing ownership
        // callbacks and occupant updates can report another contender before
        // reporting this client as the final owner. Players who entered an
        // already occupied plate never make a request in the first place.
        if (_ownershipLost)
        {
            if (_confirmedLocal && (_insideLeft || _insideRight)) _claimPending = true;
            _confirmedLocal = false;
        }
        UpdateLocalAccess();
        if (!_ownershipLost) SendCustomEventDelayedFrames(nameof(RepairOwnership), 1);
        RepairOwnership();
    }

    public void RepairOwnership()
    {
        if (_ownershipLost) return;
        if (!Networking.IsOwner(gameObject)) return;
        if (_claimPending && (_insideLeft || _insideRight)) CompleteClaim();
        else if (occupantPlayerId >= 0 && !IsLocalOccupant()) ClearOccupant();
    }

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        // The departing player's API may already be invalid. Recheck the
        // synchronized ID after the player list settles as well as now.
        RepairDepartedOccupant();
        SendCustomEventDelayedFrames(nameof(RepairDepartedOccupant), 1);
    }

    public void RepairDepartedOccupant()
    {
        if (occupantPlayerId >= 0 && Networking.IsOwner(gameObject) &&
            !Utilities.IsValid(VRCPlayerApi.GetPlayerById(occupantPlayerId))) ClearOccupant();
    }

    public override void OnDeserialization()
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        if (Utilities.IsValid(local) && Networking.IsOwner(gameObject) && !_ownershipLost && _confirmedLocal &&
            (_insideLeft || _insideRight) && occupantPlayerId != local.playerId)
        {
            _claimPending = true;
            CompleteClaim();
            return;
        }
        UpdateLocalAccess();
        if (Networking.IsOwner(gameObject)) RepairOwnership();
    }

    public override void OnAvatarChanged(VRCPlayerApi player) { RefreshLocalHeight(player); }
    public override void OnAvatarEyeHeightChanged(VRCPlayerApi player, float previousHeight) { RefreshLocalHeight(player); }

    private void RefreshLocalHeight(VRCPlayerApi player)
    {
        if (!Utilities.IsValid(player) || !player.isLocal || !IsLocalOccupant()) return;
        float oldBottom = syncedZoneBottom;
        float oldTop = syncedZoneTop;
        if (!SetEyeHeight(player.GetAvatarEyeHeightAsMeters())) return;
        if (oldBottom != syncedZoneBottom || oldTop != syncedZoneTop)
        {
            RequestSerialization();
            if (frame != null) frame.Refresh();
        }
    }

    private bool SetHeight(float height)
    {
        if (float.IsNaN(height) || height <= 0f || height > float.MaxValue) return false;
        float lower = height * bottomHeightRatio;
        float upper = height * topHeightRatio;
        if (!ValidHeight(lower, upper)) return false;
        syncedZoneBottom = lower;
        syncedZoneTop = upper;
        return true;
    }

    private bool SetEyeHeight(float eyeHeight)
    {
        if (float.IsNaN(eyeHeight) || eyeHeight <= 0f || eyeHeight > float.MaxValue) return false;
        return SetHeight(eyeHeight + eyeToHeightOffset);
    }

    private bool ValidHeight(float lower, float upper)
    {
        return !float.IsNaN(lower) && !float.IsNaN(upper) && lower >= 0f &&
            upper <= float.MaxValue && upper > lower;
    }

    public float EffectiveBottom()
    {
        if (!useBatterOccupancy) return bottom;
        if (occupantPlayerId >= 0 && ValidHeight(syncedZoneBottom, syncedZoneTop)) return syncedZoneBottom;
        float lower = defaultBatterHeight * bottomHeightRatio;
        float upper = defaultBatterHeight * topHeightRatio;
        return ValidHeight(lower, upper) ? lower : 0.486f;
    }

    public float EffectiveTop()
    {
        if (!useBatterOccupancy) return top;
        if (occupantPlayerId >= 0 && ValidHeight(syncedZoneBottom, syncedZoneTop)) return syncedZoneTop;
        float lower = defaultBatterHeight * bottomHeightRatio;
        float upper = defaultBatterHeight * topHeightRatio;
        return ValidHeight(lower, upper) ? upper : 0.963f;
    }

    private void UpdateLocalAccess()
    {
        bool active = IsLocalOccupant();
        if (occupantOnlyObjects != null)
            for (int i = 0; i < occupantOnlyObjects.Length; i++)
                if (occupantOnlyObjects[i] != null) occupantOnlyObjects[i].SetActive(active);
        if (contactListener != null) contactListener.SendCustomEvent("OnOccupancyChanged");
        if (frame != null) frame.Refresh();
    }

    public void Snapshot()
    {
        Transform plate = PlateTransform();
        snapshotForward = StrikeZoneGeometry.Forward(plate.forward);
        snapshotRight = StrikeZoneGeometry.Right(snapshotForward);
        snapshotOrigin = plate.position + snapshotRight * zoneCenterOffset.x;
        snapshotDepth = plateDepth;
        snapshotWidth = width;
        snapshotBottom = EffectiveBottom() + zoneCenterOffset.y;
        snapshotTop = EffectiveTop() + zoneCenterOffset.y;
    }

    public Vector3 AimPoint(int column, int row)
    {
        Transform plate = PlateTransform();
        return StrikeZoneGeometry.AimPoint(plate.position, plate.forward, plateDepth,
            width, EffectiveBottom(), EffectiveTop(), zoneCenterOffset, column, row);
    }

    public Transform PlateTransform()
    {
        return plateOrigin != null ? plateOrigin : transform;
    }

    public bool ContainsBall(Vector3 position, float radius)
    {
        return StrikeZoneGeometry.ContainsBall(position, radius, snapshotOrigin, snapshotRight,
            snapshotWidth, snapshotBottom, snapshotTop);
    }
}
