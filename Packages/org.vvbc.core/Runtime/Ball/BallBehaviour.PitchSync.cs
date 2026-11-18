using UnityEngine;
using VRC.SDKBase;

public struct PitchProgress
{
    public int pitcher, applied, voided;
}

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
    // Release and void watermarks per pitcher. Older attempts cannot restart a play.
    private PitchProgress[] _pitchProgress = new PitchProgress[0];
    private bool _voidPresentation;
    // A speculative pitch: flown locally from the prepared data, never published, no bat
    // contact, catch or pickup until it is merged into the formal flight or rolled back.
    private bool _speculative;
    private int _speculativePitcher, _speculativeNumber;
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

    public int AcceptedPitchAttempt(int pitcher)
    {
        return AcceptedHandHeldBy(pitcher) ? DecodeHandSession(_acceptedSpin) : 0;
    }

    private Vector3 EncodeHandSession(int session)
    {
        // A hand possession has no angular velocity. Two float lanes preserve every int bit.
        return new Vector3(session & 65535, session >> 16, 0f);
    }

    private int DecodeHandSession(Vector3 value)
    {
        return (int)value.x | ((int)value.y << 16);
    }

    public bool AcceptedHandPitch(int pitcher, int number)
    {
        return _acceptedKind == HandPitchState && _acceptedLineage.SamePitch(pitcher, number);
    }

    public bool AcceptedPitchLineage(int pitcher, int number)
    {
        return _acceptedLineage.SamePitch(pitcher, number);
    }

    public bool ShowsPitch(int pitcher, int number)
    {
        return _lineage.SamePitch(pitcher, number);
    }

    public bool PitchVoided(int pitcher, int number)
    {
        int slot = PitchProgressSlot(pitcher);
        return slot >= 0 && number > 0 && number <= _pitchProgress[slot].voided;
    }

    private int PitchProgressSlot(int pitcher)
    {
        if (pitcher <= 0) return -1;
        for (int i = 0; i < _pitchProgress.Length; i++)
            if (_pitchProgress[i].pitcher == pitcher) return i;
        return -1;
    }

    private int EnsurePitchProgress(int pitcher)
    {
        int slot = PitchProgressSlot(pitcher);
        if (slot >= 0) return slot;
        slot = _pitchProgress.Length;
        PitchProgress[] next = new PitchProgress[slot + 1];
        for (int i = 0; i < slot; i++) next[i] = _pitchProgress[i];
        PitchProgress entry = new PitchProgress();
        entry.pitcher = pitcher;
        next[slot] = entry;
        _pitchProgress = next;
        return slot;
    }

    private bool HandPitchApplied(PitchLineage lineage)
    {
        return HandPitchApplied(lineage.pitcher, lineage.number);
    }

    private bool HandPitchApplied(int pitcher, int number)
    {
        int slot = PitchProgressSlot(pitcher);
        return slot >= 0 && number > 0 && number <= _pitchProgress[slot].applied;
    }

    private void MarkHandPitchApplied(PitchLineage lineage)
    {
        if (!lineage.IsPitch()) return;
        int slot = EnsurePitchProgress(lineage.pitcher);
        PitchProgress entry = _pitchProgress[slot];
        entry.applied = Mathf.Max(entry.applied, lineage.number);
        _pitchProgress[slot] = entry;
    }

    private bool VoidedMotion()
    {
        return PitchVoided(_acceptedLineage.pitcher, _acceptedLineage.number) ||
            (_acceptedKind == PossessionState && _acceptedHold == HoldHand &&
                PitchVoided(_acceptedHolderId, DecodeHandSession(_acceptedSpin)));
    }

    private bool RejectPitchSnapshot(int kind, PitchLineage lineage, int hold, int holder, Vector3 spin, double time)
    {
        if (PitchVoided(lineage.pitcher, lineage.number)) return true;
        if (lineage.IsPitch() && !_acceptedLineage.IsPitch() && _acceptedSequence > 0 && time < _acceptedServerTime)
            return true;
        if (kind == PossessionState && hold == HoldHand)
        {
            int attempt = DecodeHandSession(spin);
            if (PitchVoided(holder, attempt) || HandPitchApplied(holder, attempt) ||
                (attempt > 0 && _acceptedLineage.SamePitch(holder, attempt))) return true;
        }
        if (kind != HandPitchState || !lineage.IsPitch()) return false;
        if (_acceptedLineage.SamePitch(lineage.pitcher, lineage.number))
            return _acceptedKind != HandPitchState;
        return HandPitchApplied(lineage) || (_acceptedSequence > 0 && time < _acceptedServerTime);
    }

    // A catch or a hit can arrive first from a new Ball owner. The original pitch still
    // reaches its batter's judge, but none of its flight or possession state is restored.
    private void JudgeOlderHandPitch(PitchLineage lineage)
    {
        if (_kind != HandPitchState || _sequence <= 0 || lineage.batter <= 0 || _publisherId != lineage.pitcher || localOnly ||
            pitchJudge == null || _acceptedKind == HandPitchState || !AcceptedPitchLineage(lineage.pitcher, lineage.number) ||
            PitchVoided(lineage.pitcher, lineage.number)) return;
        pitchJudge.GateHandPitch(lineage.pitcher, lineage.number, lineage.prep, lineage.batter);
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
        if (pitchJudge == null || localOnly || !_acceptedLineage.IsPitch() || _acceptedLineage.batter <= 0) return false;
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
    public bool BeginSpeculativePitch(int pitcher, int number, Vector3 position, Vector3 velocity, Vector3 spin,
        int wakeMode, float wakeCoefficient, Vector3 wakeDirection, float elapsed)
    {
        if (body == null || localOnly || _pending || number <= 0 || number != AcceptedPitchAttempt(pitcher) ||
            PitchVoided(pitcher, number) ||
            _localHold != HoldHand || _localHolderId != pitcher) return false;
        if (!FiniteVector(position) || !FiniteVector(velocity) || !FiniteVector(spin) || !Finite(elapsed)) return false;
        WakeProfile wake = MakeWake(HandPitchState, wakeMode, wakeCoefficient, wakeDirection, 0f, 0f);
        float duration = Mathf.Clamp(elapsed, 0f, Mathf.Max(0f, maximumCatchupSeconds));
        BallFlightState state = AdvanceToGround(position, velocity, spin, BallAerodynamics.PrepareWake(wake, spin),
            duration, Time.fixedDeltaTime, 0f);
        LaunchPitch(state.position, state.velocity, spin, wake, state.time);
        _speculative = true;
        _speculativePitcher = pitcher;
        _speculativeNumber = number;
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
        if (body == null || _acceptedKind != HandPitchState || !Finite(elapsed) ||
            VoidedMotion() || HandPitchApplied(_acceptedLineage)) return false;
        bool blend = _speculative && flying && _acceptedLineage.SamePitch(_speculativePitcher, _speculativeNumber);
        Vector3 shown = blend ? ShownBallPosition() : Vector3.zero;
        float duration = Mathf.Clamp(elapsed, 0f, Mathf.Max(0f, maximumCatchupSeconds));
        ExitHold();
        BallFlightState state = AdvanceToGround(_acceptedPosition, _acceptedVelocity, _acceptedSpin,
            BallAerodynamics.PrepareWake(_acceptedWake, _acceptedSpin), duration, Time.fixedDeltaTime, 0f);
        LaunchPitch(state.position, state.velocity, _acceptedSpin, _acceptedWake, state.time);
        _lineage = _acceptedLineage;
        MarkHandPitchApplied(_acceptedLineage);
        if (blend) BeginVisualCorrection(shown - state.position);
        if (networkDiagnostics)
            Diag("launch handpitch pitch=" + _acceptedLineage.pitcher + "/" + _acceptedLineage.number + " elapsed=" + elapsed +
                " merged=" + blend + " offset=" + (blend ? (shown - state.position).magnitude : 0f));
        NotifyPresentationListeners();
        return true;
    }

    // ---- Void ----

    // serverNow may be NaN: it is read only for the pitcher's recall.
    // Stop only this pitch and states derived from it. Its pitcher publishes an ordinary
    // glove possession with no lineage, so that every client can resume from the same state.
    public void VoidPitch(int pitcher, int number, double serverNow)
    {
        if (localOnly || pitcher <= 0 || number <= 0) return;
        int slot = EnsurePitchProgress(pitcher);
        PitchProgress progress = _pitchProgress[slot];
        progress.voided = Mathf.Max(progress.voided, number);
        _pitchProgress[slot] = progress;

        bool accepted = _acceptedLineage.SamePitch(pitcher, number) || AcceptedPitchAttempt(pitcher) == number;
        bool pending = _pending && (_pendingLineage.SamePitch(pitcher, number) ||
            (_pendingKind == PossessionState && _pendingHold == HoldHand && _pendingHolderId == pitcher &&
                DecodeHandSession(_networkPendingSpin) == number));
        bool shown = _lineage.SamePitch(pitcher, number) ||
            (_speculative && _speculativePitcher == pitcher && _speculativeNumber == number) ||
            (accepted && _localHold == HoldHand && _localHolderId == pitcher);
        if (pending)
        {
            _pending = false;
            _lossSuspected = false;
            _ownershipRequested = false;
        }
        if (_pending || (!shown && !pending && !accepted)) return;
        bool newerAccepted = pending && !accepted && _acceptedSequence > 0 && _acceptedServerTime >= _pendingServerTime;
        if (shown || pending || (accepted && !_lineage.IsPitch() && _localHold == HoldFree && !flying))
            StopVoidedPitch();
        if (networkDiagnostics) Diag("void pitch=" + pitcher + "/" + number + " shown=" + shown);
        if (pitcher == LocalId() && (accepted || (pending && !newerAccepted) || (shown && _acceptedSequence <= 0)))
            RecallToLocalGlove(double.IsNaN(serverNow) ? Networking.GetServerTimeInSeconds() : serverNow);
        else if (newerAccepted) ApplyAccepted();
    }

    private void StopVoidedPitch()
    {
        _voidPresentation = true;
        ExitHold();
        if (pickup != null && pickup.IsHeld) ForceDropPickup();
        _pickupResolving = false;
        _releaseReassert = false;
        StopFlightForHold();
        _lineage = new PitchLineage();
        if (motionObserver != null) motionObserver.CancelPitch();
        if (feedbackListener != null) feedbackListener.SendCustomEvent("OnBallReset");
        SetPresentation(false);
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
