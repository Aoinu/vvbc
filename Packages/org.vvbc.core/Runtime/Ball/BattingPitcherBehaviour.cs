using Tsukimi;
using UnityEngine;

// Local-only initial pitcher. This behaviour lives on the Animator object so
// the clip can deliver its Animation Event through Udon's SendCustomEvent.
public partial class BattingPitcherBehaviour : TsukimiBehaviour
{
    public Animator animator;
    public BallFlightBehaviour ball;
    public BallAimSolverBehaviour solver;
    public Transform releasePoint;
    public Transform strikeZoneCenter;
    public GameObject animationBall;
    public Renderer physicalBallRenderer;
    public Collider physicalBallCollider;

    public float speedKmh = 120f;
    public float spinRpm = 2500f;
    public int courseColumn = 1;
    public int courseRow = 1;
    public float strikeZoneWidth = 0.43f;
    public float strikeZoneHeight = 0.6f;
    public float resetSeconds = 3f;
    // The development clip releases at 2.0 / 5.2 normalized time.
    public float minimumReleaseNormalizedTime = 0.35f;

    // Public state is useful for local diagnostics; it is not network state.
    public int pitchSequence;
    public int phase;
    public float lastAimError;
    public bool lastAimConverged;
    private const int Preparing = 0;
    private const int PitchMotion = 1;
    private const int FollowThrough = 2;
    private int _solvingSequence;
    private bool _released;
    private float _speedSnapshot;
    private float _resetSnapshot;
    private float _resetAt;
    private Vector3 _releaseSnapshot;
    private Vector3 _spinSnapshot;
    private Vector3 _velocitySnapshot;
    private Vector3 _visualStartPosition;
    private Quaternion _visualStartRotation;

    private void Start()
    {
        if (animator == null || ball == null || ball.body == null || solver == null ||
            releasePoint == null || strikeZoneCenter == null || physicalBallRenderer == null)
        {
            Debug.LogError("BattingPitcher: required reference is missing");
            enabled = false;
            return;
        }
        _visualStartPosition = animator.transform.localPosition;
        _visualStartRotation = animator.transform.localRotation;
        PrepareNextPitch();
    }

    private void PrepareNextPitch()
    {
        pitchSequence++;
        _solvingSequence = pitchSequence;
        phase = Preparing;
        _released = false;
        _releaseSnapshot = releasePoint.position;
        _speedSnapshot = Mathf.Max(1f, speedKmh) / 3.6f;
        _resetSnapshot = Mathf.Max(0.1f, resetSeconds);
        int column = Mathf.Clamp(courseColumn, 0, 2);
        int row = Mathf.Clamp(courseRow, 0, 2);
        Vector3 target = strikeZoneCenter.position + strikeZoneCenter.right * ((column - 1) * strikeZoneWidth / 3f)
            + Vector3.up * ((1 - row) * strikeZoneHeight / 3f);
        Vector3 forward = strikeZoneCenter.position - _releaseSnapshot;
        forward.y = 0f;
        _spinSnapshot = Vector3.Cross(forward.normalized, Vector3.up) * (spinRpm * (2f * Mathf.PI / 60f));
        ball.Stage(_releaseSnapshot);
        physicalBallRenderer.enabled = false;
        if (physicalBallCollider != null) physicalBallCollider.enabled = false;
        if (animationBall != null) animationBall.SetActive(true);
        // Retain root motion during the pitch, but never carry its displacement
        // into the next cycle. Local coordinates preserve the placed parent pose.
        animator.transform.localPosition = _visualStartPosition;
        animator.transform.localRotation = _visualStartRotation;
        animator.Play("Pitch Idle", 0, 0f);
        solver.BeginSolve(_releaseSnapshot, target, strikeZoneCenter.forward, _speedSnapshot,
            _spinSnapshot, Physics.gravity, ball.dragCoefficient, ball.magnusCoefficient, Time.fixedDeltaTime);
    }

    private void Update()
    {
        if (phase == Preparing)
        {
            if (solver.solving) solver.Advance();
            if (solver.solving || _solvingSequence != pitchSequence) return;
            _velocitySnapshot = solver.launchDirection * _speedSnapshot;
            lastAimConverged = solver.converged;
            lastAimError = solver.errorMeters;
            phase = PitchMotion;
            animator.Play("Pitching", 0, 0f);
        }
        else if (ResetDue(Time.time)) PrepareNextPitch();
    }

    // Invoked only by the Animation Event on this Animator's GameObject.
    public void _OnPitchRelease()
    {
        if (animator == null || ball == null || physicalBallRenderer == null) return;
        AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
        if (!state.IsName("Pitching")) return;
        if (!AcceptRelease(Time.time, state.normalizedTime)) return;
        ball.Launch(_releaseSnapshot, _velocitySnapshot, _spinSnapshot);
        if (animationBall != null) animationBall.SetActive(false);
        physicalBallRenderer.enabled = true;
        if (physicalBallCollider != null) physicalBallCollider.enabled = true;
    }

    private bool AcceptRelease(float now, float normalizedTime)
    {
        if (phase != PitchMotion || _released || _solvingSequence != pitchSequence ||
            normalizedTime < minimumReleaseNormalizedTime) return false;
        _released = true;
        phase = FollowThrough;
        _resetAt = now + _resetSnapshot;
        return true;
    }

    private bool ResetDue(float now)
    {
        return phase == FollowThrough && _released && now >= _resetAt;
    }
}
