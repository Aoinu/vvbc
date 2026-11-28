using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.Udon;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class BaseRunnerDisplayBehaviour : TsukimiBehaviour
{
    public MatchControllerBehaviour match;

    [Header("Appearance")]
    public Color backgroundColor = new Color(16f / 255f, 30f / 255f, 41f / 255f);
    public Color occupied = new Color(1f, 211f / 255f, 110f / 255f);
    public Color empty = new Color(39f / 255f, 59f / 255f, 70f / 255f);
    public Color outlineColor = new Color(54f / 255f, 79f / 255f, 94f / 255f);

    [Header("Elements")]
    public Image background;
    [Tooltip("Diamond fills in 1B, 2B, 3B order.")]
    public Image[] bases = new Image[3];
    public Image[] outlines = new Image[3];

    private int _shownMask = -1;
    private int _refreshes;

    public int GetRefreshCount() { return _refreshes; }
    public int GetShownMask() { return _shownMask; }

    void Start()
    {
        ApplyStyle();
        if (match != null) match.AddChangeListener((UdonBehaviour)(Component)this);
        _OnMatchChanged();
    }

    public void _OnMatchChanged()
    {
        int mask = 0;
        if (match != null && match.IsMatchReady() && match.GetPhase() == MatchRules.PhaseInProgress)
            for (int b = 1; b <= 3; b++)
                if (match.GetRunnerOnBase(b) > 0) mask |= 1 << (b - 1);
        Show(mask);
    }

    public void ApplyStyle()
    {
        if (background != null) background.color = backgroundColor;
        foreach (Image outline in outlines)
            if (outline != null) outline.color = outlineColor;
    }

    // Bit 0 = 1B, bit 1 = 2B, bit 2 = 3B.
    public void Show(int mask)
    {
        if (mask == _shownMask) return;
        _shownMask = mask;
        _refreshes++;
        for (int i = 0; i < bases.Length; i++)
        {
            Image diamond = bases[i];
            Color color = (mask & (1 << i)) != 0 ? occupied : empty;
            if (diamond != null && diamond.color != color) diamond.color = color;
        }
    }
}
