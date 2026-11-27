using Tsukimi;
using UnityEngine;

// The tag contact effect and sound (TagContactFeedback prefab,
// docs/development/player-event-collector.md). It shows that a contact happened, never a call: no
// "out" or "safe" is shown or played.
//
// The fielder's client plays it at once when it records the tag (PlayLocal). Everyone else plays
// it when the MatchManager Owner has accepted the event and announced it (PlayConfirmed), at the
// recorded world position of the contact, not where the players are now. One event plays once per
// client: its id (play, source, sequence) is remembered, so the fielder's own copy of the
// announcement, a second announcement after an Owner change, or the Owner's own echo are skipped.
// An announcement older than maxAgeSeconds is not shown (the event itself is unaffected). Late
// joiners never receive past announcements, so they see no past effects. Local only; no sync.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class TagContactFeedbackBehaviour : TsukimiBehaviour
{
    public const int Remembered = 32;

    [Header("References")]
    [Tooltip("Burst effect, moved to the contact position. Use world simulation space.")]
    public ParticleSystem effect;
    [Tooltip("Short contact sound, moved to the contact position.")]
    public AudioSource sound;

    [Header("Feedback")]
    [Tooltip("Particles emitted per contact. Their lifetime (display time) is set on the Particle System. Default 24.")]
    public int emitCount = 24;
    [Range(0f, 1f), Tooltip("Sound volume. Default 0.8.")]
    public float volume = 0.8f;
    [Tooltip("Uniform scale of the effect. Default 1.")]
    public float effectScale = 1f;
    [Tooltip("Announcements older than this many seconds (by the event's server time) are not shown. Default 1.5.")]
    public float maxAgeSeconds = 1.5f;

    private readonly int[] _seenPlay = new int[Remembered];
    private readonly int[] _seenSource = new int[Remembered];
    private readonly int[] _seenSeq = new int[Remembered];
    private int _seenNext;

    // Diagnostics.
    private int _played;
    private int _skippedDuplicate;
    private int _skippedLate;
    private Vector3 _lastPosition;

    public int GetPlayedCount() { return _played; }
    public int GetSkippedDuplicateCount() { return _skippedDuplicate; }
    public int GetSkippedLateCount() { return _skippedLate; }
    public Vector3 GetLastPosition() { return _lastPosition; }

    // The fielder's client, right after recording the event.
    public void PlayLocal(int playId, int source, int seq, Vector3 position)
    {
        if (Seen(playId, source, seq))
        {
            _skippedDuplicate++;
            return;
        }
        Remember(playId, source, seq);
        Show(position);
    }

    // Everyone, on the Owner's announcement of an accepted event.
    public bool PlayConfirmed(int playId, int source, int seq, Vector3 position, double eventTime, int localId, double serverNow)
    {
        if (Seen(playId, source, seq))
        {
            _skippedDuplicate++;
            return false;
        }
        Remember(playId, source, seq);
        if (serverNow - eventTime > maxAgeSeconds)
        {
            _skippedLate++;
            return false;
        }
        Show(position);
        return true;
    }

    public bool Seen(int playId, int source, int seq)
    {
        for (int i = 0; i < Remembered; i++)
            if (_seenSeq[i] == seq && _seenSource[i] == source && _seenPlay[i] == playId && seq > 0) return true;
        return false;
    }

    private void Remember(int playId, int source, int seq)
    {
        _seenPlay[_seenNext] = playId;
        _seenSource[_seenNext] = source;
        _seenSeq[_seenNext] = seq;
        _seenNext = (_seenNext + 1) % Remembered;
    }

    private void Show(Vector3 position)
    {
        _played++;
        _lastPosition = position;
        if (effect != null)
        {
            Transform t = effect.transform;
            t.position = position;
            t.localScale = new Vector3(effectScale, effectScale, effectScale);
            effect.Emit(emitCount);
        }
        if (sound != null)
        {
            sound.transform.position = position;
            sound.volume = volume;
            sound.Play();
        }
    }
}
