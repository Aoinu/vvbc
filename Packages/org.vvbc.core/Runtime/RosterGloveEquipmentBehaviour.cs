using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

// Equips the local player's glove from the match roster (docs/development/roster-glove-equipment.md).
// It sits in Runtime/ because it is the only place that sees both Match/ and Ball/ (T# sees the own
// folder and below). One per scene; every client runs it for its own player only and changes only
// its own PlayerObject's glove (the glove's existing synced `equipped` reaches the others). Nobody
// takes the Ownership of anyone's glove.
//
// While a match is in progress (MatchRules.PhaseInProgress), the glove is worn exactly by the
// defense's fielders P, C, 1B .. RF; the DH, the offense and players not in the roster go without.
// The defense is MatchController.GetDefenseTeam(): the half changes only at the commit that changes
// sides, never at the third out alone. While live the role comes from the play's roster snapshot
// (GetSnapshotRoleSlot), while dead from the RosterManager's current registration. The batting
// order plays no part. Before the match and after it the locker's manual equipping is back; the
// glove is left as it is. Nothing is changed while the match state or the roster is not known yet
// (not received, recovering): it is decided again once it is.
//
// Unequipping waits while the ball is in the glove or calibration is open (the glove's own safety
// rules) and happens when they end; the ball is never dropped or taken. Calibration is allowed for
// a fielder of the defense while the ball is dead and refused otherwise during a match; saved
// fittings are never touched. Runs only on _OnMatchChanged / _OnRosterChanged (and a few retries
// while the PlayerObject is not there yet); nothing runs per frame. Tags still require the roster
// snapshot (TagContactDetector), never just a worn glove.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class RosterGloveEquipmentBehaviour : TsukimiBehaviour
{
    // Decisions (DecideAt).
    public const int DutyUnknown = 0;        // the state is not known yet: change nothing
    public const int DutyManual = 1;         // no match in progress: manual equipping
    public const int DutyDefense = 2;
    public const int DutyOffense = 3;
    public const int DutyDesignatedHitter = 4;
    public const int DutyNotRegistered = 5;

    [Tooltip("The scene's MatchController (MatchManager).")]
    public MatchControllerBehaviour match;
    [Tooltip("The scene's RosterManager. Empty = the controller's roster reference.")]
    public RosterManagerBehaviour roster;
    [Tooltip("Retries, one second apart, while the local PlayerObject's glove is not available yet. Default 30.")]
    public int gloveSearchRetries = 30;

    private GloveBehaviour _glove;
    private bool _registered;
    private int _retriesLeft;
    private bool _retryScheduled;
    private int _lastDuty = -1;
    private int _evaluations;

    void Start()
    {
        _retriesLeft = gloveSearchRetries;
        Register();
        Evaluate();
    }

    private void Register()
    {
        if (_registered) return;
        if (roster == null && match != null) roster = match.roster;
        UdonBehaviour self = (UdonBehaviour)GetComponent(typeof(UdonBehaviour));
        if (match != null) match.AddChangeListener(self);
        if (roster != null) roster.AddChangeListener(self);
        _registered = true;
    }

    public void _OnMatchChanged() { Evaluate(); }
    public void _OnRosterChanged() { Evaluate(); }

    public void _RetryGlove()
    {
        _retryScheduled = false;
        Evaluate();
    }

    public int GetLastDuty() { return _lastDuty; }
    public int GetEvaluationCount() { return _evaluations; }

    // Reads the state, decides and hands the decision to the local glove.
    private void Evaluate()
    {
        _evaluations++;
        GloveBehaviour glove = LocalGlove();
        if (glove == null)
        {
            if (!_retryScheduled && _retriesLeft-- > 0)
            {
                _retryScheduled = true;
                SendCustomEventDelayedSeconds(nameof(_RetryGlove), 1f);
            }
            return;
        }
        VRCPlayerApi local = Networking.LocalPlayer;
        int localId = Utilities.IsValid(local) ? local.playerId : 0;
        int duty = Decide(localId);
        _lastDuty = duty;
        Apply(glove, duty, match != null && match.IsLive());
    }

    private int Decide(int localId)
    {
        if (match == null || localId <= 0) return DutyUnknown;
        bool ready = match.IsMatchReady();
        if (!ready) return DutyUnknown;
        int phase = match.GetPhase();
        bool live = match.IsLive();
        int defense = match.GetDefenseTeam();
        int snapshotSlot = live ? match.GetSnapshotRoleSlot(localId) : -1;
        bool rosterReady = roster != null && roster.IsRosterReady();
        int team = rosterReady ? roster.GetTeamOfPlayer(localId) : RosterRules.NotRegistered;
        int role = rosterReady ? roster.GetRoleOfPlayer(localId) : RosterRules.NotRegistered;
        return DecideAt(ready, phase, live, defense, snapshotSlot, rosterReady, team, role);
    }

    // The rule, with every answer passed in (tests call this).
    public int DecideAt(bool matchReady, int phase, bool live, int defenseTeam, int snapshotRoleSlot, bool rosterReady, int rosterTeam, int rosterRole)
    {
        if (!matchReady) return DutyUnknown;
        if (phase != MatchRules.PhaseInProgress) return DutyManual;
        if (!RosterRules.ValidTeam(defenseTeam)) return DutyUnknown;
        int team;
        int role;
        if (live)
        {
            if (snapshotRoleSlot < 0) return DutyNotRegistered;
            team = snapshotRoleSlot / RosterRules.RoleCount;
            role = snapshotRoleSlot % RosterRules.RoleCount;
        }
        else
        {
            if (!rosterReady) return DutyUnknown;
            if (!RosterRules.ValidTeam(rosterTeam) || !RosterRules.ValidRole(rosterRole)) return DutyNotRegistered;
            team = rosterTeam;
            role = rosterRole;
        }
        if (team != defenseTeam) return DutyOffense;
        if (role == RosterRules.RoleDH) return DutyDesignatedHitter;
        return DutyDefense;
    }

    // Calibration is allowed during a match only for a fielder while the ball is dead.
    public static bool CalibrationAllowed(int duty, bool live) { return duty == DutyDefense && !live; }

    public static int ReasonOf(int duty)
    {
        switch (duty)
        {
            case DutyDefense: return GloveBehaviour.AutoReasonDefense;
            case DutyOffense: return GloveBehaviour.AutoReasonOffense;
            case DutyDesignatedHitter: return GloveBehaviour.AutoReasonDesignatedHitter;
            case DutyNotRegistered: return GloveBehaviour.AutoReasonNotRegistered;
        }
        return GloveBehaviour.AutoReasonNone;
    }

    private void Apply(GloveBehaviour glove, int duty, bool live)
    {
        if (duty == DutyUnknown) return;
        if (duty == DutyManual)
        {
            if (glove.IsAutomaticEquipment()) glove.SetAutomaticEquipment(false, false, true, GloveBehaviour.AutoReasonNone);
            return;
        }
        glove.SetAutomaticEquipment(true, duty == DutyDefense, CalibrationAllowed(duty, live), ReasonOf(duty));
    }

    // Exactly one locally owned GloveBehaviour among the local player's PlayerObjects.
    private GloveBehaviour LocalGlove()
    {
        if (_glove != null) return _glove;
        VRCPlayerApi local = Networking.LocalPlayer;
        if (!Utilities.IsValid(local)) return null;
        GameObject[] objects = Networking.GetPlayerObjects(local);
        if (objects == null) return null;
        GloveBehaviour found = null;
        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] == null) continue;
            GloveBehaviour candidate = objects[i].GetComponentInChildren<GloveBehaviour>(true);
            if (candidate == null) continue;
            if (found != null) return null;
            found = candidate;
        }
        if (found == null || !found.IsLocalOwner()) return null;
        _glove = found;
        return found;
    }
}
