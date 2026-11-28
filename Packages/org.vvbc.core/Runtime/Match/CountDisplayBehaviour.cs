using TMPro;
using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.Udon;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class CountDisplayBehaviour : TsukimiBehaviour
{
    public MatchControllerBehaviour match;

    [Header("Appearance")]
    public Color backgroundColor = new Color(16f / 255f, 30f / 255f, 41f / 255f);
    public Color textColor = new Color(244f / 255f, 248f / 255f, 250f / 255f);
    public Color ballOn = new Color(85f / 255f, 216f / 255f, 122f / 255f);
    public Color strikeOn = new Color(245f / 255f, 204f / 255f, 85f / 255f);
    public Color outOn = new Color(240f / 255f, 109f / 255f, 101f / 255f);
    public Color lampOff = new Color(39f / 255f, 59f / 255f, 70f / 255f);
    [Min(1f)] public float labelFontSize = 62f;

    [Header("Elements")]
    public Image background;
    public TextMeshProUGUI[] labels = new TextMeshProUGUI[3];
    public Image[] ballLamps = new Image[3];
    public Image[] strikeLamps = new Image[2];
    public Image[] outLamps = new Image[2];

    private int _balls = -1, _strikes = -1, _outs = -1;
    private int _refreshes;

    public int GetRefreshCount() { return _refreshes; }
    public int GetShownBalls() { return _balls; }
    public int GetShownStrikes() { return _strikes; }
    public int GetShownOuts() { return _outs; }

    void Start()
    {
        ApplyStyle();
        if (match != null) match.AddChangeListener((UdonBehaviour)(Component)this);
        _OnMatchChanged();
    }

    public void _OnMatchChanged()
    {
        bool active = match != null && match.IsMatchReady() && match.GetPhase() == MatchRules.PhaseInProgress;
        Show(active ? match.GetBalls() : 0, active ? match.GetStrikes() : 0, active ? match.GetOuts() : 0);
    }

    public void ApplyStyle()
    {
        if (background != null) background.color = backgroundColor;
        foreach (TextMeshProUGUI label in labels)
        {
            if (label == null) continue;
            label.color = textColor;
            label.fontSize = labelFontSize;
        }
    }

    public void Show(int balls, int strikes, int outs)
    {
        balls = Mathf.Clamp(balls, 0, 3);
        strikes = Mathf.Clamp(strikes, 0, 2);
        outs = Mathf.Clamp(outs, 0, 2);
        if (balls == _balls && strikes == _strikes && outs == _outs) return;
        if (balls != _balls) Light(ballLamps, balls, ballOn);
        if (strikes != _strikes) Light(strikeLamps, strikes, strikeOn);
        if (outs != _outs) Light(outLamps, outs, outOn);
        _balls = balls;
        _strikes = strikes;
        _outs = outs;
        _refreshes++;
    }

    private void Light(Image[] lamps, int lit, Color on)
    {
        for (int i = 0; i < lamps.Length; i++)
        {
            Image lamp = lamps[i];
            Color color = i < lit ? on : lampOff;
            if (lamp != null && lamp.color != color) lamp.color = color;
        }
    }
}
