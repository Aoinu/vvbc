using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// The MatchManager Owner's checks of raw contact events. The live play's context (play, roster
// snapshot, runners, the ball's confirmed hold) is set here, as PrepareContext reads it from the
// controller and the ball query. Accepting is never ruling: the controller is never written.
public partial class MatchEventReceiverBehaviour
{
    private const int Slots = 16;
    private const int AwayRunner = 11;      // away batting (top half): 11..19
    private const int HomePitcher = 21;     // home defending: 21..29, DH 30
    private const int HomeDh = 30;
    private const int Spectator = 50;
    private readonly int[] _testRecords = new int[Slots * ContactRules.Stride];
    private readonly double[] _testTimes = new double[Slots];
    private readonly Vector3[] _testPositions = new Vector3[Slots];

    private void ResetReceiverForTest()
    {
        match = null;
        ballQuery = null;
        tagFeedback = null;
        _ackPlayId = 0;
        ContactEvents.ClearTable(_acks);
        for (int i = 0; i < _log.Length; i++) _log[i] = 0;
        _logNext = 0;
        _logTotal = 0;
        for (int i = 0; i < _resultCounts.Length; i++) _resultCounts[i] = 0;
        _feedbackSent = 0;
        _announceCount = 0;
        _ctxPlayId = 5;
        _ctxPlayStart = 100.0;
        _ctxDefense = RosterRules.TeamHome;
        for (int r = 0; r < 9; r++)
        {
            _ctxRoles[RosterRules.RoleSlot(0, r)] = 11 + r;
            _ctxRoles[RosterRules.RoleSlot(1, r)] = 21 + r;
        }
        _ctxRoles[RosterRules.RoleSlot(0, RosterRules.RoleDH)] = 0;
        _ctxRoles[RosterRules.RoleSlot(1, RosterRules.RoleDH)] = HomeDh;
        for (int s = 0; s < RunnerRules.SlotCount; s++) _ctxRunners[s] = 0;
        _ctxRunners[0] = AwayRunner;
        _ctxHolder = HomePitcher;
        _ctxHoldMode = ContactRules.HoldGlove;
        _ctxHoldSeq = 7;
        BeginPlay(5);
        ClearRecords();
    }

    private void ClearRecords()
    {
        for (int i = 0; i < _testRecords.Length; i++) _testRecords[i] = 0;
        for (int i = 0; i < Slots; i++) _testTimes[i] = 0.0;
    }

    private void Put(int slot, int play, int seq, int type, int target, int baseIndex, int holdMode, int holdSeq, double time)
    {
        int at = slot * ContactRules.Stride;
        _testRecords[at + ContactRules.RecPlay] = play;
        _testRecords[at + ContactRules.RecSeq] = seq;
        _testRecords[at + ContactRules.RecType] = type;
        _testRecords[at + ContactRules.RecTarget] = target;
        _testRecords[at + ContactRules.RecBase] = baseIndex;
        _testRecords[at + ContactRules.RecParts] = ContactRules.PartLeftFoot;
        _testRecords[at + ContactRules.RecHoldMode] = holdMode;
        _testRecords[at + ContactRules.RecHoldSeq] = holdSeq;
        _testRecords[at + ContactRules.RecFlags] = 0;
        _testTimes[slot] = time;
        _testPositions[slot] = new Vector3(20f, 0.1f, 20f);
    }

    private void PutTouch(int slot, int seq, double time) { Put(slot, 5, seq, ContactRules.TypeBaseTouchEnter, 0, 1, 0, 0, time); }
    private void PutTag(int slot, int seq, int target, int holdSeq) { Put(slot, 5, seq, ContactRules.TypeTagContactEnter, target, ContactRules.NoBase, ContactRules.HoldGlove, holdSeq, 105.0); }
    private void Read(int source) { ProcessRecords(source, source, _testRecords, _testTimes, _testPositions, 110.0); }

    [TsukimiTest]
    public void AnEventIsAcceptedOnceAndCopiesAreDuplicates()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetReceiverForTest();
        PutTouch(0, 1, 101.0);
        Read(AwayRunner);
        Assert.AreEqual(ResultAccepted, GetLastResult());
        Assert.IsTrue(IsSettled(5, AwayRunner, 1));
        Assert.AreEqual(1, GetLogTotal());
        Assert.AreEqual(ContactRules.TypeBaseTouchEnter, GetLogEntry(0, LogType));
        Assert.AreEqual(101.0, GetLogTime(0), "the detection time is kept");
        for (int i = 0; i < 3; i++) Read(AwayRunner);
        Assert.AreEqual(3, GetResultCount(ResultDuplicate));
        Assert.AreEqual(1, GetLogTotal(), "a resend is never logged or accepted twice");
    }

    [TsukimiTest]
    public void SequencesMaySettleInAnyOrder()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetReceiverForTest();
        PutTouch(0, 3, 103.0);
        Read(AwayRunner);
        Assert.AreEqual(0, GetWatermark(5, AwayRunner), "1 and 2 have not arrived: a hole, not settled");
        Assert.IsFalse(IsSettled(5, AwayRunner, 1));
        ClearRecords();
        PutTouch(0, 1, 101.0);
        PutTouch(1, 2, 102.0);
        Read(AwayRunner);
        Assert.AreEqual(3, GetWatermark(5, AwayRunner));
        Assert.AreEqual(3, GetLogTotal());
    }

    [TsukimiTest]
    public void TheSourceMustBeThePlayerObjectOwner()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetReceiverForTest();
        PutTouch(0, 1, 101.0);
        ProcessRecords(AwayRunner, 12, _testRecords, _testTimes, _testPositions, 110.0);
        Assert.AreEqual(RejectedSource, GetLastResult());
        Assert.AreEqual(0, GetSourceCount(), "nothing read from it");
        ProcessRecords(PlayEvents.SourceManualBase + 3, PlayEvents.SourceManualBase + 3, _testRecords, _testTimes, _testPositions, 110.0);
        Assert.AreEqual(RejectedSource, GetLastResult(), "never a ruling source id");
        ProcessRecords(0, 0, _testRecords, _testTimes, _testPositions, 110.0);
        Assert.AreEqual(0, GetLogTotal());
    }

    [TsukimiTest]
    public void OtherPlaysAndWithdrawnEventsAreNotSettled()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetReceiverForTest();
        Put(0, 4, 1, ContactRules.TypeBaseTouchEnter, 0, 1, 0, 0, 90.0);
        PutTouch(1, 1, 101.0);
        _testRecords[1 * ContactRules.Stride + ContactRules.RecFlags] = ContactRules.FlagInvalid;
        Read(AwayRunner);
        Assert.AreEqual(1, GetResultCount(PendingWrongPlay), "an old play's event changes nothing in this one");
        Assert.AreEqual(0, GetLogTotal());
        Assert.IsFalse(IsSettled(4, AwayRunner, 1));
    }

    [TsukimiTest]
    public void BaseTouchesComeFromPlayersOfTheSnapshot()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetReceiverForTest();
        PutTouch(0, 1, 101.0);
        Read(Spectator);
        Assert.AreEqual(RejectedNotParticipant, GetLastResult());
        Assert.IsTrue(IsSettled(5, Spectator, 1), "settled: the sender stops resending");
        Read(HomePitcher);
        Assert.AreEqual(ResultAccepted, GetLastResult(), "any player of the snapshot, either team");
        ClearRecords();
        Put(0, 5, 2, ContactRules.TypeBaseTouchEnter, 0, 7, 0, 0, 101.0);
        Read(HomePitcher);
        Assert.AreEqual(RejectedInvalid, GetLastResult(), "no base 7");
        ClearRecords();
        PutTouch(0, 3, 80.0);
        Read(HomePitcher);
        Assert.AreEqual(RejectedTime, GetLastResult(), "before the play started");
    }

    [TsukimiTest]
    public void ATagNeedsADefenseFielderAnActiveRunnerAndTheHold()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetReceiverForTest();
        PutTag(0, 1, AwayRunner, 7);
        Read(HomePitcher);
        Assert.AreEqual(ResultAccepted, GetLastResult());
        Assert.AreEqual(1, GetPendingAnnouncementCount(), "announced to everyone");
        Assert.AreEqual(AwayRunner, GetLogEntry(0, LogTarget));
        ClearRecords();
        PutTag(0, 1, HomePitcher, 7);
        Read(12);
        Assert.AreEqual(RejectedNotFielder, GetLastResult(), "an offense player");
        Read(HomeDh);
        Assert.AreEqual(RejectedNotFielder, GetLastResult(), "the DH");
        Read(Spectator);
        Assert.AreEqual(RejectedNotParticipant, GetLastResult(), "a spectator");
        ClearRecords();
        PutTag(0, 2, 12, 7);
        Read(HomePitcher);
        Assert.AreEqual(RejectedTarget, GetLastResult(), "not an active runner");
        ClearRecords();
        Put(0, 5, 3, ContactRules.TypeTagContactEnter, AwayRunner, ContactRules.NoBase, ContactRules.HoldFree, 7, 105.0);
        Read(HomePitcher);
        Assert.AreEqual(RejectedHold, GetLastResult(), "no hold");
        ClearRecords();
        PutTag(0, 1, AwayRunner, 7);
        Read(22);
        Assert.AreEqual(RejectedHold, GetLastResult(), "that hold was the pitcher's, not the catcher's");
        Assert.AreEqual(1, GetPendingAnnouncementCount(), "only the accepted tag was announced");
    }

    [TsukimiTest]
    public void ATagNewerThanTheBallStateHereWaits()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetReceiverForTest();
        PutTag(0, 1, AwayRunner, 8);       // made with hold 8; this client still has hold 7
        Read(HomePitcher);
        Assert.AreEqual(PendingBallBehind, GetLastResult());
        Assert.IsFalse(IsSettled(5, HomePitcher, 1), "not settled: taken again when it is resent");
        _ctxHoldSeq = 8;
        Read(HomePitcher);
        Assert.AreEqual(ResultAccepted, GetLastResult());
        _ctxHoldSeq = 9;                     // the ball has moved on since: an earlier hold is not contradicted
        ClearRecords();
        PutTag(0, 2, AwayRunner, 8);
        Read(HomePitcher);
        Assert.AreEqual(ResultAccepted, GetLastResult());
    }

    [TsukimiTest]
    public void NoRoomIsRefusedOpenlyAndNeverSettled()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetReceiverForTest();
        PutTouch(0, ContactEvents.Window + 1, 101.0);
        Read(AwayRunner);
        Assert.AreEqual(PendingFull, GetLastResult(), "beyond the window of a new source");
        ClearRecords();
        PutTouch(0, 1, 101.0);
        for (int p = 1; p <= ContactEvents.MaxSources; p++)
        {
            _ctxRoles[0] = p;                        // each one a player of the snapshot
            ProcessRecords(p, p, _testRecords, _testTimes, _testPositions, 110.0);
        }
        Assert.AreEqual(ContactEvents.MaxSources, GetSourceCount(), "32 sources");
        _ctxRoles[0] = 900;
        ProcessRecords(900, 900, _testRecords, _testTimes, _testPositions, 110.0);
        Assert.AreEqual(PendingFull, GetLastResult(), "a 33rd source is refused, not dropped");
        Assert.IsFalse(IsSettled(5, 900, 1));
    }

    [TsukimiTest]
    public void ANewPlayStartsANewTable()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetReceiverForTest();
        PutTouch(0, 1, 101.0);
        Read(AwayRunner);
        BeginPlay(6);
        _ctxPlayId = 6;
        Assert.AreEqual(6, GetAckPlayId());
        Assert.IsFalse(IsSettled(5, AwayRunner, 1), "the old play's table is gone");
        Assert.AreEqual(0, GetSourceCount());
        Read(AwayRunner);
        Assert.AreEqual(1, GetResultCount(PendingWrongPlay), "play 5's event is never taken into play 6");
    }

    [TsukimiTest]
    public void SettledEventsReachTheSendersAndANewOwner()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetReceiverForTest();
        PutTouch(0, 1, 101.0);
        PutTag(1, 2, AwayRunner, 7);
        Read(HomePitcher);
        RequestSerialization();
        Mimic.Deliver();
        Mimic.Become(other);
        Assert.IsTrue(IsSettled(5, HomePitcher, 1));
        Assert.IsTrue(IsSettled(5, HomePitcher, 2));
        Assert.AreEqual(2, GetLogTotal(), "the log travels with the table");
        // The other client becomes the MatchManager Owner and reads the same collector again.
        Networking.SetOwner(other, gameObject);
        ResetContextOnlyForTest();
        int before = GetLogTotal();
        Read(HomePitcher);
        Assert.AreEqual(before, GetLogTotal(), "nothing is accepted twice after the Owner change");
        Assert.AreEqual(0, GetPendingAnnouncementCount(), "and the tag is not announced again by the new Owner");
        VRCPlayerApi late = Mimic.Join();
        RequestSerialization();
        Mimic.Deliver();
        Mimic.Become(late);
        Assert.AreEqual(2, GetLogTotal(), "a late joiner sees the checked events");
    }

    // The context of play 5 on a client that never prepared it (a new Owner), keeping its table.
    private void ResetContextOnlyForTest()
    {
        match = null;
        _feedbackSent = 0;
        _announceCount = 0;
        _ctxPlayId = 5;
        _ctxPlayStart = 100.0;
        _ctxDefense = RosterRules.TeamHome;
        for (int r = 0; r < 9; r++)
        {
            _ctxRoles[RosterRules.RoleSlot(0, r)] = 11 + r;
            _ctxRoles[RosterRules.RoleSlot(1, r)] = 21 + r;
        }
        _ctxRunners[0] = AwayRunner;
        _ctxHolder = HomePitcher;
        _ctxHoldMode = ContactRules.HoldGlove;
        _ctxHoldSeq = 7;
    }

    [TsukimiTest]
    public void NothingIsReadWhileTheMatchIsNotReady()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetReceiverForTest();
        _scanPending = false;
        // No controller answer (as while it recovers): nothing can be checked yet.
        ScanAll();
        Assert.IsTrue(_scanPending, "read everything again once the match is ready");
        Assert.AreEqual(0, GetScanCount());
    }

    [TsukimiTest]
    public void AcceptingContactsNeverRules()
    {
        VRCPlayerApi owner = Mimic.Join();
        MatchControllerBehaviour controller = match;     // the test world's controller
        int revision = controller.GetMatchRevision();
        int outs = controller.GetOuts();
        int runs = controller.GetRuns(RosterRules.TeamAway);
        int onFirst = controller.GetRunnerOnBase(1);
        ResetReceiverForTest();
        PutTouch(0, 1, 101.0);
        PutTag(1, 2, AwayRunner, 7);
        Put(2, 5, 3, ContactRules.TypeBaseTouchEnter, 0, 0, 0, 0, 106.0);   // home plate
        Read(HomePitcher);
        Assert.AreEqual(3, GetResultCount(ResultAccepted));
        // The context (and the controller it came from) is only read: runners, roles and the
        // hold are unchanged, and the receiver has no path to MatchState or PlayState.
        Assert.AreEqual(AwayRunner, _ctxRunners[0], "the tagged runner is still active");
        Assert.AreEqual(revision, controller.GetMatchRevision(), "no MatchState change");
        Assert.AreEqual(outs, controller.GetOuts(), "a tag is not an out");
        Assert.AreEqual(runs, controller.GetRuns(RosterRules.TeamAway), "touching home is not a run");
        Assert.AreEqual(onFirst, controller.GetRunnerOnBase(1), "touching a base awards nothing");
    }
}
