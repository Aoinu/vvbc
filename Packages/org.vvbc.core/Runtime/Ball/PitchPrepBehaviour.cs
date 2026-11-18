using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;

public struct PitchVerdictWatch
{
    public int number, batter, cancelReason;
    public float due;
    public double cancelTime;
}

// The prepared pitch of one mound, published by its pitcher before the release so the batter
// can recognise the release motion and fly the pitch speculatively
// (docs/development/manual-pitch-sync.md). The mound fills the prep* fields and calls Publish
// whenever the course a release would use changes (lock, default course, batter); nothing is
// sent every frame. A generation names each content; the HandPitch carries the generation it
// was released against, so a pitch is never matched with other prepared data. The values are
// a prediction: the release's own arm speed, pose and control still decide the formal flight.
//
// The pitcher also cancels its own pitch here when the batter it was prepared for left or
// changed before judging it; that is not a validity verdict (PitchJudgeBehaviour has those).
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class PitchPrepBehaviour : TsukimiBehaviour
{
    public const int PitchTypeNone = 0;
    public const int PitchTypeFourSeam = 1;
    private const int MaximumRetries = 3;
    private const int MaximumWatches = 16;

    [Tooltip("The mound this prepared data belongs to.")]
    public ManualPitchBehaviour mound;
    [Tooltip("Optional: the judge of the mound's HomePlate. When empty it is the one registered on that HomePlate's Ball.")]
    public PitchJudgeBehaviour judge;
    [Tooltip("Seconds a publication stays valid for receivers; the pitcher republishes before it ends. Guards against data left by a pitcher who stopped without withdrawing it. Default 600.")]
    public float prepLifetimeSeconds = 600f;
    [Tooltip("Seconds after a pitch prepared for a batter until the pitcher checks that it was judged. Default 3.")]
    public float verdictTimeoutSeconds = 3f;
    [Tooltip("Logs every publication, send and reception with the prefix PitchPrep[diag]. Off in normal play.")]
    public bool diagnostics;

    [UdonSynced] public int prepPitcherId;
    [UdonSynced] public int prepGeneration;
    [UdonSynced] public bool prepReady;
    [UdonSynced] public int prepTargetBatterId = -1;
    [UdonSynced] public int prepPitchType;
    [UdonSynced] public bool prepCourseLocked;
    [UdonSynced] public int prepThrowHand;
    // The ball's ideal release (the solver's P0) and the throwing controller's ideal release
    // in world space at the solve, the calibrated controller release and pose in the pitching
    // frame (for the remote hand, rebuilt from the pitcher's chest), the target and solved
    // direction, the solver speed (m/s), the ideal spin (world, rad/s) and the wake.
    [UdonSynced] public Vector3 prepBallRelease;
    [UdonSynced] public Vector3 prepControllerRelease;
    [UdonSynced] public Vector3 prepCalibratedLocal;
    [UdonSynced] public Quaternion prepCalibratedRotation = Quaternion.identity;
    [UdonSynced] public Vector3 prepTarget;
    [UdonSynced] public Vector3 prepDirection;
    [UdonSynced] public float prepSpeed;
    [UdonSynced] public Vector3 prepSpin;
    [UdonSynced] public int prepWakeMode;
    [UdonSynced] public float prepWakeCoefficient;
    [UdonSynced] public Vector3 prepWakeDirection;
    [UdonSynced] public double prepExpiresAt;
    [UdonSynced] private int[] _cancelKeys = new int[0];
    [UdonSynced] private double[] _cancelTiming = new double[0];
    private readonly PitchDecision[] _cancelHistory = new PitchDecision[PitchDecisionHistory.Capacity];
    private int _cancelSequence;

    private bool _awaitingOwnership;
    private PreparedPitch _queuedPreparation;
    private int _localGeneration;
    private int _latestReceivedPitcher, _latestReceivedGeneration;
    private int _requestedRevision, _serializedRevision, _sentRevision;
    private int _serializedGeneration;
    private int _ownershipEpoch, _serializedEpoch;
    private int _sendRetries;
    private bool _retryScheduled;
    private readonly PitchVerdictWatch[] _watches = new PitchVerdictWatch[MaximumWatches];

    private void Start()
    {
        RegisterWithJudge();
        ApplyStoredCancellations();
    }

    // The judge of the mound's HomePlate: it registers itself on that HomePlate's Ball at its
    // Start, which may come after this one, so it is looked up again until found.
    public PitchJudgeBehaviour Judge()
    {
        if (judge != null) return judge;
        if (mound != null && mound.strikeZone != null && mound.strikeZone.ball != null) judge = mound.strikeZone.ball.pitchJudge;
        return judge;
    }

    private void RegisterWithJudge()
    {
        PitchJudgeBehaviour found = Judge();
        if (found != null) found.RegisterPrep(this);
    }

    private int LocalId()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        return Utilities.IsValid(player) ? player.playerId : 0;
    }

    public bool OwnedLocally()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        return Utilities.IsValid(player) && Networking.IsOwner(player, gameObject);
    }

    // The data is usable for the given pitcher: published ready by that pitcher, who still
    // owns it, and not expired.
    public bool UsableFor(int pitcher, double serverNow)
    {
        if (!prepReady || pitcher <= 0 || prepPitcherId != pitcher || prepGeneration <= 0) return false;
        if (pitcher == _latestReceivedPitcher && prepGeneration < _latestReceivedGeneration) return false;
        return serverNow < prepExpiresAt && PublishedBy(pitcher);
    }

    public bool PublishedBy(int pitcher)
    {
        VRCPlayerApi owner = Networking.GetOwner(gameObject);
        return Utilities.IsValid(owner) && owner.playerId == pitcher;
    }

    private void RestoreQueuedPreparation()
    {
        PreparedPitch data = _queuedPreparation;
        prepPitcherId = data.pitcher;
        prepGeneration = data.generation;
        prepTargetBatterId = data.batter;
        prepThrowHand = data.hand;
        prepWakeMode = data.wakeMode;
        prepExpiresAt = data.expiresAt;
        prepBallRelease = data.ballRelease;
        prepControllerRelease = data.controllerRelease;
        prepCalibratedLocal = data.calibratedLocal;
        prepCalibratedRotation = data.rotation;
        prepTarget = data.target;
        prepDirection = data.direction;
        prepSpin = data.spin;
        prepWakeDirection = data.wakeDirection;
        prepSpeed = data.speed;
        prepWakeCoefficient = data.wakeCoefficient;
        prepReady = data.ready;
        prepCourseLocked = data.locked;
        prepPitchType = data.ready ? PitchTypeFourSeam : PitchTypeNone;
    }

    // The pitcher's mode started: this client publishes from now on.
    public void TakeOwnership()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player) || Networking.IsOwner(player, gameObject)) return;
        Networking.SetOwner(player, gameObject);
    }

    // A new generation describing the course a release would use now (4-Seam), sent.
    public void PublishCourse(int targetBatter, bool locked, int throwHand, Vector3 ballRelease, Vector3 controllerRelease,
        Vector3 calibratedLocal, Quaternion calibratedRotation, Vector3 target, Vector3 direction, float speed, Vector3 spin,
        int wakeMode, float wakeCoefficient, Vector3 wakeDirection, double serverNow)
    {
        SetCourse(targetBatter, locked, throwHand, ballRelease, controllerRelease, calibratedLocal, calibratedRotation, target,
            direction, speed, spin, wakeMode, wakeCoefficient, wakeDirection, serverNow);
        Send();
    }

    // The content of a new generation, not sent yet.
    public void SetCourse(int targetBatter, bool locked, int throwHand, Vector3 ballRelease, Vector3 controllerRelease,
        Vector3 calibratedLocal, Quaternion calibratedRotation, Vector3 target, Vector3 direction, float speed, Vector3 spin,
        int wakeMode, float wakeCoefficient, Vector3 wakeDirection, double serverNow)
    {
        prepTargetBatterId = targetBatter;
        prepPitchType = PitchTypeFourSeam;
        prepCourseLocked = locked;
        prepThrowHand = throwHand;
        prepBallRelease = ballRelease;
        prepControllerRelease = controllerRelease;
        prepCalibratedLocal = calibratedLocal;
        prepCalibratedRotation = calibratedRotation;
        prepTarget = target;
        prepDirection = direction;
        prepSpeed = speed;
        prepSpin = spin;
        prepWakeMode = wakeMode;
        prepWakeCoefficient = wakeCoefficient;
        prepWakeDirection = wakeDirection;
        StampGeneration(true, serverNow);
    }

    // Publishes the current prep* fields as a new generation (ready) or withdraws them.
    // Without ownership yet, the newest content waits for it; it is never assumed sent.
    public void Publish(bool ready, double serverNow)
    {
        StampGeneration(ready, serverNow);
        Send();
    }

    private void StampGeneration(bool ready, double serverNow)
    {
        prepPitcherId = LocalId();
        prepReady = ready;
        _localGeneration = Mathf.Max(prepGeneration, _localGeneration) + 1;
        prepGeneration = _localGeneration;
        if (!ready) prepPitchType = PitchTypeNone;
        prepExpiresAt = serverNow + Mathf.Max(1f, prepLifetimeSeconds);
    }

    // The same content for longer, before receivers would drop it; the generation is kept.
    public void RefreshIfExpiring(double serverNow)
    {
        if (!prepReady || prepPitcherId != LocalId() || !OwnedLocally()) return;
        double left = prepExpiresAt - serverNow;
        if (left > Mathf.Max(1f, prepLifetimeSeconds) * 0.1f) return;
        prepExpiresAt = serverNow + Mathf.Max(1f, prepLifetimeSeconds);
        Send();
    }

    private void Send()
    {
        RegisterWithJudge();
        _requestedRevision++;
        _sendRetries = 0;
        if (!OwnedLocally())
        {
            _queuedPreparation = PreparedPitch.Read(this);
            _awaitingOwnership = true;
            TakeOwnership();
            if (diagnostics) Diag("publish waits for ownership gen=" + prepGeneration);
            if (!_awaitingOwnership || !OwnedLocally()) return;
        }
        _awaitingOwnership = false;
        RequestSerialization();
        if (diagnostics) Diag("publish requested gen=" + prepGeneration + " ready=" + prepReady + " target=" + prepTargetBatterId);
    }

    // A successful serialization reports the revision captured in OnPreSerialization.
    public bool CurrentSent()
    {
        return OwnedLocally() && _requestedRevision > 0 && _sentRevision == _requestedRevision;
    }

    public bool SendFailed() { return !CurrentSent() && _sendRetries > MaximumRetries; }

    public override void OnPreSerialization()
    {
        _serializedRevision = _requestedRevision;
        _serializedGeneration = prepGeneration;
        _serializedEpoch = _ownershipEpoch;
        _cancelKeys = PitchDecisionHistory.Keys(_cancelHistory);
        _cancelTiming = PitchDecisionHistory.Timing(_cancelHistory, false);
    }

    public override void OnPostSerialization(SerializationResult result)
    {
        FinishSend(result.success);
        if (diagnostics) Diag("sent gen=" + _serializedGeneration + " success=" + result.success + " bytes=" + result.byteCount);
    }

    private void FinishSend(bool success)
    {
        if (!OwnedLocally() || _serializedEpoch != _ownershipEpoch || _serializedRevision == 0) return;
        if (success)
        {
            PitchDecisionHistory.MarkSent(_cancelHistory, _serializedRevision);
            if (FlushCancellations()) Send();
        }
        if (_serializedRevision == _requestedRevision)
        {
            if (success) _sentRevision = _serializedRevision;
            else if (!_retryScheduled && _sendRetries <= MaximumRetries)
            {
                _sendRetries++;
                if (_sendRetries <= MaximumRetries)
                {
                    _retryScheduled = true;
                    SendCustomEventDelayedSeconds(nameof(RetrySend), 0.5f);
                }
            }
        }
        if (mound != null) mound.OnPitchJudgeChanged();
    }

    public void RetrySend()
    {
        if (!_retryScheduled) return;
        _retryScheduled = false;
        if (OwnedLocally() && !CurrentSent()) RequestSerialization();
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        _ownershipEpoch++;
        _sentRevision = 0;
        if (!Utilities.IsValid(player) || !player.isLocal || !OwnedLocally()) return;
        _sendRetries = 0;
        if (_awaitingOwnership)
        {
            _awaitingOwnership = false;
            RestoreQueuedPreparation();
            RequestSerialization();
            if (diagnostics) Diag("publish after ownership gen=" + prepGeneration);
            return;
        }
        // Data from the former owner cannot prepare a new pitch.
        if (prepReady && prepPitcherId != LocalId())
        {
            StampGeneration(false, prepExpiresAt);
            Send();
        }
        else if (_requestedRevision > 0) RequestSerialization();
    }

    public override void OnDeserialization()
    {
        if (prepPitcherId > 0 && PublishedBy(prepPitcherId))
        {
            if (_latestReceivedPitcher != prepPitcherId) _latestReceivedGeneration = 0;
            _latestReceivedPitcher = prepPitcherId;
            _latestReceivedGeneration = Mathf.Max(_latestReceivedGeneration, prepGeneration);
        }
        if (diagnostics)
            Diag("received gen=" + prepGeneration + " ready=" + prepReady + " pitcher=" + prepPitcherId + " target=" + prepTargetBatterId +
                " rt=" + Time.realtimeSinceStartup);
        PitchJudgeBehaviour found = Judge();
        int count = PitchDecisionHistory.WireCount(_cancelKeys, _cancelTiming, false);
        for (int i = 0; i < count; i++)
        {
            PitchDecision entry = PitchDecisionHistory.Read(_cancelKeys, _cancelTiming, i, false);
            if (entry.author != entry.pitcher || entry.pitcher <= 0 || entry.sequence <= 0 || entry.number <= 0 ||
                entry.status != PitchJudgeMath.StatusCancelled) continue;
            PitchDecisionHistory.Store(_cancelHistory, entry);
            if (found != null)
                found.ApplyOutcomeAt(entry.pitcher, entry.number, entry.status, entry.reason, 0f, false, 0f, entry.time, double.NaN);
        }
        if (found == null)
        {
            if (count > 0) SendCustomEventDelayedFrames(nameof(ApplyStoredCancellations), 1);
            return;
        }
        found.RegisterPrep(this);
        found.OnPrepChanged(this);
    }

    public void ApplyStoredCancellations()
    {
        PitchJudgeBehaviour found = Judge();
        if (found == null) return;
        for (int i = 0; i < _cancelHistory.Length; i++)
        {
            PitchDecision entry = _cancelHistory[i];
            if (entry.sequence > 0)
                found.ApplyOutcomeAt(entry.pitcher, entry.number, entry.status, entry.reason, 0f, false, 0f, entry.time, double.NaN);
        }
    }

    // ---- The pitcher's check ----

    // A pitch released by this client for a batter is checked once after
    // verdictTimeoutSeconds. A pitch without a confirmed verdict is cancelled.
    public bool WatchPitch(int number, int targetBatter, float now)
    {
        if (number <= 0 || targetBatter <= 0) return true;
        RegisterWithJudge();
        float wait = Mathf.Max(0.1f, verdictTimeoutSeconds);
        int slot = 0;
        while (slot < _watches.Length && _watches[slot].number != 0) slot++;
        if (slot == _watches.Length)
        {
            if (SendFailed()) Send();
            if (diagnostics) Diag("pitch held: all verdict watches are occupied");
            return false;
        }
        PitchVerdictWatch watch = new PitchVerdictWatch();
        watch.number = number;
        watch.batter = targetBatter;
        watch.due = now + wait;
        _watches[slot] = watch;
        SendCustomEventDelayedSeconds(nameof(CheckVerdict), wait);
        return true;
    }

    public void ConfirmVerdict(int pitcher, int number)
    {
        if (pitcher != LocalId()) return;
        for (int i = 0; i < _watches.Length; i++)
            if (_watches[i].number == number && _watches[i].cancelReason == 0) _watches[i] = new PitchVerdictWatch();
    }

    public void CheckVerdict()
    {
        CheckVerdictAt(Time.time, Networking.GetServerTimeInSeconds());
    }

    public void CheckVerdictAt(float now, double serverNow)
    {
        int occupant = mound != null && mound.strikeZone != null ? mound.strikeZone.occupantPlayerId : -1;
        CheckVerdictAt(now, serverNow, occupant);
    }

    private void CheckVerdictAt(float now, double serverNow, int occupant)
    {
        PitchJudgeBehaviour found = Judge();
        bool cancelled = false;
        for (int i = 0; i < _watches.Length; i++)
        {
            PitchVerdictWatch watch = _watches[i];
            if (watch.number == 0 || watch.cancelReason != 0 || now < watch.due - 0.01f) continue;
            watch.cancelReason = occupant == watch.batter ? PitchJudgeMath.ReasonVerdictMissing : PitchJudgeMath.ReasonBatterChanged;
            watch.cancelTime = serverNow;
            _watches[i] = watch;
            cancelled = true;
            if (found != null)
                found.ApplyOutcomeAt(LocalId(), watch.number, PitchJudgeMath.StatusCancelled, watch.cancelReason,
                    0f, false, 0f, serverNow, serverNow);
        }
        if (FlushCancellations() || cancelled) Send();
    }

    // A due cancellation stays with its watch if every history slot is still awaiting
    // serialization. A successful send frees those slots and drains the remaining watches.
    private bool FlushCancellations()
    {
        bool added = false;
        int pitcher = LocalId();
        for (int i = 0; i < _watches.Length; i++)
        {
            PitchVerdictWatch watch = _watches[i];
            if (watch.cancelReason == 0) continue;
            PitchDecision entry = new PitchDecision();
            entry.author = pitcher;
            entry.sequence = _cancelSequence + 1;
            entry.revision = _requestedRevision + 1;
            entry.pitcher = pitcher;
            entry.number = watch.number;
            entry.status = PitchJudgeMath.StatusCancelled;
            entry.reason = watch.cancelReason;
            entry.time = watch.cancelTime;
            if (!PitchDecisionHistory.Store(_cancelHistory, entry)) break;
            _cancelSequence = entry.sequence;
            _watches[i] = new PitchVerdictWatch();
            added = true;
            if (diagnostics) Diag("cancel pitch=" + pitcher + "/" + entry.number + " reason=" + entry.reason);
        }
        return added;
    }

    private void Diag(string message)
    {
        Debug.Log("PitchPrep[diag] local=" + LocalId() + " server=" + Networking.GetServerTimeInSeconds() + " " + message);
    }
}
