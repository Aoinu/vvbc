using Tsukimi;
using UnityEngine;

public partial class BallBehaviour
{
    [TsukimiTest]
    public void BackspinProducesUpwardMagnusAndDragOpposesMotion()
    {
        Vector3 velocity = new Vector3(0f, 0f, 40f);
        Vector3 spin = Vector3.Cross(Vector3.forward, Vector3.up).normalized * (2500f * 2f * Mathf.PI / 60f);
        Vector3 acceleration = BallAerodynamics.Acceleration(velocity, spin, 0.00622f, 0.00063f);
        Assert.IsTrue(acceleration.y > 6.5f && acceleration.y < 6.7f);
        Assert.IsTrue(acceleration.z < -9.9f && acceleration.z > -10.0f);
        Assert.IsTrue(Mathf.Abs(spin.magnitude - 261.7994f) < 0.01f);
    }

    [TsukimiTest]
    public void NoMotionProducesNoAerodynamicForce()
    {
        Assert.AreEqual(Vector3.zero, BallAerodynamics.Acceleration(Vector3.zero, Vector3.one, 0.00622f, 0.00063f));
    }

    [TsukimiTest]
    public void MotionSamplesInterpolateAndRejectTimesOutsideHistory()
    {
        RecordMotion(1f, Vector3.zero, Vector3.right, Vector3.up);
        RecordMotion(2f, Vector3.right * 2f, Vector3.right * 3f, Vector3.up * 3f);
        Assert.IsFalse(TrySampleMotion(0.9f));
        Assert.IsTrue(TrySampleMotion(1.5f));
        Assert.AreEqual(Vector3.right, sampledPosition);
        Assert.AreEqual(Vector3.right * 2f, sampledVelocity);
        Assert.AreEqual(Vector3.up * 2f, sampledAngularVelocity);
        Assert.IsFalse(TrySampleMotion(2.1f));
        Stage(Vector3.zero);
        Assert.IsFalse(TrySampleMotion(1.5f));
    }

    [TsukimiTest]
    public void MotionHistoryDiscardsOldestSamples()
    {
        for (int i = 0; i < 70; i++)
            RecordMotion(i, Vector3.right * i, Vector3.zero, Vector3.zero);
        Assert.IsFalse(TrySampleMotion(5f));
        Assert.IsTrue(TrySampleMotion(6f));
        Assert.AreEqual(Vector3.right * 6f, sampledPosition);
        Assert.IsTrue(TrySampleMotion(68.5f));
        Assert.AreEqual(Vector3.right * 68.5f, sampledPosition);
        Assert.IsFalse(TrySampleMotion(70f));
    }

    [TsukimiTest]
    public void ContactReconstructionUsesHermiteDerivativeAndDoesNotStorePrediction()
    {
        RecordMotion(1f, Vector3.zero, Vector3.zero, Vector3.zero);
        RecordMotion(2f, Vector3.right * 2f, Vector3.right * 4f, Vector3.up);
        Assert.IsTrue(SampleContactMotion(1.5f, 0.025f, Vector3.down * 9.81f, 0.02f));
        Assert.IsTrue(Mathf.Abs(sampledPosition.x - 0.5f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(sampledVelocity.x - 2f) < 0.0001f);
        Assert.IsTrue(SampleContactMotion(2.02f, 0.025f, Vector3.down * 9.81f, 0.02f));
        Assert.IsFalse(SampleContactMotion(2.04f, 0.1f, Vector3.down * 9.81f, 0.02f));
        Assert.IsFalse(TrySampleMotion(2.02f));
    }

    [TsukimiTest]
    public void ContactHistoryFindsFixedSampleBoundaries()
    {
        RecordMotion(1f, Vector3.zero, Vector3.forward, Vector3.zero);
        RecordMotion(1.02f, Vector3.forward, Vector3.forward, Vector3.zero);
        RecordMotion(1.04f, Vector3.forward * 2f, Vector3.forward, Vector3.zero);
        Assert.IsTrue(Mathf.Abs(NextMotionSampleTime(1.01f, 2f) - 1.02f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(NextMotionSampleTime(1.02f, 2f) - 1.04f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(NextMotionSampleTime(1.04f, 1.05f) - 1.05f) < 0.00001f);
    }

    [TsukimiTest]
    public void StagingDiscardsPendingResultAndAdvancesGeneration()
    {
        flightSequence = 4;
        contactConsumed = true;
        _contactPending = true;
        _pendingSequence = 4;
        Stage(Vector3.zero);
        Assert.AreEqual(5, flightSequence);
        Assert.IsFalse(contactConsumed);
        Assert.IsFalse(_contactPending);
        Assert.IsFalse(ApplyContactResult(4, 1f, Vector3.zero, Vector3.zero,
            Vector3.zero, Vector3.forward, Vector3.zero, 1, 0.5f));
    }

    [TsukimiTest]
    public void ContactFlightAdvancesFromContactTimeInFixedAndPartialSteps()
    {
        BallFlightState state = new BallFlightState();
        state.position = new Vector3(1f, 2f, 3f);
        state.velocity = Vector3.right * 10f;
        BallFlightState same = BallFlightModel.Advance(state, Vector3.zero, Vector3.down * 10f, 0f, 0f, 0f, 0.02f);
        Assert.AreEqual(state.position, same.position);
        BallFlightState later = BallFlightModel.Advance(state, Vector3.zero, Vector3.down * 10f, 0f, 0f, 0.03f, 0.02f);
        Assert.IsTrue(Mathf.Abs(later.position.x - 1.3f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(later.position.y - 1.993f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(later.velocity.y + 0.3f) < 0.0001f);
    }
}
