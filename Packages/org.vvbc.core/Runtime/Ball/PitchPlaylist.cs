using UnityEngine;

// Packed batting-pitcher playlist. Every operation is a small in-place edit of two parallel
// arrays: the items and their builds, which always move together.
//
// Item (one int): bits 0-8 course mask (zone 1-9 = bit 0-8, rows high to low and columns
// batter-left to batter-right, as StrikeZoneGeometry), bits 9-12 pitch type (PitchType id),
// bits 13-28 item id, bits 29-31 reserved (zero).
//
// Build (one int): parameters A, B and C of the item's PitchBuild in bits 0-9, 10-19 and
// 20-29, each 0-1 quantized to 0-BuildSteps (so 0.5 is exact). Parameters the type does not
// use are stored at the default. An empty slot holds 0.
//
// State (one int): bits 0-3 count, bits 4-13 done history (one bit per array index, so it
// moves with the item), bit 14 Sequence (clear = Random), bit 15 running, bit 16 reset On Timer
// (clear = On Event). The next item is the first not-done item from the top of the array,
// so no separate cursor is stored.
public static class PitchPlaylist
{
    public const int MaxItems = 10;
    public const int CourseBits = 0x1FF;
    public const int CenterZone = 4;
    public const int CenterMask = 1 << CenterZone;
    public const int TypeShift = 9;
    public const int TypeBits = 0xF;
    public const int IdShift = 13;
    public const int IdBits = 0xFFFF;
    public const int BuildSteps = 1022;
    private const int BuildBits = 0x3FF;
    // A, B and C at PitchBuild.DefaultParameter.
    public const int DefaultBuild = 511 | (511 << 10) | (511 << 20);

    private const int CountBits = 0xF;
    private const int HistoryShift = 4;
    private const int HistoryBits = 0x3FF;
    public const int SequenceFlag = 1 << 14;
    public const int RunningFlag = 1 << 15;
    public const int TimerFlag = 1 << 16;

    public static int Pack(int id, int type, int mask)
    {
        return ((id & IdBits) << IdShift) | ((type & TypeBits) << TypeShift) | (mask & CourseBits);
    }

    public static int Id(int item) { return (item >> IdShift) & IdBits; }
    public static int Type(int item) { return (item >> TypeShift) & TypeBits; }
    public static int Mask(int item) { return item & CourseBits; }

    // Packs a build for a type; the parameters it does not use are stored at the default.
    public static int PackBuild(int type, float a, float b, float c)
    {
        int count = PitchType.ParameterCount(type);
        return Step(count > 0 ? a : PitchBuild.DefaultParameter) |
            (Step(count > 1 ? b : PitchBuild.DefaultParameter) << 10) |
            (Step(count > 2 ? c : PitchBuild.DefaultParameter) << 20);
    }

    // A packed build normalized for the type: out-of-range steps clamped, unused parameters reset.
    public static int NormalizeBuild(int type, int build)
    {
        return PackBuild(type, Parameter(build, 0), Parameter(build, 1), Parameter(build, 2));
    }

    // Parameter 0 (A), 1 (B) or 2 (C) of a packed build, 0-1.
    public static float Parameter(int build, int parameter)
    {
        int step = (build >> (parameter * 10)) & BuildBits;
        return Mathf.Min(step, BuildSteps) / (float)BuildSteps;
    }

    public static PitchBuild Build(int item, int build)
    {
        PitchBuild result = new PitchBuild();
        result.pitchTypeId = PitchType.Safe(Type(item));
        result.parameterA = Parameter(build, 0);
        result.parameterB = Parameter(build, 1);
        result.parameterC = Parameter(build, 2);
        return result;
    }

    private static int Step(float value)
    {
        return (int)(PitchPropertiesCalculator.Parameter(value) * BuildSteps + 0.5f);
    }

    public static int Count(int state) { return Mathf.Min(state & CountBits, MaxItems); }

    public static int History(int state)
    {
        return (state >> HistoryShift) & HistoryBits & ((1 << Count(state)) - 1);
    }

    public static bool Done(int state, int index) { return (History(state) & (1 << index)) != 0; }
    public static bool Has(int state, int flag) { return (state & flag) != 0; }

    public static int WithFlag(int state, int flag, bool on)
    {
        return on ? state | flag : state & ~flag;
    }

    private static int With(int state, int count, int history)
    {
        int flags = state & ~(CountBits | (HistoryBits << HistoryShift));
        count = Mathf.Clamp(count, 0, MaxItems);
        history &= (1 << count) - 1;
        return flags | count | (history << HistoryShift);
    }

    public static int WithHistory(int state, int history) { return With(state, Count(state), history); }

    public static int MarkDone(int state, int index, bool done)
    {
        int history = History(state);
        history = done ? history | (1 << index) : history & ~(1 << index);
        return WithHistory(state, history);
    }

    // Index of the next item to pitch, or -1 when every item is done (or the list is empty).
    public static int FirstPending(int state)
    {
        int count = Count(state);
        int history = History(state);
        for (int i = 0; i < count; i++) if ((history & (1 << i)) == 0) return i;
        return -1;
    }

    public static int IndexOf(int[] items, int state, int id)
    {
        if (id <= 0) return -1;
        int count = Count(state);
        for (int i = 0; i < count && i < items.Length; i++) if (Id(items[i]) == id) return i;
        return -1;
    }

    // Lowest unused id at or after the candidate, wrapping inside 1..IdBits.
    public static int FreeId(int[] items, int state, int candidate)
    {
        int id = candidate < 1 || candidate > IdBits ? 1 : candidate;
        for (int attempt = 0; attempt <= MaxItems; attempt++)
        {
            if (IndexOf(items, state, id) < 0) return id;
            id = id >= IdBits ? 1 : id + 1;
        }
        return id;
    }

    // Appends a not-yet-pitched item with its build. Returns the new state, or -1 when the list is full.
    public static int Append(int[] items, int[] builds, int state, int item, int build)
    {
        int count = Count(state);
        if (count >= MaxItems || count >= items.Length) return -1;
        items[count] = item;
        builds[count] = build;
        return With(state, count + 1, History(state));
    }

    // The freed slot is cleared, so a later item never inherits its build.
    public static int RemoveAt(int[] items, int[] builds, int state, int index)
    {
        int count = Count(state);
        if (index < 0 || index >= count) return state;
        for (int i = index; i < count - 1; i++)
        {
            items[i] = items[i + 1];
            builds[i] = builds[i + 1];
        }
        items[count - 1] = 0;
        builds[count - 1] = 0;
        int history = History(state);
        int lower = history & ((1 << index) - 1);
        int upper = (history >> (index + 1)) << index;
        return With(state, count - 1, lower | upper);
    }

    // Swaps two items; their builds and done bits travel with them.
    public static int Swap(int[] items, int[] builds, int state, int a, int b)
    {
        int count = Count(state);
        if (a < 0 || b < 0 || a >= count || b >= count || a == b) return state;
        Exchange(items, builds, a, b);
        int history = History(state);
        bool doneA = (history & (1 << a)) != 0;
        bool doneB = (history & (1 << b)) != 0;
        history &= ~((1 << a) | (1 << b));
        if (doneA) history |= 1 << b;
        if (doneB) history |= 1 << a;
        return WithHistory(state, history);
    }

    // Linear congruential step for the Owner's shuffle; the seed is local and never synced.
    public static int NextSeed(int seed)
    {
        return seed * 1103515245 + 12345;
    }

    private static void Exchange(int[] items, int[] builds, int a, int b)
    {
        int item = items[a];
        items[a] = items[b];
        items[b] = item;
        int build = builds[a];
        builds[a] = builds[b];
        builds[b] = build;
    }

    // Fisher-Yates over the used slots, builds with their items; a shuffle starts a new lap.
    public static int Shuffle(int[] items, int[] builds, int state, int seed)
    {
        int count = Count(state);
        for (int i = count - 1; i > 0; i--)
        {
            seed = NextSeed(seed);
            int j = ((seed >> 16) & 0x7FFF) % (i + 1);
            Exchange(items, builds, i, j);
        }
        return WithHistory(state, 0);
    }

    // Starts the next lap once every item is done: Random reshuffles, Sequence restarts from the top.
    public static int BeginLapIfComplete(int[] items, int[] builds, int state, int seed)
    {
        if (Count(state) == 0 || FirstPending(state) >= 0) return state;
        return Has(state, SequenceFlag) ? WithHistory(state, 0) : Shuffle(items, builds, state, seed);
    }

    // Removes duplicate or zero ids and clamps the count, so a malformed snapshot stays usable.
    public static int Sanitize(int[] items, int[] builds, int state)
    {
        int count = Mathf.Min(Count(state), items.Length);
        state = With(state, count, History(state));
        for (int i = count - 1; i >= 0; i--)
        {
            int id = Id(items[i]);
            bool duplicate = id == 0;
            for (int j = 0; j < i && !duplicate; j++) duplicate = Id(items[j]) == id;
            if (duplicate) state = RemoveAt(items, builds, state, i);
        }
        return state;
    }

    public static int CountCourses(int mask)
    {
        int count = 0;
        for (int i = 0; i < 9; i++) if ((mask & (1 << i)) != 0) count++;
        return count;
    }

    // The choice-th enabled zone; an empty mask means the center zone.
    public static int SelectCourse(int mask, int choice)
    {
        for (int i = 0; i < 9; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            if (choice == 0) return i;
            choice--;
        }
        return CenterZone;
    }
}
