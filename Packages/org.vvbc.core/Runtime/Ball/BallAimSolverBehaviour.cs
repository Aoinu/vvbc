using Tsukimi;
using UnityEngine;

// Each Advance call has a bounded integration budget. The caller snapshots all inputs
// before BeginSolve and starts its animation only once solving has finished.
public partial class BallAimSolverBehaviour : TsukimiBehaviour
{
    public int stepsPerFrame = 64;
    public int maxIterations = 8;
    public int maxStepsPerTrajectory = 500;
    public float tolerance = 0.03f;
    public float angleDelta = 0.002f;

    public bool solving;
    public bool converged;
    public Vector3 launchDirection;
    public Vector3 launchSpin;
    public Vector3 predictedCrossing;
    public float errorMeters;
    public int iterations;

    private Vector3 _release, _target, _normal, _right, _spin, _gravity;
    private Vector3 _localSpin, _trialSpin;
    private bool _relativeSpin;
    private float _speed, _drag, _magnus, _dt, _yaw, _pitch;
    private float _bestError, _bestYaw, _bestPitch;
    private Vector3 _bestCrossing, _position, _velocity;
    private float _initialSide;
    private int _trial, _step;
    private Vector3 _baseError, _yawError;
    private bool _baseValid, _yawValid;

    public void BeginSolve(Vector3 release, Vector3 target, Vector3 planeNormal,
        float speed, Vector3 spin, Vector3 gravity, float drag, float magnus, float stepSeconds)
    {
        _relativeSpin = false;
        solving = false;
        converged = false;
        iterations = 0;
        _release = release; _target = target; _spin = spin; _gravity = gravity;
        _speed = speed; _drag = drag; _magnus = magnus; _dt = stepSeconds;
        _normal = new Vector3(planeNormal.x, 0f, planeNormal.z).normalized;
        _right = Vector3.Cross(Vector3.up, _normal);
        Vector3 offset = target - release;
        Vector3 horizontal = new Vector3(offset.x, 0f, offset.z);
        float distance = horizontal.magnitude;
        _yaw = Mathf.Atan2(horizontal.x, horizontal.z);
        _pitch = Mathf.Atan2(offset.y, distance);
        launchDirection = Direction(_yaw, _pitch);
        launchSpin = spin;
        predictedCrossing = release;
        errorMeters = float.PositiveInfinity;
        if (speed <= 0f || stepSeconds <= 0f || distance < 0.0001f || _normal.sqrMagnitude < 0.5f) return;
        _initialSide = Vector3.Dot(release - target, _normal);
        if (Mathf.Abs(_initialSide) < 0.0001f) return;
        float gravityMagnitude = -gravity.y;
        if (gravityMagnitude > 0.0001f)
        {
            float a = gravityMagnitude * distance * distance / (2f * speed * speed);
            float discriminant = distance * distance - 4f * a * (offset.y + a);
            if (discriminant >= 0f)
                _pitch = Mathf.Atan((2f * (offset.y + a)) / (distance + Mathf.Sqrt(discriminant)));
        }
        _bestError = float.PositiveInfinity;
        _bestYaw = _yaw; _bestPitch = _pitch; _bestCrossing = release;
        _trial = 0;
        solving = true;
        StartTrial();
    }

    // The spin components are relative to every trial direction, so the final
    // launch has exactly the same spin conditions used by the predictor.
    public void BeginSolveRelativeSpin(Vector3 release, Vector3 target, Vector3 planeNormal,
        float speed, Vector3 localSpin, Vector3 gravity, float drag, float magnus, float stepSeconds)
    {
        BeginSolve(release, target, planeNormal, speed, Vector3.zero, gravity, drag, magnus, stepSeconds);
        _relativeSpin = true;
        _localSpin = localSpin;
        launchSpin = PitchSpin.ToWorld(localSpin, launchDirection);
        if (solving) StartTrial();
    }

    private Vector3 Direction(float yaw, float pitch)
    {
        float c = Mathf.Cos(pitch);
        return new Vector3(Mathf.Sin(yaw) * c, Mathf.Sin(pitch), Mathf.Cos(yaw) * c);
    }

    private void StartTrial()
    {
        float delta = Mathf.Max(0.0001f, angleDelta);
        float yaw = _yaw + (_trial == 1 ? delta : 0f);
        float pitch = _pitch + (_trial == 2 ? delta : 0f);
        _position = _release;
        Vector3 direction = Direction(yaw, pitch);
        _velocity = direction * _speed;
        _trialSpin = _relativeSpin ? PitchSpin.ToWorld(_localSpin, direction) : _spin;
        _step = 0;
    }

    public void Advance()
    {
        int budget = Mathf.Max(1, stepsPerFrame);
        for (int i = 0; i < budget && solving; i++)
        {
            Vector3 previous = _position;
            BallFlightState state = new BallFlightState();
            state.position = _position;
            state.velocity = _velocity;
            state = BallFlightModel.Step(state, _trialSpin, _gravity, _drag, _magnus, _dt);
            _position = state.position;
            _velocity = state.velocity;
            _step++;
            float previousSide = Vector3.Dot(previous - _target, _normal);
            float side = Vector3.Dot(_position - _target, _normal);
            if (previousSide * _initialSide > 0f && side * _initialSide <= 0f)
            {
                float fraction = previousSide / (previousSide - side);
                FinishTrial(true, previous + (_position - previous) * fraction);
            }
            else if (_step >= Mathf.Max(1, maxStepsPerTrajectory)) FinishTrial(false, _position);
        }
    }

    private void FinishTrial(bool valid, Vector3 crossing)
    {
        Vector3 difference = crossing - _target;
        Vector3 error = new Vector3(Vector3.Dot(difference, _right), difference.y, 0f);
        float delta = Mathf.Max(0.0001f, angleDelta);
        if (valid && error.magnitude < _bestError)
        {
            _bestError = error.magnitude;
            _bestYaw = _yaw + (_trial == 1 ? delta : 0f);
            _bestPitch = _pitch + (_trial == 2 ? delta : 0f);
            _bestCrossing = crossing;
        }
        if (valid && _bestError <= tolerance) { FinishSolve(); return; }
        if (_trial == 0) { _baseError = error; _baseValid = valid; }
        if (_trial == 1) { _yawError = error; _yawValid = valid; }
        if (_trial < 2) { _trial++; StartTrial(); return; }
        iterations++;
        if (valid && _baseValid && _yawValid)
        {
            float a = (_yawError.x - _baseError.x) / delta;
            float b = (error.x - _baseError.x) / delta;
            float c = (_yawError.y - _baseError.y) / delta;
            float d = (error.y - _baseError.y) / delta;
            float determinant = a * d - b * c;
            if (Mathf.Abs(determinant) > 0.000001f)
            {
                _yaw += Mathf.Clamp((-_baseError.x * d + b * _baseError.y) / determinant, -0.2f, 0.2f);
                _pitch += Mathf.Clamp((c * _baseError.x - a * _baseError.y) / determinant, -0.2f, 0.2f);
            }
        }
        if (iterations >= Mathf.Max(1, maxIterations)) { FinishSolve(); return; }
        _trial = 0;
        StartTrial();
    }

    private void FinishSolve()
    {
        solving = false;
        launchDirection = Direction(_bestYaw, _bestPitch);
        launchSpin = _relativeSpin ? PitchSpin.ToWorld(_localSpin, launchDirection) : _spin;
        predictedCrossing = _bestCrossing;
        errorMeters = _bestError;
        converged = _bestError <= tolerance;
    }
}
