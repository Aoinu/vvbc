using TMPro;
using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.Udon;

// Inning-by-inning back-screen scoreboard (docs/development/automatic-umpire.md#inning-scoreboard),
// InningScoreboard.prefab: a RectTransform root without a Canvas, placed and scaled inside a
// parent world-space Canvas (a future BackScreenCanvas.prefab).
//
// Display only. It reads the MatchController's effective state (CopyEffectiveState: the committed
// MatchState, with the live play's Delta while live) on _OnMatchChanged and never per frame. It
// keeps no match data of its own and syncs nothing; a late joiner, an Owner change, a recovery or
// a correction is drawn from whatever the controller holds at that event. While the controller is
// not ready (IsMatchReady) nothing is shown as a score.
//
// Layout (top-left design units, Inspector values): fixed board, team and R / H / E widths; the
// inning area is shared by 9..12 equal columns, 12 prepared in the prefab and switched on as the
// game needs them. Past 12 innings the board keeps 12 columns and shows the last 12 (the header
// numbers follow), logging one warning; the match data itself keeps every inning.
//
// Drawing is the same code at runtime and in the Editor preview (Draw on a MatchState array):
// each cell keeps the value it shows, and a TMP text, colour or RectTransform is written only when
// that value changes.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class InningScoreboardBehaviour : TsukimiBehaviour
{
    public const int MaxColumns = 12;
    public const int BaseColumns = 9;
    public const int TotalsCount = 3;           // R, H, E
    public const int CellBlank = -1;            // inning not reached, or nothing to show
    public const int CellX = -2;                // the Home half that was not needed
    private const int Unset = -100;
    private const int FirstGlyph = 32;          // glyphAdvances covers ' '..'~'
    private const int GlyphCount = 95;

    [Header("Match")]
    [Tooltip("The only source of the match state.")]
    public MatchControllerBehaviour match;
    [Tooltip("Away (first-batting) team name shown on the board.")]
    public string awayTeamName = "AWAY";
    [Tooltip("Home team name shown on the board.")]
    public string homeTeamName = "HOME";

    [Header("Layout (design units, top-left origin)")]
    [Tooltip("Size of the whole board.")]
    public Vector2 boardSize = new Vector2(1800f, 320f);
    [Tooltip("Left and right margin.")]
    public float marginX = 24f;
    [Tooltip("Top and bottom margin.")]
    public float marginY = 16f;
    public float teamColumnWidth = 360f;
    [Tooltip("Width shared by the 9..12 inning columns.")]
    public float inningAreaWidth = 1062f;
    [Tooltip("Gap between the innings and R / H / E.")]
    public float separatorWidth = 18f;
    [Tooltip("Width of each of R, H and E.")]
    public float totalsCellWidth = 104f;
    public float headerHeight = 64f;
    [Tooltip("Height of the Away row and of the Home row.")]
    public float rowHeight = 112f;
    [Tooltip("Left and right padding of the team names.")]
    public float teamNamePadding = 24f;
    [Tooltip("Left and right padding that a two-digit score must fit within in the narrowest inning column.")]
    public float cellPadding = 8f;
    [Tooltip("Header / rows, Away / Home, team / innings and innings / R H E lines.")]
    public float majorLineWidth = 3f;
    [Tooltip("Lines between inning columns (and between R, H and E).")]
    public float inningLineWidth = 1f;
    public float offenseBarWidth = 6f;
    public float offenseBarHeight = 88f;
    public float currentUnderlineHeight = 4f;

    [Header("Typography")]
    [Tooltip("TMP Font Asset applied by the Editor (Apply Layout); used at runtime only through the texts.")]
    public Object fontAsset;
    [Tooltip("Inning scores, R / H / E and X. Never auto-sized.")]
    public float numberFontSize = 66f;
    public float inningHeaderFontSize = 47f;
    public float totalsHeaderFontSize = 46f;
    [Tooltip("Largest team name size; both names share the size the wider one fits.")]
    public float maxNameFontSize = 64f;
    [Tooltip("Smallest shared team name size (a name wider than this overflows; it is never cut).")]
    public float minNameFontSize = 24f;
    [Tooltip("The shared team name size is rounded down to a multiple of this.")]
    public float nameFontSizeStep = 1f;
    [Tooltip("Advance of ' '..'~' per unit of font size, measured from the Font Asset by the Editor (Measure Font).")]
    public float[] glyphAdvances = new float[]
    {
        0.2f, 0.31094f, 0.35208f, 0.65104f, 0.50799f, 0.79497f, 0.65399f, 0.16892f, 0.32899f, 0.32899f, 0.38003f, 0.46198f, 0.23802f, 0.36997f, 0.24705f, 0.44392f,
        0.51094f, 0.31892f, 0.49896f, 0.48698f, 0.54392f, 0.48802f, 0.48802f, 0.45104f, 0.48993f, 0.48194f, 0.31892f, 0.27396f, 0.46198f, 0.46198f, 0.46198f, 0.47899f,
        0.78594f, 0.57708f, 0.54497f, 0.53594f, 0.54705f, 0.51094f, 0.48993f, 0.53993f, 0.55399f, 0.24601f, 0.51806f, 0.55799f, 0.49896f, 0.63108f, 0.58993f, 0.54705f,
        0.53108f, 0.52708f, 0.54306f, 0.51997f, 0.52604f, 0.55399f, 0.55503f, 0.78299f, 0.55295f, 0.54601f, 0.48507f, 0.38108f, 0.44392f, 0.38108f, 0.44792f, 0.47205f,
        0.22604f, 0.48698f, 0.50503f, 0.48594f, 0.50503f, 0.49201f, 0.34201f, 0.49792f, 0.49705f, 0.23698f, 0.23507f, 0.49097f, 0.22708f, 0.75503f, 0.49705f, 0.50295f,
        0.50799f, 0.50799f, 0.35399f, 0.45694f, 0.33507f, 0.49601f, 0.47795f, 0.69792f, 0.48802f, 0.46406f, 0.41597f, 0.36007f, 0.19392f, 0.36007f, 0.51198f
    };

    [Header("Colors")]
    public Color backgroundColor = new Color(0.039216f, 0.074510f, 0.105882f, 1f);        // #0A131B
    public Color textColor = new Color(0.956863f, 0.972549f, 0.980392f, 1f);              // #F4F8FA
    public Color headerTextColor = new Color(0.686275f, 0.756863f, 0.803922f, 1f);        // #AFC1CD
    public Color runsColor = new Color(1f, 0.827451f, 0.431373f, 1f);                     // #FFD36E
    public Color offenseNameColor = new Color(1f, 0.827451f, 0.431373f, 1f);              // #FFD36E
    public Color offenseBarColor = new Color(1f, 0.827451f, 0.431373f, 1f);               // #FFD36E
    public Color currentInningTextColor = new Color(0.427451f, 0.850980f, 0.960784f, 1f); // #6DD9F5
    public Color currentInningBackgroundColor = new Color(0.145098f, 0.305882f, 0.376471f, 1f); // #254E60
    public Color currentInningUnderlineColor = new Color(0.329412f, 0.784314f, 0.905882f, 1f);  // #54C8E7
    public Color lineColor = new Color(0.211765f, 0.309804f, 0.368627f, 1f);              // #364F5E

    [Header("Elements (set by the prefab)")]
    public RectTransform board;
    public Image background;
    public TextMeshProUGUI awayName;
    public TextMeshProUGUI homeName;
    public Image awayOffenseBar;
    public Image homeOffenseBar;
    [Tooltip("Inning columns (header and both cells), left to right.")]
    public RectTransform[] inningColumns = new RectTransform[MaxColumns];
    public TextMeshProUGUI[] inningHeaders = new TextMeshProUGUI[MaxColumns];
    public TextMeshProUGUI[] awayInnings = new TextMeshProUGUI[MaxColumns];
    public TextMeshProUGUI[] homeInnings = new TextMeshProUGUI[MaxColumns];
    [Tooltip("Lines between inning columns (MaxColumns - 1).")]
    public Image[] inningLines = new Image[MaxColumns - 1];
    public Image currentInningBackground;
    public Image currentInningUnderline;
    [Tooltip("R, H, E headers.")]
    public TextMeshProUGUI[] totalsHeaders = new TextMeshProUGUI[TotalsCount];
    [Tooltip("Away R, H, E.")]
    public TextMeshProUGUI[] awayTotals = new TextMeshProUGUI[TotalsCount];
    [Tooltip("Home R, H, E.")]
    public TextMeshProUGUI[] homeTotals = new TextMeshProUGUI[TotalsCount];
    [Tooltip("Lines between R, H and E (TotalsCount - 1).")]
    public Image[] totalsLines = new Image[TotalsCount - 1];
    public Image headerLine;
    public Image rowLine;
    public Image teamLine;
    public Image totalsLine;

    // What the board shows now: a value per cell, written to its text only when it changes.
    private int[] _state = new int[MatchRules.Length];
    private int[] _headerCodes = new int[] { Unset, Unset, Unset, Unset, Unset, Unset, Unset, Unset, Unset, Unset, Unset, Unset };
    private int[] _awayCodes = new int[] { Unset, Unset, Unset, Unset, Unset, Unset, Unset, Unset, Unset, Unset, Unset, Unset };
    private int[] _homeCodes = new int[] { Unset, Unset, Unset, Unset, Unset, Unset, Unset, Unset, Unset, Unset, Unset, Unset };
    private int[] _totalCodes = new int[] { Unset, Unset, Unset, Unset, Unset, Unset };
    private int _columns = Unset;
    private int _firstInning = 1;
    private int _currentColumn = Unset;
    private int _highlightColumn = Unset;
    private int _offense = Unset;
    private float _nameFontSize;
    private int _overflowInning;
    private int _reportedOverflow;
    private int _draws;
    private int _textWrites;
    private bool _listening;

    public int GetShownColumns() { return _columns; }
    public int GetFirstShownInning() { return _firstInning; }
    public int GetCurrentColumn() { return _currentColumn; }
    public int GetOffenseShown() { return _offense; }
    public float GetNameFontSize() { return _nameFontSize; }
    public int GetOverflowInning() { return _overflowInning; }
    public int GetDrawCount() { return _draws; }
    public int GetTextWriteCount() { return _textWrites; }
    public int GetHeaderCode(int column) { return column >= 0 && column < MaxColumns ? _headerCodes[column] : Unset; }
    public int GetInningCode(int team, int column)
    {
        if (column < 0 || column >= MaxColumns) return Unset;
        return team == RosterRules.TeamHome ? _homeCodes[column] : _awayCodes[column];
    }
    // k: 0 R, 1 H, 2 E.
    public int GetTotalCode(int team, int k) { return k >= 0 && k < TotalsCount ? _totalCodes[(team == RosterRules.TeamHome ? TotalsCount : 0) + k] : Unset; }

    void Start()
    {
        ApplyLayout();
        ApplyNames();
        if (match != null && !_listening)
        {
            _listening = true;
            match.AddChangeListener((UdonBehaviour)(Component)this);
        }
        _OnMatchChanged();
    }

    // Initial draw, every commit, live ruling, correction, recovery and received state.
    public void _OnMatchChanged()
    {
        bool ready = match != null && match.IsMatchReady() && match.CopyEffectiveState(_state);
        int toShow = ready ? match.GetInningsToShow() : BaseColumns;
        Draw(_state, ready, toShow);
        ReportOverflow();
    }

    // ---- Pure layout and text measurement ----

    // Columns shown: at least nine, at most twelve.
    public static int ColumnsFor(int toShow) { return Mathf.Clamp(toShow, BaseColumns, MaxColumns); }
    // First inning shown, so that the current inning is always on the board.
    public static int FirstInningFor(int current, int columns) { return current > columns ? current - columns + 1 : 1; }
    public static float InningColumnWidth(float areaWidth, int columns) { return columns > 0 ? areaWidth / columns : 0f; }

    public float InningsLeft() { return marginX + teamColumnWidth; }
    public float TotalsLeft() { return InningsLeft() + inningAreaWidth + separatorWidth; }
    public float TotalsRight() { return TotalsLeft() + totalsCellWidth * TotalsCount; }
    public float InningColumnX(int columns, int column) { return InningsLeft() + InningColumnWidth(inningAreaWidth, columns) * column; }

    // Width of text at size, from the measured glyph advances (a character outside the table
    // counts as 'W', the widest letter).
    public float TextWidth(string text, float size)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        float fallback = AdvanceOf('W');
        float em = 0f;
        for (int i = 0; i < text.Length; i++)
        {
            int c = text[i];
            int index = c - FirstGlyph;
            em += index >= 0 && index < GlyphCount && index < glyphAdvances.Length ? glyphAdvances[index] : fallback;
        }
        return em * size;
    }

    private float AdvanceOf(int c)
    {
        int index = c - FirstGlyph;
        return index >= 0 && index < glyphAdvances.Length ? glyphAdvances[index] : 0.6f;
    }

    // Widest two-digit score at size ("10".."99").
    public float WidestTwoDigitWidth(float size)
    {
        float widest = 0f;
        for (int d = '0'; d <= '9'; d++) widest = Mathf.Max(widest, AdvanceOf(d));
        float tens = 0f;
        for (int d = '1'; d <= '9'; d++) tens = Mathf.Max(tens, AdvanceOf(d));
        return (tens + widest) * size;
    }

    // Width a score may take in the narrowest (twelve-column) inning cell.
    public float NarrowestScoreSpace() { return InningColumnWidth(inningAreaWidth, MaxColumns) - 2f * cellPadding; }

    // One size for both names: the largest at which the wider name fits, within the limits.
    public float SharedNameFontSize(string away, string home)
    {
        float space = teamColumnWidth - 2f * teamNamePadding;
        float widest = Mathf.Max(TextWidth(away, 1f), TextWidth(home, 1f));
        float size = maxNameFontSize;
        if (widest > 0f && space > 0f) size = Mathf.Min(size, space / widest);
        if (nameFontSizeStep > 0f) size = Mathf.Floor(size / nameFontSizeStep + 0.0001f) * nameFontSizeStep;
        return Mathf.Clamp(size, minNameFontSize, maxNameFontSize);
    }

    // ---- Drawing (runtime and Editor preview) ----

    public void ApplyNames()
    {
        _nameFontSize = SharedNameFontSize(awayTeamName, homeTeamName);
        if (awayName != null) { awayName.text = awayTeamName; awayName.fontSize = _nameFontSize; }
        if (homeName != null) { homeName.text = homeTeamName; homeName.fontSize = _nameFontSize; }
    }

    // m: a MatchState (MatchRules layout); ready: m is the controller's state; toShow: innings
    // the game needs on a board (GetInningsToShow).
    public void Draw(int[] m, bool ready, int toShow)
    {
        _draws++;
        int phase = ready ? m[MatchRules.Phase] : MatchRules.PhaseNotStarted;
        bool started = phase != MatchRules.PhaseNotStarted;
        int current = started ? Mathf.Clamp(m[MatchRules.Inning], 1, MatchRules.MaxInnings) : 1;
        int columns = ColumnsFor(started ? Mathf.Max(toShow, current) : BaseColumns);
        int first = FirstInningFor(current, columns);
        CheckOverflow(started ? Mathf.Max(toShow, current) : 0);
        if (columns != _columns)
        {
            _columns = columns;
            LayoutColumns(columns);
        }
        _firstInning = first;

        bool inProgress = phase == MatchRules.PhaseInProgress;
        bool homeNotNeeded = phase == MatchRules.PhaseEnded && m[MatchRules.Half] == MatchRules.HalfTop
            && m[MatchRules.Result] == MatchRules.ResultHomeWin
            && MatchRules.TeamRuns(m, RosterRules.TeamHome) > MatchRules.TeamRuns(m, RosterRules.TeamAway);
        for (int c = 0; c < columns; c++)
        {
            int inning = first + c;
            SetCode(inningHeaders, _headerCodes, c, inning);
            SetCode(awayInnings, _awayCodes, c, started ? RunsCode(m, RosterRules.TeamAway, inning) : CellBlank);
            int home = !started ? CellBlank : homeNotNeeded && inning == current ? CellX : RunsCode(m, RosterRules.TeamHome, inning);
            SetCode(homeInnings, _homeCodes, c, home);
        }
        ShowCurrentInning(inProgress ? current - first : -1, columns);
        for (int team = 0; team < RosterRules.TeamCount; team++)
        {
            int at = team == RosterRules.TeamHome ? TotalsCount : 0;
            TextMeshProUGUI[] cells = team == RosterRules.TeamHome ? homeTotals : awayTotals;
            SetCode(cells, _totalCodes, at, 0, started ? MatchRules.TeamRuns(m, team) : CellBlank);
            SetCode(cells, _totalCodes, at, 1, started ? m[MatchRules.Hits + team] : CellBlank);
            SetCode(cells, _totalCodes, at, 2, started ? m[MatchRules.Errors + team] : CellBlank);
        }
        ShowOffense(inProgress ? MatchRules.OffenseTeam(m) : -1);
    }

    // A half-inning not reached yet is blank; one reached shows its runs, 0 included.
    private static int RunsCode(int[] m, int team, int inning)
    {
        if (!MatchRules.InningReached(m, team, inning)) return CellBlank;
        return MatchRules.RunsIn(m, team, inning);
    }

    public static string CodeText(int code)
    {
        if (code == CellX) return "X";
        if (code < 0) return "";
        return code.ToString();
    }

    private void SetCode(TextMeshProUGUI[] cells, int[] shown, int index, int code)
    {
        if (shown[index] == code) return;
        shown[index] = code;
        _textWrites++;
        if (index < cells.Length && cells[index] != null) cells[index].text = CodeText(code);
    }

    private void SetCode(TextMeshProUGUI[] cells, int[] shown, int offset, int index, int code)
    {
        if (shown[offset + index] == code) return;
        shown[offset + index] = code;
        _textWrites++;
        if (index < cells.Length && cells[index] != null) cells[index].text = CodeText(code);
    }

    // Past twelve innings: twelve columns stay and the last twelve are shown (GetOverflowInning).
    private void CheckOverflow(int needed)
    {
        _overflowInning = needed > MaxColumns ? needed : 0;
    }

    // One warning each time the board starts to need more columns than it has.
    public void ReportOverflow()
    {
        if (_overflowInning == _reportedOverflow) return;
        _reportedOverflow = _overflowInning;
        if (_overflowInning > 0)
            Debug.LogWarning("[VVBC Scoreboard] inning " + _overflowInning + " needs more than " + MaxColumns
                + " columns; the board keeps " + MaxColumns + " and shows innings " + _firstInning + "-" + (_firstInning + MaxColumns - 1));
    }

    // Header colour, background and underline of the current inning (column -1: none).
    private void ShowCurrentInning(int column, int columns)
    {
        if (column < 0 || column >= columns) column = -1;
        if (column != _currentColumn)
        {
            if (_currentColumn >= 0 && _currentColumn < inningHeaders.Length && inningHeaders[_currentColumn] != null)
                inningHeaders[_currentColumn].color = headerTextColor;
            if (column >= 0 && column < inningHeaders.Length && inningHeaders[column] != null)
                inningHeaders[column].color = currentInningTextColor;
            _currentColumn = column;
        }
        int placed = column < 0 ? -1 : column + columns * MaxColumns;
        if (placed == _highlightColumn) return;
        _highlightColumn = placed;
        bool on = column >= 0;
        if (currentInningBackground != null)
        {
            currentInningBackground.gameObject.SetActive(on);
            if (on) PlaceCurrent(currentInningBackground.rectTransform, column, columns, marginY, headerHeight);
        }
        if (currentInningUnderline != null)
        {
            currentInningUnderline.gameObject.SetActive(on);
            if (on) PlaceCurrent(currentInningUnderline.rectTransform, column, columns, marginY + headerHeight - currentUnderlineHeight, currentUnderlineHeight);
        }
    }

    private void PlaceCurrent(RectTransform r, int column, int columns, float y, float height)
    {
        r.anchoredPosition = new Vector2(InningColumnX(columns, column), -y);
        r.sizeDelta = new Vector2(InningColumnWidth(inningAreaWidth, columns), height);
    }

    // The team at bat: its name in the offense colour and its bar (the row background never changes).
    private void ShowOffense(int team)
    {
        if (team == _offense) return;
        _offense = team;
        if (awayName != null) awayName.color = team == RosterRules.TeamAway ? offenseNameColor : textColor;
        if (homeName != null) homeName.color = team == RosterRules.TeamHome ? offenseNameColor : textColor;
        if (awayOffenseBar != null) awayOffenseBar.gameObject.SetActive(team == RosterRules.TeamAway);
        if (homeOffenseBar != null) homeOffenseBar.gameObject.SetActive(team == RosterRules.TeamHome);
    }

    // Only the inning columns and the lines between them move when the count changes.
    private void LayoutColumns(int columns)
    {
        float width = InningColumnWidth(inningAreaWidth, columns);
        float height = headerHeight + 2f * rowHeight;
        for (int c = 0; c < MaxColumns && c < inningColumns.Length; c++)
        {
            RectTransform column = inningColumns[c];
            if (column == null) continue;
            bool shown = c < columns;
            column.gameObject.SetActive(shown);
            if (!shown) continue;
            column.anchoredPosition = new Vector2(InningColumnX(columns, c), -marginY);
            column.sizeDelta = new Vector2(width, height);
        }
        for (int i = 0; i < inningLines.Length; i++)
        {
            Image line = inningLines[i];
            if (line == null) continue;
            bool shown = i < columns - 1;
            line.gameObject.SetActive(shown);
            if (!shown) continue;
            RectTransform r = line.rectTransform;
            r.anchoredPosition = new Vector2(InningColumnX(columns, i + 1) - inningLineWidth * 0.5f, -marginY);
            r.sizeDelta = new Vector2(inningLineWidth, height);
        }
    }

    // ---- Layout and style of everything (once at Start; the Editor's Apply Layout runs it too) ----

    public void ApplyLayout()
    {
        float rowsTop = marginY + headerHeight;
        float bodyHeight = headerHeight + 2f * rowHeight;
        float inningsLeft = InningsLeft();
        float totalsLeft = TotalsLeft();
        if (board != null) board.sizeDelta = boardSize;
        if (background != null)
        {
            Place(background.rectTransform, 0f, 0f, boardSize.x, boardSize.y);
            Style(background, backgroundColor);
        }

        // Team names and offense bars.
        float nameWidth = teamColumnWidth - 2f * teamNamePadding;
        PlaceText(awayName, marginX + teamNamePadding, rowsTop, nameWidth, rowHeight, maxNameFontSize, textColor, false);
        PlaceText(homeName, marginX + teamNamePadding, rowsTop + rowHeight, nameWidth, rowHeight, maxNameFontSize, textColor, false);
        float barX = marginX + (teamNamePadding - offenseBarWidth) * 0.5f;
        float barY = (rowHeight - offenseBarHeight) * 0.5f;
        PlaceImage(awayOffenseBar, barX, rowsTop + barY, offenseBarWidth, offenseBarHeight, offenseBarColor);
        PlaceImage(homeOffenseBar, barX, rowsTop + rowHeight + barY, offenseBarWidth, offenseBarHeight, offenseBarColor);

        // Inning cells inside their columns (the columns themselves move in LayoutColumns).
        for (int c = 0; c < MaxColumns; c++)
        {
            if (c < inningHeaders.Length) PlaceInColumn(inningHeaders[c], 0f, headerHeight, inningHeaderFontSize, headerTextColor);
            if (c < awayInnings.Length) PlaceInColumn(awayInnings[c], headerHeight, rowHeight, numberFontSize, textColor);
            if (c < homeInnings.Length) PlaceInColumn(homeInnings[c], headerHeight + rowHeight, rowHeight, numberFontSize, textColor);
        }
        for (int i = 0; i < inningLines.Length; i++) if (inningLines[i] != null) Style(inningLines[i], lineColor);
        if (currentInningBackground != null) Style(currentInningBackground, currentInningBackgroundColor);
        if (currentInningUnderline != null) Style(currentInningUnderline, currentInningUnderlineColor);

        // R / H / E.
        for (int k = 0; k < TotalsCount; k++)
        {
            float x = totalsLeft + totalsCellWidth * k;
            Color value = k == 0 ? runsColor : textColor;
            if (k < totalsHeaders.Length) PlaceText(totalsHeaders[k], x, marginY, totalsCellWidth, headerHeight, totalsHeaderFontSize, headerTextColor, true);
            if (k < awayTotals.Length) PlaceText(awayTotals[k], x, rowsTop, totalsCellWidth, rowHeight, numberFontSize, value, true);
            if (k < homeTotals.Length) PlaceText(homeTotals[k], x, rowsTop + rowHeight, totalsCellWidth, rowHeight, numberFontSize, value, true);
        }
        for (int i = 0; i < totalsLines.Length; i++)
            PlaceImage(totalsLines[i], totalsLeft + totalsCellWidth * (i + 1) - inningLineWidth * 0.5f, marginY, inningLineWidth, bodyHeight, lineColor);

        // Major lines.
        float contentWidth = TotalsRight() - marginX;
        PlaceImage(headerLine, marginX, rowsTop - majorLineWidth * 0.5f, contentWidth, majorLineWidth, lineColor);
        PlaceImage(rowLine, marginX, rowsTop + rowHeight - majorLineWidth * 0.5f, contentWidth, majorLineWidth, lineColor);
        PlaceImage(teamLine, inningsLeft - majorLineWidth * 0.5f, marginY, majorLineWidth, bodyHeight, lineColor);
        PlaceImage(totalsLine, inningsLeft + inningAreaWidth + (separatorWidth - majorLineWidth) * 0.5f, marginY, majorLineWidth, bodyHeight, lineColor);

        // Everything that depends on the layout or the colours is placed again by the next Draw.
        _columns = Unset;
        _currentColumn = Unset;
        _highlightColumn = Unset;
        _offense = Unset;
    }

    // Top-left anchored rectangle at (x, y) design units from the board's top-left corner.
    private static void Place(RectTransform r, float x, float y, float width, float height)
    {
        if (r == null) return;
        r.anchorMin = new Vector2(0f, 1f);
        r.anchorMax = new Vector2(0f, 1f);
        r.pivot = new Vector2(0f, 1f);
        r.anchoredPosition = new Vector2(x, -y);
        r.sizeDelta = new Vector2(width, height);
    }

    private static void Style(Image image, Color color)
    {
        image.color = color;
        image.raycastTarget = false;
    }

    private static void PlaceImage(Image image, float x, float y, float width, float height, Color color)
    {
        if (image == null) return;
        Place(image.rectTransform, x, y, width, height);
        Style(image, color);
    }

    private static void StyleText(TextMeshProUGUI text, float size, Color color, bool midline)
    {
        text.fontSize = size;
        text.color = color;
        text.enableAutoSizing = false;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Overflow;
        text.alignment = midline ? TextAlignmentOptions.Midline : TextAlignmentOptions.Center;
        text.raycastTarget = false;
    }

    private static void PlaceText(TextMeshProUGUI text, float x, float y, float width, float height, float size, Color color, bool midline)
    {
        if (text == null) return;
        Place(text.rectTransform, x, y, width, height);
        StyleText(text, size, color, midline);
    }

    // A cell spanning its column's width, y below the column's top.
    private static void PlaceInColumn(TextMeshProUGUI text, float y, float height, float size, Color color)
    {
        if (text == null) return;
        RectTransform r = text.rectTransform;
        r.anchorMin = new Vector2(0f, 1f);
        r.anchorMax = new Vector2(1f, 1f);
        r.pivot = new Vector2(0.5f, 1f);
        r.anchoredPosition = new Vector2(0f, -y);
        r.sizeDelta = new Vector2(0f, height);
        StyleText(text, size, color, true);
    }
}
