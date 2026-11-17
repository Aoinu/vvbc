using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// Flight-time bookkeeping of the wake, and Pitch snapshots that arrive again or late. These
// check which snapshot becomes the accepted state; launching needs a Rigidbody and the
// server clock (IsLocalPitch), neither of which a test run has.
public partial class BallBehaviour
{
    private void ArriveWake(int sequence, int kind, int publisher, double time, WakeProfile wake)
    {
        _sequence = sequence;
        _kind = kind;
        _position = Vector3.up;
        _velocity = Vector3.forward * 33f;
        _spin = Vector3.left * 200f;
        _serverTime = time;
        _batterId = -1;
        _hitStrength = 0f;
        _hold = HoldFree;
        _holderId = 0;
        _publisherId = publisher;
        _wakeMode = wake.mode;
        _wakeCoefficient = wake.coefficient;
        _wakeDirection = wake.direction;
        _wakeConeAngle = wake.coneAngle;
        _wakePhase = wake.initialPhase;
        ReceiveSnapshot();
    }

    [TsukimiTest(maxSteps: 2000000)]
    public void ContactExtrapolationReadsTheWakeAtTheNewestSampleFlightTime()
    {
        WakeProfile profile = TestWake(BallAerodynamics.WakeRotating, 0.04f, Vector3.forward, Mathf.PI * 0.5f, 0.3f);
        Vector3 spin = Vector3.forward * 20f;
        RecordMotion(2f, new Vector3(0f, 1.5f, 5f), new Vector3(0f, -1f, 38f), spin);
        RecordMotion(2.02f, new Vector3(0f, 1.48f, 5.76f), new Vector3(0f, -1.2f, 38f), spin);
        _wake = BallAerodynamics.PrepareWake(profile, spin);
        _wakeSampleTime = 0.41f;
        Assert.IsTrue(SampleContactMotion(2.045f, 0.03f, Vector3.down * 9.81f, 0.02f));
        BallFlightState expected = new BallFlightState();
        expected.position = new Vector3(0f, 1.48f, 5.76f);
        expected.velocity = new Vector3(0f, -1.2f, 38f);
        expected.time = 0.41f;
        expected = BallFlightModel.Advance(expected, spin, Vector3.down * 9.81f, dragCoefficient, magnusCoefficient,
            _wake, 2.045f - 2.02f, 0.02f);
        Assert.IsTrue(Vector3.Distance(expected.position, sampledPosition) < 0.000001f);
        Assert.IsTrue(Vector3.Distance(expected.velocity, sampledVelocity) < 0.000001f);
        BallFlightState wrongBase = new BallFlightState();
        wrongBase.position = new Vector3(0f, 1.48f, 5.76f);
        wrongBase.velocity = new Vector3(0f, -1.2f, 38f);
        wrongBase = BallFlightModel.Advance(wrongBase, spin, Vector3.down * 9.81f, dragCoefficient, magnusCoefficient,
            _wake, 2.045f - 2.02f, 0.02f);
        Assert.IsTrue(Vector3.Distance(wrongBase.velocity, sampledVelocity) > 0.0001f, "the sample's flight time matters");
    }

    [TsukimiTest(maxSteps: 2000000)]
    public void CatchUpByANonStepLatenessContinuesAtTheReachedFlightTime()
    {
        WakeProfile profile = TestWake(BallAerodynamics.WakeRotating, 0.04f, Vector3.forward, Mathf.PI * 0.5f, 1.1f);
        Vector3 spin = Vector3.forward * 15f;
        BallWake wake = BallAerodynamics.PrepareWake(profile, spin);
        BallFlightState caught = AdvanceSnapshot(new Vector3(0f, 1.75f, 0f), new Vector3(0f, 0.5f, 33f), spin,
            Vector3.down * 9.81f, dragCoefficient, magnusCoefficient, wake, 0.37f, 0.02f, 0f);
        Assert.IsTrue(Mathf.Abs(caught.time - 0.37f) < 0.00001f);
        // The same lateness stepped by hand: 18 fixed steps, one 0.01 partial step.
        BallFlightState manual = new BallFlightState();
        manual.position = new Vector3(0f, 1.75f, 0f);
        manual.velocity = new Vector3(0f, 0.5f, 33f);
        for (int i = 0; i < 18; i++) manual = BallFlightModel.Step(manual, spin, Vector3.down * 9.81f, dragCoefficient, magnusCoefficient, wake, 0.02f);
        manual = BallFlightModel.Step(manual, spin, Vector3.down * 9.81f, dragCoefficient, magnusCoefficient, wake, 0.01f);
        Assert.IsTrue(Vector3.Distance(caught.position, manual.position) < 0.00001f);
        // The flight then continues from 0.37 s, as FixedUpdate does from _wakeTime.
        Vector3 next = BallAerodynamics.WakeAcceleration(wake, caught.velocity, caught.time);
        Vector3 fromZero = BallAerodynamics.WakeAcceleration(wake, caught.velocity, 0f);
        Assert.IsTrue(Vector3.Distance(next, fromZero) > 0.01f, "the phase is not restarted");
    }

    [TsukimiTest]
    public void ALatePitchNeverReplacesTheHitThatFollowedIt()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi me = Mimic.Join();
        Mimic.Become(me);
        Networking.SetOwner(pitcher, gameObject);
        WakeProfile arm = TestWake(BallAerodynamics.WakeFixed, 0.03f, Vector3.right, 0f, 0f);
        ArriveWake(3, HitState, pitcher.playerId, 11.0, new WakeProfile());
        // The same publisher's older Pitch.
        ArriveWake(2, PitchState, pitcher.playerId, 10.0, arm);
        Assert.AreEqual(HitState, _acceptedKind);
        Assert.AreEqual(BallAerodynamics.WakeNone, _acceptedWake.mode);
        // The hitter owns the ball now; the old owner's Pitch only becomes its candidate.
        Networking.SetOwner(me, gameObject);
        QueueHit(Vector3.one, Vector3.forward * 40f, Vector3.left * 100f, 11.5, me.playerId, 1f);
        int hitSequence = _acceptedSequence;
        ArriveWake(4, PitchState, pitcher.playerId, 10.0, arm);
        Assert.AreEqual(HitState, _acceptedKind);
        Assert.AreEqual(hitSequence, _acceptedSequence);
        Assert.AreEqual(BallAerodynamics.WakeNone, _acceptedWake.mode);
    }

    [TsukimiTest]
    public void ALatePitchNeverReplacesACatchAndThrow()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi me = Mimic.Join();
        Mimic.Become(me);
        Networking.SetOwner(me, gameObject);
        QueueSnapshot(PossessionState, Vector3.one, Vector3.zero, Vector3.zero, 10.5, -1, 0f, HoldGlove, me.playerId);
        QueueSnapshot(ThrowState, Vector3.one, new Vector3(0f, 3f, 25f), Vector3.right, 11.0, -1, 0f, HoldFree, 0);
        Assert.AreEqual(ThrowState, _acceptedKind);
        int throwSequence = _acceptedSequence;
        ArriveWake(1, PitchState, pitcher.playerId, 10.0, TestWake(BallAerodynamics.WakeFixed, 0.03f, Vector3.right, 0f, 0f));
        Assert.AreEqual(ThrowState, _acceptedKind);
        Assert.AreEqual(throwSequence, _acceptedSequence);
        Assert.AreEqual(BallAerodynamics.WakeNone, _acceptedWake.mode);
        OnPreSerialization();
        Assert.AreEqual(ThrowState, _kind);
        Assert.AreEqual(BallAerodynamics.WakeNone, _wakeMode, "the throw is serialized without a wake");
    }

    [TsukimiTest]
    public void AnOwnerRepublishOfThePitchKeepsItsWakeEverywhere()
    {
        VRCPlayerApi a = Mimic.Join();
        VRCPlayerApi b = Mimic.Join();
        VRCPlayerApi c = Mimic.Join();
        Mimic.Become(a);
        QueuePitch(Vector3.up, Vector3.forward * 30f, Vector3.left, 10.0, BallAerodynamics.WakeRotating, 0.04f, Vector3.forward, 1.5f, 0.6f);
        Mimic.Deliver();
        Mimic.Become(c);
        Assert.AreEqual(BallAerodynamics.WakeRotating, _acceptedWake.mode);
        Mimic.Become(b);
        Networking.SetOwner(b, gameObject);
        Mimic.Deliver();
        Mimic.Become(c);
        Assert.AreEqual(b.playerId, _acceptedPublisherId, "the new owner's republish is accepted");
        Assert.AreEqual(PitchState, _acceptedKind);
        Assert.AreEqual(BallAerodynamics.WakeRotating, _acceptedWake.mode);
        Assert.AreEqual(0.6f, _acceptedWake.initialPhase);
    }
}
