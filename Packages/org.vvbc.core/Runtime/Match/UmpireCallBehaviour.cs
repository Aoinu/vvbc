using TMPro;
using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

// Umpire calls, heard and seen on every client (docs/development/automatic-umpire.md#umpire-calls).
// The calls follow the rulings the MatchController has applied (the live play's log, the commit's
// end reason, a new live play), never a physical event: a tag contact alone never says OUT. The
// PlayState is already synced, so nothing more is sent: each client turns the same applied
// rulings into the same calls. Each call is played once per ruling event id; a client that joins
// later, or receives a ruling long after it happened, plays nothing of the past.
//
// Clips are optional Inspector references (a call without a clip still shows its text): the
// package ships none, so a world assigns its own (the development project assigns local clips
// in its sample scene).
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class UmpireCallBehaviour : TsukimiBehaviour
{
    public const int CallNone = 0;
    public const int CallPlay = 1;
    public const int CallBall = 2;
    public const int CallStrike = 3;
    public const int CallStrikeThree = 4;
    public const int CallFoul = 5;
    public const int CallFair = 6;
    public const int CallOut = 7;
    public const int CallSafe = 8;
    public const int CallTime = 9;
    public const int CallWalk = 10;
    public const int CallHitByPitch = 11;
    public const int CallCount = 12;

    [Header("References")]
    [Tooltip("The MatchController (calls follow its applied rulings).")]
    public MatchControllerBehaviour match;
    [Tooltip("3D AudioSource the calls play from. Unset = text only.")]
    public AudioSource audioSource;
    [Tooltip("Where the calls are heard from (the AudioSource moves there). Unset = where it is.")]
    public Transform callPosition;
    [Tooltip("Text showing the last call (optional).")]
    public TextMeshProUGUI callText;

    [Header("Clips (optional; a missing clip shows the text only)")]
    public AudioClip playClip;
    public AudioClip ballClip;
    public AudioClip strikeClip;
    public AudioClip strikeThreeClip;
    public AudioClip foulClip;
    public AudioClip fairClip;
    public AudioClip outClip;
    public AudioClip safeClip;
    public AudioClip timeClip;
    public AudioClip walkClip;
    public AudioClip hitByPitchClip;

    [Header("Volumes (0..1)")]
    [Range(0f, 1f)] public float playVolume = 1f;
    [Range(0f, 1f)] public float ballVolume = 1f;
    [Range(0f, 1f)] public float strikeVolume = 1f;
    [Range(0f, 1f)] public float strikeThreeVolume = 1f;
    [Range(0f, 1f)] public float foulVolume = 1f;
    [Range(0f, 1f)] public float fairVolume = 1f;
    [Range(0f, 1f)] public float outVolume = 1f;
    [Range(0f, 1f)] public float safeVolume = 1f;
    [Range(0f, 1f)] public float timeVolume = 1f;
    [Range(0f, 1f)] public float walkVolume = 1f;
    [Range(0f, 1f)] public float hitByPitchVolume = 1f;

    [Header("Timing")]
    [Tooltip("A ruling older than this (server time) when it arrives is not called. Default 3.")]
    public float maxCallAgeSeconds = 3f;
    [Tooltip("Shortest time between two calls; later calls wait their turn. Default 0.7.")]
    public float callSpacingSeconds = 0.7f;
    [Tooltip("How long the call text stays. Default 2.")]
    public float textSeconds = 2f;

    // Event ids already called (play, source, seq), the last MaxSeen.
    private const int MaxSeen = 48;
    private readonly int[] _seenPlay = new int[MaxSeen];
    private readonly int[] _seenSource = new int[MaxSeen];
    private readonly int[] _seenSeq = new int[MaxSeen];
    private int _seenNext;
    private bool _primed;
    private int _lastLivePlayId;
    private int _lastCommittedSeen;
    // Calls waiting for their turn (by priority when full).
    private const int MaxQueue = 4;
    private readonly int[] _queue = new int[MaxQueue];
    private int _queueCount;
    private bool _playing;
    private float _lastCallTime = -100f;
    private int _textSerial;
    private bool _listening;

    // ---- Diagnostics ----
    private int _played;
    private int _skippedOld;
    private int _skippedDuplicate;
    private int _dropped;
    private int _lastCall;

    public int GetPlayedCount() { return _played; }
    public int GetSkippedOldCount() { return _skippedOld; }
    public int GetSkippedDuplicateCount() { return _skippedDuplicate; }
    public int GetDroppedCount() { return _dropped; }
    public int GetLastCall() { return _lastCall; }
    public int GetQueuedCount() { return _queueCount; }

    void Start()
    {
        if (match != null && !_listening)
        {
            _listening = true;
            match.AddChangeListener((UdonBehaviour)GetComponent(typeof(UdonBehaviour)));
        }
    }

    public static string CallName(int call)
    {
        switch (call)
        {
            case CallPlay: return "PLAY!";
            case CallBall: return "BALL";
            case CallStrike: return "STRIKE!";
            case CallStrikeThree: return "STRIKE THREE!";
            case CallFoul: return "FOUL BALL";
            case CallFair: return "FAIR BALL";
            case CallOut: return "OUT!";
            case CallSafe: return "SAFE!";
            case CallTime: return "TIME!";
            case CallWalk: return "BALL FOUR";
            case CallHitByPitch: return "HIT BY PITCH";
        }
        return "";
    }

    // Outs and safes first, then the pitch calls, then the rest (when the queue is full).
    public static int Priority(int call)
    {
        switch (call)
        {
            case CallOut: case CallSafe: case CallStrikeThree: return 3;
            case CallBall: case CallStrike: case CallFoul: case CallFair: case CallWalk: case CallHitByPitch: return 2;
        }
        return 1;
    }

    // The call of one applied ruling. strikeoutPending: the third strike is open now; lastPa:
    // the plate appearance result now (a ball that ended it is ball four).
    public static int CallFor(int code, int result, bool strikeoutPending, int lastPa, int ballsNow)
    {
        if (result != PlayRules.ResultApplied && result != PlayRules.ResultEndsLive && result != PlayRules.ResultAppliedFromPending) return CallNone;
        switch (code)
        {
            case PlayRules.JudgeBall: return lastPa == PlayRules.PaWalk && ballsNow == 0 ? CallWalk : CallBall;
            case PlayRules.JudgeCalledStrike:
            case PlayRules.JudgeSwingingStrike:
            case PlayRules.JudgeFoulTip: return strikeoutPending ? CallStrikeThree : CallStrike;
            case PlayRules.JudgeFoul: return CallFoul;
            case PlayRules.JudgeFairBall: return CallFair;
            case PlayRules.JudgeWalk: return CallWalk;
            case PlayRules.JudgeHitByPitch: return CallHitByPitch;
            case PlayRules.JudgeStrikeoutCaught:
            case PlayRules.JudgeCatch:
            case PlayRules.JudgeForceOut:
            case PlayRules.JudgeTagOut:
            case PlayRules.RulingOut: return CallOut;
            case PlayRules.JudgeForceSafe:
            case PlayRules.JudgeTagSafe: return CallSafe;
        }
        return CallNone;
    }

    // ---- Following the controller ----

    public void _OnMatchChanged()
    {
        if (match == null || !match.IsMatchReady()) return;
        Follow(Networking.GetServerTimeInSeconds());
    }

    // Reads what is new since the last look: a new live play (Play), its applied rulings, and the
    // commit of the play (Time). The first look only remembers what is there (a late joiner).
    public void Follow(double serverNow)
    {
        bool live = match.IsLive();
        int livePlay = live ? match.GetPlayId() : 0;
        int committed = match.GetLastCommittedPlayId();
        if (!_primed)
        {
            _primed = true;
            _lastLivePlayId = livePlay;
            _lastCommittedSeen = committed;
            RememberAll();
            return;
        }
        if (live && livePlay != _lastLivePlayId)
        {
            _lastLivePlayId = livePlay;
            if (serverNow - match.GetPlayStartServerTime() <= maxCallAgeSeconds) Enqueue(CallPlay);
        }
        int plays = match.GetHistoryPlayCount();
        // The newest plays only: the live one, and the one just committed.
        for (int index = Mathf.Min(plays, 2) - 1; index >= 0; index--) FollowPlay(index, serverNow);
        if (committed != _lastCommittedSeen)
        {
            _lastCommittedSeen = committed;
            int index = live ? 1 : 0;
            if (index < plays && match.GetHistoryPlayField(index, MatchControllerBehaviour.HistoryPlayId) == committed)
            {
                int reason = match.GetHistoryPlayField(index, MatchControllerBehaviour.HistoryEndReason);
                if (reason == PlayRules.DeadTime || reason == PlayRules.DeadForcedTime) Enqueue(CallTime);
            }
        }
        PlayNext();
    }

    private void FollowPlay(int index, double serverNow)
    {
        int playId = match.GetHistoryPlayField(index, MatchControllerBehaviour.HistoryPlayId);
        int entries = match.GetHistoryEntryCount(index);
        bool newest = index == 0 && match.IsLive();
        for (int e = entries - 1; e >= 0; e--)
        {
            int source = match.GetHistoryEntry(index, e, PlayEvents.LogSource);
            int seq = match.GetHistoryEntry(index, e, PlayEvents.LogSeq);
            if (Seen(playId, source, seq)) { continue; }
            Remember(playId, source, seq);
            if (serverNow - match.GetHistoryEntryTime(index, e) > maxCallAgeSeconds) { _skippedOld++; continue; }
            int code = match.GetHistoryEntry(index, e, PlayEvents.LogCode);
            int result = match.GetHistoryEntry(index, e, PlayEvents.LogResult);
            bool pending = newest && match.IsStrikeoutPending();
            int lastPa = newest ? match.GetLastPlateAppearanceResult() : PlayRules.PaNone;
            int call = CallFor(code, result, pending, lastPa, newest ? match.GetBalls() : 1);
            if (call != CallNone) Enqueue(call);
        }
    }

    private void RememberAll()
    {
        int plays = match.GetHistoryPlayCount();
        for (int index = 0; index < plays && index < 2; index++)
        {
            int playId = match.GetHistoryPlayField(index, MatchControllerBehaviour.HistoryPlayId);
            int entries = match.GetHistoryEntryCount(index);
            for (int e = 0; e < entries; e++)
                Remember(playId, match.GetHistoryEntry(index, e, PlayEvents.LogSource), match.GetHistoryEntry(index, e, PlayEvents.LogSeq));
        }
    }

    public bool Seen(int playId, int source, int seq)
    {
        for (int i = 0; i < MaxSeen; i++)
            if (_seenSeq[i] == seq && _seenSource[i] == source && _seenPlay[i] == playId && seq > 0) { _skippedDuplicate++; return true; }
        return false;
    }

    public void Remember(int playId, int source, int seq)
    {
        _seenPlay[_seenNext] = playId;
        _seenSource[_seenNext] = source;
        _seenSeq[_seenNext] = seq;
        _seenNext = (_seenNext + 1) % MaxSeen;
    }

    // ---- Queue and playback ----

    public void Enqueue(int call)
    {
        if (call <= CallNone || call >= CallCount) return;
        // Strike three then the out of the same strikeout: one call says it.
        if (call == CallOut && _queueCount > 0 && _queue[_queueCount - 1] == CallStrikeThree) return;
        if (_queueCount >= MaxQueue)
        {
            int lowest = 0;
            for (int i = 1; i < _queueCount; i++) if (Priority(_queue[i]) < Priority(_queue[lowest])) lowest = i;
            if (Priority(_queue[lowest]) >= Priority(call)) { _dropped++; return; }
            for (int i = lowest; i < _queueCount - 1; i++) _queue[i] = _queue[i + 1];
            _queueCount--;
            _dropped++;
        }
        _queue[_queueCount++] = call;
    }

    private void PlayNext()
    {
        if (_playing || _queueCount == 0) return;
        float wait = callSpacingSeconds - (Time.time - _lastCallTime);
        if (wait > 0f)
        {
            _playing = true;
            SendCustomEventDelayedSeconds(nameof(_PlayQueued), wait);
            return;
        }
        PlayCall(Dequeue());
        if (_queueCount > 0)
        {
            _playing = true;
            SendCustomEventDelayedSeconds(nameof(_PlayQueued), Mathf.Max(0.05f, callSpacingSeconds));
        }
    }

    public void _PlayQueued()
    {
        _playing = false;
        PlayNext();
    }

    public int Dequeue()
    {
        if (_queueCount == 0) return CallNone;
        int call = _queue[0];
        for (int i = 0; i < _queueCount - 1; i++) _queue[i] = _queue[i + 1];
        _queueCount--;
        return call;
    }

    private void PlayCall(int call)
    {
        if (call == CallNone) return;
        _lastCall = call;
        _lastCallTime = Time.time;
        _played++;
        AudioClip clip = ClipFor(call);
        if (audioSource != null && clip != null)
        {
            if (callPosition != null) audioSource.transform.position = callPosition.position;
            audioSource.PlayOneShot(clip, VolumeFor(call));
        }
        if (callText != null)
        {
            callText.text = CallName(call);
            _textSerial++;
            SendCustomEventDelayedSeconds(nameof(_ClearText), Mathf.Max(0.1f, textSeconds));
        }
    }

    public void _ClearText()
    {
        if (callText != null && Time.time - _lastCallTime >= textSeconds - 0.05f) callText.text = "";
    }

    private AudioClip ClipFor(int call)
    {
        switch (call)
        {
            case CallPlay: return playClip;
            case CallBall: return ballClip;
            case CallStrike: return strikeClip;
            case CallStrikeThree: return strikeThreeClip != null ? strikeThreeClip : strikeClip;
            case CallFoul: return foulClip;
            case CallFair: return fairClip;
            case CallOut: return outClip;
            case CallSafe: return safeClip;
            case CallTime: return timeClip;
            case CallWalk: return walkClip != null ? walkClip : ballClip;
            case CallHitByPitch: return hitByPitchClip;
        }
        return null;
    }

    private float VolumeFor(int call)
    {
        switch (call)
        {
            case CallPlay: return playVolume;
            case CallBall: return ballVolume;
            case CallStrike: return strikeVolume;
            case CallStrikeThree: return strikeThreeClip != null ? strikeThreeVolume : strikeVolume;
            case CallFoul: return foulVolume;
            case CallFair: return fairVolume;
            case CallOut: return outVolume;
            case CallSafe: return safeVolume;
            case CallTime: return timeVolume;
            case CallWalk: return walkClip != null ? walkVolume : ballVolume;
            case CallHitByPitch: return hitByPitchVolume;
        }
        return 1f;
    }
}
