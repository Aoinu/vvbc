using Tsukimi;
using VRC.SDKBase;

// Controller side of the review fixes: duplicates across an Owner change, recovery that finds
// only invalid states, contradictions reaching other clients, batter changes at the live
// start, open rulings across the third out, and the play history.
public partial class MatchControllerBehaviour
{
    private void PlayAndCommitForTest(int rulings)
    {
        StartLiveForTest();
        for (int i = 0; i < rulings; i++) RulingForTest(PlayRules.RulingHit, 0, 0);
        DecideForceTime(true, GetPlayId(), 500.0 + GetPlayId());
    }

    [TsukimiTest]
    public void AnEventAppliedBeforeAnOwnerChangeStaysADuplicate()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetControllerForTest();
        StartLiveForTest();
        int playId = GetPlayId();
        Assert.AreEqual(PlayRules.ResultApplied, ApplyRuling(playId, 7, 1, 101.0, PlayRules.JudgeBall, 0, 0, true));
        Mimic.Deliver();
        Mimic.Become(other);
        roster = null;
        Mimic.Leave(owner);
        Assert.IsTrue(IsLive(), "resumed");
        Assert.AreEqual(PlayRules.ResultDuplicate, ApplyRuling(playId, 7, 1, 101.0, PlayRules.JudgeBall, 0, 0, true), "the event table came with the state");
        Assert.AreEqual(1, GetBalls());
        Assert.AreEqual(1, GetEventWatermark(7));
        Assert.AreEqual(PlayRules.ResultApplied, ApplyRuling(playId, 7, 2, 102.0, PlayRules.JudgeBall, 0, 0, true));
        Assert.AreEqual(2, GetBalls());
    }

    [TsukimiTest]
    public void OnlyInvalidStatesStopRecoveryWithoutStartingOver()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        _stateKnown = false;
        _recovering = true;
        _hasBest = false;
        _noStateCount = 0;
        _inconsistentSnapshots = 0;
        int[] broken = SnapshotForTest(3, true, 3, 2);
        broken[SnapshotPlay + PlayRules.DOuts] = 3;   // three outs without the half-inning end
        Assert.IsFalse(ConsiderForTest(broken));
        Assert.AreEqual(304, GetLastSnapshotViolation(), "PlayState part, outs");
        Assert.IsFalse(ConcludeRecoveryRound(1), "no valid candidate: it does not finish");
        Assert.IsTrue(IsRecoveryFailed());
        Assert.IsFalse(IsMatchReady(), "nothing is decided, nothing initialized");
        Assert.AreEqual(PlayRules.RejectedNotReady, ApplyRuling(9, 7, 1, 101.0, PlayRules.JudgeBall, 0, 0, true));
        Assert.AreEqual(RejectedDenied, DecideRecoveryReset(false), "only the umpire resets");
        // A valid state arriving later is still taken.
        Assert.IsTrue(ConsiderForTest(SnapshotForTest(3, true, 3, 1)));
        Assert.IsTrue(ConcludeRecoveryRound(1));
        Assert.IsTrue(IsLive());
        Assert.IsFalse(IsRecoveryFailed());
    }

    [TsukimiTest]
    public void TheUmpireResetsAFailedRecovery()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        _stateKnown = false;
        _recovering = true;
        _hasBest = false;
        _inconsistentSnapshots = 0;
        int[] broken = SnapshotForTest(6, false, 0, 0);
        broken[MatchRules.Outs] = 5;
        ConsiderForTest(broken);
        ConcludeRecoveryRound(1);
        Assert.AreEqual(Applied, DecideRecoveryReset(true));
        Assert.IsTrue(IsMatchReady());
        Assert.AreEqual(7, GetMatchRevision(), "above every revision seen");
        Assert.IsTrue((GetAttention() & MatchRules.AttentionRecovery) != 0, "flagged");
        Assert.AreEqual(MatchRules.PhaseNotStarted, GetPhase());
    }

    [TsukimiTest]
    public void AContradictionReachesTheOtherClients()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetControllerForTest();
        _match[MatchRules.Runners + 1] = 16;
        StartLiveForTest();
        int revision = GetPlayRevision();
        Assert.AreEqual(PlayRules.RejectedContradiction, RulingForTest(PlayRules.JudgeForceOut, GetRunnerIdOnBase(2), 3));
        Assert.AreEqual(revision + 1, GetPlayRevision());
        Assert.IsFalse(IsAcceptingPitch());
        Mimic.Deliver();
        Mimic.Become(other);
        Assert.IsTrue((GetAttention() & MatchRules.AttentionInvalidRuling) != 0, "sent with its revision");
        Mimic.Become(owner);
        Assert.AreEqual(TimeRules.Accepted, DecideForceTime(true, GetPlayId(), 150.0), "the umpire's Time still ends the play");
        Assert.AreEqual(RejectedAttention, DecideStartLive(true, GetMatchRevision(), 0f), "the next start waits for a correction");
    }

    [TsukimiTest]
    public void ABatterChangeInThePlateAppearanceRefusesTheStart()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        _match[MatchRules.PaId] = 2;
        _match[MatchRules.PaBatter] = 12;
        _match[MatchRules.Cursor] = 1;
        _match[MatchRules.Strikes] = 2;
        _snapBatters[RosterRules.OrderSlot(0, 1)] = 40;
        Assert.AreEqual(RejectedBatterChanged, StartLiveForTest());
        Assert.IsFalse(IsLive());
        Assert.AreEqual(12, GetBatterId(), "the batter is not replaced");
        Assert.AreEqual(2, GetStrikes(), "the count is not handed to another player");
        _snapBatters[RosterRules.OrderSlot(0, 1)] = 12;
        Assert.AreEqual(Applied, StartLiveForTest(), "with the same batter the plate appearance continues");
        Assert.AreEqual(2, GetStrikes());
    }

    [TsukimiTest]
    public void OpenRulingsAtTheThirdOutBlockTheNextStart()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        _match[MatchRules.Outs] = 2;
        _match[MatchRules.Runners] = 15;
        _match[MatchRules.Runners + 1] = 16;
        StartLiveForTest();
        RulingForTest(PlayRules.JudgeFairBall);
        Assert.AreEqual(PlayRules.ResultStashed, RulingForTest(PlayRules.JudgeForceSafe, GetRunnerIdOnBase(1), 2));
        RulingForTest(PlayRules.JudgeForceOut, GetRunnerIdOnBase(0), 1);
        Assert.IsTrue(IsHalfEndPending());
        DecideForceTime(true, GetPlayId(), 150.0);
        Assert.AreEqual(MatchRules.HalfBottom, GetHalf(), "sides changed");
        Assert.IsTrue((GetAttention() & MatchRules.AttentionUnresolvedRuling) != 0, "the open ruling is kept");
        Assert.AreEqual(RejectedAttention, DecideStartLive(true, GetMatchRevision(), 0f));
        Assert.AreEqual(MatchRules.AttentionUnresolvedRuling, GetHistoryPlayField(0, HistoryAttention) & MatchRules.AttentionUnresolvedRuling, "and in the history of that play");
    }

    // ---- History ----

    [TsukimiTest(maxSteps: 30000000)]
    public void HistoryKeepsTheCurrentAndRecentPlays()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        for (int play = 1; play <= 6; play++) PlayAndCommitForTest(play);
        Assert.AreEqual(MaxHistoryPlays, GetHistoryPlayCount(), "a fixed number of plays");
        Assert.AreEqual(6, GetHistoryPlayField(0, HistoryPlayId), "newest first");
        Assert.AreEqual(3, GetHistoryPlayField(3, HistoryPlayId));
        Assert.AreEqual(PlayRules.DeadForcedTime, GetHistoryPlayField(0, HistoryEndReason));
        Assert.AreEqual(6, GetHistoryEntryCount(0));
        Assert.AreEqual(PlayRules.RulingHit, GetHistoryEntry(0, 0, PlayEvents.LogCode));
        Assert.AreEqual(5, GetHistoryEntry(0, 0, PlayEvents.LogSource));
        StartLiveForTest();
        RulingForTest(PlayRules.JudgeBall);
        Assert.AreEqual(MaxHistoryPlays + 1, GetHistoryPlayCount(), "the live play and the last committed ones");
        Assert.AreEqual(7, GetHistoryPlayField(0, HistoryPlayId));
        Assert.AreEqual(PlayRules.JudgeBall, GetHistoryEntry(0, 0, PlayEvents.LogCode));
        Assert.AreEqual(6, GetHistoryPlayField(1, HistoryPlayId));
    }

    [TsukimiTest(maxSteps: 20000000)]
    public void AFullLogNeverAffectsEventsOrPending()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetControllerForTest();
        _match[MatchRules.Runners] = 15;
        _match[MatchRules.Runners + 1] = 16;
        StartLiveForTest();
        int a = GetRunnerIdOnBase(1);
        Assert.AreEqual(PlayRules.ResultStashed, RulingForTest(PlayRules.JudgeRunnerAdvance, a, 2));
        int stashedSeq = _rseq;
        for (int i = 0; i < PlayEvents.MaxLog + 5; i++) RulingForTest(PlayRules.RulingHit, 0, 0);
        Assert.AreEqual(PlayEvents.MaxLog, GetHistoryEntryCount(0), "the log keeps its last entries");
        Assert.AreEqual(PlayEvents.MaxLog + 6, GetHistoryPlayField(0, HistoryLogTotal), "and counts the rest");
        Assert.AreEqual(1, GetPendingRulingCount(), "the waiting ruling is still there");
        Assert.AreEqual(PlayRules.ResultDuplicate, ApplyRuling(GetPlayId(), 5, stashedSeq, 200.0, PlayRules.JudgeRunnerAdvance, a, 2, true), "and still a duplicate");
        Assert.AreEqual(PlayRules.ResultDuplicate, ApplyRuling(GetPlayId(), 5, 1, 200.0, PlayRules.RulingHit, 0, 0, true));
    }

    [TsukimiTest]
    public void HistoryReachesLateJoinersAndANewOwner()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetControllerForTest();
        PlayAndCommitForTest(2);
        PlayAndCommitForTest(3);
        Mimic.Deliver();
        Mimic.Become(other);
        Assert.AreEqual(2, GetHistoryPlayCount());
        Assert.AreEqual(3, GetHistoryEntryCount(0));
        roster = null;
        Mimic.Leave(owner);
        Assert.IsTrue(IsMatchReady());
        Assert.AreEqual(2, GetHistoryPlayCount(), "recovered with the state");
        Assert.AreEqual(1, GetHistoryPlayField(1, HistoryPlayId));
        VRCPlayerApi late = Mimic.Join();
        Mimic.Deliver();
        Mimic.Become(late);
        Assert.AreEqual(2, GetHistoryPlayCount());
        Assert.AreEqual(2, GetHistoryPlayField(0, HistoryPlayId));
    }
}
