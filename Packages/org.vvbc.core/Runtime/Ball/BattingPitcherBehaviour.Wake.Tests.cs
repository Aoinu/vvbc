using Tsukimi;
using UnityEngine;

public partial class BattingPitcherBehaviour
{
    [TsukimiTest]
    public void PitchTypeWakeIsOptionalAndClamped()
    {
        pitchWakeVectors = new Vector3[] { Vector3.zero, new Vector3(0.027f, -0.0134f, 0f), new Vector3(-1f, 0f, 0f) };
        Assert.AreEqual(BallAerodynamics.WakeNone, TypeWake(0).mode, "a zero entry");
        Assert.AreEqual(BallAerodynamics.WakeNone, TypeWake(5).mode, "no entry");
        WakeProfile sinker = TypeWake(1);
        Assert.AreEqual(BallAerodynamics.WakeFixed, sinker.mode);
        Assert.IsTrue(Mathf.Abs(sinker.coefficient - 0.03014f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(sinker.direction.magnitude - 1f) < 0.0001f);
        Assert.AreEqual(BallAerodynamics.MaxWakeCoefficient, TypeWake(2).coefficient);
        Assert.AreEqual(Vector3.left, TypeWake(2).direction);
        pitchWakeVectors = new Vector3[0];
        Assert.AreEqual(BallAerodynamics.WakeNone, TypeWake(1).mode, "the default bakes no wake");
    }

    // The synced schedule fields (declared [UdonSynced]) are what every client copies into the
    // pitch it plays; the release itself reads the server time and is covered in play mode.
    [TsukimiTest]
    public void TheAcceptedScheduleCarriesItsWakeAndIsTakenOnce()
    {
        _pitchId = 1;
        _startTime = 5.0;
        _releaseWakeMode = BallAerodynamics.WakeRotating;
        _releaseWakeCoefficient = 0.04f;
        _releaseWakeDirection = Vector3.forward;
        _releaseWakeConeAngle = 1.5f;
        _releaseWakePhase = 0.25f;
        Assert.IsTrue(AcceptPitch(1f));
        Assert.AreEqual(1, _appliedId);
        Assert.AreEqual(BallAerodynamics.WakeRotating, _appliedWake.mode);
        Assert.AreEqual(0.04f, _appliedWake.coefficient);
        Assert.AreEqual(Vector3.forward, _appliedWake.direction);
        Assert.AreEqual(1.5f, _appliedWake.coneAngle);
        Assert.AreEqual(0.25f, _appliedWake.initialPhase);
        Assert.IsFalse(AcceptPitch(0f), "the same schedule is not taken twice");
        _releaseWakeMode = BallAerodynamics.WakeNone;
        Assert.IsTrue(AcceptPitch(1f));
        Assert.AreEqual(BallAerodynamics.WakeNone, _appliedWake.mode, "a newer schedule replaces the wake");
    }
}
