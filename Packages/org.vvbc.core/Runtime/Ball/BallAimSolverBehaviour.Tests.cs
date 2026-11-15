using Tsukimi;
using UnityEngine;

public partial class BallAimSolverBehaviour
{
    [TsukimiTest(maxSteps: 10000000)]
    public void NineTargetsConvergeWithFixedSpinAndSpeed()
    {
        Vector3 release = new Vector3(0f, 1.8f, 0f);
        Vector3 spin = Vector3.left * (2500f * 2f * Mathf.PI / 60f);
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                Vector3 target = new Vector3((col - 1) * 0.43f / 3f, 1.0f + (1 - row) * 0.6f / 3f, 18f);
                BeginSolve(release, target, Vector3.forward, 120f / 3.6f, spin,
                    new Vector3(0f, -9.81f, 0f), 0.00622f, 0.00063f, 0.02f);
                while (solving) Advance();
                Assert.IsTrue(converged);
                Assert.IsTrue(errorMeters <= 0.03f);
                Assert.IsTrue(Mathf.Abs(launchDirection.magnitude - 1f) < 0.0001f);
            }
        }
    }

    [TsukimiTest(maxSteps: 10000000)]
    public void ImpossibleTargetCompletesWithoutClaimingConvergence()
    {
        BeginSolve(Vector3.zero, new Vector3(0f, 100f, 18f), Vector3.forward, 5f,
            Vector3.zero, new Vector3(0f, -9.81f, 0f), 0.00622f, 0.00063f, 0.02f);
        while (solving) Advance();
        Assert.IsFalse(converged);
    }

    [TsukimiTest(maxSteps: 30000000)]
    public void SevenSpinAxesReachNineTargetsWithDirectionRelativeSpin()
    {
        Vector3[] axes = new Vector3[] {
            new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f),
            new Vector3(0f, -0.55f, 0.83f), new Vector3(0f, -1f, 0f),
            new Vector3(-0.12f, 0f, 0.99f), new Vector3(0.65f, 0.76f, 0f),
            new Vector3(-0.65f, 0.55f, 0.52f)
        };
        for (int kind = 0; kind < axes.Length; kind++)
        {
            Vector3 localSpin = axes[kind].normalized * (2500f * 2f * Mathf.PI / 60f);
            for (int row = 0; row < 3; row++) for (int col = 0; col < 3; col++)
            {
                Vector3 target = new Vector3((col - 1) * 0.43f / 3f, 1f + (1 - row) * 0.6f / 3f, 18f);
                BeginSolveRelativeSpin(new Vector3(0f, 1.8f, 0f), target, Vector3.forward,
                    120f / 3.6f, localSpin, new Vector3(0f, -9.81f, 0f), 0.00622f, 0.00063f, 0.02f);
                while (solving) Advance();
                Assert.IsTrue(converged);
                Assert.IsTrue(errorMeters <= 0.03f);
                Assert.IsTrue(Vector3.Distance(launchSpin, PitchSpin.ToWorld(localSpin, launchDirection)) < 0.001f);
            }
        }
    }

    [TsukimiTest]
    public void SolverStepUsesTheSameSemiImplicitModel()
    {
        Vector3 release = new Vector3(0f, 1.8f, 0f);
        Vector3 spin = Vector3.left * 200f;
        Vector3 gravity = Vector3.down * 9.81f;
        BeginSolve(release, new Vector3(0f, 1f, 18f), Vector3.forward, 30f,
            spin, gravity, 0.00622f, 0.00063f, 0.02f);
        stepsPerFrame = 1;
        BallFlightState expected = new BallFlightState();
        expected.position = _position;
        expected.velocity = _velocity;
        expected = BallFlightModel.Step(expected, _trialSpin, gravity, 0.00622f, 0.00063f, 0.02f);
        Advance();
        Assert.IsTrue(Vector3.Distance(expected.position, _position) < 0.00001f);
        Assert.IsTrue(Vector3.Distance(expected.velocity, _velocity) < 0.00001f);
    }

    [TsukimiTest]
    public void SpinBasisRotatesWithPitchDirectionAndPreservesMagnitude()
    {
        Vector3 local = new Vector3(-100f, -60f, 120f);
        Vector3 a = PitchSpin.ToWorld(local, Vector3.forward);
        Vector3 b = PitchSpin.ToWorld(local, Vector3.right);
        Assert.IsTrue(Mathf.Abs(a.magnitude - local.magnitude) < 0.001f);
        Assert.IsTrue(Mathf.Abs(b.magnitude - local.magnitude) < 0.001f);
        Assert.IsTrue(Vector3.Dot(Vector3.Cross(a, Vector3.forward), Vector3.up) > 0f);
        Assert.IsTrue(Vector3.Dot(Vector3.Cross(b, Vector3.right), Vector3.up) > 0f);
    }

    [TsukimiTest]
    public void SpinComponentsProduceExpectedMagnusDirections()
    {
        Vector3 velocity = Vector3.forward * 30f;
        Vector3 back = Vector3.Cross(PitchSpin.ToWorld(Vector3.left, velocity), velocity);
        Vector3 top = Vector3.Cross(PitchSpin.ToWorld(Vector3.right, velocity), velocity);
        Vector3 glove = Vector3.Cross(PitchSpin.ToWorld(Vector3.down, velocity), velocity);
        Vector3 arm = Vector3.Cross(PitchSpin.ToWorld(Vector3.up, velocity), velocity);
        Vector3 gyro = Vector3.Cross(PitchSpin.ToWorld(Vector3.forward, velocity), velocity);
        Assert.IsTrue(back.y > 0f && top.y < 0f);
        Assert.IsTrue(glove.x < 0f && arm.x > 0f);
        Assert.IsTrue(gyro.sqrMagnitude < 0.000001f);
        Assert.IsTrue(PitchSpin.ToWorld(Vector3.zero, velocity).sqrMagnitude < 0.000001f);
    }

    private WakeProfile SolverWake(int mode, float coefficient, Vector3 direction, float coneAngle, float phase)
    {
        WakeProfile wake = new WakeProfile();
        wake.mode = mode;
        wake.coefficient = coefficient;
        wake.direction = direction;
        wake.coneAngle = coneAngle;
        wake.initialPhase = phase;
        return wake;
    }

    private Vector3 RpmAxis(Vector3 axis, float rpm)
    {
        return axis.normalized * (rpm * 2f * Mathf.PI / 60f);
    }

    // The trial follows BallFlightModel.Step from the release, with the spin and the wake
    // turned into world space for the trial direction and the wake read at flight time 0, dt, ...
    private bool TrialMatchesFlightModel(Vector3 localSpin, WakeProfile local, float speed, int steps)
    {
        Vector3 release = new Vector3(0f, 1.75f, 0f);
        Vector3 gravity = Vector3.down * 9.81f;
        BeginSolveRelativeSpin(release, new Vector3(0f, 0.85f, 17f), Vector3.forward, speed, localSpin,
            local.mode, local.coefficient, local.direction, local.coneAngle, local.initialPhase,
            gravity, 0.00622f, 0.00045f, 0.02f);
        if (!solving) return false;
        Vector3 direction = _velocity / speed;
        Vector3 spin = PitchSpin.ToWorld(localSpin, direction);
        WakeProfile world = local;
        world.direction = PitchSpin.ToWorld(local.direction, direction);
        BallWake wake = BallAerodynamics.PrepareWake(world, spin);
        BallFlightState expected = new BallFlightState();
        expected.position = release;
        expected.velocity = direction * speed;
        stepsPerFrame = 1;
        bool same = true;
        for (int i = 0; i < steps; i++)
        {
            expected = BallFlightModel.Step(expected, spin, gravity, 0.00622f, 0.00045f, wake, 0.02f);
            Advance();
            same = same && Vector3.Distance(expected.position, _position) < 0.00001f &&
                Vector3.Distance(expected.velocity, _velocity) < 0.00001f;
        }
        stepsPerFrame = 64;
        return same && _step == steps && Mathf.Abs(expected.time - _time) < 0.00001f;
    }

    [TsukimiTest(maxSteps: 5000000)]
    public void SolverTrialsUseTheSharedWakeFlightModel()
    {
        Vector3 fourSeam = RpmAxis(Vector3.left, 2500f);
        Assert.IsTrue(TrialMatchesFlightModel(fourSeam, new WakeProfile(), 120f / 3.6f, 20));
        Assert.IsTrue(TrialMatchesFlightModel(RpmAxis(new Vector3(-0.65f, 0.55f, 0.52f), 2500f),
            SolverWake(BallAerodynamics.WakeFixed, 0.03f, new Vector3(1f, -0.5f, 0f), 0f, 0f), 150f / 3.6f, 20));
        Assert.IsTrue(TrialMatchesFlightModel(RpmAxis(Vector3.forward, 60f),
            SolverWake(BallAerodynamics.WakeRotating, 0.04f, Vector3.forward, Mathf.PI * 0.5f, 0.8f), 110f / 3.6f, 20));
        Assert.IsTrue(TrialMatchesFlightModel(RpmAxis(new Vector3(0.65f, -0.55f, -0.52f), 2500f),
            SolverWake(BallAerodynamics.WakeFixed, 0.03f, new Vector3(-1f, -0.5f, 0f), 0f, 0f), 150f / 3.6f, 20));
        Assert.IsTrue(TrialMatchesFlightModel(Vector3.zero,
            SolverWake(BallAerodynamics.WakeFixed, 0.045f, new Vector3(-1f, 0.3f, 0f), 0f, 0f), 120f / 3.6f, 20));
        Assert.IsTrue(TrialMatchesFlightModel(Vector3.zero,
            SolverWake(BallAerodynamics.WakeRotating, 0.04f, Vector3.forward, Mathf.PI * 0.5f, 0.8f), 5f, 20));
    }

    [TsukimiTest]
    public void ZeroSpeedKeepsTheFallbackAndAWorldWakeDirection()
    {
        Vector3 local = new Vector3(1f, -0.5f, 0f);
        BeginSolveRelativeSpin(new Vector3(0f, 1.75f, 0f), new Vector3(0f, 0.85f, 17f), Vector3.forward, 0f,
            RpmAxis(Vector3.left, 2000f), BallAerodynamics.WakeFixed, 0.03f, local, 0f, 0f,
            Vector3.down * 9.81f, 0.00622f, 0.00045f, 0.02f);
        Assert.IsFalse(solving);
        Assert.IsFalse(converged);
        Assert.IsTrue(Vector3.Distance(launchWakeDirection, PitchSpin.ToWorld(local, launchDirection)) < 0.00001f);
        // A plain BeginSolve carries no wake from an earlier relative-spin solve.
        BeginSolve(new Vector3(0f, 1.75f, 0f), new Vector3(0f, 0.85f, 17f), Vector3.forward, 30f,
            Vector3.zero, Vector3.down * 9.81f, 0.00622f, 0.00045f, 0.02f);
        Assert.AreEqual(BallAerodynamics.WakeNone, _trialWake.mode);
        Assert.AreEqual(Vector3.zero, launchWakeDirection);
    }

    // The schedule publishes launchDirection, launchSpin and launchWakeDirection; the Ball's
    // model flown from exactly those values crosses where the solver aimed.
    [TsukimiTest(maxSteps: 40000000)]
    public void WakePitchesConvergeAndTheirPublishedFlightReachesTheAim()
    {
        Vector3[] spins = new Vector3[] {
            RpmAxis(new Vector3(-0.65f, 0.55f, 0.52f), 2500f), RpmAxis(Vector3.down, 2500f), RpmAxis(Vector3.forward, 60f) };
        WakeProfile[] wakes = new WakeProfile[] {
            SolverWake(BallAerodynamics.WakeFixed, 0.03f, new Vector3(1f, -0.5f, 0f), 0f, 0f),
            SolverWake(BallAerodynamics.WakeFixed, 0.045f, new Vector3(-1f, 0.3f, 0f), 0f, 0f),
            SolverWake(BallAerodynamics.WakeRotating, 0.04f, Vector3.forward, Mathf.PI * 0.5f, 0.8f) };
        Vector3 release = new Vector3(0f, 1.75f, 0f);
        Vector3 gravity = Vector3.down * 9.81f;
        float speed = 120f / 3.6f;
        for (int kind = 0; kind < 3; kind++)
        for (int col = 0; col < 3; col++)
        {
            Vector3 target = new Vector3((col - 1) * 0.15f, 0.85f, 17f);
            WakeProfile local = wakes[kind];
            BeginSolveRelativeSpin(release, target, Vector3.forward, speed, spins[kind],
                local.mode, local.coefficient, local.direction, local.coneAngle, local.initialPhase,
                gravity, 0.00622f, 0.00045f, 0.02f);
            while (solving) Advance();
            Assert.IsTrue(converged);
            Assert.IsTrue(Vector3.Distance(launchWakeDirection, PitchSpin.ToWorld(local.direction, launchDirection)) < 0.00001f);
            WakeProfile world = local;
            world.direction = launchWakeDirection;
            BallWake wake = BallAerodynamics.PrepareWake(world, launchSpin);
            BallFlightState state = new BallFlightState();
            state.position = release;
            state.velocity = launchDirection * speed;
            Vector3 crossing = release;
            for (int i = 0; i < 200; i++)
            {
                BallFlightState next = BallFlightModel.Step(state, launchSpin, gravity, 0.00622f, 0.00045f, wake, 0.02f);
                if (state.position.z < target.z && next.position.z >= target.z)
                {
                    float u = (target.z - state.position.z) / (next.position.z - state.position.z);
                    crossing = state.position + (next.position - state.position) * u;
                    break;
                }
                state = next;
            }
            Assert.IsTrue(Vector3.Distance(crossing, target) <= tolerance + 0.001f);
        }
    }

    // Mirroring a pitch left-right mirrors the solved direction: the wake direction is a
    // polar vector (x flips), the spin an axial one (pitch-local y and z flip).
    [TsukimiTest(maxSteps: 10000000)]
    public void MirroredWakePitchSolvesToTheMirroredDirection()
    {
        Vector3 release = new Vector3(0f, 1.75f, 0f);
        Vector3 gravity = Vector3.down * 9.81f;
        Vector3 spin = RpmAxis(new Vector3(-0.3f, -1f, 0.2f), 2500f);
        Vector3 wake = new Vector3(-1f, 0.3f, 0f);
        BeginSolveRelativeSpin(release, new Vector3(0.12f, 0.85f, 17f), Vector3.forward, 120f / 3.6f, spin,
            BallAerodynamics.WakeFixed, 0.045f, wake, 0f, 0f, gravity, 0.00622f, 0.00045f, 0.02f);
        while (solving) Advance();
        Assert.IsTrue(converged);
        Vector3 direction = launchDirection;
        BeginSolveRelativeSpin(release, new Vector3(-0.12f, 0.85f, 17f), Vector3.forward, 120f / 3.6f,
            new Vector3(spin.x, -spin.y, -spin.z), BallAerodynamics.WakeFixed, 0.045f,
            new Vector3(-wake.x, wake.y, wake.z), 0f, 0f, gravity, 0.00622f, 0.00045f, 0.02f);
        while (solving) Advance();
        Assert.IsTrue(converged);
        Assert.IsTrue(Vector3.Distance(launchDirection, new Vector3(-direction.x, direction.y, direction.z)) < 0.0005f);
    }
}
