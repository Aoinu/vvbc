using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.Udon;

// Ball / strike / out lamps (CountDisplay.prefab; docs/development/automatic-umpire.md#count-and-bases).
// The MatchController's count only (effective while live, committed while dead), redrawn on
// _OnMatchChanged. A third strike still open, or the third out before the dead ball, lights every
// lamp of its row: the display never shows more lamps than it has, and never a count the state
// does not hold.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class CountDisplayBehaviour : TsukimiBehaviour
{
    public MatchControllerBehaviour match;
    [Tooltip("Ball lamps (three).")]
    public Image[] ballLamps = new Image[3];
    [Tooltip("Strike lamps (two).")]
    public Image[] strikeLamps = new Image[2];
    [Tooltip("Out lamps (two).")]
    public Image[] outLamps = new Image[2];
    public Color ballOn = new Color(0.2f, 0.9f, 0.3f, 1f);
    public Color strikeOn = new Color(1f, 0.8f, 0.1f, 1f);
    public Color outOn = new Color(1f, 0.2f, 0.2f, 1f);
    public Color lampOff = new Color(0.15f, 0.15f, 0.15f, 1f);

    private int _balls = -1, _strikes = -1, _outs = -1;
    private int _refreshes;
    private bool _listening;

    public int GetRefreshCount() { return _refreshes; }
    public int GetShownBalls() { return _balls; }
    public int GetShownStrikes() { return _strikes; }
    public int GetShownOuts() { return _outs; }

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
        bool known = match.IsMatchReady() && match.GetPhase() == MatchRules.PhaseInProgress;
        Show(known ? match.GetBalls() : 0, known ? match.GetStrikes() : 0, known ? match.GetOuts() : 0);
    }

    // Lit lamps per row, clamped to the lamps there are. Only a change redraws.
    public void Show(int balls, int strikes, int outs)
    {
        balls = Mathf.Clamp(balls, 0, ballLamps.Length);
        strikes = Mathf.Clamp(strikes, 0, strikeLamps.Length);
        outs = Mathf.Clamp(outs, 0, outLamps.Length);
        if (balls == _balls && strikes == _strikes && outs == _outs) return;
        _balls = balls; _strikes = strikes; _outs = outs;
        _refreshes++;
        Light(ballLamps, balls, ballOn);
        Light(strikeLamps, strikes, strikeOn);
        Light(outLamps, outs, outOn);
    }

    private void Light(Image[] lamps, int lit, Color on)
    {
        for (int i = 0; i < lamps.Length; i++) if (lamps[i] != null) lamps[i].color = i < lit ? on : lampOff;
    }
}
