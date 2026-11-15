using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// Seam-shifted wake: the shared model, the flights that end it, and the snapshots that carry it.
public partial class BallBehaviour
{
    private WakeProfile TestWake(int mode, float coefficient, Vector3 direction, float coneAngle, float phase)
    {
        WakeProfile wake = new WakeProfile();
        wake.mode = mode;
        wake.coefficient = coefficient;
        wake.direction = direction;
        wake.coneAngle = coneAngle;
        wake.initialPhase = phase;
        return wake;
    }

    [TsukimiTest(maxSteps: 2000000)]
    public void NoWakeReproducesTheLegacyModelExactly()
    {
        Vector3 gravity = Vector3.down * 9.81f;
        Vector3 spin = new Vector3(-180f, 40f, 25f);
        BallWake none = BallAerodynamics.PrepareWake(new WakeProfile(), spin);
        Assert.AreEqual(BallAerodynamics.WakeNone, none.mode);
        BallFlightState legacy = new BallFlightState();
        legacy.position = new Vector3(0f, 1.75f, 0f);
        legacy.velocity = new Vector3(0.4f, -0.8f, 41f);
        BallFlightState wake = legacy;
        BallFlightState decayed = legacy;
        Vector3 decayedSpin = spin;
        for (int i = 0; i < 40; i++)
        {
            Assert.AreEqual(BallAerodynamics.Acceleration(legacy.velocity, spin, 0.00622f, 0.00063f),
                BallAerodynamics.Acceleration(legacy.velocity, spin, 0.00622f, 0.00063f, none, i * 0.02f));
            legacy = BallFlightModel.Step(legacy, spin, gravity, 0.00622f, 0.00063f, 0.02f);
            wake = BallFlightModel.Step(wake, spin, gravity, 0.00622f, 0.00063f, none, 0.02f);
            Assert.AreEqual(legacy.position, wake.position);
            Assert.AreEqual(legacy.velocity, wake.velocity);
            decayed = BallFlightModel.Step(decayed, decayedSpin, gravity, 0.00622f, 0.00063f, 0.02f);
            decayedSpin = BallFlightModel.DecaySpin(decayedSpin, 1f, 0.02f);
        }
        BallFlightState start = new BallFlightState();
        start.position = new Vector3(0f, 1.75f, 0f);
        start.velocity = new Vector3(0.4f, -0.8f, 41f);
        BallFlightState advanced = BallFlightModel.AdvanceDecayingSpin(start, spin, gravity, 0.00622f, 0.00063f, 0.8f, 0.02f, 1f);
        Assert.IsTrue(Vector3.Distance(advanced.position, decayed.position) < 0.0001f);
        Assert.IsTrue(Vector3.Distance(advanced.spin, decayedSpin) < 0.001f);
        Assert.IsTrue(Mathf.Abs(advanced.time - 0.8f) < 0.0001f);
    }

    [TsukimiTest]
    public void FixedWakeIsPerpendicularToTheFlightAndScalesWithSpeed()
    {
        BallWake wake = BallAerodynamics.PrepareWake(TestWake(BallAerodynamics.WakeFixed, 0.03f, Vector3.right * 4f, 0f, 0f), Vector3.zero);
        Assert.AreEqual(BallAerodynamics.WakeFixed, wake.mode);
        Vector3 fast = BallAerodynamics.WakeAcceleration(wake, Vector3.forward * 40f, 0f);
        Assert.IsTrue(Vector3.Distance(fast, Vector3.right * 1.2f) < 0.0001f);
        Vector3 slow = BallAerodynamics.WakeAcceleration(wake, Vector3.forward * 20f, 9f);
        Assert.IsTrue(Vector3.Distance(slow, Vector3.right * 0.6f) < 0.0001f, "linear in speed and independent of time");
        // A base direction tilted toward the flight keeps only its perpendicular part, not renormalized.
        BallWake tilted = BallAerodynamics.PrepareWake(TestWake(BallAerodynamics.WakeFixed, 0.03f, new Vector3(1f, 0f, 1f), 0f, 0f), Vector3.zero);
        Vector3 velocity = new Vector3(0f, 0f, 40f);
        Vector3 a = BallAerodynamics.WakeAcceleration(tilted, velocity, 0f);
        Assert.IsTrue(Mathf.Abs(a.x - 1.2f * 0.7071068f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(Vector3.Dot(a, velocity)) < 0.001f, "the wake never speeds up or slows the ball");
        // Mirroring the profile mirrors the force.
        BallWake mirrored = BallAerodynamics.PrepareWake(TestWake(BallAerodynamics.WakeFixed, 0.03f, new Vector3(-1f, 0f, 1f), 0f, 0f), Vector3.zero);
        Vector3 b = BallAerodynamics.WakeAcceleration(mirrored, velocity, 0f);
        Assert.IsTrue(Vector3.Distance(b, new Vector3(-a.x, a.y, a.z)) < 0.0001f);
    }

    [TsukimiTest]
    public void InvalidWakeProfilesBecomeNoneAndStrongOnesAreClamped()
    {
        Vector3 spin = Vector3.forward * 10f;
        Assert.AreEqual(BallAerodynamics.WakeNone, BallAerodynamics.PrepareWake(TestWake(3, 0.03f, Vector3.right, 0f, 0f), spin).mode);
        Assert.AreEqual(BallAerodynamics.WakeNone, BallAerodynamics.PrepareWake(TestWake(1, 0f, Vector3.right, 0f, 0f), spin).mode);
        Assert.AreEqual(BallAerodynamics.WakeNone, BallAerodynamics.PrepareWake(TestWake(1, -0.01f, Vector3.right, 0f, 0f), spin).mode);
        Assert.AreEqual(BallAerodynamics.WakeNone, BallAerodynamics.PrepareWake(TestWake(1, float.NaN, Vector3.right, 0f, 0f), spin).mode);
        Assert.AreEqual(BallAerodynamics.WakeNone, BallAerodynamics.PrepareWake(TestWake(1, 0.03f, Vector3.zero, 0f, 0f), spin).mode);
        Assert.AreEqual(BallAerodynamics.WakeNone, BallAerodynamics.PrepareWake(TestWake(1, 0.03f, new Vector3(float.NaN, 0f, 0f), 0f, 0f), spin).mode);
        Assert.AreEqual(BallAerodynamics.WakeNone, BallAerodynamics.PrepareWake(TestWake(1, 0.03f, new Vector3(float.PositiveInfinity, 0f, 0f), 0f, 0f), spin).mode);
        Assert.AreEqual(BallAerodynamics.WakeNone, BallAerodynamics.PrepareWake(TestWake(2, 0.03f, Vector3.forward, float.NaN, 0f), spin).mode);
        Assert.AreEqual(BallAerodynamics.WakeNone, BallAerodynamics.PrepareWake(TestWake(2, 0.03f, Vector3.forward, 1f, float.PositiveInfinity), spin).mode);
        Assert.AreEqual(BallAerodynamics.WakeNone, BallAerodynamics.PrepareWake(TestWake(1, float.PositiveInfinity, Vector3.right, 0f, 0f), spin).mode, "+inf coefficient");
        Assert.AreEqual(BallAerodynamics.WakeNone, BallAerodynamics.PrepareWake(TestWake(2, float.PositiveInfinity, Vector3.forward, 1f, 0f), spin).mode, "+inf rotating coefficient");
        Assert.AreEqual(BallAerodynamics.WakeNone, BallAerodynamics.PrepareWake(TestWake(1, float.NegativeInfinity, Vector3.right, 0f, 0f), spin).mode, "-inf coefficient");
        Assert.AreEqual(BallAerodynamics.WakeNone, BallAerodynamics.PrepareWake(TestWake(1, 0.03f, new Vector3(0f, float.NegativeInfinity, 0f), 0f, 0f), spin).mode, "-inf direction");
        Assert.AreEqual(BallAerodynamics.WakeNone, BallAerodynamics.PrepareWake(TestWake(2, 0.03f, Vector3.forward, float.NegativeInfinity, 0f), spin).mode, "-inf cone");
        Assert.AreEqual(BallAerodynamics.WakeNone, BallAerodynamics.PrepareWake(TestWake(2, 0.03f, Vector3.forward, 1f, float.NaN), spin).mode, "NaN phase");
        Assert.AreEqual(BallAerodynamics.WakeNone, BallAerodynamics.PrepareWake(TestWake(2, 0.03f, Vector3.forward, 1f, 0f), new Vector3(0f, 0f, float.NaN)).mode, "NaN rate");
        Assert.AreEqual(BallAerodynamics.WakeNone, BallAerodynamics.PrepareWake(TestWake(2, 0.03f, Vector3.forward, 1f, 0f), Vector3.forward * float.PositiveInfinity).mode, "infinite rate");
        Assert.AreEqual(BallAerodynamics.WakeFixed, BallAerodynamics.PrepareWake(TestWake(1, 0.03f, Vector3.right, 0f, 0f), new Vector3(float.NaN, 0f, 0f)).mode, "Fixed never reads the spin");
        Assert.AreEqual(0.03f, BallAerodynamics.PrepareWake(TestWake(1, 0.03f, Vector3.right, 0f, 0f), spin).coefficient, "valid values are unchanged");
        Assert.AreEqual(BallAerodynamics.MaxWakeCoefficient, BallAerodynamics.PrepareWake(TestWake(1, 0.055f, Vector3.right, 0f, 0f), spin).coefficient);
        BallWake strong = BallAerodynamics.PrepareWake(TestWake(1, 0.2f, Vector3.right, 0f, 0f), spin);
        Assert.AreEqual(BallAerodynamics.MaxWakeCoefficient, strong.coefficient);
        Assert.AreEqual(Vector3.zero, BallAerodynamics.WakeAcceleration(strong, Vector3.zero, 0f));
        Assert.AreEqual(Vector3.zero, BallAerodynamics.WakeAcceleration(strong, Vector3.forward * 0.001f, 0f));
        Assert.AreEqual(Vector3.zero, BallAerodynamics.WakeAcceleration(strong, new Vector3(float.PositiveInfinity, 0f, 0f), 0f));
        Assert.AreEqual(Vector3.zero, BallAerodynamics.WakeAcceleration(strong, new Vector3(float.NaN, 0f, 0f), 0f));
    }

    [TsukimiTest]
    public void RotatingWakeTurnsAboutItsAxisWithTheSpinFromItsInitialPhase()
    {
        Vector3 velocity = Vector3.forward * 40f;
        float quarter = Mathf.PI * 0.5f;
        WakeProfile profile = TestWake(BallAerodynamics.WakeRotating, 0.04f, Vector3.forward, quarter, 0f);
        BallWake wake = BallAerodynamics.PrepareWake(profile, Vector3.forward * 10f);
        Assert.AreEqual(10f, wake.rate);
        // Basis rule: x = up x axis (right for a forward axis), y = axis x x (up).
        Assert.IsTrue(Vector3.Distance(BallAerodynamics.WakeAcceleration(wake, velocity, 0f), Vector3.right * 1.6f) < 0.0001f);
        Assert.IsTrue(Vector3.Distance(BallAerodynamics.WakeAcceleration(wake, velocity, quarter / 10f), Vector3.up * 1.6f) < 0.0001f);
        Assert.AreEqual(BallAerodynamics.WakeAcceleration(wake, velocity, 0.37f), BallAerodynamics.WakeAcceleration(wake, velocity, 0.37f));
        // The opposite spin turns it the other way; an initial phase shifts it.
        BallWake reverse = BallAerodynamics.PrepareWake(profile, Vector3.back * 10f);
        Assert.IsTrue(Vector3.Distance(BallAerodynamics.WakeAcceleration(reverse, velocity, quarter / 10f), Vector3.down * 1.6f) < 0.0001f);
        BallWake shifted = BallAerodynamics.PrepareWake(TestWake(BallAerodynamics.WakeRotating, 0.04f, Vector3.forward, quarter, quarter), Vector3.zero);
        Assert.IsTrue(Vector3.Distance(BallAerodynamics.WakeAcceleration(shifted, velocity, 5f), Vector3.up * 1.6f) < 0.0001f);
        // A vertical axis uses the fallback basis and stays perpendicular to it.
        BallWake vertical = BallAerodynamics.PrepareWake(TestWake(BallAerodynamics.WakeRotating, 0.04f, Vector3.up, quarter, 0f), Vector3.zero);
        Assert.IsTrue(Vector3.Distance(BallAerodynamics.WakeAcceleration(vertical, velocity, 0f), Vector3.right * 1.6f) < 0.0001f);
    }

    [TsukimiTest]
    public void EveryFlightButThePitchEndsTheWake()
    {
        WakeProfile profile = TestWake(BallAerodynamics.WakeFixed, 0.03f, Vector3.right, 0f, 0f);
        _wake = BallAerodynamics.PrepareWake(profile, Vector3.zero);
        Stage(Vector3.zero);
        Assert.AreEqual(BallAerodynamics.WakeNone, _wake.mode, "stage");
        _wake = BallAerodynamics.PrepareWake(profile, Vector3.zero);
        StartFlight(Vector3.zero, Vector3.forward, Vector3.zero, false);
        Assert.AreEqual(BallAerodynamics.WakeNone, _wake.mode, "throw, drop or remote hit");
        _wake = BallAerodynamics.PrepareWake(profile, Vector3.zero);
        StopFlightForHold();
        Assert.AreEqual(BallAerodynamics.WakeNone, _wake.mode, "glove or hand hold");
    }

    [TsukimiTest]
    public void OnlyAPitchSnapshotCarriesAWake()
    {
        Mimic.Join();
        WakeProfile profile = TestWake(BallAerodynamics.WakeFixed, 0.03f, Vector3.right, 0f, 0f);
        RecordPending(ThrowState, Vector3.one, Vector3.forward, Vector3.zero, 1.0, -1, 0f, HoldFree, 0, profile);
        Assert.AreEqual(BallAerodynamics.WakeNone, _pendingWake.mode);
        RecordPending(PitchState, Vector3.one, Vector3.forward, Vector3.zero, 1.0, -1, 0f, HoldFree, 0, profile);
        Assert.AreEqual(BallAerodynamics.WakeFixed, _pendingWake.mode);
        _pending = false;
        QueuePitch(Vector3.up, Vector3.forward * 30f, Vector3.left, 10.0, BallAerodynamics.WakeRotating, 0.04f, Vector3.forward, 1.2f, 0.7f);
        Assert.AreEqual(PitchState, _kind);
        Assert.AreEqual(BallAerodynamics.WakeRotating, _wakeMode);
        Assert.AreEqual(0.04f, _wakeCoefficient);
        Assert.AreEqual(Vector3.forward, _wakeDirection);
        Assert.AreEqual(1.2f, _wakeConeAngle);
        Assert.AreEqual(0.7f, _wakePhase);
        QueueHit(Vector3.one, Vector3.right, Vector3.up, 11.0, 17, 0.5f);
        Assert.AreEqual(HitState, _kind);
        Assert.AreEqual(BallAerodynamics.WakeNone, _wakeMode);
        Assert.AreEqual(0f, _wakeCoefficient);
        Assert.AreEqual(Vector3.zero, _wakeDirection);
    }

    [TsukimiTest]
    public void PitchWakeReachesAnotherClientAndSurvivesAnOwnerRepublish()
    {
        VRCPlayerApi pitcher = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        Mimic.Become(other);
        Mimic.Become(pitcher);
        QueuePitch(Vector3.up, Vector3.forward * 30f, Vector3.left, 10.0, BallAerodynamics.WakeFixed, 0.045f, Vector3.left, 0f, 0f);
        Mimic.Deliver(other);
        Mimic.Become(other);
        Assert.AreEqual(PitchState, _acceptedKind);
        Assert.AreEqual(BallAerodynamics.WakeFixed, _acceptedWake.mode);
        Assert.AreEqual(0.045f, _acceptedWake.coefficient);
        Assert.AreEqual(Vector3.left, _acceptedWake.direction);
        // A snapshot differing only in its wake is not the accepted state.
        Assert.IsTrue(SameAsAccepted());
        _wakeCoefficient = 0.03f;
        Assert.IsFalse(SameAsAccepted());
        _wakeCoefficient = 0.045f;
        // Ownership arriving here republishes the accepted pitch, wake included.
        Networking.SetOwner(other, gameObject);
        OnOwnershipTransferred(other);
        Assert.AreEqual(other.playerId, _publisherId);
        Assert.AreEqual(PitchState, _kind);
        Assert.AreEqual(BallAerodynamics.WakeFixed, _wakeMode);
        Assert.AreEqual(0.045f, _wakeCoefficient);
        Assert.AreEqual(Vector3.left, _wakeDirection);
    }

    [TsukimiTest]
    public void CandidatePitchKeepsItsWakeUntilItsPublisherOwnsTheBall()
    {
        VRCPlayerApi a = Mimic.Join();
        VRCPlayerApi b = Mimic.Join();
        VRCPlayerApi c = Mimic.Join();
        Mimic.Become(b);
        Networking.SetOwner(a, gameObject);
        _wakeMode = BallAerodynamics.WakeRotating;
        _wakeCoefficient = 0.04f;
        _wakeDirection = Vector3.forward;
        _wakeConeAngle = 1.5f;
        _wakePhase = 2.5f;
        _sequence = 3;
        _kind = PitchState;
        _position = Vector3.up;
        _velocity = Vector3.forward * 30f;
        _spin = Vector3.forward;
        _serverTime = 9.0;
        _batterId = -1;
        _hitStrength = 0f;
        _hold = HoldFree;
        _holderId = 0;
        _publisherId = c.playerId;
        OnDeserialization();
        Assert.IsTrue(HasCandidates());
        Assert.AreEqual(BallAerodynamics.WakeNone, _acceptedWake.mode, "nothing is accepted from a non-owner");
        _wakeMode = BallAerodynamics.WakeNone;
        Networking.SetOwner(c, gameObject);
        OnOwnershipTransferred(c);
        Assert.AreEqual(PitchState, _acceptedKind);
        Assert.AreEqual(BallAerodynamics.WakeRotating, _acceptedWake.mode);
        Assert.AreEqual(1.5f, _acceptedWake.coneAngle);
        Assert.AreEqual(2.5f, _acceptedWake.initialPhase);
    }
}
