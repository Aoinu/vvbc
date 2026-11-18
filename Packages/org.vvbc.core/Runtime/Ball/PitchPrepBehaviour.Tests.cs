using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// The prepared data's publication, generations, expiry and the pitcher's cancellation, with
// the judge the runner wires. The judge's own sync is in PitchJudgeBehaviour.Tests.cs.
public partial class PitchPrepBehaviour
{
    private const double PrepServer = 2000.0;

    private void PublishForTest(int batter)
    {
        PublishCourse(batter, true, GloveBehaviour.HandLeft, new Vector3(-0.3f, 1.9f, 17.5f), new Vector3(-0.3f, 1.85f, 17.55f),
            new Vector3(-0.25f, 0.4f, 0.3f), Quaternion.identity, new Vector3(0.1f, 0.8f, 0.43f), Vector3.back, 37.7f,
            Vector3.right * 260f, BallAerodynamics.WakeFixed, 0.006f, Vector3.left, PrepServer);
    }

    [TsukimiTest]
    public void EachPublicationIsANewGenerationThatEveryoneReceives()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi batter = Mimic.Join();
        PublishForTest(batter.playerId);
        int first = prepGeneration;
        Assert.IsTrue(prepReady);
        Assert.AreEqual(pitcher.playerId, prepPitcherId);
        Assert.AreEqual(PitchTypeFourSeam, prepPitchType);
        Assert.IsTrue(UsableFor(pitcher.playerId, PrepServer + 10.0));
        Assert.IsFalse(UsableFor(batter.playerId, PrepServer + 10.0), "only for its pitcher");
        Assert.IsFalse(UsableFor(pitcher.playerId, PrepServer + prepLifetimeSeconds + 1.0), "expired");
        Mimic.Deliver();
        Mimic.Become(batter);
        Assert.AreEqual(first, prepGeneration);
        Assert.AreEqual(batter.playerId, prepTargetBatterId);
        Assert.AreEqual(GloveBehaviour.HandLeft, prepThrowHand);
        Assert.IsTrue(UsableFor(pitcher.playerId, PrepServer + 10.0), "the batter can use it");
        Mimic.Become(pitcher);
        Publish(false, PrepServer + 20.0);
        Assert.AreEqual(first + 1, prepGeneration, "withdrawing is a new generation too");
        Mimic.Deliver();
        Mimic.Become(batter);
        Assert.IsFalse(UsableFor(pitcher.playerId, PrepServer + 21.0), "withdrawn");
    }

    [TsukimiTest]
    public void AnUnchangedPublicationIsRefreshedOnlyNearItsEnd()
    {
        Mimic.Join();
        PublishForTest(5);
        int generation = prepGeneration;
        double expires = prepExpiresAt;
        RefreshIfExpiring(PrepServer + 100.0);
        Assert.AreEqual(expires, prepExpiresAt, "nothing is sent while far from the end");
        RefreshIfExpiring(PrepServer + prepLifetimeSeconds - 10.0);
        Assert.IsTrue(prepExpiresAt > expires, "refreshed");
        Assert.AreEqual(generation, prepGeneration, "same content, same generation");
    }

    [TsukimiTest]
    public void AnUnjudgedPitchWhoseBatterLeftIsCancelledForEveryone()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi batter = Mimic.Join();
        PublishForTest(batter.playerId);
        int number = 3;
        WatchPitch(number, batter.playerId, 5f);
        CheckVerdictAt(6f, PrepServer);
        Assert.AreEqual(0, _cancelSequence, "not before the check is due");
        // The batter is not on the plate any more (it has no occupant) and judged nothing.
        CheckVerdictAt(1000f, PrepServer + 3.0);
        Assert.AreEqual(PitchJudgeMath.StatusCancelled, judge.OutcomeStatus(pitcher.playerId, number));
        Assert.AreEqual(PitchJudgeMath.ReasonBatterChanged, judge.KnownReason());
        Assert.IsTrue(judge.HasVerdictFor(pitcher.playerId, number), "the pitcher follows its own cancellation");
        Assert.AreEqual(PitchJudgeMath.StatusCancelled, judge.KnownStatus());
        Mimic.Deliver();
        Mimic.Become(batter);
        Assert.AreEqual(PitchJudgeMath.StatusCancelled, judge.OutcomeStatus(pitcher.playerId, number), "everyone receives the cancellation");
    }

    [TsukimiTest]
    public void AJudgedPitchIsNotCancelled()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi batter = Mimic.Join();
        int number = 3;
        WatchPitch(number, batter.playerId, 5f);
        judge.ApplyOutcomeAt(pitcher.playerId, number, PitchJudgeMath.StatusValid, PitchJudgeMath.ReasonNone, 30f, true, 100f,
            PrepServer, PrepServer);
        CheckVerdictAt(1000f, PrepServer + 3.0);
        Assert.AreEqual(0, _cancelSequence);
        Assert.AreEqual(PitchJudgeMath.StatusValid, judge.KnownStatus());
    }

    [TsukimiTest]
    public void AStillPresentBatterWithoutAVerdictCancelsAtThreeSeconds()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi batter = Mimic.Join();
        PublishForTest(batter.playerId);
        WatchPitch(3, batter.playerId, 5f);
        CheckVerdictAt(7.9f, PrepServer, batter.playerId);
        Assert.AreEqual(0, _cancelSequence);
        CheckVerdictAt(8f, PrepServer + 3.0, batter.playerId);
        Assert.AreEqual(PitchJudgeMath.ReasonVerdictMissing, judge.KnownReason());
        Assert.AreEqual(PitchJudgeMath.StatusCancelled, judge.KnownStatus());
        Mimic.Deliver(batter);
        Mimic.Become(batter);
        Assert.AreEqual(3, judge.KnownNumber());
        Assert.AreEqual(PitchJudgeMath.StatusCancelled, judge.KnownStatus());
    }

    [TsukimiTest]
    public void AStaleCompletionCannotConfirmTheLatestGenerationAndFailureRetriesIt()
    {
        Mimic.Join();
        PublishForTest(8);
        OnPreSerialization();
        int first = prepGeneration;
        PublishForTest(9);
        FinishSend(true);
        Assert.IsFalse(CurrentSent());
        OnPreSerialization();
        Assert.AreEqual(first + 1, _serializedGeneration);
        FinishSend(false);
        Assert.IsTrue(_retryScheduled);
        for (int i = 0; i < MaximumRetries; i++)
        {
            RetrySend();
            OnPreSerialization();
            FinishSend(false);
        }
        Assert.IsTrue(SendFailed());
        Assert.IsFalse(_retryScheduled);
        PublishForTest(10);
        OnPreSerialization();
        FinishSend(false);
        RetrySend();
        OnPreSerialization();
        FinishSend(true);
        Assert.IsTrue(CurrentSent());
        Assert.IsFalse(SendFailed());
        Assert.AreEqual(10, prepTargetBatterId);
    }

    [TsukimiTest]
    public void AnotherHandPitchDoesNotReplaceAnEarlierTimeout()
    {
        Mimic.Join();
        WatchPitch(3, 2, 5f);
        WatchPitch(4, 2, 6f);
        CheckVerdictAt(8f, PrepServer);
        Assert.AreEqual(3, judge.KnownNumber());
        CheckVerdictAt(9f, PrepServer + 1.0);
        Assert.AreEqual(4, judge.KnownNumber());
    }

    [TsukimiTest]
    public void ANewerVerdictDoesNotConfirmAnEarlierPitch()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        WatchPitch(3, 2, 5f);
        WatchPitch(4, 2, 6f);
        judge.ApplyOutcomeAt(pitcher.playerId, 4, PitchJudgeMath.StatusValid, PitchJudgeMath.ReasonNone, 30f, true, 100f,
            PrepServer, PrepServer);
        CheckVerdictAt(9f, PrepServer + 3.0, 2);
        Assert.AreEqual(PitchJudgeMath.StatusCancelled, judge.OutcomeStatus(pitcher.playerId, 3));
        Assert.AreEqual(4, judge.KnownNumber());
        Assert.AreEqual(PitchJudgeMath.StatusValid, judge.KnownStatus());
    }

    [TsukimiTest(maxSteps: 20000000)]
    public void OneSerializationDeliversTwoCancellationsAndEarlierVerdictsCannotReviveThem()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi observer = Mimic.Join();
        PublishForTest(observer.playerId);
        WatchPitch(3, observer.playerId, 5f);
        WatchPitch(70, observer.playerId, 5f);
        CheckVerdictAt(8f, PrepServer + 3.0, observer.playerId);
        Mimic.Deliver();
        Mimic.Become(observer);
        Assert.AreEqual(PitchJudgeMath.StatusCancelled, judge.OutcomeStatus(pitcher.playerId, 3));
        Assert.AreEqual(PitchJudgeMath.StatusCancelled, judge.OutcomeStatus(pitcher.playerId, 70));
        judge.ApplyOutcomeAt(pitcher.playerId, 3, PitchJudgeMath.StatusValid, 0, 0f, true, 100f, PrepServer, PrepServer);
        Assert.AreEqual(PitchJudgeMath.StatusCancelled, judge.OutcomeStatus(pitcher.playerId, 3));
        VRCPlayerApi late = Mimic.Join();
        Mimic.Become(late);
        Assert.AreEqual(PitchJudgeMath.StatusCancelled, judge.OutcomeStatus(pitcher.playerId, 3));
        Assert.AreEqual(PitchJudgeMath.StatusCancelled, judge.OutcomeStatus(pitcher.playerId, 70));
    }

    [TsukimiTest(maxSteps: 20000000)]
    public void AFullCancellationHistoryDrainsWaitingRecordsAfterTheFirstSend()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi observer = Mimic.Join();
        int count = PitchDecisionHistory.Capacity + 1;
        for (int number = 1; number <= count; number++) WatchPitch(number, observer.playerId, 5f);
        CheckVerdictAt(8f, PrepServer, observer.playerId);
        Assert.AreEqual(count - 1, _cancelSequence);
        OnPreSerialization();
        Assert.AreEqual(count - 1, PitchDecisionHistory.WireCount(_cancelKeys, _cancelTiming, false));
        FinishSend(false);
        Assert.AreEqual(count - 1, _cancelSequence);
        Mimic.Deliver();
        Assert.AreEqual(count, _cancelSequence);
        Mimic.Deliver();
        Mimic.Become(observer);
        for (int number = 1; number <= count; number++)
            Assert.AreEqual(PitchJudgeMath.StatusCancelled, judge.OutcomeStatus(pitcher.playerId, number));
    }

    [TsukimiTest(maxSteps: 20000000)]
    public void AFullWatchQueueDefersOnlyAReleaseNeedingAVerdict()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        for (int number = 1; number <= MaximumWatches; number++)
            Assert.IsTrue(WatchPitch(number, 2, 5f));
        Assert.IsFalse(WatchPitch(MaximumWatches + 1, 2, 5f));
        Assert.IsTrue(WatchPitch(MaximumWatches + 1, -1, 5f));
        ConfirmVerdict(pitcher.playerId, 1);
        Assert.IsTrue(WatchPitch(MaximumWatches + 1, 2, 5f));
    }

    [TsukimiTest]
    public void APitchPreparedForNoBatterIsNeverWatched()
    {
        Mimic.Join();
        WatchPitch(4, -1, 5f);
        CheckVerdictAt(1000f, PrepServer);
        Assert.AreEqual(0, _cancelSequence);
    }

    [TsukimiTest]
    public void DataLeftByAPitcherWhoLeftIsWithdrawnByTheNextOwner()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        PublishForTest(5);
        Mimic.Deliver();
        Mimic.Become(other);
        int generation = prepGeneration;
        Networking.SetOwner(other, gameObject);
        Assert.IsFalse(prepReady, "a new owner that is not pitching withdraws it");
        Assert.AreEqual(generation + 1, prepGeneration);
    }
}
