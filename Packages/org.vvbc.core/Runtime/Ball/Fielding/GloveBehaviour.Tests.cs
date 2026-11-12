using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

public partial class GloveBehaviour
{
    [TsukimiTest]
    public void ScaleStepsStayOnTheGridAndInsideTheLimits()
    {
        Assert.IsTrue(Mathf.Abs(FieldingMath.StepScale(1f, 1, 0.05f, 0.8f, 1.2f) - 1.05f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(FieldingMath.StepScale(1.0249f, 1, 0.05f, 0.8f, 1.2f) - 1.05f) < 0.0001f, "snaps to the grid");
        Assert.IsTrue(Mathf.Abs(FieldingMath.StepScale(1.2f, 1, 0.05f, 0.8f, 1.2f) - 1.2f) < 0.0001f, "upper limit");
        Assert.IsTrue(Mathf.Abs(FieldingMath.StepScale(0.8f, -1, 0.05f, 0.8f, 1.2f) - 0.8f) < 0.0001f, "lower limit");
    }

    [TsukimiTest]
    public void FittingLimitsRejectFarOffsetsAndOutOfRangeScale()
    {
        Assert.IsTrue(FieldingMath.FittingValid(new Vector3(0.3f, 0f, 0f), 1f, 0.3f, 0.8f, 1.2f));
        Assert.IsFalse(FieldingMath.FittingValid(new Vector3(0.2f, 0.2f, 0.2f), 1f, 0.3f, 0.8f, 1.2f), "0.35 m");
        Assert.IsFalse(FieldingMath.FittingValid(Vector3.zero, 1.25f, 0.3f, 0.8f, 1.2f));
        Assert.IsFalse(FieldingMath.FittingValid(Vector3.zero, 0.75f, 0.3f, 0.8f, 1.2f));
        Assert.IsFalse(FieldingMath.FittingValid(new Vector3(float.NaN, 0f, 0f), 1f, 0.3f, 0.8f, 1.2f));
    }

    [TsukimiTest]
    public void InvalidBonePosesAreRejected()
    {
        Assert.IsFalse(FieldingMath.ValidPose(Vector3.zero, Quaternion.identity), "missing bone");
        Assert.IsFalse(FieldingMath.ValidPose(Vector3.one, new Quaternion(0f, 0f, 0f, 0f)), "zero rotation");
        Assert.IsFalse(FieldingMath.ValidPose(new Vector3(float.PositiveInfinity, 1f, 1f), Quaternion.identity));
        Assert.IsTrue(FieldingMath.ValidPose(new Vector3(0f, 1.2f, 0.3f), Quaternion.Euler(10f, 20f, 30f)));
    }

    [TsukimiTest]
    public void SaveCommitsTheDraftAndSendsIt()
    {
        Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        Start();
        Assert.IsFalse(equipped);
        Assert.IsTrue(BeginCalibration());
        Assert.IsTrue(equipped, "adjusting wears the glove");
        StepDraftScale(1);
        Assert.AreEqual(1f, gloveScale, "the synced scale waits for Save");
        Assert.IsTrue(CommitDraft());
        Assert.IsFalse(IsCalibrating());
        Assert.IsTrue(Mathf.Abs(gloveScale - 1.05f) < 0.0001f);
        Mimic.Deliver();
        Mimic.Become(other);
        Assert.IsTrue(equipped);
        Assert.IsTrue(Mathf.Abs(gloveScale - 1.05f) < 0.0001f, "remote clients receive the fitting");
    }

    [TsukimiTest]
    public void CancelRestoresTheCommittedFitting()
    {
        Mimic.Join();
        Start();
        BeginCalibration();
        StepDraftScale(1);
        StepDraftScale(1);
        SetDraftThrowHand(HandLeft);
        Assert.IsTrue(Mathf.Abs(DraftScale() - 1.1f) < 0.0001f);
        CancelCalibration();
        Assert.IsFalse(IsCalibrating());
        Assert.AreEqual(1f, DraftScale());
        Assert.AreEqual(HandRight, DraftThrowHand());
        Assert.AreEqual(HandRight, throwHand);
    }

    [TsukimiTest]
    public void SaveIsRefusedOutsideTheLimits()
    {
        Mimic.Join();
        Start();
        BeginCalibration();
        _activePosition = new Vector3(0.31f, 0f, 0f);
        Assert.IsFalse(DraftValid());
        Assert.IsFalse(ConfirmCalibration());
        Assert.IsFalse(CommitDraft());
        Assert.IsTrue(IsCalibrating(), "still adjusting");
        Assert.AreEqual(Vector3.zero, offsetPosition, "nothing committed");
    }

    [TsukimiTest]
    public void SwitchingTheThrowingHandStartsFromTheDefaults()
    {
        Mimic.Join();
        defaultOffsetPosition = new Vector3(0f, 0.02f, 0.05f);
        Start();
        BeginCalibration();
        _activePosition = new Vector3(0.1f, 0f, 0f);
        SetDraftThrowHand(HandLeft);
        Assert.AreEqual(HandLeft, DraftThrowHand());
        Assert.AreEqual(new Vector3(0f, 0.02f, 0.05f), _activePosition);
    }

    [TsukimiTest]
    public void RestoredFittingIsAppliedOnlyWithinTheLimits()
    {
        Mimic.Join();
        Start();
        Assert.IsTrue(RestoreFitting(HandLeft, new Vector3(0.1f, 0f, 0.05f), Quaternion.Euler(0f, 90f, 0f), 0.9f));
        Assert.AreEqual(HandLeft, throwHand);
        Assert.IsTrue(Mathf.Abs(gloveScale - 0.9f) < 0.0001f);
        Assert.IsFalse(RestoreFitting(HandRight, new Vector3(0.4f, 0f, 0f), Quaternion.identity, 1f), "too far");
        Assert.IsFalse(RestoreFitting(3, Vector3.zero, Quaternion.identity, 1f), "no such hand");
        Assert.AreEqual(HandLeft, throwHand, "an invalid store changes nothing");
    }

    [TsukimiTest]
    public void ALateJoinerKeepsTheReceivedFittingWhenItStarts()
    {
        Mimic.Join();
        Start();
        RestoreFitting(HandLeft, new Vector3(0.05f, 0f, 0f), Quaternion.identity, 1.1f);
        Mimic.Deliver();
        VRCPlayerApi late = Mimic.Join();
        Mimic.Become(late);
        Start();
        Assert.AreEqual(HandLeft, throwHand);
        Assert.IsTrue(Mathf.Abs(gloveScale - 1.1f) < 0.0001f);
    }

    [TsukimiTest]
    public void OnlyTheOwnerChangesTheGlove()
    {
        Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        Mimic.Become(other);
        Start();
        SetEquipped(true);
        Assert.IsFalse(equipped);
        Assert.IsFalse(BeginCalibration());
    }

    [TsukimiTest]
    public void AGloveHoldingTheBallCannotBeRemovedOrAdjusted()
    {
        Mimic.Join();
        Start();
        SetEquipped(true);
        HoldBall(transform, BallInSocket, Vector3.zero);
        Assert.IsTrue(HoldsBall());
        SetEquipped(false);
        Assert.IsTrue(equipped);
        Assert.IsFalse(BeginCalibration());
        ReleaseBall();
        Assert.IsFalse(HoldsBall());
        SetEquipped(false);
        Assert.IsFalse(equipped);
    }

    [CostLimit(steps: 49, externs: 3)]
    [TsukimiTest]
    public void AnIdleGloveReturnsImmediately()
    {
        PostLateUpdate();
    }
}
