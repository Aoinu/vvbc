// Bookkeeping of raw contact events (docs/development/player-event-collector.md), kept apart from
// PlayEvents, which numbers rulings: a contact received here is a fact for a later umpire engine,
// never a ruling, so it has its own identity table and its own capacity.
//
// * Sender ring (PlayerEventCollectorBehaviour): fixed slots with a local status. A slot holding
//   an event the receiver has not settled is never reused; when no slot is free a new event is
//   refused and counted, never written over an unsettled one.
// * Receiver table (MatchEventReceiverBehaviour): per source (the sender's playerId) a watermark W
//   (every sequence <= W settled) and a bit mask for W+1 .. W+Window, as PlayEvents does, for one
//   play at a time. Layout: [0] = sources in use, then MaxSources x (source, watermark, mask).
public static class ContactEvents
{
    // ---- Sender slot status (local to the PlayerObject's owner) ----
    public const int SlotFree = 0;
    public const int SlotUnassigned = 1;    // detected while no live play was known; no id yet
    public const int SlotRecorded = 2;      // has an id, not yet confirmed sent
    public const int SlotSent = 3;          // a serialization carrying it succeeded
    public const int SlotAccepted = 4;      // the receiver settled it (accepted or rejected after checks)
    public const int SlotInvalid = 5;       // withdrawn by the sender (stale play, no play, early)

    // Why the sender withdrew a slot (diagnostics).
    public const int InvalidNone = 0;
    public const int InvalidNoPlay = 1;     // no live play started before the event
    public const int InvalidStalePlay = 2;  // its play was committed before the receiver settled it
    public const int InvalidEarly = 3;      // earlier than the start of the play it was assigned

    // ---- Receiver table ----
    public const int MaxSources = 32;
    public const int SourceStride = 3;
    public const int TableLength = 1 + MaxSources * SourceStride;
    public const int Window = 30;

    public const int Unseen = 0;
    public const int Seen = 1;
    public const int NoRoom = 2;            // beyond the window of the source
    public const int NoSlot = 3;            // a new source and the table is full

    public static bool IsOpen(int status) { return status == SlotRecorded || status == SlotSent; }
    public static bool Holds(int status) { return status == SlotUnassigned || IsOpen(status); }

    // The slot for a new event: a free one, else the oldest settled or withdrawn one, else the
    // oldest one still waiting for a play (it has no id yet, so nothing is lost but a detection
    // that could not be placed). -1 when every slot holds an event waiting to be settled.
    public static int ChooseSlot(int[] status, int[] order)
    {
        int best = -1;
        int bestOrder = 0;
        for (int i = 0; i < status.Length; i++) if (status[i] == SlotFree) return i;
        for (int i = 0; i < status.Length; i++)
        {
            if (status[i] != SlotAccepted && status[i] != SlotInvalid) continue;
            if (best < 0 || order[i] < bestOrder) { best = i; bestOrder = order[i]; }
        }
        if (best >= 0) return best;
        for (int i = 0; i < status.Length; i++)
        {
            if (status[i] != SlotUnassigned) continue;
            if (best < 0 || order[i] < bestOrder) { best = i; bestOrder = order[i]; }
        }
        return best;
    }

    public static int CountStatus(int[] status, int wanted)
    {
        int n = 0;
        for (int i = 0; i < status.Length; i++) if (status[i] == wanted) n++;
        return n;
    }

    // ---- Receiver table ----

    public static void ClearTable(int[] t)
    {
        for (int i = 0; i < t.Length; i++) t[i] = 0;
    }

    public static int FindSource(int[] t, int source)
    {
        int count = t[0];
        for (int i = 0; i < count && i < MaxSources; i++) if (t[1 + i * SourceStride] == source) return i;
        return -1;
    }

    public static int Check(int[] t, int source, int seq)
    {
        if (seq <= 0) return Seen;
        int slot = FindSource(t, source);
        if (slot < 0)
        {
            if (t[0] >= MaxSources) return NoSlot;
            return seq > Window ? NoRoom : Unseen;
        }
        int at = 1 + slot * SourceStride;
        int watermark = t[at + 1];
        if (seq <= watermark) return Seen;
        int distance = seq - watermark;
        if (distance > Window) return NoRoom;
        return (t[at + 2] & (1 << (distance - 1))) != 0 ? Seen : Unseen;
    }

    // Marks (source, seq) settled. Check must have answered Unseen.
    public static void Settle(int[] t, int source, int seq)
    {
        int slot = FindSource(t, source);
        if (slot < 0)
        {
            if (t[0] >= MaxSources) return;
            slot = t[0];
            t[0] = slot + 1;
            int fresh = 1 + slot * SourceStride;
            t[fresh] = source;
            t[fresh + 1] = 0;
            t[fresh + 2] = 0;
        }
        int at = 1 + slot * SourceStride;
        int distance = seq - t[at + 1];
        if (distance < 1 || distance > Window) return;
        int mask = t[at + 2] | (1 << (distance - 1));
        int watermark = t[at + 1];
        while ((mask & 1) != 0)
        {
            watermark++;
            mask = mask >> 1;
        }
        t[at + 1] = watermark;
        t[at + 2] = mask;
    }

    public static bool IsSettled(int[] t, int source, int seq) { return Check(t, source, seq) == Seen; }

    public static int Watermark(int[] t, int source)
    {
        int slot = FindSource(t, source);
        return slot < 0 ? 0 : t[1 + slot * SourceStride + 1];
    }

    // 0 when the table is well formed (a received or recovered copy is checked before use).
    public static int FindTableViolation(int[] t)
    {
        if (t == null || t.Length != TableLength) return 1;
        int count = t[0];
        if (count < 0 || count > MaxSources) return 2;
        for (int i = 0; i < MaxSources; i++)
        {
            int at = 1 + i * SourceStride;
            if (i >= count)
            {
                if (t[at] != 0 || t[at + 1] != 0 || t[at + 2] != 0) return 3;
                continue;
            }
            if (t[at] <= 0 || t[at] >= PlayEvents.SourceManualBase || t[at + 1] < 0) return 4;
            if ((t[at + 2] & 1) != 0 || t[at + 2] < 0 || t[at + 2] >= (1 << Window)) return 5;
            for (int j = 0; j < i; j++) if (t[1 + j * SourceStride] == t[at]) return 6;
        }
        return 0;
    }
}
