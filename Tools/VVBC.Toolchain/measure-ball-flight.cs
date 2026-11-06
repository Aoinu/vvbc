// Execute with isuzu-unity-cli call execute_code --file in Play Mode.
// The separate physics scene has no colliders and never modifies the open scene.
var scene = UnityEngine.SceneManagement.SceneManager.CreateScene("VVBCBallPhysicsMeasurement", new UnityEngine.SceneManagement.CreateSceneParameters(UnityEngine.SceneManagement.LocalPhysicsMode.Physics3D));
var physics = scene.GetPhysicsScene();
var go = new GameObject("Measurement");
UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
var body = go.AddComponent<Rigidbody>();
var flight = go.AddComponent<BallBehaviour>(); flight.body = body; flight.ConfigureBody(); flight.flying = true;
var solver = go.AddComponent<BallAimSolverBehaviour>();
var results = new List<string>();
var spin = Vector3.left * (2500f * 2f * Mathf.PI / 60f);
bool passed = true;
try
{
foreach (float kmh in new[] { 60f, 120f, 160f })
{
    float maxError = 0f, maxPredictionError = 0f, minSpin = float.MaxValue;
    int converged = 0, crossed = 0;
    for (int row = 0; row < 3; row++) for (int col = 0; col < 3; col++)
    {
        var release = new Vector3(0f, 1.8f, 0f);
        var target = new Vector3((col - 1) * 0.43f / 3f, 1f + (1 - row) * 0.6f / 3f, 18f);
        solver.BeginSolve(release, target, Vector3.forward, kmh / 3.6f, spin, Physics.gravity, flight.dragCoefficient, flight.magnusCoefficient, 0.02f);
        int ticks = 0; while (solver.solving && ticks++ < 10000) solver.Advance();
        if (solver.converged) converged++;
        flight.Stage(release); flight.Launch(release, solver.launchDirection * (kmh / 3.6f), spin); body.rotation = Quaternion.identity; body.WakeUp();
        for (int step = 0; step < 500; step++)
        {
            var previous = body.position;
            flight.FixedUpdate(); physics.Simulate(0.02f);
            minSpin = Mathf.Min(minSpin, body.angularVelocity.magnitude);
            var current = body.position;
            if (previous.z < target.z && current.z >= target.z)
            {
                var crossing = Vector3.Lerp(previous, current, (target.z - previous.z) / (current.z - previous.z));
                maxError = Mathf.Max(maxError, Vector3.Distance(crossing, target));
                maxPredictionError = Mathf.Max(maxPredictionError, Vector3.Distance(crossing, solver.predictedCrossing));
                crossed++;
                break;
            }
        }
    }
    results.Add(kmh + " km/h converged=" + converged + "/9 crossed=" + crossed + "/9 maxError=" + maxError + " m predictionDifference=" + maxPredictionError + " m minSpin=" + minSpin + " rad/s");
    if (converged != 9 || crossed != 9 || maxError > 0.03f || minSpin < 261f) passed = false;
}
}
finally
{
UnityEngine.Object.DestroyImmediate(go);
UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(scene);
}
if (!passed) throw new InvalidOperationException(string.Join("; ", results));
return results;
