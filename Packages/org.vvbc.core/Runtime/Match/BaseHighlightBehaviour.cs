using Tsukimi;
using UnityEngine;

// Shows which bases are being touched (docs/development/player-event-collector.md). Every client
// combines the current contact bits of every PlayerObject's collector: the local player's at once,
// the others' as they arrive. A base stays lit while anyone still touches it. A late joiner sees
// the current contact from the collectors' synced state; past touch events are not replayed.
// A physical contact only: it says nothing about who holds the base by the rules. Local; no sync.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class BaseHighlightBehaviour : TsukimiBehaviour
{
    public const int MaxCollectors = 96;

    [Tooltip("Highlight object of Home, 1B, 2B, 3B (the base's Highlight child). Shown while touched.")]
    public GameObject[] highlights = new GameObject[ContactRules.BaseCount];

    private readonly PlayerEventCollectorBehaviour[] _collectors = new PlayerEventCollectorBehaviour[MaxCollectors];
    private int _collectorCount;
    private readonly int[] _touching = new int[ContactRules.BaseCount];
    private int _shownMask = -1;

    void Start()
    {
        Apply(0);
    }

    public void RegisterCollector(PlayerEventCollectorBehaviour collector)
    {
        if (collector == null) return;
        int free = -1;
        for (int i = 0; i < _collectorCount; i++)
        {
            if (_collectors[i] == collector) return;
            if (free < 0 && _collectors[i] == null) free = i;
        }
        if (free >= 0) _collectors[free] = collector;
        else if (_collectorCount < MaxCollectors) _collectors[_collectorCount++] = collector;
        Refresh();
    }

    // Recomputed from every collector's current bits (cheap: only on a change).
    public void Refresh()
    {
        for (int b = 0; b < ContactRules.BaseCount; b++) _touching[b] = 0;
        for (int i = 0; i < _collectorCount; i++)
        {
            PlayerEventCollectorBehaviour c = _collectors[i];
            if (c == null) continue;
            AddMask(c.GetTouchMask());
        }
        Apply(MaskOfCounts());
    }

    // A PlayerObject leaving with its player (its collector's OnDisable) stops counting at once.
    public void RemoveCollector(PlayerEventCollectorBehaviour collector)
    {
        for (int i = 0; i < _collectorCount; i++) if (_collectors[i] == collector) _collectors[i] = null;
        Refresh();
    }

    public void AddMask(int mask)
    {
        for (int b = 0; b < ContactRules.BaseCount; b++) if ((mask & (1 << b)) != 0) _touching[b]++;
    }

    public int GetTouchingCount(int baseIndex) { return ContactRules.ValidBase(baseIndex) ? _touching[baseIndex] : 0; }

    public int MaskOfCounts()
    {
        int mask = 0;
        for (int b = 0; b < ContactRules.BaseCount; b++) if (_touching[b] > 0) mask |= 1 << b;
        return mask;
    }

    public void ClearCounts()
    {
        for (int b = 0; b < ContactRules.BaseCount; b++) _touching[b] = 0;
    }

    public int GetShownMask() { return _shownMask; }

    public void Apply(int mask)
    {
        if (mask == _shownMask) return;
        _shownMask = mask;
        if (highlights == null) return;
        for (int b = 0; b < ContactRules.BaseCount && b < highlights.Length; b++)
        {
            GameObject h = highlights[b];
            bool on = (mask & (1 << b)) != 0;
            if (h != null && h.activeSelf != on) h.SetActive(on);
        }
    }
}
