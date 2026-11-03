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
}
