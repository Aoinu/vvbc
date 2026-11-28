using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// PlayState: starting live play (roster lock handshake), rulings, Time, dead balls and the
// commit into MatchState. Everything here runs on the Owner, inside one event at a time.
public partial class MatchControllerBehaviour
{
    // Live play being prepared: waiting for the roster lock of _preparePlayId.
    private bool _preparing;
    private int _preparePlayId;
    private float _prepareDeadline;
    private readonly int[] _snapRoles = new int[RosterRules.RoleTableLength];
    private readonly int[] _snapBatters = new int[RosterRules.OrderTableLength];
    // Per runner slot, for the Time conditions.
    private readonly Vector3[] _runnerPositions = new Vector3[RunnerRules.SlotCount];
    private readonly bool[] _runnerKnown = new bool[RunnerRules.SlotCount];
    private readonly Vector3[] _bases = new Vector3[3];
    private readonly bool[] _baseSet = new bool[3];

    // ---- Start of live play ----
    //
    // 1. The Owner moves to preparing and hands out a new play id.
    // 2. It asks the Roster Owner to lock the roster for that play id.
    // 3-4. The Roster Owner stops taking changes and publishes the lock with its revision.
    // 5. On _OnRosterChanged the Owner sees its play id locked and the roster ready.
    // 6. It takes the roster snapshot, 7. begins the PlayState, 8. publishes live play.
    // A lock that does not arrive within lockTimeoutSeconds cancels the start.
    private int DecideStartLive(bool umpire, int expectedRevision, float now)
    {
        int check = CheckDeadCommand(umpire, expectedRevision);
        if (check != Applied) return check;
        if (_match[MatchRules.Phase] != MatchRules.PhaseInProgress) return RejectedPhase;
        if (_match[MatchRules.Attention] != 0) return RejectedAttention;
        if (roster == null || !roster.IsRosterReady()) return RejectedRoster;
        int next = Mathf.Max(Mathf.Max(_issuedPlayId, roster.GetLockHighPlayId()), _match[MatchRules.LastCommittedPlayId]) + 1;
        BeginPreparing(next, now);
        roster.SubmitLiveLock(next);
        return Applied;
    }

    private void BeginPreparing(int playId, float now)
    {
        _issuedPlayId = playId;
        _preparing = true;
        _preparePlayId = playId;
        _prepareDeadline = now + Mathf.Max(0.5f, lockTimeoutSeconds);
        _lastStartRejection = 0;
        SendCustomEventDelayedSeconds(nameof(_PrepareCheck), Mathf.Max(0.5f, lockTimeoutSeconds));
        // The handed-out play id is kept by everyone, so a new Owner never hands it out again.
        Publish();
    }

    public void _PrepareCheck()
    {
        if (!_preparing) return;
        float remaining = _prepareDeadline - Time.time;
        if (remaining > 0.05f)
        {
            SendCustomEventDelayedSeconds(nameof(_PrepareCheck), remaining);
            return;
        }
        CancelPreparing(RejectedLockTimeout);
    }

    private void CancelPreparing(int reason)
    {
        if (!_preparing) return;
        _preparing = false;
        _lastStartRejection = reason;
        if (roster != null) roster.SubmitLiveUnlock(_preparePlayId);
        NotifyChanged();
    }

    public void _OnRosterChanged()
    {
        if (!CanCommit() || roster == null || !roster.IsRosterReady()) return;
        if (_preparing && roster.GetLockPlayId() == _preparePlayId)
        {
            ReadRosterSnapshot();
            FinishStartLive(roster.GetRevision(), roster.GetLockGeneration(), Networking.GetServerTimeInSeconds());
            return;
        }
        ReconcileRosterLock(roster.GetLockPlayId());
    }

    // Kept small: every call into the roster saves this method's locals.
    private void ReadRosterSnapshot()
    {
        for (int team = 0; team < RosterRules.TeamCount; team++)
        {
            for (int role = 0; role < RosterRules.RoleCount; role++)
                _snapRoles[RosterRules.RoleSlot(team, role)] = roster.GetPlayerAtRole(team, role);
            for (int row = 0; row < RosterRules.OrderCount; row++)
                _snapBatters[RosterRules.OrderSlot(team, row)] = roster.GetPlayerAtOrder(team, row + RosterRules.FirstOrder);
        }
    }

    // Steps 6-8 with the roster snapshot in _snapRoles / _snapBatters.
    private int FinishStartLive(int rosterRevision, int lockGeneration, double serverNow)
    {
        if (!_preparing) return RejectedNotLive;
        _preparing = false;
        int[] next = PlayRules.NewPlay();
        double[] times = PlayRules.NewTimes();
        PlayRules.TakeRoster(next, _snapRoles, _snapBatters);
        int blocked = PlayRules.Begin(next, times, _match, _preparePlayId, serverNow, rosterRevision, lockGeneration, regulationInnings);
        if (blocked != 0)
        {
            _lastStartRejection = blocked == MatchRules.AttentionNoBatter ? RejectedNoBatter
                : blocked == MatchRules.AttentionBatterChanged ? RejectedBatterChanged : RejectedRunnerConflict;
            if (roster != null) roster.SubmitLiveUnlock(_preparePlayId);
            NotifyChanged();
            return _lastStartRejection;
        }
        _play = next;
        _playTimes = times;
        MatchRules.Copy(_match, _baseMatch);
        _playConsistent = true;
        Publish();
        return Applied;
    }

    // The roster lock follows the play: locked for the live play id, otherwise open. A lock left
    // by a cancelled start or a lost unlock is released; a live play whose lock was lost (for
    // example the Roster Owner recovered an older roster) asks for it again.
    private void ReconcileRosterLock(int lockPlayId)
    {
        if (roster == null) return;
        if (IsLive())
        {
            if (lockPlayId != _play[PlayRules.PlayId]) roster.SubmitLiveLock(_play[PlayRules.PlayId]);
            return;
        }
        if (lockPlayId != 0 && !(_preparing && lockPlayId == _preparePlayId)) roster.SubmitLiveUnlock(lockPlayId);
    }

    // ---- Rulings ----

    // The entry for the automatic umpire on the Owner (and, through RequestRuling, the umpire).
    // source/sequence identify the event, serverTime places it; a ruling for a committed play is
    // recorded as late and never applied. HitByPitch and DeadBall end the live period.
    public int ApplyRuling(int playId, int source, int sequence, double serverTime, int code, int runnerId, int arg, bool umpire)
    {
        if (!CanCommit()) return PlayRules.RejectedNotReady;
        if (!IsLive() || playId != _play[PlayRules.PlayId])
        {
            if (playId > 0 && playId <= _match[MatchRules.LastCommittedPlayId]) RecordLateEvent(playId);
            return IsLive() ? PlayRules.RejectedWrongPlay : PlayRules.RejectedNotLive;
        }
        // The roster must still be locked for this play (it can be lost to a roster recovery);
        // until the lock is back, rulings are refused rather than applied on an unlocked roster.
        if (!RosterLockHolds()) return PlayRules.RejectedNotReady;
        int revision = _play[PlayRules.Revision];
        int result = PlayRules.Apply(_play, _playTimes, _baseMatch, code, runnerId, arg, source, sequence, serverTime, umpire);
        if (result == PlayRules.ResultEndsLive)
        {
            int cause = code == PlayRules.JudgeHitByPitch ? PlayRules.DeadHitByPitch : arg;
            EndLive(cause, serverTime);
            return result;
        }
        if (_play[PlayRules.Revision] != revision) Publish();
        return result;
    }

    // True without a roster (nothing to lock) or when the roster holds the live play's lock.
    public bool RosterLockHolds()
    {
        if (roster == null || !IsLive()) return true;
        return roster.IsRosterReady() && roster.GetLockPlayId() == _play[PlayRules.PlayId];
    }

    private void RecordLateEvent(int playId)
    {
        _lateEventCount++;
        _lastLateEventPlayId = playId;
        Debug.Log("[VVBC Match] ruling for committed play " + playId + " ignored (late event)");
    }

    // ---- Time ----

    private int DecideTime(int requester, int playId)
    {
        if (!CanCommit()) return TimeRules.RejectedNotReady;
        if (!IsLive()) return TimeRules.RejectedNotLive;
        if (playId != _play[PlayRules.PlayId]) return TimeRules.RejectedWrongPlay;
        ReadTimeConditions();
        bool confirmed = ballQuery != null && ballQuery.HasConfirmedHold();
        int holder = ballQuery != null ? ballQuery.ConfirmedHolderId() : 0;
        return DecideTimeWith(confirmed, holder, Networking.GetServerTimeInSeconds());
    }

    // With the conditions read (ReadTimeConditions) and the ball's confirmed possession.
    private int DecideTimeWith(bool holdConfirmed, int holderId, double serverNow)
    {
        int result = CheckTime(holdConfirmed, holderId);
        if (result == TimeRules.Accepted) EndLive(PlayRules.DeadTime, serverNow);
        return result;
    }

    // Runner and base positions as the Owner sees them now.
    private void ReadTimeConditions()
    {
        for (int b = 0; b < 3; b++)
        {
            Transform point = b < basePoints.Length ? basePoints[b] : null;
            _baseSet[b] = point != null;
            if (point != null) _bases[b] = point.position;
        }
        for (int s = 0; s < RunnerRules.SlotCount; s++)
        {
            _runnerKnown[s] = false;
            if (!RunnerRules.IsActive(_play, s)) continue;
            VRCPlayerApi player = VRCPlayerApi.GetPlayerById(_play[RunnerRules.At(s, RunnerRules.Player)]);
            if (!Utilities.IsValid(player)) continue;
            _runnerKnown[s] = true;
            _runnerPositions[s] = player.GetPosition();
        }
    }

    private int CheckTime(bool holdConfirmed, int holderId)
    {
        return TimeRules.Check(_play, _baseMatch, holdConfirmed, holderId, _runnerPositions, _runnerKnown, _bases, _baseSet, timeRunnerBaseDistance);
    }

    // The umpire's Time: no conditions (recovery from a misjudgement, a lost ball, a runner
    // conflict, a network problem). It still needs a live play to end.
    private int DecideForceTime(bool umpire, int playId, double serverNow)
    {
        if (!_stateKnown || _recovering || !Networking.IsOwner(gameObject)) return TimeRules.RejectedNotReady;
        if (!umpire) return TimeRules.RejectedDenied;
        if (!IsLive()) return TimeRules.RejectedNotLive;
        if (playId != _play[PlayRules.PlayId]) return TimeRules.RejectedWrongPlay;
        EndLive(PlayRules.DeadForcedTime, serverNow);
        return TimeRules.Accepted;
    }

    // An automatic dead ball (umpire engine on the Owner).
    public bool ReportDeadBall(int playId, int cause, double serverTime)
    {
        if (!CanCommit() || !IsLive() || playId != _play[PlayRules.PlayId]) return false;
        if (cause <= PlayRules.DeadNone || cause > PlayRules.DeadMax) return false;
        return EndLive(cause, serverTime);
    }

    // ---- Commit ----
    //
    // EndLive(reason), all inside this one Owner event:
    //  1. the end of the live period is decided here; 2. Live = 0, so no ruling is taken after
    //  this; 3. the rules of the cause are applied to the Delta (hit by pitch awards); 4-5.
    //  BaseSnapshot + Delta are combined into the next MatchState, which replaces it at once;
    //  6. LastCommittedPlayId; 7. MatchRevision; 8. one send; 9. the roster unlock; 10. dead.
    // Nothing waits for rulings still on the way: they arrive late and are only recorded.
    // Returns false when there was no live play (a repeated Time commits once).
    private bool EngineHasOpenAdjudication()
    {
        return _engine != null && _engine.HasOpenAdjudication();
    }

    private bool EndLive(int cause, double serverTime)
    {
        if (!IsLive()) return false;
        int playId = _play[PlayRules.PlayId];
        if (PlayRules.ApplyDeadBallCause(_play, _playTimes, _baseMatch, cause, serverTime) != PlayRules.ResultApplied)
        {
            // The cause's award does not fit the play (no batter up): committed as it is, flagged.
            _play[PlayRules.DAttention] |= MatchRules.AttentionUnresolvedRuling;
        }
        // Physical events the umpire engine could not decide yet are never dropped silently.
        if (EngineHasOpenAdjudication()) _play[PlayRules.DAttention] |= MatchRules.AttentionUnresolvedRuling;
        _play[PlayRules.Live] = 0;
        _play[PlayRules.Revision]++;
        int[] next = new int[MatchRules.Length];
        PlayRules.Commit(_baseMatch, _play, next, Mathf.Clamp(maxInnings, 1, MatchRules.MaxInnings), allowTie);
        ArchivePlay(next[MatchRules.Attention] | _play[PlayRules.DAttention]);
        _match = next;
        _playConsistent = false;
        Publish();
        if (roster != null) roster.SubmitLiveUnlock(playId);
        return true;
    }
}
