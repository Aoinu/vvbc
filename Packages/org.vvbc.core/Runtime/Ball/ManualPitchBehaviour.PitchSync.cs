using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

// The mound's side of manual-pitch sync (docs/development/manual-pitch-sync.md): it publishes
// the course a release would use now as prepared data, only when that course or its batter
// changes, and gives every pitch an ID tied to the published generation. Recognition, speed,
// control, spin and SSW of the release are unchanged; the prepared values are the solver's.
public partial class ManualPitchBehaviour
{
    private const int PrepSourceNone = 0;
    private const int PrepSourceDefault = 1;
    private const int PrepSourceLock = 2;

    // The world-space ideal controller release at the lock's and the default course's solve.
    private Vector3 _lockIdealHand, _defaultIdealHand;
    // Bumped whenever the default course's result changes.
    private int _defaultVersion;
    // What the prepared data describes now: its source, that source's version and its batter.
    private int _prepSource = -1;
    private int _prepVersion = -1;
    private int _prepBatter = -2;
    // Bound by the Ball before reading release tracking.
    private int _releaseHandSession;
    private bool _delayListening;

    // The course a release would use now, by the same rule as ResolveReleaseCore.
    private int CurrentPrepSource()
    {
        if (state == StateLockedReady) return PrepSourceLock;
        if (_defaultReady && (state == StateTargeting || state == StateSolving)) return PrepSourceDefault;
        return PrepSourceNone;
    }

    // Publishes the prepared data when the course, its solve or the batter changed; otherwise
    // nothing is sent. Leaving the mound or pitcher mode withdraws it. A LocalRig lane never publishes.
    private void SyncPrep()
    {
        if (prepSync == null) return;
        int source = _inMound && _ball != null && !_ball.localOnly ? CurrentPrepSource() : PrepSourceNone;
        int version = source == PrepSourceLock ? _lockEpoch : source == PrepSourceDefault ? _defaultVersion : 0;
        int batter = source != PrepSourceNone && strikeZone != null ? strikeZone.occupantPlayerId : -1;
        if (source == _prepSource && version == _prepVersion && batter == _prepBatter) return;
        bool wasPublished = _prepSource > PrepSourceNone;
        _prepSource = source;
        _prepVersion = version;
        _prepBatter = batter;
        if (source == PrepSourceNone)
        {
            if (wasPublished && prepSync.OwnedLocally()) prepSync.Publish(false, Networking.GetServerTimeInSeconds());
            return;
        }
        bool locked = source == PrepSourceLock;
        Vector3 direction = locked ? _lockDirection : _defaultDirection;
        Vector3 spinLocal = locked ? _lockSpinLocal : IdealSpinLocal();
        Vector3 wakeLocal = locked ? _lockWakeLocal : _defaultWakeLocal;
        int wakeMode = WakeMode(wakeCoefficient);
        prepSync.PublishCourse(batter, locked, _activeHand, locked ? _lockRelease : _defaultRelease,
            locked ? _lockIdealHand : _defaultIdealHand, _calPosition, _calRotation, locked ? _lockTarget : _defaultTarget,
            direction, (locked ? _lockSolverSpeedKmh : _defaultSolverSpeedKmh) / 3.6f, PitchSpin.ToWorld(spinLocal, direction),
            wakeMode, wakeMode == BallAerodynamics.WakeNone ? 0f : Mathf.Max(0f, wakeCoefficient),
            wakeMode == BallAerodynamics.WakeNone ? Vector3.zero : PitchSpin.ToWorld(wakeLocal, direction),
            Networking.GetServerTimeInSeconds());
    }

    // The ID of a recognized pitch, and the published generation when the prepared data
    // describes this release's course (else none: the batter never takes it for prepared).
    // Field reads only: a call into another Behaviour here would make ResolveReleaseCore keep
    // all its locals across it (WatchReleasedPitch runs after it, from EndRelease).
    private void TakePitchId(bool locked)
    {
        releasePitchNumber = _releaseHandSession;
        releasePrepGeneration = 0;
        releaseTargetBatter = -1;
        int source = locked ? PrepSourceLock : PrepSourceDefault;
        if (prepSync == null || _prepSource != source || _prepBatter <= 0) return;
        if (!prepSync.prepReady || prepSync.prepTargetBatterId != _prepBatter) return;
        releaseTargetBatter = _prepBatter;
        releasePrepGeneration = prepSync.prepGeneration;
    }

    // A pitch prepared for a batter is watched for its verdict by the prepared data's owner.
    private void WatchReleasedPitch(float now)
    {
        if (prepSync != null && releaseTargetBatter > 0) prepSync.WatchPitch(releasePitchNumber, releaseTargetBatter, now);
    }

    // ---- Judgement display and the delay setting ----

    public PitchJudgeBehaviour PitchJudge()
    {
        if (prepSync != null) return prepSync.Judge();
        return strikeZone != null && strikeZone.ball != null ? strikeZone.ball.pitchJudge : null;
    }

    public PitchDelaySettingBehaviour DelaySetting()
    {
        PitchJudgeBehaviour judge = PitchJudge();
        return judge != null ? judge.delaySetting : null;
    }

    // The panel follows the shared setting; false while the setting cannot be found yet (the
    // judge registers on its Ball at its own Start).
    public bool ListenToDelaySetting(UdonBehaviour listener)
    {
        PitchDelaySettingBehaviour setting = DelaySetting();
        if (setting == null || listener == null) return false;
        setting.RegisterListener(listener);
        return true;
    }

    public void StepDelayThreshold(int steps)
    {
        PitchDelaySettingBehaviour setting = DelaySetting();
        if (setting != null) setting.Step(steps);
    }

    // Called by the judge when a verdict or cancellation is followed.
    public void OnPitchJudgeChanged()
    {
        if (statusListener != null) statusListener.SendCustomEvent("OnManualPitchChanged");
    }

    // One status line: the threshold, whether the prepared data is out, and the last message.
    public string PitchSyncText()
    {
        PitchDelaySettingBehaviour setting = DelaySetting();
        string text = "Sync limit: " + Mathf.RoundToInt(setting != null ? setting.ThresholdMs() : PitchJudgeMath.DefaultThresholdMs) + " ms";
        if (prepSync != null && state != StateInactive)
            text += _prepSource <= PrepSourceNone ? "  |  pitch data: none" :
                prepSync.CurrentSent() ? "  |  pitch data: sent" :
                prepSync.SendFailed() ? "  |  pitch data: send failed" : "  |  pitch data: sending";
        PitchJudgeBehaviour judge = PitchJudge();
        string message = judge != null ? judge.MessageText() : "";
        if (message.Length > 0) text += "\n" + message;
        return text;
    }
}
