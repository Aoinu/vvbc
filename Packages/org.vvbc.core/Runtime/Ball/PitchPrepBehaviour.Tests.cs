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
        Assert.AreEqual(0, cancelPitchNumber, "not before the check is due");
        // The batter is not on the plate any more (it has no occupant) and judged nothing.
        CheckVerdictAt(1000f, PrepServer + 3.0);
        Assert.AreEqual(number, cancelPitchNumber);
        Assert.AreEqual(batter.playerId, cancelBatterId);
        Assert.AreEqual(PitchJudgeMath.ReasonBatterChanged, cancelReason);
        Assert.IsTrue(judge.HasVerdictFor(pitcher.playerId, number), "the pitcher follows its own cancellation");
        Assert.AreEqual(PitchJudgeMath.StatusCancelled, judge.KnownStatus());
        Mimic.Deliver();
        Mimic.Become(batter);
        Assert.AreEqual(number, cancelPitchNumber, "everyone receives the cancellation");
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
        Assert.AreEqual(0, cancelPitchNumber);
        Assert.AreEqual(PitchJudgeMath.StatusValid, judge.KnownStatus());
    }

    [TsukimiTest]
    public void APitchPreparedForNoBatterIsNeverWatched()
    {
        Mimic.Join();
        WatchPitch(4, -1, 5f);
        CheckVerdictAt(1000f, PrepServer);
        Assert.AreEqual(0, cancelPitchNumber);
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
