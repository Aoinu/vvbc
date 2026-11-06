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
}
