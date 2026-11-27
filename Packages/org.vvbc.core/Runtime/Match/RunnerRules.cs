// Runner state of the live period (PlayState RunnerState) and the base rules built on it.
//
// Up to four runners at once: three on base and the batter-runner. Each slot is a runner of
// this play with its own runner id (never reused inside one play id), the player, the plate
// appearance that put them on base, the base they started the current batted-ball play from,
// the base the rules have awarded them, their last reported base contact and their status.
// Bases: 0 = home (a batter-runner who has not been awarded 1B), 1..3, 4 = scored.
//
// Physical position and base contact never award a base on their own: only a ruling
// (RunnerAdvance, Force/Tag Safe, a walk or hit-by-pitch award) changes Base.
public static class RunnerRules
{
    public const int SlotCount = 4;
    public const int Stride = 8;

    // Fields of a slot.
    public const int Id = 0;
    public const int Player = 1;
    public const int SourcePa = 2;     // plate appearance that put the runner on (0 = carried over from the committed state)
    public const int StartBase = 3;    // base at the start of the current batted-ball play
    public const int Base = 4;         // base awarded by the rules
    public const int Touch = 5;        // base last reported as touched (-1 none)
    public const int TouchSeq = 6;     // event sequence of that report
    public const int Status = 7;

    public const int StatusEmpty = 0;
    public const int StatusActive = 1;
    public const int StatusOut = 2;
    public const int StatusScored = 3;

    public const int HomePlate = 0;
    public const int Scored = 4;

    // CanAdvance answers.
    public const int AdvanceOk = 0;
    public const int AdvanceOccupied = 1;   // a runner ahead still holds the base: wait for their ruling
    public const int AdvancePassing = 2;    // it would pass a runner ahead: an umpire decides
    public const int AdvanceInvalid = 3;

    public static int At(int slot, int field) { return PlayRules.RunnerSlots + slot * Stride + field; }

    public static int FindById(int[] p, int runnerId)
    {
        if (runnerId <= 0) return -1;
        for (int s = 0; s < SlotCount; s++)
            if (p[At(s, Status)] != StatusEmpty && p[At(s, Id)] == runnerId) return s;
        return -1;
    }

    public static int FindActiveOnBase(int[] p, int baseIndex)
    {
        for (int s = 0; s < SlotCount; s++)
            if (p[At(s, Status)] == StatusActive && p[At(s, Base)] == baseIndex) return s;
        return -1;
    }

    public static int FindActiveByPlayer(int[] p, int playerId)
    {
        if (playerId <= 0) return -1;
        for (int s = 0; s < SlotCount; s++)
            if (p[At(s, Status)] == StatusActive && p[At(s, Player)] == playerId) return s;
        return -1;
    }

    public static int ActiveCount(int[] p)
    {
        int n = 0;
        for (int s = 0; s < SlotCount; s++) if (p[At(s, Status)] == StatusActive) n++;
        return n;
    }

    public static bool IsActive(int[] p, int slot)
    {
        return slot >= 0 && slot < SlotCount && p[At(slot, Status)] == StatusActive;
    }

    // A new runner in a free slot (an empty one, or one whose runner is out or scored).
    // Returns the slot, or -1 when four runners are already active.
    public static int Add(int[] p, int playerId, int sourcePa, int baseIndex)
    {
        int slot = -1;
        for (int s = 0; s < SlotCount && slot < 0; s++) if (p[At(s, Status)] == StatusEmpty) slot = s;
        for (int s = 0; s < SlotCount && slot < 0; s++) if (p[At(s, Status)] != StatusActive) slot = s;
        if (slot < 0) return -1;
        p[PlayRules.RunnerSeq]++;
        p[At(slot, Id)] = p[PlayRules.RunnerSeq];
        p[At(slot, Player)] = playerId;
        p[At(slot, SourcePa)] = sourcePa;
        p[At(slot, StartBase)] = baseIndex;
        p[At(slot, Base)] = baseIndex;
        p[At(slot, Touch)] = -1;
        p[At(slot, TouchSeq)] = 0;
        p[At(slot, Status)] = StatusActive;
        return slot;
    }

    // A batted-ball play starts: every runner's start base is where they stand now.
    public static void MarkStartBases(int[] p)
    {
        for (int s = 0; s < SlotCount; s++)
            if (p[At(s, Status)] == StatusActive) p[At(s, StartBase)] = p[At(s, Base)];
    }

    // Forced to advance on the current batted ball: the batter-runner until awarded 1B, and a
    // runner who has not left their start base while every base behind them, down to home,
    // was the start base of a runner who is not out (a following runner put out removes the
    // force on those ahead of them).
    public static bool IsForced(int[] p, int slot)
    {
        if (p[PlayRules.BattedBall] == 0 || !IsActive(p, slot)) return false;
        int start = p[At(slot, StartBase)];
        if (p[At(slot, Base)] != start) return false;
        if (start == HomePlate) return true;
        for (int behind = start - 1; behind >= 0; behind--)
            if (!StartedFromNotOut(p, behind)) return false;
        return true;
    }

    private static bool StartedFromNotOut(int[] p, int baseIndex)
    {
        for (int s = 0; s < SlotCount; s++)
        {
            int status = p[At(s, Status)];
            if ((status == StatusActive || status == StatusScored) && p[At(s, StartBase)] == baseIndex) return true;
        }
        return false;
    }

    public static int ForceTarget(int[] p, int slot) { return p[At(slot, StartBase)] + 1; }

    // Whether the runner in slot may be awarded target now. Two runners never hold the same
    // base, and a runner never passes one ahead of them.
    public static int CanAdvance(int[] p, int slot, int target)
    {
        if (!IsActive(p, slot) || target < 1 || target > Scored) return AdvanceInvalid;
        int current = p[At(slot, Base)];
        if (target <= current) return AdvanceInvalid;
        for (int s = 0; s < SlotCount; s++)
        {
            if (s == slot || p[At(s, Status)] != StatusActive) continue;
            int other = p[At(s, Base)];
            if (other <= current) continue;          // behind (or level with) this runner
            if (other < target) return AdvancePassing;
            if (other == target && target < Scored) return AdvanceOccupied;
        }
        return AdvanceOk;
    }

    // Awards target (scored at 4). Returns true when the runner scored.
    public static bool Award(int[] p, int slot, int target)
    {
        p[At(slot, Base)] = target;
        if (target < Scored) return false;
        p[At(slot, Status)] = StatusScored;
        return true;
    }

    public static void PutOut(int[] p, int slot)
    {
        p[At(slot, Status)] = StatusOut;
    }

    // Walk / hit-by-pitch: the batter is awarded 1B and only runners forced by it move up one
    // base (1B, then 2B when 1B was occupied, then 3B). Runners not forced stay. Returns the
    // number of runs forced in, or -1 when there was no free slot for the batter.
    public static int AwardBatterFirst(int[] p, int batter, int paId)
    {
        int on1 = FindActiveOnBase(p, 1);
        int on2 = on1 >= 0 ? FindActiveOnBase(p, 2) : -1;
        int on3 = on2 >= 0 ? FindActiveOnBase(p, 3) : -1;
        int runs = 0;
        if (on3 >= 0 && Award(p, on3, Scored)) runs++;
        if (on2 >= 0) Award(p, on2, 3);
        if (on1 >= 0) Award(p, on1, 2);
        if (Add(p, batter, paId, 1) < 0) return -1;
        return runs;
    }

    // playerId of the runner awarded baseIndex (1..3), 0 when none.
    public static int PlayerOnBase(int[] p, int baseIndex)
    {
        int slot = FindActiveOnBase(p, baseIndex);
        return slot < 0 ? 0 : p[At(slot, Player)];
    }

    // A batter-runner still running to 1B.
    public static int FindBatterRunner(int[] p)
    {
        return FindActiveOnBase(p, HomePlate);
    }
}
