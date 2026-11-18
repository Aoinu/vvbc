using UnityEngine;
using VRC.SDKBase;

// Manual-pitch synchronisation on the shared Ball (docs/development/manual-pitch-sync.md):
// the PitchLineage of the state shown here, the judging batter's gate for an arriving
// HandPitch, a speculative pitch flown from the prepared data before the HandPitch arrives,
// its merge into the formal flight, and voiding a pitch the batter declared invalid. The
// judgement itself is PitchJudgeBehaviour's; the Ball only presents what it decides.
public partial class BallBehaviour
{
    [Header("Manual pitch sync")]
    [Tooltip("Seconds over which the shown ball moves from the speculative flight onto the formal one when the HandPitch arrives. The physics body switches at once; only the model is offset meanwhile. Default 0.15.")]
    public float speculationBlendSeconds = 0.15f;
    // The HomePlate's judge; it registers itself (PitchJudgeBehaviour.Start).
    [HideInInspector] public PitchJudgeBehaviour pitchJudge;

    // The lineage of what this client shows: set from every applied state and every local action.
    private PitchLineage _lineage;
    // The lineage a HandPitch released here starts (from the mound at the release).
    private PitchLineage _releaseLineage;
    // The newest pitch this client knows to be invalid or cancelled.
    private int _voidPitcher, _voidNumber;
    // A speculative pitch: flown locally from the prepared data, never published, no bat
    // contact, catch or pickup until it is merged into the formal flight or rolled back.
    private bool _speculative;
    private int _speculativePitcher;
    // Visual correction of the model (the physics body is already on the formal flight).
    private Vector3 _visualOffset;
    private float _visualBlendLeft, _visualBlendTotal;
    private bool _visualCorrecting;
    private Vector3 _modelRestLocal;
    private bool _modelRestKnown;

    public void SetPitchJudge(PitchJudgeBehaviour judge)
    {
        pitchJudge = judge;
    }

    // ---- What the judge reads ----

    public bool SpeculationActive() { return _speculative; }

    // The accepted state is the given player's hand holding the ball (a pitch can follow).
    public bool AcceptedHandHeldBy(int playerId)
    {
        return playerId > 0 && _acceptedKind == PossessionState && _acceptedHold == HoldHand && _acceptedHolderId == playerId;
    }

    public bool AcceptedHandPitch(int pitcher, int number)
    {
        return _acceptedKind == HandPitchState && _acceptedLineage.SamePitch(pitcher, number);
    }

    public bool ShowsPitch(int pitcher, int number)
    {
        return _lineage.SamePitch(pitcher, number);
    }

    public bool PitchVoided(int pitcher, int number)
    {
        return pitcher > 0 && pitcher == _voidPitcher && number == _voidNumber;
    }

    private bool VoidedMotion()
    {
        return (_acceptedKind == HandPitchState || _acceptedKind == HitState) &&
            _acceptedLineage.SamePitch(_voidPitcher, _voidNumber);
    }

    private PitchLineage LineageFor(int kind)
    {
        if (kind == HandPitchState) return _releaseLineage;
        if (kind == StageState || kind == PitchState) return new PitchLineage();
        return _lineage;
    }

    // The mound's pitch ID for the HandPitch about to be launched by this client.
    private void PrepareReleaseLineage(int pitchNumber, int prepGeneration, int targetBatter)
    {
        _releaseLineage = new PitchLineage();
        _releaseLineage.pitcher = LocalId();
        _releaseLineage.number = pitchNumber;
        _releaseLineage.prep = prepGeneration;
        _releaseLineage.batter = targetBatter;
    }

    private bool GateHandPitch()
    {
        if (pitchJudge == null || localOnly || !_acceptedLineage.IsPitch()) return false;
        return pitchJudge.GateHandPitch(_acceptedLineage.pitcher, _acceptedLineage.number, _acceptedLineage.prep,
            _acceptedLineage.batter);
    }

    private void NotifyPitchJudge()
    {
        if (pitchJudge != null && !localOnly) pitchJudge.OnBallStateApplied();
    }

    // ---- Speculation ----

    // Flies the prepared pitch from its release point while the ball is shown, and accepted,
    // in that pitcher's hand; elapsed is the flight time since the release the batter saw.
    // Nothing is published; the formal HandPitch merges it, anything else rolls it back.
    public bool BeginSpeculativePitch(int pitcher, Vector3 position, Vector3 velocity, Vector3 spin,
        int wakeMode, float wakeCoefficient, Vector3 wakeDirection, float elapsed)
    {
        if (body == null || localOnly || _pending || !AcceptedHandHeldBy(pitcher) ||
            _localHold != HoldHand || _localHolderId != pitcher) return false;
        if (!FiniteVector(position) || !FiniteVector(velocity) || !FiniteVector(spin) || !Finite(elapsed)) return false;
        WakeProfile wake = MakeWake(HandPitchState, wakeMode, wakeCoefficient, wakeDirection, 0f, 0f);
        float duration = Mathf.Clamp(elapsed, 0f, Mathf.Max(0f, maximumCatchupSeconds));
        BallFlightState state = AdvanceToGround(position, velocity, spin, BallAerodynamics.PrepareWake(wake, spin),
            duration, Time.fixedDeltaTime, 0f);
        LaunchPitch(state.position, state.velocity, spin, wake, state.time);
        _speculative = true;
        _speculativePitcher = pitcher;
        // No bat contact, strike call, catch or pickup on a flight nobody has published.
        contactConsumed = true;
        UpdatePickupable();
        if (networkDiagnostics) Diag("speculate pitcher=" + pitcher + " elapsed=" + elapsed);
        return true;
    }

    // Rolls a speculative pitch back: to the pitcher's hand when that is still the accepted
    // state, otherwise hidden until the next state (the pitcher's recall) arrives.
    public void EndSpeculation()
    {
        if (!_speculative) return;
        _speculative = false;
        if (motionObserver != null) motionObserver.CancelPitch();
        if (feedbackListener != null) feedbackListener.SendCustomEvent("OnBallReset");
        // Not a catch: the speculative flight never was the network state.
        _catchEffectFlight = flightSequence;
        if (_acceptedKind == PossessionState) ApplyPossession();
        else
        {
            StopFlightForHold();
            SetPresentation(false);
        }
        if (networkDiagnostics) Diag("speculation rolled back pitcher=" + _speculativePitcher);
        NotifyPresentationListeners();
    }

    private void ClearSpeculation()
    {
        _speculative = false;
    }

    // Launches the accepted HandPitch at flight time `elapsed` from its release, counted from
    // the release the batter saw (never from the server time). A speculative flight in progress
    // is replaced: the body takes the formal state at once and the model eases over.
    public bool LaunchAcceptedHandPitchAt(float elapsed)
    {
        if (body == null || _acceptedKind != HandPitchState || !Finite(elapsed)) return false;
        bool blend = _speculative && flying;
        Vector3 shown = blend ? ShownBallPosition() : Vector3.zero;
        float duration = Mathf.Clamp(elapsed, 0f, Mathf.Max(0f, maximumCatchupSeconds));
        ExitHold();
        BallFlightState state = AdvanceToGround(_acceptedPosition, _acceptedVelocity, _acceptedSpin,
            BallAerodynamics.PrepareWake(_acceptedWake, _acceptedSpin), duration, Time.fixedDeltaTime, 0f);
        LaunchPitch(state.position, state.velocity, _acceptedSpin, _acceptedWake, state.time);
        _lineage = _acceptedLineage;
        if (blend) BeginVisualCorrection(shown - state.position);
        if (networkDiagnostics)
            Diag("launch handpitch pitch=" + _acceptedLineage.pitcher + "/" + _acceptedLineage.number + " elapsed=" + elapsed +
                " merged=" + blend + " offset=" + (blend ? (shown - state.position).magnitude : 0f));
        NotifyPresentationListeners();
        return true;
    }

    // ---- Void ----

    // serverNow may be NaN: it is read only for the pitcher's recall.
    // The pitch is invalid or cancelled: nothing of it is launched again here; its strike call
    // and batting feedback are dropped if it is what this client shows; its pitcher takes the
    // ball back into the glove (an ordinary Possession) when the ball still comes from it.
    public void VoidPitch(int pitcher, int number, double serverNow)
    {
        if (pitcher <= 0 || number <= 0) return;
        _voidPitcher = pitcher;
        _voidNumber = number;
        bool shown = _lineage.SamePitch(pitcher, number);
        if (_speculative && _speculativePitcher == pitcher) EndSpeculation();
        if (shown && flying)
        {
            contactConsumed = true;
            if (motionObserver != null) motionObserver.CancelPitch();
            if (feedbackListener != null) feedbackListener.SendCustomEvent("OnBallReset");
        }
        if (networkDiagnostics) Diag("void pitch=" + pitcher + "/" + number + " shown=" + shown);
        if (pitcher == LocalId() && (shown || _acceptedLineage.SamePitch(pitcher, number)))
            RecallToLocalGlove(double.IsNaN(serverNow) ? Networking.GetServerTimeInSeconds() : serverNow);
    }

    // ---- Visual correction ----

    private Vector3 ShownBallPosition()
    {
        if (_visualCorrecting && ballRenderer != null) return ballRenderer.transform.position;
        return body != null ? body.position : transform.position;
    }

    private void BeginVisualCorrection(Vector3 offset)
    {
        if (ballRenderer == null || !FiniteVector(offset) || !(speculationBlendSeconds > 0f) || offset.sqrMagnitude < 0.000001f)
        {
            EndVisualCorrection();
            return;
        }
        Transform model = ballRenderer.transform;
        if (!_modelRestKnown)
        {
            _modelRestLocal = model.localPosition;
            _modelRestKnown = true;
        }
        _visualOffset = offset;
        _visualBlendTotal = speculationBlendSeconds;
        _visualBlendLeft = speculationBlendSeconds;
        if (_visualCorrecting) return;
        _visualCorrecting = true;
        StepVisualCorrection();
    }

    // Once per rendered frame while correcting: the model sits at the body plus the remaining
    // offset in world space (the body spins, so a local offset would orbit).
    public void StepVisualCorrection()
    {
        if (!_visualCorrecting) return;
        _visualBlendLeft -= Time.deltaTime;
        if (_visualBlendLeft <= 0f || body == null || !flying || ballRenderer == null)
        {
            EndVisualCorrection();
            return;
        }
        float k = _visualBlendLeft / _visualBlendTotal;
        // Smooth ease-out: the remaining offset shrinks faster than linearly at the start.
        ballRenderer.transform.position = body.position + _visualOffset * (k * k);
        SendCustomEventDelayedFrames(nameof(StepVisualCorrection), 1);
    }

    private void EndVisualCorrection()
    {
        if (_visualCorrecting && _modelRestKnown && ballRenderer != null) ballRenderer.transform.localPosition = _modelRestLocal;
        _visualCorrecting = false;
        _visualBlendLeft = 0f;
    }
}
