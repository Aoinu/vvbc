using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.Udon;

// First / second / third base diamonds (BaseRunnerDisplay.prefab;
// docs/development/automatic-umpire.md#count-and-bases). A base is lit while the MatchController
// holds a runner awarded to it (the RunnerState while live, the committed runners while dead);
// touching a base, or being near it, never lights it.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class BaseRunnerDisplayBehaviour : TsukimiBehaviour
{
    public MatchControllerBehaviour match;
    [Tooltip("Diamonds of 1B, 2B, 3B.")]
    public Image[] bases = new Image[3];
    public Color occupied = new Color(1f, 0.85f, 0.2f, 1f);
    public Color empty = new Color(0.2f, 0.2f, 0.2f, 1f);

    private int _shownMask = -1;
    private int _refreshes;
    private bool _listening;

    public int GetRefreshCount() { return _refreshes; }
    public int GetShownMask() { return _shownMask; }

    void Start()
    {
        if (match != null && !_listening)
        {
            _listening = true;
            match.AddChangeListener((UdonBehaviour)GetComponent(typeof(UdonBehaviour)));
        }
        _OnMatchChanged();
    }

    public void _OnMatchChanged()
    {
        if (match == null) return;
        int mask = 0;
        if (match.IsMatchReady() && match.GetPhase() == MatchRules.PhaseInProgress)
            for (int b = 1; b <= 3; b++) if (match.GetRunnerOnBase(b) > 0) mask |= 1 << (b - 1);
        Show(mask);
    }

    // bit 0 = 1B, bit 1 = 2B, bit 2 = 3B. Only a change redraws.
    public void Show(int mask)
    {
        if (mask == _shownMask) return;
        _shownMask = mask;
        _refreshes++;
        for (int i = 0; i < bases.Length; i++) if (bases[i] != null) bases[i].color = (mask & (1 << i)) != 0 ? occupied : empty;
    }
}
