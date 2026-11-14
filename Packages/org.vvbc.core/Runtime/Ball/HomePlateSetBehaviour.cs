using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;

// One lane (HomePlateSet). Syncs only the lane mode. In Local Mode every client suspends the
// SharedRig, and each player may practise on a LocalRig instantiated on their own client only.
// See docs/development/home-plate-set.md.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class HomePlateSetBehaviour : TsukimiBehaviour
{
    [Tooltip("Allow players to switch this lane between Shared and Local Mode. Off: Shared Mode only.")]
    public bool allowModeSwitching;
    [Header("Lane (wired inside the prefab)")]
    public BattingPitcherBehaviour sharedPitcher;
    public StrikeZoneBehaviour sharedHomePlate;
    public PitchSettingsBehaviour settingsPage;
    [Tooltip("Prefab asset instantiated on this client for Local practice.")]
    public GameObject localRigPrefab;
    [Tooltip("Parent and pose of the LocalRig.")]
    public Transform localRigAnchor;
    public float serializationRetrySeconds = 1f;

    [UdonSynced] private bool _localMode;

    private bool _started;
    private bool _appliedLocal;
    private bool _changing;
    private GameObject _localRig;
    private BattingPitcherBehaviour _localPitcher;
    // Diagnostics: LocalRigs created and destroyed on this client.
    public int localRigsCreated;
    public int localRigsDestroyed;

    private void Start()
    {
        _started = true;
        ApplyMode();
    }

    // ---- Queries ----

    public bool IsLocalMode() { return allowModeSwitching && _localMode; }
    public bool HasLocalRig() { return _localRig != null; }
    public BattingPitcherBehaviour LocalPitcher() { return _localPitcher; }

    // The pitcher the lane UI edits now: the LocalRig's in Local Mode (none before Join).
    public BattingPitcherBehaviour ActivePitcher()
    {
        return IsLocalMode() ? _localPitcher : sharedPitcher;
    }

    // ---- Requests (any player) ----

    // Shared -> Local without confirmation; the requester joins at once.
    public void RequestLocalMode()
    {
        if (!allowModeSwitching) return;
        if (!_localMode)
        {
            if (!SetMode(true)) return;
            // The shared pitcher waits Stopped for the lane's return; its playlist is kept.
            if (sharedPitcher != null) sharedPitcher.RequestRunning(false);
        }
        JoinPractice();
    }

    // Local -> Shared. The UI asks for confirmation first; every LocalRig ends on its own client.
    public void RequestSharedMode()
    {
        if (!allowModeSwitching || !_localMode) return;
        SetMode(false);
    }

    private bool SetMode(bool local)
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        if (player == null) return false;
        if (!Networking.IsOwner(player, gameObject)) Networking.SetOwner(player, gameObject);
        _localMode = local;
        RequestSerialization();
        ApplyMode();
        return true;
    }

    public override void OnDeserialization()
    {
        ApplyMode();
        // Two players switching at once: the Owner re-sends what it now holds, so every client
        // ends on the Owner's value.
        VRCPlayerApi player = Networking.LocalPlayer;
        if (player != null && Networking.IsOwner(player, gameObject)) RequestSerialization();
    }

    public override void OnPostSerialization(SerializationResult result)
    {
        if (!result.success) SendCustomEventDelayedSeconds(nameof(_RetrySerialization), Mathf.Max(0.1f, serializationRetrySeconds));
    }

    public void _RetrySerialization()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        if (player != null && Networking.IsOwner(player, gameObject)) RequestSerialization();
    }

    // Brings this client to the synced mode. Runs again harmlessly for the same mode.
    private void ApplyMode()
    {
        if (!_started) return;
        bool local = IsLocalMode();
        if (local != _appliedLocal)
        {
            _appliedLocal = local;
            if (local)
            {
                if (sharedPitcher != null) sharedPitcher.Suspend();
            }
            else
            {
                EndPractice();
                if (sharedPitcher != null) sharedPitcher.Resume();
            }
        }
        RefreshPage();
    }

    // ---- LocalRig lifecycle (this client only) ----

    public void JoinPractice()
    {
        if (!IsLocalMode() || _localRig != null || _changing || localRigPrefab == null) return;
        _changing = true;
        GameObject rig = Instantiate(localRigPrefab);
        Transform anchor = localRigAnchor != null ? localRigAnchor : transform;
        rig.transform.SetParent(anchor, false);
        rig.transform.localPosition = Vector3.zero;
        rig.transform.localRotation = Quaternion.identity;
        rig.transform.localScale = Vector3.one;
        BattingPitcherBehaviour pitcher = rig.GetComponentInChildren<BattingPitcherBehaviour>(true);
        if (pitcher == null)
        {
            Debug.LogError("HomePlateSet: LocalRig has no BattingPitcherBehaviour");
            Destroy(rig);
            _changing = false;
            return;
        }
        // The flags are baked in the LocalRig prefab; set again before their Start runs.
        pitcher.localOnly = true;
        BallBehaviour[] balls = rig.GetComponentsInChildren<BallBehaviour>(true);
        for (int i = 0; i < balls.Length; i++) balls[i].localOnly = true;
        StrikeZoneBehaviour[] zones = rig.GetComponentsInChildren<StrikeZoneBehaviour>(true);
        for (int i = 0; i < zones.Length; i++) zones[i].localOnly = true;
        _localRig = rig;
        _localPitcher = pitcher;
        localRigsCreated++;
        if (sharedPitcher != null) sharedPitcher.SetVisualHidden(true);
        if (sharedHomePlate != null) sharedHomePlate.SetOccupantObjectsForced(true);
        _changing = false;
        RefreshPage();
    }

    // Ends only this client's LocalRig; the lane mode is unchanged.
    public void EndPractice()
    {
        if (_localRig == null || _changing) return;
        _changing = true;
        GameObject rig = _localRig;
        BattingPitcherBehaviour pitcher = _localPitcher;
        // 1. No new pitch or contact; the motion, flight and events stop.
        if (pitcher != null) pitcher.Suspend();
        // 2. The PlayerObject Bat forgets this rig's contact listeners (BatContactBehaviour.Shutdown).
        StrikeZoneBehaviour[] zones = rig.GetComponentsInChildren<StrikeZoneBehaviour>(true);
        for (int i = 0; i < zones.Length; i++)
            if (zones[i].contactListener != null) zones[i].contactListener.SendCustomEvent("Shutdown");
        // 3. The UI leaves the LocalRig's pitcher.
        _localRig = null;
        _localPitcher = null;
        RefreshPage();
        if (sharedPitcher != null) sharedPitcher.SetVisualHidden(false);
        if (sharedHomePlate != null) sharedHomePlate.SetOccupantObjectsForced(false);
        // 4. Destroy; nothing here holds a reference to it any more.
        Destroy(rig);
        localRigsDestroyed++;
        _changing = false;
    }

    private void RefreshPage()
    {
        if (settingsPage != null) settingsPage._OnLaneChanged();
    }
}
