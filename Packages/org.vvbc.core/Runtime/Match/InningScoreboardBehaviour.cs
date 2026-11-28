using TMPro;
using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.Udon;

// Inning-by-inning scoreboard (docs/development/automatic-umpire.md#scoreboard), a world-space
// Canvas of InningScoreboard.prefab. It shows the MatchController's state only (the effective
// state while live, the committed one while dead), redrawn on _OnMatchChanged when something it
// shows changed: never per frame, and never from a contact or a call.
//
// Layout: two rows (Away, Home), a fixed total width with fixed team and R / H / E columns, and
// MaxColumns inning columns sharing the rest equally: nine by default, up to twelve when the game
// needs them. Past twelve innings the last twelve are shown (the header numbers follow), so the
// inning being played never leaves the board; the match data itself keeps every inning.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class InningScoreboardBehaviour : TsukimiBehaviour
{
    public const int MaxColumns = 12;
    public const int BaseColumns = 9;

    [Header("References")]
    public MatchControllerBehaviour match;
    [Tooltip("Team names (Away, Home).")]
    public TextMeshProUGUI awayName;
    public TextMeshProUGUI homeName;
    [Tooltip("Inning header cells, left to right (MaxColumns).")]
    public TextMeshProUGUI[] inningHeaders = new TextMeshProUGUI[MaxColumns];
    [Tooltip("Away runs per inning column (MaxColumns).")]
    public TextMeshProUGUI[] awayInnings = new TextMeshProUGUI[MaxColumns];
    [Tooltip("Home runs per inning column (MaxColumns).")]
    public TextMeshProUGUI[] homeInnings = new TextMeshProUGUI[MaxColumns];
    [Tooltip("Column containers (header and both cells), laid out across the inning area (MaxColumns).")]
    public RectTransform[] inningColumns = new RectTransform[MaxColumns];
    [Tooltip("The area the inning columns share.")]
    public RectTransform inningArea;
    public TextMeshProUGUI awayRuns, awayHits, awayErrors;
    public TextMeshProUGUI homeRuns, homeHits, homeErrors;
    [Tooltip("Thin label beside the team at bat (Away, Home).")]
    public GameObject awayOffenseLabel;
    public GameObject homeOffenseLabel;
    [Tooltip("Label marking the current inning's column (moved to it).")]
    public RectTransform currentInningLabel;

    [Header("Text")]
    public string awayTeamName = "AWAY";
    public string homeTeamName = "HOME";
    [Tooltip("Largest team name size; both names share the size the longer one fits.")]
    public float maxNameFontSize = 40f;
    [Tooltip("Smallest team name size.")]
    public float minNameFontSize = 14f;
    [Tooltip("Width of the team name column in canvas units (for the name size).")]
    public float nameColumnWidth = 220f;
    [Tooltip("Average glyph width as a fraction of the font size (for the name size).")]
    public float glyphWidthFactor = 0.62f;

    [Header("Colours")]
    public Color textColor = Color.white;
    public Color offenseNameColor = new Color(1f, 0.85f, 0.2f, 1f);
    public Color currentHeaderColor = new Color(1f, 0.85f, 0.2f, 1f);
    public Color runsColor = new Color(1f, 0.55f, 0.2f, 1f);

    // What the board shows now (redrawn only when one of these changes).
    private int _shownMatchRevision = -1;
    private int _shownPlayRevision = -1;
    private int _shownPlayId = -1;
    private int _firstInning = 1;
    private int _columns = BaseColumns;
    private int _refreshes;
    private bool _listening;

    public int GetRefreshCount() { return _refreshes; }
    public int GetFirstShownInning() { return _firstInning; }
    public int GetShownColumns() { return _columns; }

    void Start()
    {
        if (match != null && !_listening)
        {
            _listening = true;
            match.AddChangeListener((UdonBehaviour)GetComponent(typeof(UdonBehaviour)));
        }
        ApplyNames();
        _OnMatchChanged();
    }

    public void _OnMatchChanged()
    {
        if (match == null) return;
        int matchRevision = match.GetMatchRevision();
        int playRevision = match.GetPlayRevision();
        int playId = match.GetPlayId();
        if (matchRevision == _shownMatchRevision && playRevision == _shownPlayRevision && playId == _shownPlayId) return;
        _shownMatchRevision = matchRevision;
        _shownPlayRevision = playRevision;
        _shownPlayId = playId;
        Refresh();
    }

    // Columns shown for a game at inning 'current' with 'toShow' innings to show: at least nine,
    // at most twelve; the first column shown keeps the current inning on the board.
    public static int ColumnsFor(int toShow) { return Mathf.Clamp(toShow, BaseColumns, MaxColumns); }
    public static int FirstInningFor(int current, int columns) { return current > columns ? current - columns + 1 : 1; }

    // Both names share one size: the largest at which the longer name fits its column.
    public static float NameFontSize(string a, string b, float width, float glyphFactor, float maxSize, float minSize)
    {
        int longest = Mathf.Max(a != null ? a.Length : 0, b != null ? b.Length : 0);
        if (longest <= 0 || glyphFactor <= 0f) return maxSize;
        return Mathf.Clamp(width / (longest * glyphFactor), minSize, maxSize);
    }

    public void ApplyNames()
    {
        float size = NameFontSize(awayTeamName, homeTeamName, nameColumnWidth, glyphWidthFactor, maxNameFontSize, minNameFontSize);
        if (awayName != null) { awayName.text = awayTeamName; awayName.fontSize = size; }
        if (homeName != null) { homeName.text = homeTeamName; homeName.fontSize = size; }
    }

    private void Refresh()
    {
        _refreshes++;
        bool known = match.IsMatchReady();
        int current = known ? match.GetInning() : 1;
        int half = known ? match.GetHalf() : MatchRules.HalfTop;
        bool started = known && match.GetPhase() != MatchRules.PhaseNotStarted;
        int offense = started && match.GetPhase() == MatchRules.PhaseInProgress ? match.GetOffenseTeam() : -1;
        _columns = ColumnsFor(known ? match.GetInningsToShow() : BaseColumns);
        _firstInning = FirstInningFor(current, _columns);
        LayoutColumns(_columns);
        for (int c = 0; c < MaxColumns; c++)
        {
            int inning = _firstInning + c;
            bool shown = c < _columns;
            if (!shown) continue;
            bool isCurrent = started && inning == current;
            SetText(inningHeaders, c, inning.ToString(), isCurrent ? currentHeaderColor : textColor);
            SetText(awayInnings, c, RunsCell(started, inning, current, half, RosterRules.TeamAway), textColor);
            SetText(homeInnings, c, RunsCell(started, inning, current, half, RosterRules.TeamHome), textColor);
        }
        if (currentInningLabel != null)
        {
            int col = current - _firstInning;
            bool on = started && col >= 0 && col < _columns && inningColumns.Length > col && inningColumns[col] != null;
            currentInningLabel.gameObject.SetActive(on);
            if (on) currentInningLabel.anchoredPosition = new Vector2(inningColumns[col].anchoredPosition.x, currentInningLabel.anchoredPosition.y);
        }
        SetTotals(awayRuns, awayHits, awayErrors, started, RosterRules.TeamAway);
        SetTotals(homeRuns, homeHits, homeErrors, started, RosterRules.TeamHome);
        if (awayName != null) awayName.color = offense == RosterRules.TeamAway ? offenseNameColor : textColor;
        if (homeName != null) homeName.color = offense == RosterRules.TeamHome ? offenseNameColor : textColor;
        if (awayOffenseLabel != null) awayOffenseLabel.SetActive(offense == RosterRules.TeamAway);
        if (homeOffenseLabel != null) homeOffenseLabel.SetActive(offense == RosterRules.TeamHome);
    }

    // A half-inning not reached yet is blank; one reached shows its runs (0 included).
    private string RunsCell(bool started, int inning, int current, int half, int team)
    {
        if (!started || inning > current) return "";
        if (inning == current && team == RosterRules.TeamHome && half == MatchRules.HalfTop) return "";
        return match.GetInningRuns(team, inning).ToString();
    }

    private void SetTotals(TextMeshProUGUI runs, TextMeshProUGUI hits, TextMeshProUGUI errors, bool started, int team)
    {
        if (runs != null) { runs.text = started ? match.GetRuns(team).ToString() : ""; runs.color = runsColor; }
        if (hits != null) { hits.text = started ? match.GetHits(team).ToString() : ""; hits.color = textColor; }
        if (errors != null) { errors.text = started ? match.GetErrors(team).ToString() : ""; errors.color = textColor; }
    }

    private static void SetText(TextMeshProUGUI[] cells, int index, string value, Color color)
    {
        if (cells == null || index >= cells.Length || cells[index] == null) return;
        cells[index].text = value;
        cells[index].color = color;
    }

    // The inning columns share the inning area equally; columns beyond the count are hidden.
    private void LayoutColumns(int count)
    {
        float width = inningArea != null ? inningArea.rect.width : 0f;
        float step = count > 0 ? width / count : 0f;
        for (int c = 0; c < MaxColumns && c < inningColumns.Length; c++)
        {
            RectTransform column = inningColumns[c];
            if (column == null) continue;
            bool shown = c < count;
            column.gameObject.SetActive(shown);
            if (!shown || width <= 0f) continue;
            column.sizeDelta = new Vector2(step, column.sizeDelta.y);
            column.anchoredPosition = new Vector2(step * (c + 0.5f), column.anchoredPosition.y);
        }
    }
}
