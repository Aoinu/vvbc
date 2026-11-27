using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;

// The controller on the Owner and across clients. The roster, the umpire authority and the
// ball are other Behaviours: these tests pass their answers in (umpire flags, the roster
// snapshot, the confirmed holder), as the network wrappers do with the real ones.
public partial class MatchControllerBehaviour
{
    private int _rseq;

    // An Owner holding a match in progress with nine batters on each team.
    private void ResetControllerForTest()
    {
        // The test world places a RosterManager of its own (not ready, unlocked); these tests
        // pass the roster's answers in instead (RulingsWaitForTheRosterLock keeps it).
        roster = null;
        _match = MatchRules.NewState();
        _match[MatchRules.Revision] = 1;
        _match[MatchRules.Phase] = MatchRules.PhaseInProgress;
        _play = PlayRules.NewPlay();
        _playTimes = PlayRules.NewTimes();
        _stateKnown = true;
        _recovering = false;
        _preparing = false;
        _playConsistent = false;
        _issuedPlayId = 0;
        _rseq = 0;
        for (int i = 0; i < RosterRules.RoleTableLength; i++) _snapRoles[i] = 0;
        for (int i = 0; i < RosterRules.OrderTableLength; i++) _snapBatters[i] = 0;
        for (int r = 0; r < 9; r++)
        {
            _snapRoles[RosterRules.RoleSlot(0, r)] = 11 + r;
            _snapBatters[RosterRules.OrderSlot(0, r)] = 11 + r;
            _snapRoles[RosterRules.RoleSlot(1, r)] = 21 + r;
            _snapBatters[RosterRules.OrderSlot(1, r)] = 21 + r;
        }
    }

    // Steps 1-8 of the live start with the roster lock confirmed.
    private int StartLiveForTest()
    {
        BeginPreparing(_issuedPlayId + 1, 0f);
        return FinishStartLive(1, 1, 100.0);
    }

    private int RulingForTest(int code, int runnerId, int arg)
    {
        _rseq++;
        return ApplyRuling(GetPlayId(), 5, _rseq, 100.0 + _rseq, code, runnerId, arg, true);
    }

    private int RulingForTest(int code) { return RulingForTest(code, 0, 0); }

    private void SetTimeConditionsForTest()
    {
        for (int b = 0; b < 3; b++) _baseSet[b] = true;
        _bases[0] = new Vector3(20f, 0f, 20f);
        _bases[1] = new Vector3(0f, 0f, 40f);
        _bases[2] = new Vector3(-20f, 0f, 20f);
        for (int s = 0; s < RunnerRules.SlotCount; s++)
        {
            _runnerKnown[s] = RunnerRules.IsActive(_play, s);
            int baseIndex = _play[RunnerRules.At(s, RunnerRules.Base)];
            _runnerPositions[s] = baseIndex >= 1 && baseIndex <= 3 ? _bases[baseIndex - 1] + new Vector3(1f, 0f, 1f) : new Vector3(0f, 0f, 0f);
        }
    }

    // ---- Live start ----

    [TsukimiTest]
    public void LiveStartTakesTheSnapshotAndGoesLive()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        Assert.AreEqual(Applied, StartLiveForTest());
        Assert.IsTrue(IsLive());
        Assert.AreEqual(1, GetPlayId());
        Assert.AreEqual(GetMatchRevision(), GetPlayBaseRevision(), "the play is based on the committed state");
        Assert.AreEqual(11, GetBatterId());
        Assert.AreEqual(RosterRules.RoleSlot(1, RosterRules.RoleP), GetSnapshotRoleSlot(21));
        _snapRoles[RosterRules.RoleSlot(1, RosterRules.RoleP)] = 99;
        Assert.AreEqual(RosterRules.RoleSlot(1, RosterRules.RoleP), GetSnapshotRoleSlot(21), "a later roster change does not reach the play");
    }

    [TsukimiTest]
    public void LiveStartChecksTheCommittedState()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        Assert.AreEqual(RejectedDenied, DecideStartLive(false, GetMatchRevision(), 0f), "only the umpire starts live play");
        Assert.AreEqual(RejectedStale, DecideStartLive(true, GetMatchRevision() - 1, 0f));
        _match[MatchRules.Attention] = MatchRules.AttentionUnresolvedRuling;
        Assert.AreEqual(RejectedAttention, DecideStartLive(true, GetMatchRevision(), 0f), "a known issue waits for a correction");
        _match[MatchRules.Attention] = 0;
        Assert.AreEqual(RejectedRoster, DecideStartLive(true, GetMatchRevision(), 0f), "no roster to lock");
        _match[MatchRules.Phase] = MatchRules.PhaseNotStarted;
        Assert.AreEqual(RejectedPhase, DecideStartLive(true, GetMatchRevision(), 0f));
        _match[MatchRules.Phase] = MatchRules.PhaseInProgress;
        StartLiveForTest();
        Assert.AreEqual(RejectedLive, DecideStartLive(true, GetMatchRevision(), 0f), "already live");
    }

    [TsukimiTest]
    public void BatterOnBaseRefusesTheStart()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        _match[MatchRules.Runners] = 11;
        Assert.AreEqual(RejectedRunnerConflict, StartLiveForTest());
        Assert.IsFalse(IsLive(), "nothing is removed or replaced by itself");
        Assert.AreEqual(RejectedRunnerConflict, GetLastStartRejection());
        Assert.IsFalse(IsPreparingLive());
        for (int r = 0; r < 9; r++) _snapBatters[RosterRules.OrderSlot(0, r)] = 0;
        _match[MatchRules.Runners] = 0;
        Assert.AreEqual(RejectedNoBatter, StartLiveForTest());
    }

    [TsukimiTest]
    public void PlayIdsAreNeverReused()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        BeginPreparing(1, 0f);
        CancelPreparing(RejectedLockTimeout);
        Assert.AreEqual(RejectedLockTimeout, GetLastStartRejection());
        StartLiveForTest();
        Assert.AreEqual(2, GetPlayId(), "a cancelled start used play id 1");
    }

    // ---- Rulings and commit ----

    [TsukimiTest]
    public void PitchesAccumulateWithoutCommitting()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        StartLiveForTest();
        int revision = GetMatchRevision();
        RulingForTest(PlayRules.JudgeBall);
        RulingForTest(PlayRules.JudgeCalledStrike);
        RulingForTest(PlayRules.JudgeBall);
        Assert.AreEqual(revision, GetMatchRevision(), "pitch results do not commit");
        Assert.AreEqual(2, GetBalls(), "the effective state shows them");
        Assert.AreEqual(1, GetStrikes());
        Assert.AreEqual(0, _match[MatchRules.Balls], "the committed state does not");
        Assert.AreEqual(4, GetPlayRevision());
    }

    [TsukimiTest]
    public void TimeCommitsAtOnceAndOnlyOnce()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        StartLiveForTest();
        int playId = GetPlayId();
        int revision = GetMatchRevision();
        RulingForTest(PlayRules.JudgeBall);
        RulingForTest(PlayRules.JudgeBall);
        Assert.AreEqual(TimeRules.Accepted, DecideForceTime(true, playId, 150.0));
        Assert.IsFalse(IsLive());
        Assert.AreEqual(revision + 1, GetMatchRevision());
        Assert.AreEqual(playId, GetLastCommittedPlayId());
        Assert.AreEqual(2, _match[MatchRules.Balls], "the count is committed");
        Assert.AreEqual(TimeRules.RejectedNotLive, DecideForceTime(true, playId, 151.0), "a second Time commits nothing");
        Assert.AreEqual(revision + 1, GetMatchRevision());
        Assert.AreEqual(TimeRules.RejectedDenied, DecideForceTime(false, playId, 151.0), "only the umpire forces Time");
    }

    [TsukimiTest]
    public void CommitDoesNotWaitForOpenRulings()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        _match[MatchRules.Runners] = 15;
        StartLiveForTest();
        RulingForTest(PlayRules.JudgeFairBall);
        int batter = GetRunnerIdOnBase(0);
        Assert.AreEqual(PlayRules.ResultStashed, RulingForTest(PlayRules.JudgeForceSafe, batter, 1), "waits for the lead runner");
        Assert.AreEqual(TimeRules.Accepted, DecideForceTime(true, GetPlayId(), 150.0));
        Assert.IsFalse(IsLive(), "committed at once");
        Assert.IsTrue((_match[MatchRules.Attention] & MatchRules.AttentionUnresolvedRuling) != 0, "and flagged for the umpire");
        Assert.AreEqual(15, _match[MatchRules.Runners], "only what the rules awarded is on base");
        Assert.AreEqual(0, MatchRules.FindViolation(_match));
    }

    [TsukimiTest]
    public void RulingsWaitForTheRosterLock()
    {
        VRCPlayerApi owner = Mimic.Join();
        RosterManagerBehaviour placed = roster;
        ResetControllerForTest();
        StartLiveForTest();
        roster = placed;
        Assert.IsNotNull(roster, "the test world places a roster");
        Assert.IsFalse(RosterLockHolds(), "that roster does not hold this play's lock");
        Assert.AreEqual(PlayRules.RejectedNotReady, RulingForTest(PlayRules.JudgeBall), "no automatic update on an unlocked roster");
        Assert.AreEqual(0, GetBalls());
        Assert.AreEqual(TimeRules.Accepted, DecideForceTime(true, GetPlayId(), 150.0), "the umpire's Time still ends the play");
        Assert.IsTrue(RosterLockHolds(), "dead: nothing to hold");
    }

    [TsukimiTest]
    public void LateRulingsNeverChangeTheCommittedState()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        StartLiveForTest();
        int playId = GetPlayId();
        DecideForceTime(true, playId, 150.0);
        int[] committed = MatchRules.NewState();
        CopyMatchState(committed);
        Assert.AreEqual(PlayRules.RejectedNotLive, ApplyRuling(playId, 5, 99, 140.0, PlayRules.JudgeBall, 0, 0, true));
        Assert.AreEqual(1, GetLateEventCount(), "recorded for diagnostics");
        Assert.AreEqual(playId, GetLastLateEventPlayId());
        Assert.IsTrue(MatchRules.SameState(committed, _match), "never applied to the committed state");
        StartLiveForTest();
        Assert.AreEqual(PlayRules.RejectedWrongPlay, ApplyRuling(playId, 5, 100, 160.0, PlayRules.JudgeBall, 0, 0, true), "nor to the next play");
        Assert.AreEqual(0, GetBalls());
    }

    [TsukimiTest]
    public void HitByPitchEndsTheLivePeriodWithTheAward()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        _match[MatchRules.Runners] = 15;
        StartLiveForTest();
        Assert.AreEqual(PlayRules.ResultEndsLive, RulingForTest(PlayRules.JudgeHitByPitch));
        Assert.IsFalse(IsLive());
        Assert.AreEqual(11, _match[MatchRules.Runners]);
        Assert.AreEqual(15, _match[MatchRules.Runners + 1]);
        Assert.AreEqual(12, _match[MatchRules.PaBatter]);
        Assert.AreEqual(0, MatchRules.FindViolation(_match));
    }

    [TsukimiTest]
    public void ThreeOutsThenTimeChangeSides()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        StartLiveForTest();
        RulingForTest(PlayRules.JudgeCatch);
        RulingForTest(PlayRules.JudgeCatch);
        RulingForTest(PlayRules.JudgeCatch);
        Assert.IsTrue(IsHalfEndPending());
        Assert.IsFalse(IsAcceptingPitch());
        Assert.IsTrue(IsLive(), "the third out waits for Time");
        DecideForceTime(true, GetPlayId(), 150.0);
        Assert.AreEqual(MatchRules.HalfBottom, GetHalf());
        Assert.AreEqual(RosterRules.TeamHome, GetOffenseTeam());
        Assert.AreEqual(0, GetOuts());
        Assert.AreEqual(3, GetCursor(RosterRules.TeamAway), "Away's next batter is the 4th");
        StartLiveForTest();
        Assert.AreEqual(21, GetBatterId(), "Home's first batter");
    }

    [TsukimiTest]
    public void PlayerTimeFollowsTheConditions()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        _match[MatchRules.Runners] = 15;
        StartLiveForTest();
        SetTimeConditionsForTest();
        Assert.AreEqual(TimeRules.RejectedNoConfirmedHold, DecideTimeWith(false, 21, 150.0));
        Assert.AreEqual(TimeRules.RejectedHolderNotFielder, DecideTimeWith(true, 12, 150.0), "an offensive player holds it");
        _runnerPositions[RunnerRules.FindActiveOnBase(_play, 1)] = new Vector3(25f, 0f, 20f);
        Assert.AreEqual(TimeRules.RejectedRunnerAway, DecideTimeWith(true, 21, 150.0));
        Assert.IsTrue(IsLive());
        SetTimeConditionsForTest();
        Assert.AreEqual(TimeRules.Accepted, DecideTimeWith(true, 23, 150.0), "the 1B holds it, the runner is at 1B");
        Assert.IsFalse(IsLive());
        Assert.AreEqual(TimeRules.RejectedNotLive, DecideTime(owner.playerId, GetLastCommittedPlayId()), "a repeated request commits nothing");
    }

    // ---- Manual correction ----

    private int[] ProposalForTest()
    {
        int[] proposed = MatchRules.NewState();
        CopyMatchState(proposed);
        return proposed;
    }

    [TsukimiTest]
    public void UmpireCorrectsTheDeadState()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        int[] proposed = ProposalForTest();
        proposed[MatchRules.Inning] = 3;
        proposed[MatchRules.Half] = MatchRules.HalfBottom;
        proposed[MatchRules.InningRunsIndex(0, 2)] = 2;
        proposed[MatchRules.Hits + 1] = 4;
        proposed[MatchRules.Errors] = 1;
        proposed[MatchRules.Runners + 1] = 25;
        proposed[MatchRules.Revision] = 999;
        int revision = GetMatchRevision();
        Assert.AreEqual(Applied, DecideCorrection(owner.playerId, true, proposed, 7, revision, 300.0));
        Assert.AreEqual(revision + 1, GetMatchRevision(), "the controller keeps its own revision");
        Assert.AreEqual(1, GetCorrectionRevision());
        Assert.AreEqual(3, GetInning());
        Assert.AreEqual(2, GetRuns(0));
        Assert.AreEqual(25, GetRunnerOnBase(2));
        Assert.AreEqual(owner.playerId, GetCorrectionEntry(0, 0), "the operator is recorded");
        Assert.AreEqual(revision, GetCorrectionEntry(0, 1));
        Assert.AreEqual(revision + 1, GetCorrectionEntry(0, 2));
        Assert.AreEqual(7, GetCorrectionEntry(0, 3));
        int[] before = MatchRules.NewState();
        int[] after = MatchRules.NewState();
        Assert.IsTrue(CopyLastCorrection(before, after));
        Assert.AreEqual(1, before[MatchRules.Inning]);
        Assert.AreEqual(3, after[MatchRules.Inning]);
        StartLiveForTest();
        Assert.AreEqual(GetMatchRevision(), GetPlayBaseRevision(), "the next play starts from the corrected state");
        Assert.AreEqual(25, GetRunnerOnBase(2));
    }

    [TsukimiTest]
    public void InvalidOrUnauthorizedCorrectionsChangeNothing()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        int revision = GetMatchRevision();
        int[] proposed = ProposalForTest();
        proposed[MatchRules.Outs] = 3;
        Assert.AreEqual(RejectedInvalid * 100 - 4, DecideCorrection(owner.playerId, true, proposed, 0, revision, 0.0), "the violation is named");
        proposed[MatchRules.Outs] = 1;
        Assert.AreEqual(RejectedDenied, DecideCorrection(owner.playerId, false, proposed, 0, revision, 0.0), "only the umpire");
        Assert.AreEqual(RejectedStale, DecideCorrection(owner.playerId, true, proposed, 0, revision - 1, 0.0), "edited on an older state");
        Assert.AreEqual(revision, GetMatchRevision());
        Assert.AreEqual(0, GetOuts());
        StartLiveForTest();
        Assert.AreEqual(RejectedLive, DecideCorrection(owner.playerId, true, proposed, 0, revision, 0.0), "never while live");
        DecideForceTime(true, GetPlayId(), 150.0);
        proposed = ProposalForTest();
        proposed[MatchRules.Attention] = 0;
        Assert.AreEqual(Applied, DecideCorrection(owner.playerId, true, proposed, 0, GetMatchRevision(), 0.0), "after Time it can be edited");
    }

    [TsukimiTest]
    public void MatchCommandsFollowThePhase()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        Assert.AreEqual(Applied, DecideInitialize(true, GetMatchRevision()));
        Assert.AreEqual(MatchRules.PhaseNotStarted, GetPhase());
        Assert.AreEqual(1, GetInning());
        Assert.AreEqual(RejectedPhase, DecideEndMatch(true, MatchRules.ResultTie, GetMatchRevision()));
        Assert.AreEqual(Applied, DecideStartMatch(true, GetMatchRevision()));
        Assert.AreEqual(RejectedPhase, DecideStartMatch(true, GetMatchRevision()));
        Assert.AreEqual(RejectedDenied, DecideEndMatch(false, MatchRules.ResultTie, GetMatchRevision()), "the Owner is not the umpire");
        Assert.AreEqual(Applied, DecideEndMatch(true, MatchRules.ResultAwayWin, GetMatchRevision()));
        Assert.AreEqual(MatchRules.PhaseEnded, GetPhase());
        Assert.AreEqual(MatchRules.ResultAwayWin, GetResult());
    }

    // ---- Sync, recovery, late join ----

    [TsukimiTest]
    public void LivePlayReachesOtherClientsAsOneSet()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetControllerForTest();
        StartLiveForTest();
        RulingForTest(PlayRules.JudgeBall);
        Mimic.Deliver();
        Mimic.Become(other);
        Assert.IsTrue(IsMatchReady());
        Assert.IsTrue(IsLive());
        Assert.AreEqual(1, GetBalls());
        Assert.AreEqual(11, GetBatterId());
        Assert.AreEqual(GetMatchRevision(), GetPlayBaseRevision());
    }

    [TsukimiTest]
    public void CommittedPlayIsNotShownAgain()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetControllerForTest();
        StartLiveForTest();
        RulingForTest(PlayRules.JudgeBall);
        Mimic.Deliver();
        Mimic.Become(owner);
        DecideForceTime(true, GetPlayId(), 150.0);
        Mimic.Deliver();
        Mimic.Become(other);
        Assert.IsFalse(IsLive(), "the committed play stays committed");
        Assert.AreEqual(1, GetBalls(), "its result is in the MatchState");
        Assert.AreEqual(1, GetLastCommittedPlayId());
    }

    [TsukimiTest]
    public void LateJoinerSeesTheLivePlay()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        StartLiveForTest();
        RulingForTest(PlayRules.JudgeCalledStrike);
        Mimic.Deliver();
        VRCPlayerApi late = Mimic.Join();
        Mimic.Become(late);
        Assert.IsTrue(IsLive());
        Assert.AreEqual(1, GetStrikes());
    }

    [TsukimiTest]
    public void NothingIsShownBeforeAStateArrives()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi late = Mimic.Join();
        Mimic.Become(late);
        _stateKnown = false;
        _match = MatchRules.NewState();
        OnDeserialization();
        Assert.IsFalse(IsMatchReady(), "revision 0 is not a match");
        Assert.IsFalse(CopyEffectiveState(MatchRules.NewState()));
    }

    [TsukimiTest]
    public void APlayOfAnotherMatchStateIsNotLive()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        Mimic.Become(other);
        ResetControllerForTest();
        // A valid live play (the validator checks the whole PlayState now), then its pairing.
        _match[MatchRules.Revision] = 4;
        PlayRules.TakeRoster(_play, _snapRoles, _snapBatters);
        PlayRules.Begin(_play, _playTimes, _match, 3, 100.0, 1, 1, 9);
        _match[MatchRules.Revision] = 5;
        OnDeserialization();
        Assert.IsTrue(IsStateKnown());
        Assert.IsFalse(IsLive(), "based on revision 4, paired with 5");
        _play[PlayRules.BaseMatchRevision] = 5;
        OnDeserialization();
        Assert.IsTrue(IsLive());
        _match[MatchRules.LastCommittedPlayId] = 3;
        OnDeserialization();
        Assert.IsFalse(IsLive(), "a play already committed");
    }

    private double[] _snapshotTimesForTest;

    // A recovery candidate built the way the Owner builds it: a live play begun on a MatchState
    // of baseRevision, `balls` balls ruled, paired with a MatchState of matchRevision.
    private int[] SnapshotForTest(int matchRevision, bool live, int baseRevision, int balls)
    {
        int[] m = MatchRules.NewState();
        m[MatchRules.Revision] = live ? baseRevision : matchRevision;
        m[MatchRules.Phase] = MatchRules.PhaseInProgress;
        int[] p = PlayRules.NewPlay();
        double[] t = PlayRules.NewTimes();
        if (live)
        {
            PlayRules.TakeRoster(p, _snapRoles, _snapBatters);
            PlayRules.Begin(p, t, m, 9, 100.0, 1, 1, 9);
            for (int i = 0; i < balls; i++) PlayRules.Apply(p, t, m, PlayRules.JudgeBall, 0, 0, 5, i + 1, 101.0 + i, true);
        }
        m[MatchRules.Revision] = matchRevision;
        int[] snapshot = new int[SnapshotLength];
        for (int i = 0; i < MatchRules.Length; i++) snapshot[i] = m[i];
        for (int i = 0; i < PlayRules.Length; i++) snapshot[SnapshotPlay + i] = p[i];
        snapshot[SnapshotIssued] = 9;
        _snapshotTimesForTest = new double[SnapshotTimesLength];
        for (int i = 0; i < PlayRules.TimesLength; i++) _snapshotTimesForTest[i] = t[i];
        return snapshot;
    }

    private bool ConsiderForTest(int[] snapshot) { return ConsiderSnapshot(snapshot, _snapshotTimesForTest); }

    [TsukimiTest]
    public void RecoveryTakesTheNewestConsistentSetAndResumes()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        _stateKnown = false;
        _recovering = true;
        _hasBest = false;
        _bestMatchRevision = -1;
        _bestPlayRevision = -1;
        _noStateCount = 0;
        Assert.IsTrue(ConsiderForTest(SnapshotForTest(3, true, 3, 1)));
        Assert.IsTrue(ConsiderForTest(SnapshotForTest(3, true, 3, 3)), "same MatchState, newer play");
        Assert.IsFalse(ConsiderForTest(SnapshotForTest(4, true, 3, 2)), "a play not based on its MatchState is never mixed in");
        Assert.IsFalse(ConsiderForTest(SnapshotForTest(2, false, 0, 0)), "older");
        Assert.AreEqual(1, GetInconsistentSnapshotCount());
        Assert.IsTrue(ConcludeRecoveryRound(3));
        Assert.IsTrue(IsMatchReady());
        Assert.IsTrue(IsLive(), "live play resumes");
        Assert.AreEqual(4, GetPlayRevision(), "begun at 1, three rulings");
        Assert.AreEqual(3, GetBalls());
        Assert.AreEqual(3, GetMatchRevision(), "republished as it was");
        StartLiveForTest();
        Assert.IsTrue(IsLive());
    }

    [TsukimiTest]
    public void RecoveryWaitsUnlessNobodyHasAState()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        _stateKnown = false;
        _recovering = true;
        _hasBest = false;
        _noStateCount = 0;
        Assert.AreEqual(PlayRules.RejectedNotReady, ApplyRuling(1, 5, 1, 1.0, PlayRules.JudgeBall, 0, 0, true), "nothing is decided while recovering");
        Assert.AreEqual(RejectedNotReady, DecideStartLive(true, 1, 0f));
        RecordNoState(2);
        Assert.IsFalse(ConcludeRecoveryRound(3), "one player has not answered: stay not ready");
        Assert.IsFalse(IsMatchReady());
        RecordNoState(3);
        Assert.IsTrue(ConcludeRecoveryRound(3));
        Assert.IsTrue(IsMatchReady());
        Assert.AreEqual(1, GetMatchRevision(), "a new match");
        Assert.AreEqual(MatchRules.PhaseNotStarted, GetPhase());
    }

    [TsukimiTest]
    public void OwnerLeavingDuringLivePlayResumesOnTheNewOwner()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetControllerForTest();
        StartLiveForTest();
        RulingForTest(PlayRules.JudgeBall);
        RulingForTest(PlayRules.JudgeBall);
        Mimic.Deliver();
        Mimic.Become(other);
        roster = null;   // fields are per client
        int playId = GetPlayId();
        Mimic.Leave(owner);
        Assert.IsTrue(Networking.IsOwner(gameObject));
        Assert.IsTrue(IsMatchReady(), "alone: recovered from its own copy");
        Assert.IsTrue(IsLive());
        Assert.AreEqual(playId, GetPlayId());
        Assert.AreEqual(2, GetBalls());
        Assert.AreEqual(PlayRules.ResultApplied, ApplyRuling(playId, 5, 3, 200.0, PlayRules.JudgeBall, 0, 0, true), "rulings are taken again");
        Assert.AreEqual(3, GetBalls());
    }

    [TsukimiTest]
    public void FailedSendIsRetriedWhileOwned()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetControllerForTest();
        StartLiveForTest();
        SerializationResult failure = new SerializationResult();
        OnPreSerialization();
        OnPostSerialization(failure);
        Assert.AreEqual(1, GetSerializationFailureStreak());
        Assert.IsTrue(_retryScheduled);
        Assert.IsTrue(RetryNeeded());
        RulingForTest(PlayRules.JudgeBall);
        _RetrySerialization();
        Mimic.Deliver();
        Assert.AreEqual(0, GetSerializationFailureStreak(), "the newest state got through");
        Assert.IsFalse(RetryNeeded());
        Mimic.Become(other);
        Assert.AreEqual(1, GetBalls(), "the retry carried the change made meanwhile");
        OnPostSerialization(failure);
        Assert.IsFalse(_retryScheduled, "a client that does not own the object does not resend");
    }
}
