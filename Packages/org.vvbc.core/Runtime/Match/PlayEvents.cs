// Event identity, duplicate suppression and the display log of a PlayState
// (docs/development/match-state.md, events). Three separate structures, never one ring:
//
// * Sources (dedup): an event is (play id, source id, sequence). The play id is the PlayState
//   itself (a new play starts a new table; rulings for another play are refused before this).
//   Each source numbers its events of a play 1, 2, 3, ... Per source the table keeps a
//   watermark W (every sequence <= W is settled) and a bit mask of settled sequences in
//   W+1 .. W+Window. Settled = applied, accepted into the pending list, superseded, explicitly
//   rejected after a state check, or declared missing by the sender. A sequence above W that
//   is not in the mask has not arrived (a hole), which is different from settled: a late copy
//   of it is still taken. The watermark moves up over consecutive settled sequences, so the
//   table never grows with the length of the play. A sequence beyond the window, or a new
//   source when the table is full, is refused with RejectedFull (never dropped silently): the
//   sender keeps it, and the play is flagged AttentionEventCapacity.
// * Pending (PlayRules): rulings received and settled here but not applied yet.
// * Log: the recent rulings of the play for display, with a total count; overwriting an old
//   log entry never touches the sources or the pending list.
//
// Source ids: a player's own events (PlayerObject / EventCollector) use the playerId; the
// umpire's manual rulings use SourceManualBase + playerId; an automatic umpire engine running
// on the Owner uses SourceEngineBase + the Owner's playerId, so a new Owner's engine never
// reuses an earlier engine's numbers.
public static class PlayEvents
{
    public const int SourceManualBase = 1000000;
    public const int SourceEngineBase = 2000000;
    public const int SourceMax = 3000000;

    public const int MaxSources = 12;
    public const int SourceStride = 3;          // source id, watermark, mask
    public const int Window = 30;               // sequences tracked above the watermark

    // Check answers.
    public const int Unseen = 0;
    public const int Seen = 1;
    public const int NoRoom = 2;                // beyond the window of this source
    public const int NoSlot = 3;                // a new source and the table is full

    public const int LogStride = 7;             // code, source, seq, runnerId, arg, result, pitch
    public const int LogCode = 0;
    public const int LogSource = 1;
    public const int LogSeq = 2;
    public const int LogRunner = 3;
    public const int LogArg = 4;
    public const int LogResult = 5;
    public const int LogPitch = 6;
    public const int MaxLog = 12;

    public static bool ValidSource(int source) { return source > 0 && source < SourceMax; }

    public static int FindSource(int[] p, int source)
    {
        int count = p[PlayRules.SourceCount];
        for (int i = 0; i < count && i < MaxSources; i++)
            if (p[PlayRules.Sources + i * SourceStride] == source) return i;
        return -1;
    }

    public static int Check(int[] p, int source, int seq)
    {
        int slot = FindSource(p, source);
        if (slot < 0)
        {
            if (p[PlayRules.SourceCount] >= MaxSources) return NoSlot;
            return seq > Window ? NoRoom : Unseen;
        }
        int at = PlayRules.Sources + slot * SourceStride;
        int watermark = p[at + 1];
        if (seq <= watermark) return Seen;
        int distance = seq - watermark;
        if (distance > Window) return NoRoom;
        return (p[at + 2] & (1 << (distance - 1))) != 0 ? Seen : Unseen;
    }

    // Marks (source, seq) settled. Check must have answered Unseen.
    public static void Settle(int[] p, int source, int seq)
    {
        int slot = FindSource(p, source);
        if (slot < 0)
        {
            slot = p[PlayRules.SourceCount];
            p[PlayRules.SourceCount] = slot + 1;
            int fresh = PlayRules.Sources + slot * SourceStride;
            p[fresh] = source;
            p[fresh + 1] = 0;
            p[fresh + 2] = 0;
        }
        int at = PlayRules.Sources + slot * SourceStride;
        int distance = seq - p[at + 1];
        if (distance < 1 || distance > Window) return;
        int mask = p[at + 2] | (1 << (distance - 1));
        int watermark = p[at + 1];
        while ((mask & 1) != 0)
        {
            watermark++;
            mask = mask >> 1;
        }
        p[at + 1] = watermark;
        p[at + 2] = mask;
    }

    public static bool IsSettled(int[] p, int source, int seq) { return Check(p, source, seq) == Seen; }

    // Every sequence <= the answer is settled (0 = nothing yet, or an unknown source).
    public static int Watermark(int[] p, int source)
    {
        int slot = FindSource(p, source);
        return slot < 0 ? 0 : p[PlayRules.Sources + slot * SourceStride + 1];
    }

    public static void Log(int[] p, double[] t, int code, int source, int seq, int runnerId, int arg, int result, double time)
    {
        int index = p[PlayRules.LogNext];
        int at = PlayRules.Log + index * LogStride;
        p[at + LogCode] = code;
        p[at + LogSource] = source;
        p[at + LogSeq] = seq;
        p[at + LogRunner] = runnerId;
        p[at + LogArg] = arg;
        p[at + LogResult] = result;
        p[at + LogPitch] = p[PlayRules.PitchSeq];
        t[PlayRules.TimeLog + index] = time;
        p[PlayRules.LogNext] = (index + 1) % MaxLog;
        p[PlayRules.LogTotal]++;
    }

    // The table, watermarks, masks and log indices are well formed (0 = yes).
    public static int FindViolation(int[] p)
    {
        int count = p[PlayRules.SourceCount];
        if (count < 0 || count > MaxSources) return 1;
        for (int i = 0; i < MaxSources; i++)
        {
            int at = PlayRules.Sources + i * SourceStride;
            if (i >= count)
            {
                if (p[at] != 0 || p[at + 1] != 0 || p[at + 2] != 0) return 2;
                continue;
            }
            if (!ValidSource(p[at]) || p[at + 1] < 0) return 3;
            // Bit 0 set would have moved the watermark; no bits beyond the window.
            if ((p[at + 2] & 1) != 0 || p[at + 2] < 0 || p[at + 2] >= (1 << Window)) return 4;
            for (int j = 0; j < i; j++) if (p[PlayRules.Sources + j * SourceStride] == p[at]) return 5;
        }
        int next = p[PlayRules.LogNext];
        int total = p[PlayRules.LogTotal];
        if (next < 0 || next >= MaxLog || total < 0) return 6;
        if (total < MaxLog && next != total) return 6;
        if (total >= MaxLog && next != total % MaxLog) return 6;
        return 0;
    }
}
