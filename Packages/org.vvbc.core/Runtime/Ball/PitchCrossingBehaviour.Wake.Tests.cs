using Tsukimi;
using UnityEngine;

// Contact and ground-contact predictions continue the pitch's wake from the flight time of
// the state they start at. Expectations step BallFlightModel.Step independently.
public partial class PitchCrossingBehaviour
{
    private WakeProfile CrossingWake(int mode, float coefficient, Vector3 direction, float coneAngle, float phase)
    {
        WakeProfile wake = new WakeProfile();
        wake.mode = mode;
        wake.coefficient = coefficient;
        wake.direction = direction;
        wake.coneAngle = coneAngle;
        wake.initialPhase = phase;
        return wake;
    }

    // The plate at the origin, its front toward +z; the pitch flies toward -z.
    private void BeginTestPitch(float time)
    {
        // Without a zone BeginPitch only ends the previous pitch's wake; the geometry is set here.
        zone = null;
        BeginPitch(time, new Vector3(0f, 1.6f, 17f), new Vector3(0f, -0.5f, -38f), 0.0366f);
        tracking = true; hasFrontCrossing = false; hasResult = false; bounced = false;
        _origin = Vector3.zero; _forward = Vector3.forward; _right = Vector3.right;
        _depth = 0.4318f; _width = 0.4318f; _bottom = 0.5f; _top = 1.2f; _radius = 0.0366f;
        _previousTime = time;
        _previousPosition = new Vector3(0f, 1.6f, 17f);
        _previousVelocity = new Vector3(0f, -0.5f, -38f);
    }

    private Vector3 ExpectedCrossing(WakeProfile profile, float flightTime, Vector3 position, Vector3 velocity,
        Vector3 spin, float planeDepth)
    {
        BallWake wake = BallAerodynamics.PrepareWake(profile, spin);
        float dt = 0.02f;
        BallFlightState state = new BallFlightState();
        state.position = position;
        state.velocity = velocity;
        state.time = flightTime;
        Vector3 plane = _origin + _forward * planeDepth;
        for (int i = 0; i < 500; i++)
        {
            BallFlightState next = BallFlightModel.Step(state, spin, Vector3.down * 9.81f,
                flight.dragCoefficient, flight.magnusCoefficient, wake, dt);
            float u = CrossingFraction(state.position, state.velocity, next.position, next.velocity, dt, plane, _forward);
            if (u >= 0f) return Hermite(state.position, state.velocity, next.position, next.velocity, dt, u);
            state = next;
        }
        return Vector3.one * 1000f;
    }

    // NotifyContact without the fixed step it reads from Time: the sample, then the prediction.
    private Vector3 PredictedContactCrossing(float time, Vector3 position, Vector3 velocity, Vector3 spin)
    {
        AcceptSample(time, position, velocity);
        tracking = false;
        PredictCrossings(time, position, velocity, spin, false, 0.02f, Vector3.down * 9.81f);
        return hasResult ? resultCrossing : Vector3.one * -1000f;
    }

    [TsukimiTest(maxSteps: 3000000)]
    public void ContactPredictionContinuesTheWakeFromTheContactFlightTime()
    {
        Vector3 position = new Vector3(0f, 1.3f, 10f);
        Vector3 velocity = new Vector3(0f, -1.5f, -38f);
        Vector3 backspin = new Vector3(-200f, 0f, 0f);
        // None.
        BeginTestPitch(1f);
        Vector3 none = PredictedContactCrossing(1.3f, position, velocity, backspin);
        Assert.IsTrue(Vector3.Distance(none, ExpectedCrossing(new WakeProfile(), 0f, position, velocity, backspin, 0.2159f)) < 0.0001f);
        // Fixed, and its mirror.
        WakeProfile arm = CrossingWake(BallAerodynamics.WakeFixed, 0.03f, Vector3.right, 0f, 0f);
        BeginTestPitch(1f);
        SetPitchWake(arm.mode, arm.coefficient, arm.direction, 0f, 0f, 0f, 1f);
        Vector3 fixedArm = PredictedContactCrossing(1.3f, position, velocity, backspin);
        Assert.IsTrue(Vector3.Distance(fixedArm, ExpectedCrossing(arm, 0.3f, position, velocity, backspin, 0.2159f)) < 0.0001f);
        Assert.IsTrue(fixedArm.x - none.x > 0.02f, "the wake moves the predicted crossing");
        BeginTestPitch(1f);
        SetPitchWake(BallAerodynamics.WakeFixed, 0.03f, Vector3.left, 0f, 0f, 0f, 1f);
        Vector3 fixedGlove = PredictedContactCrossing(1.3f, position, velocity, backspin);
        Assert.IsTrue(Mathf.Abs((fixedGlove.x - none.x) + (fixedArm.x - none.x)) < 0.0001f, "mirrored left-right");
        Assert.IsTrue(Mathf.Abs(fixedGlove.y - fixedArm.y) < 0.0001f);
        // A following pitch without a wake predicts as None again.
        BeginTestPitch(5f);
        Assert.IsTrue(Vector3.Distance(PredictedContactCrossing(5.3f, position, velocity, backspin), none) < 0.0001f);
    }

    [TsukimiTest(maxSteps: 3000000)]
    public void RotatingPredictionUsesTheInitialPhaseAndTheSampleTimeBase()
    {
        Vector3 position = new Vector3(0f, 1.3f, 10f);
        Vector3 velocity = new Vector3(0f, -1.5f, -38f);
        Vector3 spin = Vector3.back * 20f;
        WakeProfile zero = CrossingWake(BallAerodynamics.WakeRotating, 0.04f, Vector3.back, Mathf.PI * 0.5f, 0f);
        WakeProfile quarter = CrossingWake(BallAerodynamics.WakeRotating, 0.04f, Vector3.back, Mathf.PI * 0.5f, Mathf.PI * 0.5f);
        // Joined late: the first sample (fixed time 1.02) is already 0.37 s into the flight,
        // so the contact at 1.3 is at flight time 0.65.
        BeginTestPitch(1f);
        SetPitchWake(zero.mode, zero.coefficient, zero.direction, zero.coneAngle, zero.initialPhase, 0.37f, 1.02f);
        Vector3 a = PredictedContactCrossing(1.3f, position, velocity, spin);
        Assert.IsTrue(Vector3.Distance(a, ExpectedCrossing(zero, 0.65f, position, velocity, spin, 0.2159f)) < 0.0001f);
        Assert.IsTrue(Vector3.Distance(a, ExpectedCrossing(zero, 0.28f, position, velocity, spin, 0.2159f)) > 0.005f,
            "a different flight-time base would move the crossing");
        BeginTestPitch(1f);
        SetPitchWake(quarter.mode, quarter.coefficient, quarter.direction, quarter.coneAngle, quarter.initialPhase, 0.37f, 1.02f);
        Vector3 b = PredictedContactCrossing(1.3f, position, velocity, spin);
        Assert.IsTrue(Vector3.Distance(b, ExpectedCrossing(quarter, 0.65f, position, velocity, spin, 0.2159f)) < 0.0001f);
        Assert.IsTrue(Vector3.Distance(a, b) > 0.005f, "the initial phase changes the path");
        // Just before the plate: the last step still uses the same base.
        Vector3 near = new Vector3(0f, 0.9f, 0.6f);
        BeginTestPitch(1f);
        SetPitchWake(zero.mode, zero.coefficient, zero.direction, zero.coneAngle, zero.initialPhase, 0.37f, 1.02f);
        Vector3 c = PredictedContactCrossing(1.55f, near, velocity, spin);
        Assert.IsTrue(Vector3.Distance(c, ExpectedCrossing(zero, 0.9f, near, velocity, spin, 0.2159f)) < 0.0001f);
    }

    [TsukimiTest(maxSteps: 3000000)]
    public void GroundContactPredictionKeepsTheWake()
    {
        Vector3 position = new Vector3(0f, 0.04f, 6f);
        Vector3 velocity = new Vector3(0f, -3f, -36f);
        Vector3 backspin = new Vector3(-200f, 0f, 0f);
        WakeProfile arm = CrossingWake(BallAerodynamics.WakeFixed, 0.045f, Vector3.right, 0f, 0f);
        BeginTestPitch(1f);
        SetPitchWake(arm.mode, arm.coefficient, arm.direction, 0f, 0f, 0.1f, 1.02f);
        AcceptSample(1.4f, position, velocity);
        PredictCrossings(1.4f, position, velocity, backspin, true, 0.02f, Vector3.down * 9.81f);
        Assert.IsTrue(hasFrontCrossing);
        Vector3 expected = ExpectedCrossing(arm, 0.48f, position, velocity, backspin, _depth);
        Assert.IsTrue(Mathf.Abs(frontCrossing.x - expected.x) < 0.0001f);
        Assert.IsTrue(frontCrossing.x > 0.01f, "the wake still acts until the bounce");
    }
}
