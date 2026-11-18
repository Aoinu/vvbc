using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;

public struct PitchVerdictWatch
{
    public int number, batter;
    public float due;
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
    // A cancellation keeps its pitcher even when ownership of the mound changes.
    [UdonSynced] public int cancelPitchNumber;
    [UdonSynced] public int cancelPitcherId;
    [UdonSynced] public int cancelReason;
    [UdonSynced] public double cancelServerTime;

    private bool _awaitingOwnership;
    private PreparedPitch _queuedPreparation;
    private int _queuedCancelPitcher, _queuedCancelNumber, _queuedCancelReason;
    private double _queuedCancelTime;
    private int _localGeneration;
    private int _latestReceivedPitcher, _latestReceivedGeneration;
    private int _requestedRevision, _serializedRevision, _sentRevision;
    private int _serializedGeneration;
    private int _ownershipEpoch, _serializedEpoch;
    private int _sendRetries;
    private bool _retryScheduled;
    private int _lastCancelledPitch;
    private PitchVerdictWatch[] _watches = new PitchVerdictWatch[2];

    private void Start()
    {
        RegisterWithJudge();
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
        cancelPitcherId = _queuedCancelPitcher;
        cancelPitchNumber = _queuedCancelNumber;
        cancelReason = _queuedCancelReason;
        cancelServerTime = _queuedCancelTime;
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
            _queuedCancelPitcher = cancelPitcherId;
            _queuedCancelNumber = cancelPitchNumber;
            _queuedCancelReason = cancelReason;
            _queuedCancelTime = cancelServerTime;
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
    }

    public override void OnPostSerialization(SerializationResult result)
    {
        FinishSend(result.success);
        if (diagnostics) Diag("sent gen=" + _serializedGeneration + " success=" + result.success + " bytes=" + result.byteCount);
    }

    private void FinishSend(bool success)
    {
        if (!OwnedLocally() || _serializedEpoch != _ownershipEpoch || _serializedRevision == 0) return;
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
                " cancel=" + cancelPitchNumber + " rt=" + Time.realtimeSinceStartup);
        PitchJudgeBehaviour found = Judge();
        if (found == null) return;
        found.RegisterPrep(this);
        found.OnPrepChanged(this);
    }

    // ---- The pitcher's check ----

    // A pitch released by this client for a batter is checked once after
    // verdictTimeoutSeconds. A pitch without a confirmed verdict is cancelled.
    public void WatchPitch(int number, int targetBatter, float now)
    {
        if (number <= 0 || targetBatter <= 0) return;
        RegisterWithJudge();
        float wait = Mathf.Max(0.1f, verdictTimeoutSeconds);
        int slot = 0;
        while (slot < _watches.Length && _watches[slot].number != 0) slot++;
        if (slot == _watches.Length)
        {
            PitchVerdictWatch[] next = new PitchVerdictWatch[slot + 1];
            for (int i = 0; i < slot; i++) next[i] = _watches[i];
            _watches = next;
        }
        PitchVerdictWatch watch = new PitchVerdictWatch();
        watch.number = number;
        watch.batter = targetBatter;
        watch.due = now + wait;
        _watches[slot] = watch;
        SendCustomEventDelayedSeconds(nameof(CheckVerdict), wait);
    }

    public void ConfirmVerdict(int pitcher, int number)
    {
        if (pitcher != LocalId()) return;
        for (int i = 0; i < _watches.Length; i++)
            if (_watches[i].number == number) _watches[i] = new PitchVerdictWatch();
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
        for (int i = 0; i < _watches.Length; i++)
        {
            PitchVerdictWatch watch = _watches[i];
            if (watch.number == 0 || now < watch.due - 0.01f) continue;
            _watches[i] = new PitchVerdictWatch();
            int reason = occupant == watch.batter ? PitchJudgeMath.ReasonVerdictMissing : PitchJudgeMath.ReasonBatterChanged;
            CancelPitch(watch.number, reason, serverNow);
        }
    }

    // Publishes the cancellation of this client's own pitch and applies it here at once.
    private void CancelPitch(int number, int reason, double serverNow)
    {
        PitchJudgeBehaviour found = Judge();
        int pitcher = LocalId();
        if (number <= _lastCancelledPitch)
        {
            if (found != null) found.ApplyOutcomeAt(pitcher, number, PitchJudgeMath.StatusCancelled, reason, 0f, false,
                0f, serverNow, serverNow);
            return;
        }
        _lastCancelledPitch = number;
        cancelPitcherId = pitcher;
        cancelPitchNumber = number;
        cancelReason = reason;
        cancelServerTime = serverNow;
        Send();
        if (diagnostics) Diag("cancel pitch=" + cancelPitcherId + "/" + number + " reason=" + reason);
        if (found != null) found.OnPrepChangedAt(this, serverNow);
    }

    private void Diag(string message)
    {
        Debug.Log("PitchPrep[diag] local=" + LocalId() + " server=" + Networking.GetServerTimeInSeconds() + " " + message);
    }
}
