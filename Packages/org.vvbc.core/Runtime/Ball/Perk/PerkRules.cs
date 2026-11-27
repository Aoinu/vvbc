// Selection rules of the player perks (docs/development/player-perks.md). A selection is a list
// of perk ids, `count` entries used, with no duplicates. The only game rules are:
//   1. a perk cannot be selected twice;
//   2. two stages of one series exclude each other (PerkCatalog.Group);
//   3. a buff and a debuff of the same ability exclude each other (per effect, so a perk with
//      several abilities conflicts on any of them).
// Anything else may be combined: there is no point, slot or count limit. MaxSelected is only the
// array size; with the rules above no selection can reach it.
public static class PerkRules
{
    public const int Ok = 0;
    public const int UnknownPerk = 1;
    public const int NotImplemented = 2;
    public const int Duplicate = 3;
    public const int SeriesConflict = 4;
    public const int BuffDebuffConflict = 5;
    public const int NotEditable = 6;
    public const int NotSelected = 7;
    public const int Full = 8;

    // Technical array size, not a game rule (more than one perk per series is never allowed).
    public const int MaxSelected = 32;

    public static int IndexOf(int[] selected, int count, int id)
    {
        for (int i = 0; i < count; i++)
            if (selected[i] == id) return i;
        return -1;
    }

    // Result of adding `id` to the selection (Ok, UnknownPerk, NotImplemented, Duplicate,
    // SeriesConflict, BuffDebuffConflict or Full). Nothing is changed.
    public static int CheckAdd(int[] selected, int count, int id)
    {
        if (!PerkCatalog.IsKnown(id)) return UnknownPerk;
        if (!PerkCatalog.IsImplemented(id)) return NotImplemented;
        if (IndexOf(selected, count, id) >= 0) return Duplicate;
        int group = PerkCatalog.Group(id);
        for (int i = 0; i < count; i++)
            if (PerkCatalog.Group(selected[i]) == group) return SeriesConflict;
        for (int i = 0; i < count; i++)
            if (OppositeOnSameAbility(selected[i], id)) return BuffDebuffConflict;
        if (count >= selected.Length) return Full;
        return Ok;
    }

    // The selected perk that a SeriesConflict or BuffDebuffConflict of `id` is with; 0 for none.
    public static int ConflictingPerk(int[] selected, int count, int id)
    {
        int group = PerkCatalog.Group(id);
        for (int i = 0; i < count; i++)
            if (selected[i] != id && PerkCatalog.Group(selected[i]) == group) return selected[i];
        for (int i = 0; i < count; i++)
            if (OppositeOnSameAbility(selected[i], id)) return selected[i];
        return 0;
    }

    // True when the two perks change one ability in opposite directions.
    public static bool OppositeOnSameAbility(int a, int b)
    {
        if (PerkCatalog.Sign(a) == PerkCatalog.Sign(b)) return false;
        int countA = PerkCatalog.EffectCount(a);
        int countB = PerkCatalog.EffectCount(b);
        for (int i = 0; i < countA; i++)
        {
            int ability = PerkCatalog.EffectAbility(a, i);
            for (int j = 0; j < countB; j++)
                if (PerkCatalog.EffectAbility(b, j) == ability) return true;
        }
        return false;
    }

    // Rebuilds a received selection into `into` (same rules as adding one by one, in order):
    // unknown, unimplemented, duplicate and conflicting ids are dropped. Returns the count.
    public static int Sanitize(int[] source, int[] into)
    {
        int count = 0;
        if (source == null) return 0;
        for (int i = 0; i < source.Length; i++)
        {
            if (CheckAdd(into, count, source[i]) != Ok) continue;
            into[count] = source[i];
            count++;
        }
        return count;
    }

    // Whether a selection obeys every rule (used by tests and after receiving).
    public static bool IsValidSelection(int[] selected, int count)
    {
        if (count < 0 || count > selected.Length) return false;
        for (int i = 0; i < count; i++)
        {
            int id = selected[i];
            if (!PerkCatalog.IsKnown(id) || !PerkCatalog.IsImplemented(id)) return false;
            for (int j = i + 1; j < count; j++)
            {
                if (selected[j] == id) return false;
                if (PerkCatalog.Group(selected[j]) == PerkCatalog.Group(id)) return false;
                if (OppositeOnSameAbility(selected[j], id)) return false;
            }
        }
        return true;
    }

    public static string ResultText(int result, int id, int other)
    {
        string name = PerkCatalog.Name(id);
        switch (result)
        {
            case Ok: return "";
            case UnknownPerk: return "Unknown perk id " + id + ".";
            case NotImplemented: return name + " is not implemented yet.";
            case Duplicate: return name + " is already selected.";
            case SeriesConflict: return "Cannot add " + name + ": it conflicts with " + PerkCatalog.Name(other) + " (same series).";
            case BuffDebuffConflict: return "Cannot add " + name + ": it conflicts with " + PerkCatalog.Name(other) + " (buff and debuff of the same ability).";
            case NotEditable: return "Only the player who owns these perks can change them.";
            case NotSelected: return name + " is not selected.";
            case Full: return "The selection array is full.";
        }
        return "Rejected (" + result + ").";
    }
}
