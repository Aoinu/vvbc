using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

// The batter's own observation of a manual pitch, sent to the umpire engine as one PitchResult
// event of the batter's PlayerObject event stream (docs/development/automatic-umpire.md#pitch-result).
// It sits on the HomePlate (one per lane) and acts only on the client of the batter of the plate
// appearance in progress, during a live play, for a pitch with a PitchLineage (a manual pitch;
// batting-pitcher pitches have none and are never reported).
//
// When the lane's PitchCrossing has this pitch's outcome on this client (the crossing at the
// zone snapshot the pitch was released with, or the bat contact), the swing is measured from the
// bat's own motion history around the crossing after a short window, and the event is recorded:
// zone strike, bounced, swing (or swing unknown), local contact, no crossing. A pitch that touched
// the ground before the zone is a bounce, but the batter can still swing at it: the report then
// waits for the ball to pass the plate (PitchCrossing watches the flight on) and measures the
// swing there, or, when it never gets there within bouncePlateTimeoutSeconds, over the whole
// time since the bounce. One event per pitch. It is a fact, never a call; the engine decides on
// the MatchManager Owner. It lives in Runtime/ because it reads the Ball, the Bat and the match
// (T# sees the own folder and below).
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class UmpirePitchReporterBehaviour : TsukimiBehaviour
{
    [Header("References (the HomePlate's own)")]
    public MatchControllerBehaviour match;
    public BallBehaviour ball;
    public PitchCrossingBehaviour crossing;
    [Tooltip("The HomePlate's strike zone: its plate gives the direction toward the pitcher.")]
    public StrikeZoneBehaviour strikeZone;

    [Header("Swing recognition")]
    [Tooltip("Seconds before the plate crossing the swing window starts. Default 0.25.")]
    public float swingWindowBeforeSeconds = 0.25f;
    [Tooltip("Seconds after the crossing the window ends (the report waits for it). Default 0.12.")]
    public float swingWindowAfterSeconds = 0.12f;
    [Tooltip("Lowest peak sweet-spot speed of a swing, in m/s. Default 9.")]
    public float swingMinSpeed = 9f;
    [Tooltip("Lowest peak bat angular speed of a swing, in degrees per second. Default 360.")]
    public float swingMinAngularSpeed = 360f;
    [Tooltip("Lowest turn of the bat's horizontal direction within the window, in degrees. Default 45.")]
    public float swingMinSweepDegrees = 45f;
    [Tooltip("Require the sweet spot to come toward the pitcher within the window.")]
    public bool swingRequireForward = true;
    [Tooltip("Lowest forward motion of the sweet spot toward the pitcher, in metres. Default 0.1.")]
    public float swingMinForward = 0.1f;
    [Tooltip("A pitch that bounced before the zone is reported this long after the bounce at the latest, when it never passes the plate. Default 1.")]
    public float bouncePlateTimeoutSeconds = 1f;

    private BatMotionBehaviour _motion;
    private PlayerEventCollectorBehaviour _collector;
    private float _nextLookup;
    private int _reportedPitcher;
    private int _reportedNumber;
    private bool _waiting;
    private int _waitPitcher;
    private int _waitNumber;
    private int _waitFlags;
    private double _waitServerTime;
    private float _waitLocalTime;
    private Vector3 _waitPosition;
    // A bounced pitch waiting for the plate (or the timeout) before its swing window is known.
    private bool _waitPlate;
    private float _plateDeadline;

    // ---- Diagnostics ----
    private int _reports;
    private int _swings;
    private int _unrecorded;
    private int _swingUnknown;
    private SwingMeasure _lastMeasure;
    private int _lastSwing;

    public int GetReportCount() { return _reports; }
    public int GetSwingCount() { return _swings; }
    public int GetUnrecordedCount() { return _unrecorded; }
    public int GetSwingUnknownCount() { return _swingUnknown; }
    public int GetLastSwing() { return _lastSwing; }
    public float GetLastPeakSpeed() { return _lastMeasure.peakSpeed; }
    public float GetLastPeakAngularSpeed() { return _lastMeasure.peakAngularSpeed; }

    void Start()
    {
        if (crossing == null) return;
        crossing.resultListener = (UdonBehaviour)(Component)this;
        // The lane's ball: the one its crossing observes, unless set.
        if (ball == null) ball = crossing.flight;
    }

    // PitchCrossing: this pitch's outcome on this client is known.
    public void _OnPitchCrossingResult()
    {
        if (match == null || ball == null || crossing == null || !match.IsLive()) return;
        VRCPlayerApi local = Networking.LocalPlayer;
        if (!Utilities.IsValid(local) || match.GetBatterId() != local.playerId) return;
        int pitcher = ball.CurrentLineagePitcher();
        int number = ball.CurrentLineageNumber();
        if (pitcher <= 0 || number <= 0) return;
        if (pitcher == _reportedPitcher && number == _reportedNumber) return;
        if (_waiting && pitcher == _waitPitcher && number == _waitNumber) return;
        // Crossing time on this client's fixed clock, moved onto the server clock.
        float age = crossing.hasResult ? Mathf.Max(0f, Time.fixedTime - crossing.resultTime) : 0f;
        _waiting = true;
        _waitPitcher = pitcher;
        _waitNumber = number;
        _waitFlags = OutcomeFlags(crossing.hasResult, crossing.isStrike, crossing.bounced, crossing.contactEnded);
        _waitServerTime = Networking.GetServerTimeInSeconds() - age;
        _waitLocalTime = Time.time - age;
        _waitPosition = crossing.hasResult ? crossing.resultCrossing : ball.transform.position;
        _waitPlate = crossing.bounced && crossing.PlateWatching();
        if (_waitPlate)
        {
            // The bounce is not the end of the pitch for the batter: wait for the plate.
            _plateDeadline = Time.time + Mathf.Max(0.1f, bouncePlateTimeoutSeconds);
            SendCustomEventDelayedSeconds(nameof(_PlateTimeout), Mathf.Max(0.1f, bouncePlateTimeoutSeconds));
            return;
        }
        SendCustomEventDelayedSeconds(nameof(_Report), Mathf.Max(0.02f, swingWindowAfterSeconds));
    }

    // PitchCrossing: the bounced pitch passed the plate, or a bat contact ended it first.
    public void _OnPitchPlateReached()
    {
        if (!_waiting || !_waitPlate || crossing == null) return;
        _waitPlate = false;
        if (crossing.contactEnded) _waitFlags |= ContactRules.PitchFlagContact;
        float age = crossing.plateReached ? Mathf.Max(0f, Time.fixedTime - crossing.plateTime) : 0f;
        _waitServerTime = Networking.GetServerTimeInSeconds() - age;
        _waitLocalTime = Time.time - age;
        if (crossing.plateReached) _waitPosition = crossing.platePoint;
        SendCustomEventDelayedSeconds(nameof(_Report), Mathf.Max(0.02f, swingWindowAfterSeconds));
    }

    // The bounced pitch never reached the plate: the swing is measured from before the bounce
    // until now, and the pitch is reported as it is (bounced).
    public void _PlateTimeout()
    {
        if (!_waiting || !_waitPlate || Time.time < _plateDeadline - 0.01f) return;
        _waitPlate = false;
        ReportWindow(_waitLocalTime - swingWindowBeforeSeconds, Time.time);
    }

    // The swing's flag: a swing, or the swing could not be told (never a no swing by default).
    public static int SwingFlags(int flags, int swing)
    {
        if (swing == SwingRules.SwingYes) return flags | ContactRules.PitchFlagSwing;
        if (swing == SwingRules.SwingUnknown) return flags | ContactRules.PitchFlagSwingUnknown;
        return flags;
    }

    public static int OutcomeFlags(bool hasResult, bool isStrike, bool bounced, bool contact)
    {
        int flags = 0;
        if (!hasResult) flags |= ContactRules.PitchFlagNoCrossing;
        if (hasResult && isStrike) flags |= ContactRules.PitchFlagZoneStrike;
        if (bounced) flags |= ContactRules.PitchFlagBounced;
        if (contact) flags |= ContactRules.PitchFlagContact;
        return flags;
    }

    public void _Report()
    {
        if (!_waiting || _waitPlate) return;
        ReportWindow(_waitLocalTime - swingWindowBeforeSeconds, _waitLocalTime + swingWindowAfterSeconds);
    }

    // Measures the swing over [start, end] (Time.time) and records the pitch's one event.
    private void ReportWindow(float start, float end)
    {
        _waiting = false;
        _lastMeasure = Measure(start, end);
        _lastSwing = SwingRules.Classify(_lastMeasure, swingMinSpeed, swingMinAngularSpeed, swingMinSweepDegrees, swingMinForward, swingRequireForward);
        int flags = SwingFlags(_waitFlags, _lastSwing);
        if (_lastSwing == SwingRules.SwingYes)
        {
            _swings++;
            if (crossing != null) crossing.NotifySwing();
        }
        else if (_lastSwing == SwingRules.SwingUnknown) _swingUnknown++;
        PlayerEventCollectorBehaviour collector = Collector();
        int slot = collector != null
            ? collector.RecordWithArg(ContactRules.TypePitchResult, _waitPitcher, ContactRules.NoBase, flags, _waitPosition, 0, 0, 0, _waitNumber, _waitServerTime)
            : -1;
        if (slot < 0)
        {
            _unrecorded++;
            return;
        }
        _reports++;
        _reportedPitcher = _waitPitcher;
        _reportedNumber = _waitNumber;
    }

    // The bat's own history (Bat Motion, recorded during the pitch) over [start, end] (Time.time).
    private SwingMeasure Measure(float start, float end)
    {
        SwingMeasure m = new SwingMeasure();
        BatMotionBehaviour motion = Motion();
        if (motion == null) return m;
        m.measured = motion.TryGetMeasuredPeaks(start, end);
        m.speedCovers = m.measured && SwingRules.Covers(motion.measuredOldestTime, motion.measuredLatestTime, start, end);
        m.peakSpeed = motion.measuredPeakReferenceSpeed;
        m.peakAngularSpeed = motion.measuredPeakAngularSpeed;
        float first = Mathf.Max(start, motion.contactOldestTime);
        float last = Mathf.Min(end, motion.contactLatestTime);
        if (last > first && motion.TrySampleContactPose(first, 0f))
        {
            m.axisStart = motion.contactSampleRotation * Vector3.up;
            m.sweetStart = motion.contactSamplePosition + m.axisStart * motion.SweetSpotLocalY();
            if (motion.TrySampleContactPose(last, 0f))
            {
                m.axisEnd = motion.contactSampleRotation * Vector3.up;
                m.sweetEnd = motion.contactSamplePosition + m.axisEnd * motion.SweetSpotLocalY();
                m.poseKnown = true;
                m.poseCovers = SwingRules.Covers(motion.contactOldestTime, motion.contactLatestTime, start, end);
            }
        }
        m.towardPitcher = strikeZone != null ? StrikeZoneGeometry.Forward(strikeZone.PlateTransform().forward) : Vector3.forward;
        return m;
    }

    private BatMotionBehaviour Motion()
    {
        if (_motion != null) return _motion;
        VRCPlayerApi player = Networking.LocalPlayer;
        if (!Utilities.IsValid(player)) return null;
        GameObject[] objects = Networking.GetPlayerObjects(player);
        if (objects == null) return null;
        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] == null) continue;
            BatMotionBehaviour candidate = objects[i].GetComponentInChildren<BatMotionBehaviour>(true);
            if (candidate != null) { _motion = candidate; break; }
        }
        return _motion;
    }

    private PlayerEventCollectorBehaviour Collector()
    {
        if (_collector != null) return _collector;
        if (Time.time < _nextLookup) return null;
        _nextLookup = Time.time + 2f;
        _collector = ContactLookup.LocalCollector();
        return _collector;
    }
}
