using Tsukimi;
using VRC.SDKBase;

// Display history of the current play and the last committed plays, and the event contract
// for the automatic umpire and the future EventCollector (docs/development/match-state.md).
//
// The history is for reading only. It is separate from the event table that suppresses
// duplicates (PlayEvents sources in the PlayState) and from the pending rulings, so an old
// entry dropped from a full log never makes an event applicable again or loses a waiting one.
public partial class MatchControllerBehaviour
{
    // Committed plays kept for display, newest first after the current one.
    public const int MaxHistoryPlays = 4;
    public const int HistoryHeader = 6;          // play id, end reason, attention, log total, log next, pitches
    public const int HistoryStride = HistoryHeader + PlayEvents.MaxLog * PlayEvents.LogStride;

    public const int HistoryPlayId = 0;
    public const int HistoryEndReason = 1;
    public const int HistoryAttention = 2;
    public const int HistoryLogTotal = 3;
    public const int HistoryLogNext = 4;
    public const int HistoryPitches = 5;

    [UdonSynced] private int[] _history = new int[MaxHistoryPlays * HistoryStride];
    [UdonSynced] private double[] _historyTimes = new double[MaxHistoryPlays * PlayEvents.MaxLog];
    [UdonSynced] private int _historyNext;
    [UdonSynced] private int _historyCount;

    // ---- Event contract ----

    // The source id an automatic umpire engine on this client uses (unique per Owner).
    public int EngineSourceId() { return PlayEvents.SourceEngineBase + LocalId(); }
    // The source id of a player's manual rulings.
    public static int ManualSourceId(int playerId) { return PlayEvents.SourceManualBase + playerId; }

    // Every sequence of source up to the answer is settled in the live play (0 = none yet).
    // Sequences above it may be settled out of order (IsEventSettled) or not have arrived.
    public int GetEventWatermark(int source) { return IsLive() ? PlayEvents.Watermark(_play, source) : 0; }
    public bool IsEventSettled(int source, int sequence) { return IsLive() && PlayEvents.IsSettled(_play, source, sequence); }

    // The sender (EventCollector, engine) states that (source, sequence) of the live play will
    // never be sent: the hole closes, so a missing event is told apart from one still on its way.
    public int DeclareEventMissing(int playId, int source, int sequence, double serverTime)
    {
        if (!CanCommit()) return PlayRules.RejectedNotReady;
        if (!IsLive() || playId != _play[PlayRules.PlayId]) return PlayRules.RejectedWrongPlay;
        int revision = _play[PlayRules.Revision];
        int result = PlayRules.DeclareMissing(_play, _playTimes, source, sequence, serverTime);
        if (_play[PlayRules.Revision] != revision) Publish();
        return result;
    }

    // ---- Queries: index 0 is the live play (if any), then committed plays, newest first ----

    public int GetHistoryPlayCount() { return (IsLive() ? 1 : 0) + _historyCount; }

    // Header field of play index (HistoryPlayId, HistoryEndReason, HistoryAttention,
    // HistoryLogTotal, HistoryPitches). The live play answers from the PlayState.
    public int GetHistoryPlayField(int index, int field)
    {
        if (IsLive() && index == 0)
        {
            switch (field)
            {
                case HistoryPlayId: return _play[PlayRules.PlayId];
                case HistoryEndReason: return PlayRules.DeadNone;
                case HistoryAttention: return _play[PlayRules.DAttention];
                case HistoryLogTotal: return _play[PlayRules.LogTotal];
                case HistoryLogNext: return _play[PlayRules.LogNext];
                case HistoryPitches: return _play[PlayRules.PitchSeq];
            }
            return 0;
        }
        int slot = ArchiveSlot(index);
        if (slot < 0 || field < 0 || field >= HistoryHeader) return 0;
        return _history[slot * HistoryStride + field];
    }

    // Entries kept for play index (at most PlayEvents.MaxLog; older ones were dropped).
    public int GetHistoryEntryCount(int index)
    {
        int total = GetHistoryPlayField(index, HistoryLogTotal);
        return total < PlayEvents.MaxLog ? total : PlayEvents.MaxLog;
    }

    // Entry (0 = newest) of play index: PlayEvents.LogCode, LogSource, LogSeq, LogRunner, LogArg,
    // LogResult, LogPitch.
    public int GetHistoryEntry(int index, int entry, int field)
    {
        if (entry < 0 || entry >= GetHistoryEntryCount(index) || field < 0 || field >= PlayEvents.LogStride) return 0;
        int at = (GetHistoryPlayField(index, HistoryLogNext) - 1 - entry + PlayEvents.MaxLog * 2) % PlayEvents.MaxLog;
        if (IsLive() && index == 0) return _play[PlayRules.Log + at * PlayEvents.LogStride + field];
        int slot = ArchiveSlot(index);
        return _history[slot * HistoryStride + HistoryHeader + at * PlayEvents.LogStride + field];
    }

    public double GetHistoryEntryTime(int index, int entry)
    {
        if (entry < 0 || entry >= GetHistoryEntryCount(index)) return 0.0;
        int at = (GetHistoryPlayField(index, HistoryLogNext) - 1 - entry + PlayEvents.MaxLog * 2) % PlayEvents.MaxLog;
        if (IsLive() && index == 0) return _playTimes[PlayRules.TimeLog + at];
        return _historyTimes[ArchiveSlot(index) * PlayEvents.MaxLog + at];
    }

    // The live play's log, newest first (as GetHistoryEntry(0, ...) while live).
    public int GetLogEntry(int index, int field) { return IsLive() ? GetHistoryEntry(0, index, field) : 0; }

    private int ArchiveSlot(int index)
    {
        int archived = index - (IsLive() ? 1 : 0);
        if (archived < 0 || archived >= _historyCount) return -1;
        return (_historyNext - 1 - archived + MaxHistoryPlays * 2) % MaxHistoryPlays;
    }

    // At the commit: the play's log and header move into the archive (the oldest is replaced).
    private void ArchivePlay(int attention)
    {
        int slot = _historyNext;
        int at = slot * HistoryStride;
        _history[at + HistoryPlayId] = _play[PlayRules.PlayId];
        _history[at + HistoryEndReason] = _play[PlayRules.EndReason];
        _history[at + HistoryAttention] = attention;
        _history[at + HistoryLogTotal] = _play[PlayRules.LogTotal];
        _history[at + HistoryLogNext] = _play[PlayRules.LogNext];
        _history[at + HistoryPitches] = _play[PlayRules.PitchSeq];
        for (int i = 0; i < PlayEvents.MaxLog * PlayEvents.LogStride; i++) _history[at + HistoryHeader + i] = _play[PlayRules.Log + i];
        for (int i = 0; i < PlayEvents.MaxLog; i++) _historyTimes[slot * PlayEvents.MaxLog + i] = _playTimes[PlayRules.TimeLog + i];
        _historyNext = (slot + 1) % MaxHistoryPlays;
        if (_historyCount < MaxHistoryPlays) _historyCount++;
    }

    // The archive's indices and entries are well formed (0 = yes).
    private static int FindHistoryViolation(int[] history, double[] times, int next, int count)
    {
        if (history == null || history.Length != MaxHistoryPlays * HistoryStride || times == null || times.Length != MaxHistoryPlays * PlayEvents.MaxLog) return 1;
        if (next < 0 || next >= MaxHistoryPlays || count < 0 || count > MaxHistoryPlays) return 2;
        if (count < MaxHistoryPlays && next != count) return 2;
        for (int i = 0; i < count; i++)
        {
            int at = ((next - 1 - i + MaxHistoryPlays * 2) % MaxHistoryPlays) * HistoryStride;
            int total = history[at + HistoryLogTotal];
            int logNext = history[at + HistoryLogNext];
            if (history[at + HistoryPlayId] <= 0 || total < 0 || logNext < 0 || logNext >= PlayEvents.MaxLog) return 3;
            if (logNext != total % PlayEvents.MaxLog) return 3;
        }
        for (int i = 0; i < times.Length; i++) if (!PlayRules.ValidTime(times[i])) return 4;
        return 0;
    }
}
