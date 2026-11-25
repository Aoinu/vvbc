using UnityEngine;
using VRC.SDKBase;

public struct PitchProgress
{
    public int pitcher, applied;
}

// Manual-pitch synchronisation on the shared Ball (docs/development/manual-pitch-sync.md).
// The pitcher's client decides the formal pitch (release point P0, velocity, spin, wake and
// the release's server time) and publishes it once as a HandPitch snapshot. Every receiver
// plays that same snapshot, timed to the pitcher's avatar as this client shows it:
//
//   ageServer   = server now - release server time                 (shared server clock)
//   avatarDelay = Time.realtimeSinceStartup - SimulationTime(pitcher) (this client's local clock)
//   displayAge  = ageServer - avatarDelay
//
// The plan is made once per pitch ID, when the snapshot is first applied. A negative
// displayAge waits for the release as shown here; otherwise the flight starts displayAge
// into the shared flight model. Nothing re-times a flight after it started, and no delay ever
// invalidates a pitch.
public partial class BallBehaviour
{
    public const int AvatarDelayUsed = 0;
    public const int AvatarDelayServerOnly = 1;
    public const int AvatarDelayClamped = 2;

    [Header("Manual pitch sync")]
    [Tooltip("Largest avatar display delay (Time.realtimeSinceStartup - Networking.SimulationTime(pitcher)) a receiver waits for, in seconds. A larger delay is clamped to it; a negative, non-finite or unavailable one falls back to the server time alone. Default 1.")]
    public float maximumAvatarDelaySeconds = 1f;
    [Tooltip("Logs every manual pitch with the prefix PitchSync[diag], keyed by pitch ID (pitcher/number), on the pitcher (release, serialization) and on receivers (reception, timing plan, launch, later states). For real-client verification only; leave off in normal play.")]
    public bool pitchSyncDiagnostics;

    // The lineage of what this client shows: set from every applied state and every local action.
    private PitchLineage _lineage;
    // The lineage a HandPitch released here starts (from the mound at the release).
    private PitchLineage _releaseLineage;
    // The highest pitch number per pitcher whose flight was planned or launched here.
    private PitchProgress[] _pitchProgress = new PitchProgress[0];
    // A HandPitch planned ahead of its displayed release: launched when that time comes, if it
    // is still the accepted state.
    private bool _handPitchWaiting;
    private PitchLineage _waitingLineage;
    private float _waitingDue;
    // Diagnostics: the timing plan of the last HandPitch and the last received snapshot.
    [HideInInspector] public double lastPitchAgeServer;
    [HideInInspector] public float lastPitchAvatarDelay;
    [HideInInspector] public float lastPitchDisplayAge;
    [HideInInspector] public int lastPitchDelaySource = -1;
    private int _diagReceivedSequence = -1;
    private int _diagReceivedPublisher = -1;

    // The accepted state is the given player's hand holding the ball (a pitch can follow).
    public bool AcceptedHandHeldBy(int playerId)
    {
        return playerId > 0 && _acceptedKind == PossessionState && _acceptedHold == HoldHand && _acceptedHolderId == playerId;
    }

    public bool ShowsPitch(int pitcher, int number)
    {
        return _lineage.SamePitch(pitcher, number);
    }

    public bool HandPitchWaiting() { return _handPitchWaiting; }

    private Vector3 EncodeHandSession(int session)
    {
        // A hand possession has no angular velocity. Two float lanes preserve every int bit.
        return new Vector3(session & 65535, session >> 16, 0f);
    }

    private int DecodeHandSession(Vector3 value)
    {
        return (int)value.x | ((int)value.y << 16);
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

    // A pitch that was already planned here, a hand hold it was pitched from, or a pitch state
    // older than the accepted state is history: it never restores a flight, a hold, or a hit or
    // catch that the pitch led to.
    private bool RejectPitchSnapshot(int kind, PitchLineage lineage, int hold, int holder, Vector3 spin, double time)
    {
        if (lineage.IsPitch() && !_acceptedLineage.IsPitch() && _acceptedSequence > 0 && time < _acceptedServerTime)
            return true;
        if (kind == PossessionState && hold == HoldHand)
        {
            int attempt = DecodeHandSession(spin);
            if (HandPitchApplied(holder, attempt) || (attempt > 0 && _acceptedLineage.SamePitch(holder, attempt))) return true;
        }
        if (kind != HandPitchState || !lineage.IsPitch()) return false;
        if (_acceptedLineage.SamePitch(lineage.pitcher, lineage.number))
            return _acceptedKind != HandPitchState;
        return HandPitchApplied(lineage) || (_acceptedSequence > 0 && time < _acceptedServerTime);
    }

    private PitchLineage LineageFor(int kind)
    {
        if (kind == HandPitchState) return _releaseLineage;
        if (kind == StageState || kind == PitchState) return new PitchLineage();
        return _lineage;
    }

    // The pitch ID of the HandPitch about to be launched by this client.
    private void PrepareReleaseLineage(int pitchNumber)
    {
        _releaseLineage = new PitchLineage();
        _releaseLineage.pitcher = LocalId();
        _releaseLineage.number = pitchNumber;
    }

    // ---- Timing ----

    // Which avatar delay is used: the measured one within 0..maxDelay, maxDelay for a larger
    // one, and none (server time alone) for a negative, non-finite or unavailable one.
    public static int AvatarDelaySource(float avatarDelay, float maxDelay)
    {
        if (!(avatarDelay >= 0f) || avatarDelay > float.MaxValue) return AvatarDelayServerOnly;
        return avatarDelay > Mathf.Max(0f, maxDelay) ? AvatarDelayClamped : AvatarDelayUsed;
    }

    public static float UsedAvatarDelay(float avatarDelay, float maxDelay)
    {
        int source = AvatarDelaySource(avatarDelay, maxDelay);
        if (source == AvatarDelayServerOnly) return 0f;
        return source == AvatarDelayClamped ? Mathf.Max(0f, maxDelay) : avatarDelay;
    }

    // Flight time to show now, from the release's server age and this client's avatar delay.
    // Negative: the release is still ahead on this client's view of the pitcher.
    public static float DisplayAge(double ageServer, float avatarDelay, float maxDelay)
    {
        // NaN fails both comparisons; infinities fail one.
        double age = ageServer > -1e9 && ageServer < 1e9 ? ageServer : 0.0;
        return (float)(age - UsedAvatarDelay(avatarDelay, maxDelay));
    }

    // Reads the clocks once and plans the accepted HandPitch. SimulationTime is read only when
    // the pitcher is a valid remote player; otherwise the server time alone decides.
    private void PlanAcceptedHandPitch()
    {
        double age = Networking.CalculateServerDeltaTime(Networking.GetServerTimeInSeconds(), _acceptedServerTime);
        float now = Time.realtimeSinceStartup;
        float avatarDelay = float.NaN;
        float simulation = float.NaN;
        VRCPlayerApi pitcher = VRCPlayerApi.GetPlayerById(_acceptedLineage.pitcher);
        if (Utilities.IsValid(pitcher))
        {
            if (pitcher.isLocal) avatarDelay = 0f;
            else
            {
                simulation = Networking.SimulationTime(pitcher);
                avatarDelay = now - simulation;
            }
        }
        if (pitchSyncDiagnostics)
            PitchDiag(_acceptedLineage, "simulationTime=" + simulation + " (local rt s) pitcherPresent=" + Utilities.IsValid(pitcher));
        PlanHandPitchAt(age, avatarDelay, now);
    }

    // The plan from given clocks (tested directly). ageServer is server seconds since the
    // release; avatarDelay and now are this client's Time.realtimeSinceStartup seconds.
    public void PlanHandPitchAt(double ageServer, float avatarDelay, float now)
    {
        if (_acceptedKind != HandPitchState || !_acceptedLineage.IsPitch() || HandPitchApplied(_acceptedLineage)) return;
        MarkHandPitchApplied(_acceptedLineage);
        lastPitchAgeServer = ageServer;
        lastPitchDelaySource = AvatarDelaySource(avatarDelay, maximumAvatarDelaySeconds);
        lastPitchAvatarDelay = UsedAvatarDelay(avatarDelay, maximumAvatarDelaySeconds);
        lastPitchDisplayAge = DisplayAge(ageServer, avatarDelay, maximumAvatarDelaySeconds);
        if (pitchSyncDiagnostics)
            PitchDiag(_acceptedLineage, "plan ageServer=" + ageServer + " (server s) avatarDelay=" + avatarDelay +
                " used=" + lastPitchAvatarDelay + " source=" + DelaySourceName(lastPitchDelaySource) + " (local rt s) displayAge=" +
                lastPitchDisplayAge + " rt=" + now);
        if (lastPitchDisplayAge >= 0f)
        {
            LaunchHandPitchAt(lastPitchDisplayAge);
            return;
        }
        // The release is still ahead on this client's view of the pitcher: the ball stays as
        // shown (in the pitcher's hand) until then. The wait never exceeds the delay bound.
        float wait = Mathf.Min(-lastPitchDisplayAge, Mathf.Max(0f, maximumAvatarDelaySeconds));
        _handPitchWaiting = true;
        _waitingLineage = _acceptedLineage;
        _waitingDue = now + wait;
        SendCustomEventDelayedSeconds(nameof(LaunchWaitingHandPitch), wait);
    }

    // The displayed release has come. Only the same accepted HandPitch is launched; anything
    // accepted since (a recall, a Stage) has replaced it.
    public void LaunchWaitingHandPitch()
    {
        LaunchWaitingHandPitchAt(Time.realtimeSinceStartup);
    }

    // The delayed event runs on the frame clock, the due time on Time.realtimeSinceStartup read
    // mid-frame, so the event can come a little early: it is then scheduled again for the rest
    // of the wait (never dropped). A late event launches that much into the flight.
    public void LaunchWaitingHandPitchAt(float now)
    {
        if (!_handPitchWaiting) return;
        float remaining = _waitingDue - now;
        if (remaining > 0.002f)
        {
            if (remaining > 0.05f) SendCustomEventDelayedSeconds(nameof(LaunchWaitingHandPitch), remaining);
            else SendCustomEventDelayedFrames(nameof(LaunchWaitingHandPitch), 1);
            return;
        }
        _handPitchWaiting = false;
        if (_acceptedKind != HandPitchState || !_acceptedLineage.SamePitch(_waitingLineage.pitcher, _waitingLineage.number) ||
            SpeculationUnresolved())
        {
            if (pitchSyncDiagnostics) PitchDiag(_waitingLineage, "wait dropped: accepted kind=" + _acceptedKind);
            return;
        }
        LaunchHandPitchAt(Mathf.Max(0f, now - _waitingDue));
    }

    private void CancelHandPitchWait()
    {
        _handPitchWaiting = false;
    }

    // Starts the accepted HandPitch `elapsed` seconds into its flight, from its own P0, velocity,
    // spin and wake. Older than the catch-up window, it is shown from the window (or where it
    // first met the ground), never hidden or invalidated, and it can no longer meet a bat.
    private void LaunchHandPitchAt(float elapsed)
    {
        _lineage = _acceptedLineage;
        if (body == null) return;
        float window = Mathf.Max(0f, maximumCatchupSeconds);
        bool stale = elapsed > window;
        float duration = Mathf.Clamp(elapsed, 0f, window);
        ExitHold();
        BallFlightState state = AdvanceToGround(_acceptedPosition, _acceptedVelocity, _acceptedSpin,
            BallAerodynamics.PrepareWake(_acceptedWake, _acceptedSpin), duration, Time.fixedDeltaTime, 0f);
        LaunchPitch(_acceptedPosition, _acceptedVelocity, state.position, state.velocity, _acceptedSpin, _acceptedWake, state.time);
        if (stale)
        {
            contactConsumed = true;
            if (motionObserver != null) motionObserver.CancelPitch();
        }
        _lineage = _acceptedLineage;
        if (pitchSyncDiagnostics)
            PitchDiag(_acceptedLineage, "launch elapsed=" + elapsed + " simulated=" + state.time + " stale=" + stale +
                " start=" + state.position.ToString("F4") + " p0=" + _acceptedPosition.ToString("F4"));
        NotifyPresentationListeners();
    }

    // ---- Diagnostics (pitchSyncDiagnostics) ----

    private static string DelaySourceName(int source)
    {
        if (source == AvatarDelayUsed) return "avatar";
        if (source == AvatarDelayClamped) return "avatar-clamped";
        return "server-only";
    }

    // One number that changes with any field of the formal pitch, to match a sender's and a
    // receiver's lines at a glance.
    private static float PitchDigest(Vector3 position, Vector3 velocity, Vector3 spin, double time)
    {
        return Vector3.Dot(position, new Vector3(1f, 3f, 7f)) + Vector3.Dot(velocity, new Vector3(11f, 13f, 17f)) +
            Vector3.Dot(spin, new Vector3(19f, 23f, 29f)) + (float)(time % 1000.0);
    }

    private string PitchValues(Vector3 position, Vector3 velocity, Vector3 spin, double time)
    {
        return "p0=" + position.ToString("F4") + " v=" + velocity.ToString("F4") + " speedKmh=" + (velocity.magnitude * 3.6f).ToString("F2") +
            " spin=" + spin.ToString("F3") + " release=" + time + " (server s) digest=" + PitchDigest(position, velocity, spin, time).ToString("F4");
    }

    private void PitchDiag(PitchLineage lineage, string message)
    {
        Debug.Log("PitchSync[diag] pitch=" + lineage.pitcher + "/" + lineage.number + " local=" + LocalId() +
            " rt=" + Time.realtimeSinceStartup + " server=" + Networking.GetServerTimeInSeconds() + " " + message);
    }

    // Pitcher: the formal pitch as decided at the release (P0 is the mound's fixed release point).
    private void PitchDiagRelease(Vector3 position, Vector3 velocity, Vector3 spin, WakeProfile wake, double serverTime)
    {
        string mound = manualPitcher != null
            ? " hand=" + (manualPitcher.ThrowingHand() == GloveBehaviour.HandLeft ? "L" : "R") + " anchor=" +
                manualPitcher.ReleaseAnchorPosition().ToString("F4")
            : "";
        PitchDiag(_releaseLineage, "release pitcher=" + LocalId() + mound + " " + PitchValues(position, velocity, spin, serverTime) +
            " wake=" + wake.mode + "/" + wake.coefficient + " sendRequested rt=" + Time.realtimeSinceStartup);
    }

    private void PitchDiagSerialize()
    {
        PitchDiag(SyncedLineage(), "serialize seq=" + _sequence + " publisher=" + _publisherId + " " +
            PitchValues(_position, _velocity, _spin, _serverTime));
    }

    private void PitchDiagSerialized(bool success, int bytes)
    {
        PitchDiag(SyncedLineage(), "serialized success=" + success + " bytes=" + bytes + " seq=" + _sequence);
    }

    // Receiver: what arrived, and whether the deserialization brought anything new. A repeated
    // sequence from the same publisher is reported, so that a send that succeeded but left the
    // synced fields unchanged on this client can be told apart from a missing send.
    private void PitchDiagReceived(float sendTime, float receiveTime, bool storage)
    {
        bool unchanged = _sequence == _diagReceivedSequence && _publisherId == _diagReceivedPublisher;
        _diagReceivedSequence = _sequence;
        _diagReceivedPublisher = _publisherId;
        PitchLineage lineage = SyncedLineage();
        if (!lineage.IsPitch() && !unchanged) return;
        PitchDiag(lineage, "received kind=" + _kind + " seq=" + _sequence + " publisher=" + _publisherId +
            (_kind == HandPitchState ? " " + PitchValues(_position, _velocity, _spin, _serverTime) : "") +
            " sendTime=" + sendTime + " receiveTime=" + receiveTime + " (local rt s) storage=" + storage +
            (unchanged ? " UNCHANGED (no new sequence)" : ""));
    }

    // ---- Flight diagnostics (pitchSyncDiagnostics) ----

    // A launched pitch is compared, fixed step by fixed step, with the flight model it was
    // solved with, started from the same launch state. The first FlightDiagFirstSteps steps
    // and every FlightDiagEvery-th step up to FlightDiagMaxSteps are logged, with each
    // collision while the comparison runs. Off (-1) whenever the diagnostics are off.
    private const int FlightDiagMaxSteps = 90;
    private const int FlightDiagEvery = 6;
    private const int FlightDiagFirstSteps = 3;
    private int _diagFlightStep = -1;
    private int _diagFlightSequence;
    private BallFlightState _diagModel;
    private Vector3 _diagModelSpin;
    private BallWake _diagModelWake;

    private void FlightDiagLog(string message)
    {
        Debug.Log("FlightDiag local=" + LocalId() + " flight=" + flightSequence + " fixedTime=" + Time.fixedTime.ToString("F4") +
            " fixedDt=" + Time.fixedDeltaTime.ToString("F5") + " " + message);
    }

    private void FlightDiagBegin(Vector3 position, Vector3 velocity, Vector3 spin, float elapsed)
    {
        _diagFlightStep = 0;
        _diagFlightSequence = flightSequence;
        _diagModel = new BallFlightState();
        _diagModel.position = position;
        _diagModel.velocity = velocity;
        _diagModel.time = elapsed;
        _diagModelSpin = spin;
        _diagModelWake = _wake;
        FlightDiagLog("begin p0=" + position.ToString("F4") + " v=" + velocity.ToString("F4") + " w=" + spin.ToString("F3") +
            " elapsed=" + elapsed.ToString("F4") + " drag=" + dragCoefficient.ToString("F5") + " magnus=" + magnusCoefficient.ToString("F5") +
            " wake=" + _wake.mode + "/" + _wake.coefficient.ToString("F4") + " dir=" + _wake.direction.ToString("F3") +
            " maxW=" + (body != null ? body.maxAngularVelocity.ToString("F1") : "-") + " gravity=" + Physics.gravity.ToString("F2"));
    }

    // Before this step's force: the body as PhysX left it against the model at the same time.
    private void FlightDiagStep()
    {
        if (flightSequence != _diagFlightSequence)
        {
            FlightDiagLog("end: another flight started");
            _diagFlightStep = -1;
            return;
        }
        int step = _diagFlightStep;
        if (step < FlightDiagFirstSteps || step % FlightDiagEvery == 0)
        {
            Vector3 w = body.angularVelocity;
            Vector3 v = body.velocity;
            FlightDiagLog("step " + step + " t=" + _diagModel.time.ToString("F4") +
                " pos=" + body.position.ToString("F3") + " v=" + v.ToString("F3") + " w=" + w.ToString("F2") +
                " aMagnus=" + (magnusCoefficient * Vector3.Cross(w, v)).ToString("F2") +
                " | model pos=" + _diagModel.position.ToString("F3") + " v=" + _diagModel.velocity.ToString("F3") +
                " | dpos=" + (body.position - _diagModel.position).ToString("F3") + " dw=" + (w - _diagModelSpin).ToString("F2") +
                " kinematic=" + body.isKinematic + " grounded=" + _flightGrounded + " hold=" + _localHold + " wakeTime=" + _wakeTime.ToString("F4"));
        }
        _diagModel = BallFlightModel.Step(_diagModel, _diagModelSpin, Physics.gravity, dragCoefficient, magnusCoefficient,
            _diagModelWake, Time.fixedDeltaTime);
        _diagFlightStep = step + 1 < FlightDiagMaxSteps ? step + 1 : -1;
        if (_diagFlightStep < 0) FlightDiagLog("end: step limit");
    }

    private void FlightDiagReassert(bool kept, float elapsed)
    {
        FlightDiagLog("reassert kept=" + kept + " elapsed=" + elapsed.ToString("F4") + " kinematic=" + body.isKinematic +
            " pos=" + body.position.ToString("F3") + " v=" + body.velocity.ToString("F3") + " w=" + body.angularVelocity.ToString("F2") +
            " | release pos=" + _releasePosition.ToString("F3") + " v=" + _releaseVelocity.ToString("F3") + " w=" + _releaseSpin.ToString("F2"));
    }

    private void FlightDiagCollision(Collision collision)
    {
        if (collision == null) { FlightDiagLog("collision (null)"); return; }
        GameObject other = collision.gameObject;
        FlightDiagLog("collision with=" + (other != null ? other.name + " layer=" + other.layer : "-") +
            " pos=" + (body != null ? body.position.ToString("F3") : "-") + " relV=" + collision.relativeVelocity.ToString("F2"));
    }
}
