using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;

// One lane (HomePlateSet). Syncs the lane's batting machine state (Hidden / Off / On) and the
// lane mode. In Local Mode every client suspends the SharedRig, and each player may practise on
// a LocalRig instantiated on their own client only. See docs/development/home-plate-set.md.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class HomePlateSetBehaviour : TsukimiBehaviour
{
    [Tooltip("Allow players to switch this lane between Shared and Local Mode. Off: Shared Mode only.")]
    public bool allowModeSwitching;
    [Header("Lane (wired inside the prefab)")]
    public BattingPitcherBehaviour sharedPitcher;
    public StrikeZoneBehaviour sharedHomePlate;
    public PitchSettingsBehaviour settingsPage;
    [Tooltip("Canvas of the lane's PitchSettingsPanel: drawn only while the machine is shown.")]
    public Canvas settingsCanvas;
    [Tooltip("Pointer collider of the PitchSettingsPanel: enabled only while the machine is shown.")]
    public Collider settingsCollider;
    [Tooltip("The lane's SHOW / HIDE switch for the batting machine.")]
    public MachineVisibilitySwitchBehaviour visibilitySwitch;
    [Tooltip("Prefab asset instantiated on this client for Local practice.")]
    public GameObject localRigPrefab;
    [Tooltip("Parent and pose of the LocalRig.")]
    public Transform localRigAnchor;
    public float serializationRetrySeconds = 1f;

    // Batting machine of the SharedRig. Manual pitching on the mound is never affected.
    public const int MachineHidden = 0;
    public const int MachineOff = 1;
    public const int MachineOn = 2;

    [UdonSynced] private int _machineState = MachineHidden;
    // Local Mode exists only while the machine is shown; Hidden always ends it.
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

    // An unknown synced value counts as Hidden.
    public int MachineState()
    {
        return _machineState == MachineOff || _machineState == MachineOn ? _machineState : MachineHidden;
    }

    public bool IsMachineShown() { return MachineState() != MachineHidden; }
    public bool IsLocalMode() { return allowModeSwitching && _localMode && IsMachineShown(); }
    public bool HasLocalRig() { return _localRig != null; }
    public BattingPitcherBehaviour LocalPitcher() { return _localPitcher; }

    // The pitcher the lane UI edits now: the LocalRig's in Local Mode (none before Join).
    public BattingPitcherBehaviour ActivePitcher()
    {
        return IsLocalMode() ? _localPitcher : sharedPitcher;
    }

    // ---- Requests (any player) ----

    // The visibility switch: Hidden -> Off (shown, Shared, stopped); Off / On / Local -> Hidden.
    public void ToggleMachineVisibility()
    {
        if (IsMachineShown())
        {
            if (!SetState(MachineHidden, false)) return;
            // The shared pitcher waits Stopped; its playlist and settings are kept.
            if (sharedPitcher != null) sharedPitcher.RequestRunning(false);
        }
        else SetState(MachineOff, false);
    }

    // START / STOP of the lane page. Shared: the lane state and the shared pitcher's Running flag
    // change together. Local: only this client's LocalRig pitcher. Refused while hidden.
    public void RequestMachineRunning(bool running)
    {
        if (!IsMachineShown()) return;
        if (IsLocalMode())
        {
            if (_localPitcher != null) _localPitcher.RequestRunning(running);
            return;
        }
        if (!SetState(running ? MachineOn : MachineOff, false)) return;
        if (sharedPitcher != null) sharedPitcher.RequestRunning(running);
    }

    // Shared -> Local without confirmation; the requester joins at once. The shared machine is Off.
    public void RequestLocalMode()
    {
        if (!allowModeSwitching || !IsMachineShown()) return;
        if (!_localMode)
        {
            if (!SetState(MachineOff, true)) return;
            // The shared pitcher waits Stopped for the lane's return; its playlist is kept.
            if (sharedPitcher != null) sharedPitcher.RequestRunning(false);
        }
        JoinPractice();
    }

    // Local -> Shared. The UI asks for confirmation first; every LocalRig ends on its own client.
    public void RequestSharedMode()
    {
        if (!IsLocalMode()) return;
        SetState(MachineState(), false);
    }

    // The only writer of the synced lane state: both values change in one serialization.
    private bool SetState(int state, bool local)
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        if (player == null) return false;
        if (!Networking.IsOwner(player, gameObject)) Networking.SetOwner(player, gameObject);
        _machineState = state == MachineOff || state == MachineOn ? state : MachineHidden;
        _localMode = local && _machineState != MachineHidden;
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

    // Brings this client to the synced state. Runs again harmlessly for the same state. The shared
    // pitcher is stopped before anything resumes it, so no transition pitches on the way.
    private void ApplyMode()
    {
        if (!_started) return;
        int state = MachineState();
        bool local = IsLocalMode();
        if (sharedPitcher != null) sharedPitcher.SetLaneStopped(local || state != MachineOn);
        if (local != _appliedLocal)
        {
            _appliedLocal = local;
            if (local)
            {
                if (sharedPitcher != null) sharedPitcher.Suspend();
            }
            else EndPractice();
        }
        if (!local && sharedPitcher != null)
        {
            // Hidden never stages the shared Ball; a flight in progress continues.
            if (state == MachineHidden) sharedPitcher.Halt();
            else sharedPitcher.Resume();
        }
        RefreshVisuals();
        RefreshPage();
    }

    // The only place that shows or hides the shared machine model, the lane page and the switch.
    private void RefreshVisuals()
    {
        bool shown = IsMachineShown();
        if (sharedPitcher != null) sharedPitcher.SetVisualHidden(!shown || _localRig != null);
        if (settingsCanvas != null) settingsCanvas.enabled = shown;
        if (settingsCollider != null) settingsCollider.enabled = shown;
        if (visibilitySwitch != null) visibilitySwitch._OnMachineStateChanged();
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
        RefreshVisuals();
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
        RefreshVisuals();
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
