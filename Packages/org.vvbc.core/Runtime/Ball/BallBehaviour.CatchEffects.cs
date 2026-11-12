using UnityEngine;
using VRC.SDKBase;

// Catch sound and haptics, decided from the flight this client was showing just before
// the catch. No synced data is added: the local catcher plays at once, and other clients
// play when they apply the owner's GloveHeld possession to a ball they still show in flight.
// The glove plays the effect (GloveBehaviour.CatchEffects).
public partial class BallBehaviour
{
    [Header("Catch Effects")]
    [Tooltip("Maximum age of a remote catch, in seconds, for its sound to play. Default 0.75.")]
    public float maximumCatchAudioAge = 0.75f;

    // Server clocks of two clients can disagree slightly; a catch this far "in the future" still counts.
    private const float CatchClockTolerance = 0.25f;
    private const int FlightOriginUnknown = 0;

    // What started the flight this client shows: PitchState, HitState, ThrowState, or unknown.
    // Local only. A bounce does not change it; a new hit or throw does.
    private int _flightOrigin;
    // The flight sequence whose catch already produced an effect on this client.
    private int _catchEffectFlight = -1;

    // Diagnostics: catch effects requested here, and the last one's inputs.
    [HideInInspector] public int catchEffectsRequested;
    private bool _lastCatchEffectLocal;
    private int _lastCatchEffectOrigin;
    private float _lastCatchEffectSpeed;

    // Called once per caught flight. speed below zero means unknown.
    private void RequestCatchEffect(int flight, GloveBehaviour glove, Vector3 position, float speed, int origin, bool local)
    {
        if (_catchEffectFlight == flight) return;
        _catchEffectFlight = flight;
        catchEffectsRequested++;
        _lastCatchEffectLocal = local;
        _lastCatchEffectOrigin = origin;
        _lastCatchEffectSpeed = speed;
        if (glove != null) glove.PlayCatch(position, speed, origin == PitchState, local);
    }

    // The accepted state is another player's GloveHeld possession and this client still shows
    // the ball flying freely, in a flight it has not produced a catch effect for. A late
    // joiner, a repeated or republished state, a ball already held and the catcher's own
    // returning state all fail here. Checked before the server time is read.
    private bool RemoteCatchCandidate()
    {
        int holder = _acceptedHolderId;
        return _acceptedKind == PossessionState && _acceptedHold == HoldGlove && holder > 0 && holder != LocalId() &&
            flying && _localHold == HoldFree && _catchEffectFlight != flightSequence;
    }

    // elapsed: server seconds since the catch.
    private bool RemoteCatchRecent(double elapsed)
    {
        return elapsed >= -CatchClockTolerance && elapsed <= Mathf.Max(0f, maximumCatchAudioAge);
    }

    // The local flight's speed at the catch time (local seconds), from the fixed-step history;
    // the current speed when the catch is newer than the last sample; unknown (-1) otherwise.
    private float RemoteCatchSpeed(float catchTime)
    {
        if (TrySampleMotion(catchTime)) return sampledVelocity.magnitude;
        if (_sampleCount > 0 && catchTime >= latestMotionTime && body != null) return body.velocity.magnitude;
        return -1f;
    }

    // Applies another player's possession; a catch observed on this client gets its effect,
    // judged from the flight shown before the hold stops it and requested after the hold.
    private void ApplyRemotePossession()
    {
        bool caught = false;
        float speed = -1f;
        if (RemoteCatchCandidate())
        {
            double elapsed = Networking.CalculateServerDeltaTime(Networking.GetServerTimeInSeconds(), _acceptedServerTime);
            caught = RemoteCatchRecent(elapsed);
            if (caught) speed = RemoteCatchSpeed(Time.time - Mathf.Max(0f, (float)elapsed));
        }
        int flight = flightSequence;
        int origin = _flightOrigin;
        if (body != null) body.position = _acceptedPosition;
        EnterHold(_acceptedHold, _acceptedHolderId, _acceptedVelocity);
        if (caught) RequestCatchEffect(flight, FindGlove(_acceptedHolderId), _acceptedPosition, speed, origin, false);
    }
}
