using Tsukimi;
using UnityEngine;

public partial class BallFlightBehaviour
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
}
