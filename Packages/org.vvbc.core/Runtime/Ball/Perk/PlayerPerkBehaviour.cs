using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

// One player's perk selection (docs/development/player-perks.md), on the `Perks` child of
// VVBCPlayerObjectTemplate. The PlayerObject's owner is the only writer: they add, remove and
// clear perks here, and every other client only reads what arrives.
//
// Synced (Manual): the selected ids, in selection order, and a revision that grows with every
// committed change. Abilities are not synced: every client derives them from the ids with the
// shared catalog, and only the owner's client applies them to the game (PerkEffectsBehaviour),
// so a remote player's perks never change anything a second time here. Received selections
// older than or equal to the held revision are ignored; unknown, unimplemented or conflicting ids
// in a received selection are dropped by the same rules as a local add.
//
// A new PlayerObject starts empty; nothing is persisted, so a rejoin starts empty again.
//
// Listeners: the custom event `_OnPerksChanged` goes to every Behaviour in changeListeners and to
// those registered with AddChangeListener, after each local commit and each accepted selection.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class PlayerPerkBehaviour : TsukimiBehaviour
{
    public const string ChangedEventName = "_OnPerksChanged";
    // Sent after the applied values (ReportStat) changed, e.g. once a system became ready.
    public const string StatsChangedEventName = "_OnPerkStatsChanged";
    // Display state of a reported ability (ReportStat).
    public const int StatNotReported = 0;
    public const int StatApplied = 1;
    public const int StatUnavailable = 2;
    // Applied, but the system makes it ineffective now (control with penalty strength 0).
    public const int StatNoEffect = 3;

    [Tooltip("Behaviours that receive _OnPerksChanged (the PlayerObject's PerkEffects). Others register at runtime.")]
    public UdonBehaviour[] changeListeners = new UdonBehaviour[0];

    [Header("Control perk: error 1 sigma at control score 0 (m; sigma at 85 = 0.15 x), provisional")]
    public float controlSigmaAtZeroDebuff = 1.30f;
    public float controlSigmaAtZeroStandard = 0.90f;
    public float controlSigmaAtZeroI = 0.60f;
    public float controlSigmaAtZeroII = 0.40f;
    public float controlSigmaAtZeroIII = 0.18f;

    [UdonSynced] private int[] _syncedPerks = new int[0];
    [UdonSynced] private int _syncedRevision;

    private int[] _selected = new int[PerkRules.MaxSelected];
    private int _count;
    private int _revision;

    // Ability values of the current selection, recomputed only when it changes (index = ability).
    private float[] _factor = new float[PerkCatalog.AbilityCount + 1];
    private float[] _value = new float[PerkCatalog.AbilityCount + 1];
    private bool[] _has = new bool[PerkCatalog.AbilityCount + 1];
    // False until the first calculation: another Behaviour may read before this one's Start.
    private bool _calculated;

    // What the owner's PerkEffects actually applied (display only; never synced).
    private float[] _statBase = new float[PerkCatalog.AbilityCount + 1];
    private float[] _statFinal = new float[PerkCatalog.AbilityCount + 1];
    private int[] _statState = new int[PerkCatalog.AbilityCount + 1];
    private bool _statsDirty;

    void Start()
    {
        Recalculate();
        // The owner's empty start is a state too: it switches every perk effect off.
        if (Networking.IsOwner(gameObject)) Notify();
    }

    // ---- Editing (the owner only) ----

    public bool CanEdit()
    {
        return Networking.IsOwner(gameObject);
    }

    // PerkRules result; the selection changes only on Ok.
    public int TryAddPerk(int id)
    {
        if (!CanEdit()) return PerkRules.NotEditable;
        int result = PerkRules.CheckAdd(_selected, _count, id);
        if (result != PerkRules.Ok) return result;
        _selected[_count] = id;
        _count++;
        Commit();
        return PerkRules.Ok;
    }

    public int TryRemovePerk(int id)
    {
        if (!CanEdit()) return PerkRules.NotEditable;
        int index = PerkRules.IndexOf(_selected, _count, id);
        if (index < 0) return PerkRules.NotSelected;
        for (int i = index; i < _count - 1; i++) _selected[i] = _selected[i + 1];
        _count--;
        _selected[_count] = 0;
        Commit();
        return PerkRules.Ok;
    }

    public int ClearPerks()
    {
        if (!CanEdit()) return PerkRules.NotEditable;
        if (_count == 0) return PerkRules.Ok;
        for (int i = 0; i < _count; i++) _selected[i] = 0;
        _count = 0;
        Commit();
        return PerkRules.Ok;
    }

    private void Commit()
    {
        _revision++;
        int[] ids = new int[_count];
        for (int i = 0; i < _count; i++) ids[i] = _selected[i];
        _syncedPerks = ids;
        _syncedRevision = _revision;
        RequestSerialization();
        Recalculate();
        Notify();
    }

    // ---- Receiving ----

    public override void OnDeserialization()
    {
        if (Networking.IsOwner(gameObject)) return;
        ReceiveSelection(_syncedPerks, _syncedRevision);
    }

    // Accepts a selection newer than the held one. Returns whether it was taken.
    public bool ReceiveSelection(int[] ids, int revision)
    {
        if (revision <= _revision) return false;
        _count = PerkRules.Sanitize(ids, _selected);
        for (int i = _count; i < _selected.Length; i++) _selected[i] = 0;
        _revision = revision;
        Recalculate();
        Notify();
        return true;
    }

    // ---- Queries ----

    public bool HasPerk(int id) { return PerkRules.IndexOf(_selected, _count, id) >= 0; }
    public int GetSelectedCount() { return _count; }
    public int GetSelectedPerk(int index) { return index >= 0 && index < _count ? _selected[index] : 0; }
    public int GetRevision() { return _revision; }

    public int[] GetSelectedPerks()
    {
        int[] ids = new int[_count];
        for (int i = 0; i < _count; i++) ids[i] = _selected[i];
        return ids;
    }

    // What TryAddPerk would answer now for the rules (not for edit rights), and with what.
    public int CheckAdd(int id) { return PerkRules.CheckAdd(_selected, _count, id); }
    public int ConflictingPerk(int id) { return PerkRules.ConflictingPerk(_selected, _count, id); }

    // ---- Abilities of the selection (cached; see PerkStats) ----

    private void Recalculate()
    {
        _calculated = true;
        for (int ability = 1; ability <= PerkCatalog.AbilityCount; ability++)
        {
            _has[ability] = PerkStats.HasEffect(_selected, _count, ability);
            _factor[ability] = PerkStats.PercentFactor(_selected, _count, ability);
            _value[ability] = PerkStats.EffectValue(_selected, _count, ability);
        }
    }

    private static bool ValidAbility(int ability) { return ability >= 1 && ability <= PerkCatalog.AbilityCount; }

    // 1 + summed modifier of a percent ability (1 without perks).
    public float GetFactor(int ability)
    {
        if (!_calculated) Recalculate();
        return ValidAbility(ability) ? _factor[ability] : 1f;
    }
    public float GetModifier(int ability) { return GetFactor(ability) - 1f; }
    // Level or cap value of the selected stage (0 without).
    public float GetEffectValue(int ability)
    {
        if (!_calculated) Recalculate();
        return ValidAbility(ability) ? _value[ability] : 0f;
    }

    public bool HasEffect(int ability)
    {
        if (!_calculated) Recalculate();
        return ValidAbility(ability) && _has[ability];
    }
    public bool IsFlagOn(int ability) { return HasEffect(ability) && PerkCatalog.AbilityKind(ability) == PerkCatalog.KindFlag; }

    public float GetHomingRate()
    {
        float rate = GetEffectValue(PerkCatalog.AbilityHoming);
        return PerkStats.Finite(rate) ? Mathf.Clamp01(rate) : 0f;
    }

    public int GetFadeLevel()
    {
        float level = GetEffectValue(PerkCatalog.AbilityPitchFade);
        return PerkStats.Finite(level) ? Mathf.Clamp(Mathf.RoundToInt(level), 0, 2) : 0;
    }

    // The selected cap or level value, else the design standard (Ball Speed caps in km/h,
    // Batted Ball Speed km/h, gamma, contact width offset cm, early / late yaw degrees).
    public float GetValueOrStandard(int ability)
    {
        if (!HasEffect(ability)) return PerkCatalog.Standard(ability);
        float value = GetEffectValue(ability);
        return PerkStats.Finite(value) ? value : PerkCatalog.Standard(ability);
    }

    // Stage of Spin or Control: -1 debuff, 0 none, 1-3 I-III.
    public int GetStage(int ability)
    {
        if (!HasEffect(ability)) return 0;
        float value = GetEffectValue(ability);
        return PerkStats.Finite(value) ? Mathf.Clamp(Mathf.RoundToInt(value), -1, 3) : 0;
    }

    // The control error's 1 sigma at control score 0 (metres) for the selected Control stage,
    // from the tunable values above.
    public float GetControlSigmaAtZero()
    {
        int stage = GetStage(PerkCatalog.AbilityControl);
        float sigma = stage == -1 ? controlSigmaAtZeroDebuff : stage == 1 ? controlSigmaAtZeroI
            : stage == 2 ? controlSigmaAtZeroII : stage == 3 ? controlSigmaAtZeroIII : controlSigmaAtZeroStandard;
        return PerkStats.Finite(sigma) && sigma >= 0f ? sigma : PerkCatalog.ControlSigmaAtZero(stage);
    }

    // ---- Applied values (reported by the owner's PerkEffects, read by the UI) ----

    public void ReportStat(int ability, float baseValue, float finalValue, int state)
    {
        if (!ValidAbility(ability)) return;
        if (_statBase[ability] != baseValue || _statFinal[ability] != finalValue || _statState[ability] != state) _statsDirty = true;
        _statBase[ability] = baseValue;
        _statFinal[ability] = finalValue;
        _statState[ability] = state;
    }

    // Tells the listeners once that reported values changed since the last call.
    public void NotifyStatsChanged()
    {
        if (!_statsDirty) return;
        _statsDirty = false;
        for (int i = 0; i < changeListeners.Length; i++)
            if (Utilities.IsValid(changeListeners[i])) changeListeners[i].SendCustomEvent(StatsChangedEventName);
    }

    public float GetStatBase(int ability) { return ValidAbility(ability) ? _statBase[ability] : 0f; }
    public float GetStatFinal(int ability) { return ValidAbility(ability) ? _statFinal[ability] : 0f; }
    public int GetStatState(int ability) { return ValidAbility(ability) ? _statState[ability] : StatNotReported; }

    // ---- Listeners ----

    public void AddChangeListener(UdonBehaviour listener)
    {
        if (listener == null) return;
        for (int i = 0; i < changeListeners.Length; i++)
            if (changeListeners[i] == listener) return;
        for (int i = 0; i < changeListeners.Length; i++)
        {
            if (Utilities.IsValid(changeListeners[i])) continue;
            changeListeners[i] = listener;
            return;
        }
        UdonBehaviour[] grown = new UdonBehaviour[changeListeners.Length + 1];
        for (int i = 0; i < changeListeners.Length; i++) grown[i] = changeListeners[i];
        grown[changeListeners.Length] = listener;
        changeListeners = grown;
    }

    private void Notify()
    {
        for (int i = 0; i < changeListeners.Length; i++)
            if (Utilities.IsValid(changeListeners[i])) changeListeners[i].SendCustomEvent(ChangedEventName);
    }
}
