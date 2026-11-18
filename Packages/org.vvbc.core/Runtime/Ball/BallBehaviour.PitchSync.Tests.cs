using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// The Ball's side of manual-pitch sync: the PitchLineage in its snapshots, voiding an invalid
// pitch, the pitcher taking the ball back, and the end of a speculation. A test run has no
// Rigidbody, so flights are not flown here; the judge's decisions are PitchJudgeBehaviour.Tests.cs.
public partial class BallBehaviour
{
    [TsukimiTest]
    public void ThePitchIdTravelsFromTheHandPitchToItsHitAndEndsAtAStageOrRecall()
    {
        VRCPlayerApi me = Mimic.Join();
        PrepareReleaseLineage(3, 7, 9);
        QueueSnapshot(HandPitchState, Vector3.up, Vector3.forward * 38f, Vector3.zero, 4.0, -1, 0f, HoldFree, 0, new WakeProfile());
        Assert.AreEqual(me.playerId, _pitchPitcher);
        Assert.AreEqual(3, _pitchNumber);
        Assert.AreEqual(7, _pitchPrep);
        Assert.AreEqual(9, _pitchBatter);
        _lineage = _acceptedLineage;
        QueueHit(Vector3.one, Vector3.right, Vector3.up, 5.0, 17, 0.5f);
        Assert.AreEqual(3, _pitchNumber, "the hit keeps the pitch it was struck on");
        QueueSnapshot(PossessionState, Vector3.one, Vector3.zero, Vector3.zero, 5.5, -1, 0f, HoldGlove, me.playerId);
        Assert.AreEqual(3, _pitchNumber, "so does the catch");
        Stage(Vector3.zero);
        QueueSnapshot(StageState, Vector3.zero, Vector3.zero, Vector3.zero, 6.0, -1, 0f, HoldFree, 0);
        Assert.AreEqual(0, _pitchNumber, "a Stage starts no pitch");
        _lineage = _releaseLineage;
        RecallToLocalGlove(7.0);
        Assert.AreEqual(0, _pitchNumber, "a recall starts no pitch");
    }

    [TsukimiTest]
    public void AVoidedPitchOrItsHitIsNeverLaunchedAgain()
    {
        Mimic.Join();
        VoidPitch(4, 9, 10.0);
        Assert.IsTrue(PitchVoided(4, 9));
        _acceptedLineage = new PitchLineage();
        _acceptedLineage.pitcher = 4;
        _acceptedLineage.number = 9;
        _acceptedKind = HandPitchState;
        Assert.IsTrue(VoidedMotion(), "the invalid pitch arriving again");
        _acceptedKind = HitState;
        Assert.IsTrue(VoidedMotion(), "a hit on the invalid pitch");
        _acceptedKind = ThrowState;
        Assert.IsTrue(VoidedMotion(), "a throw from the invalid pitch");
        _acceptedKind = PossessionState;
        Assert.IsTrue(VoidedMotion(), "a catch from the invalid pitch");
        _acceptedKind = HandPitchState;
        _acceptedLineage.number = 10;
        Assert.IsFalse(VoidedMotion(), "the next pitch");
        // Applying the voided pitch launches nothing and never asks the judge.
        _acceptedLineage.number = 9;
        _acceptedSequence = 3;
        ApplyAccepted();
        Assert.IsFalse(flying);
    }

    [TsukimiTest]
    public void ThePitcherTakesBackTheBallOfItsInvalidPitch()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        PrepareReleaseLineage(1, 4, 2);
        _lineage = _releaseLineage;
        _acceptedLineage = _releaseLineage;
        _acceptedKind = HandPitchState;
        _acceptedPublisherId = pitcher.playerId;
        _acceptedSequence = 3;
        VoidPitch(pitcher.playerId, 1, 20.0);
        Assert.AreEqual(HoldGlove, _localHold, "in the pitcher's glove again");
        Assert.AreEqual(PossessionState, _acceptedKind, "published as an ordinary possession");
        Assert.AreEqual(pitcher.playerId, _acceptedHolderId);
        Assert.IsFalse(_acceptedLineage.IsPitch(), "the recalled ball comes from no pitch");
        Assert.IsTrue(contactConsumed, "nothing of the invalid pitch can be hit any more");
    }

    [TsukimiTest]
    public void AnOldInvalidVerdictDoesNotTakeTheNextPitchBack()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        PrepareReleaseLineage(2, 4, 3);
        _lineage = _releaseLineage;
        _acceptedLineage = _releaseLineage;
        _acceptedKind = HandPitchState;
        _acceptedPublisherId = pitcher.playerId;
        VoidPitch(pitcher.playerId, 1, 20.0);
        Assert.AreEqual(HandPitchState, _acceptedKind, "pitch 2 is not recalled for pitch 1");
        Assert.IsTrue(_lineage.SamePitch(pitcher.playerId, 2));
        Assert.IsTrue(PitchVoided(pitcher.playerId, 1));
        Assert.IsFalse(PitchVoided(pitcher.playerId, 2));
    }

    [TsukimiTest]
    public void AnotherPlayersInvalidPitchIsNotRecalledHere()
    {
        Mimic.Join();
        _lineage.pitcher = 40;
        _lineage.number = 1;
        _acceptedLineage = _lineage;
        _acceptedKind = HandPitchState;
        flying = true;
        VoidPitch(40, 1, 20.0);
        Assert.IsFalse(flying);
        Assert.IsTrue(contactConsumed);
        Assert.AreEqual(HandPitchState, _acceptedKind, "only the pitcher recalls; others wait for it");
        Assert.AreEqual(HoldFree, _localHold);
    }

    [TsukimiTest]
    public void AnOlderPitchSnapshotNeverReplacesTheCatchOrHitAfterIt()
    {
        Mimic.Join();
        _acceptedSequence = 10;
        _acceptedKind = PossessionState;
        _acceptedPublisherId = 3;
        Assert.AreEqual(SnapshotIgnore, SnapshotDecision(9, HandPitchState, 3, 3), "the pitch before the catch");
        _acceptedKind = HitState;
        Assert.AreEqual(SnapshotIgnore, SnapshotDecision(10, HandPitchState, 3, 3), "the pitch the hit answered");
    }

    [TsukimiTest]
    public void TheLineageIsPartOfTheSameStateComparison()
    {
        Mimic.Join();
        _acceptedSequence = 4;
        _acceptedKind = HandPitchState;
        _acceptedBatterId = -1;
        _acceptedLineage.pitcher = 5;
        _acceptedLineage.number = 2;
        CopyAccepted();
        Assert.IsTrue(SameAsAccepted());
        _pitchNumber = 3;
        Assert.IsFalse(SameAsAccepted(), "another pitch with the same flight is not the same state");
    }

    [TsukimiTest]
    public void AnyHoldStageOrNewFlightEndsASpeculation()
    {
        Mimic.Join();
        _speculative = true;
        EnterHold(HoldGlove, 1, Vector3.zero);
        Assert.IsFalse(_speculative, "a hold");
        _speculative = true;
        Stage(Vector3.zero);
        Assert.IsFalse(_speculative, "a Stage");
        Assert.IsFalse(_lineage.IsPitch(), "a Stage ends the lineage");
        Assert.IsFalse(BeginSpeculativePitch(1, 1, Vector3.zero, Vector3.forward, Vector3.zero, 0, 0f, Vector3.zero, 0f),
            "nothing to fly without the pitcher's hand holding the ball");
    }

    [TsukimiTest]
    public void VoidStopsSpeculationOfOnlyTheIdentifiedAttempt()
    {
        Mimic.Join();
        _speculative = true;
        _speculativePitcher = 4;
        _speculativeNumber = 2;
        flying = true;
        VoidPitch(4, 1, 10.0);
        Assert.IsTrue(SpeculationActive());
        VoidPitch(4, 2, 10.0);
        Assert.IsFalse(SpeculationActive());
        Assert.IsFalse(flying);
        Assert.IsTrue(_voidPresentation);
        Assert.IsTrue(contactConsumed);
    }

    [TsukimiTest]
    public void RepublishedHandPitchCannotRestartFlightOrOverwriteADescendant()
    {
        Mimic.Join();
        VRCPlayerApi pitcher = Mimic.Join();
        Networking.SetOwner(pitcher, gameObject);
        _acceptedKind = HandPitchState;
        _acceptedLineage.pitcher = pitcher.playerId;
        _acceptedLineage.number = 4;
        _acceptedServerTime = 5.0;
        _acceptedSequence = 3;
        _acceptedPublisherId = pitcher.playerId;
        MarkHandPitchApplied(_acceptedLineage);
        CopyAccepted();
        _sequence = 4;
        int before = flightSequence;
        ReceiveSnapshot();
        Assert.AreEqual(before, flightSequence);
        Assert.AreEqual(4, _acceptedSequence, "same pitch can still be adopted by the owner");
        _acceptedKind = PossessionState;
        _acceptedHold = HoldGlove;
        _acceptedServerTime = 6.0;
        _sequence = 5;
        ReceiveSnapshot();
        Assert.AreEqual(PossessionState, _acceptedKind);
        Assert.AreEqual(before, flightSequence);
        _acceptedKind = HitState;
        ReceiveSnapshot();
        Assert.AreEqual(HitState, _acceptedKind);
    }

    [TsukimiTest]
    public void VoidedSnapshotsAndPendingFieldingCannotReplaceThePitchersRecall()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        PrepareReleaseLineage(5, 3, other.playerId);
        _lineage = _releaseLineage;
        _acceptedLineage = _releaseLineage;
        _acceptedKind = HandPitchState;
        _acceptedPublisherId = pitcher.playerId;
        _acceptedSequence = 1;
        RecordPending(ThrowState, Vector3.zero, Vector3.up, Vector3.zero, 6.0, -1, 0f, HoldFree, 0);
        VoidPitch(pitcher.playerId, 5, 7.0);
        Assert.IsFalse(_pending);
        Mimic.Deliver(other);
        Mimic.Become(other);
        Assert.AreEqual(HoldGlove, LocalHoldState());
        Assert.AreEqual(pitcher.playerId, LocalHolderId());
        VoidPitch(pitcher.playerId, 5, 7.2);
        Assert.AreEqual(HoldGlove, LocalHoldState(), "late cancellation preserves the recalled ball");
        _pitchPitcher = pitcher.playerId;
        _pitchNumber = 5;
        _sequence = 100;
        byte[] kinds = new byte[] { HandPitchState, HitState, PossessionState, ThrowState };
        for (int i = 0; i < kinds.Length; i++)
        {
            _kind = kinds[i];
            ReceiveSnapshot();
            Assert.AreEqual(HoldGlove, LocalHoldState());
            Assert.AreEqual(PossessionState, _acceptedKind);
            Assert.IsFalse(flying);
        }
    }

    [TsukimiTest]
    public void ConsecutiveHandPossessionsCarryDifferentExactAttemptIds()
    {
        VRCPlayerApi player = Mimic.Join();
        VRCPlayerApi observer = Mimic.Join();
        int first = 16777217;
        QueueSnapshot(PossessionState, Vector3.zero, Vector3.zero, EncodeHandSession(first), 5.0, -1, 0f, HoldHand, player.playerId);
        Mimic.Deliver(observer);
        Mimic.Become(observer);
        Assert.AreEqual(first, AcceptedPitchAttempt(player.playerId));
        Mimic.Become(player);
        QueueSnapshot(PossessionState, Vector3.zero, Vector3.zero, EncodeHandSession(first + 1), 6.0, -1, 0f, HoldHand, player.playerId);
        Mimic.Deliver(observer);
        Mimic.Become(observer);
        Assert.AreEqual(first + 1, AcceptedPitchAttempt(player.playerId));
    }
}
