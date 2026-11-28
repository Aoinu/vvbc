using Tsukimi;

// Layout, measurement and what the board shows for match states built by the engine's own rules
// (MatchRules, PlayRules for a live play, GameFlowRules for half-innings and the end of the game).
// The prefab's texts are not present here: what a cell shows is read back from the value it holds
// (GetInningCode / GetTotalCode, CellBlank and CellX included), which is what its text is set to.
public partial class InningScoreboardBehaviour
{
    private const int Away = RosterRules.TeamAway;
    private const int Home = RosterRules.TeamHome;

    private int[] InProgressForTest(int inning, int half)
    {
        int[] m = MatchRules.NewState();
        m[MatchRules.Revision] = 1;
        m[MatchRules.Phase] = MatchRules.PhaseInProgress;
        m[MatchRules.Inning] = inning;
        m[MatchRules.Half] = half;
        return m;
    }

    private static bool Near(float expected, float actual, float tolerance) { return UnityEngine.Mathf.Abs(expected - actual) <= tolerance; }

    private void DrawForTest(int[] m) { Draw(m, true, UnityEngine.Mathf.Max(9, m[MatchRules.Inning])); }

    // ---- Layout ----

    [TsukimiTest]
    public void BoardAreasAddUpToTheBaseSize()
    {
        Assert.AreEqual(384f, InningsLeft());
        Assert.AreEqual(1464f, TotalsLeft());
        Assert.AreEqual(1776f, TotalsRight());
        Assert.AreEqual(boardSize.x, TotalsRight() + marginX, "left margin, team, innings, separator, R / H / E, right margin");
        Assert.AreEqual(boardSize.y, 2f * marginY + headerHeight + 2f * rowHeight);
    }

    [TsukimiTest]
    public void OnlyTheInningColumnsChangeWidth()
    {
        Assert.AreEqual(118f, InningColumnWidth(inningAreaWidth, 9));
        Assert.IsTrue(Near(106.2f, InningColumnWidth(inningAreaWidth, 10), 0.0001f), "");
        Assert.IsTrue(Near(96.54545f, InningColumnWidth(inningAreaWidth, 11), 0.0001f), "");
        Assert.AreEqual(88.5f, InningColumnWidth(inningAreaWidth, 12));
        for (int columns = 9; columns <= 12; columns++)
        {
            Assert.AreEqual(384f, InningColumnX(columns, 0), "the innings start after the fixed team column");
            Assert.IsTrue(Near(1446f, InningColumnX(columns, columns), 0.001f), "the columns fill exactly the inning area");
            float width = InningColumnWidth(inningAreaWidth, columns);
            for (int c = 1; c < columns; c++) Assert.IsTrue(Near(width, InningColumnX(columns, c) - InningColumnX(columns, c - 1), 0.001f), "equal columns");
            Assert.AreEqual(1464f, TotalsLeft(), "R / H / E do not move");
        }
    }

    [TsukimiTest]
    public void NineToTwelveColumnsAndTheCurrentInningStaysOnTheBoard()
    {
        Assert.AreEqual(9, ColumnsFor(9));
        Assert.AreEqual(9, ColumnsFor(3), "at least the regulation nine");
        Assert.AreEqual(11, ColumnsFor(11), "extra innings only when needed");
        Assert.AreEqual(12, ColumnsFor(18), "never more than twelve");
        Assert.AreEqual(1, FirstInningFor(9, 9));
        Assert.AreEqual(1, FirstInningFor(12, 12));
        Assert.AreEqual(4, FirstInningFor(15, 12), "the last twelve innings past twelve");
    }

    // ---- Typography ----

    [TsukimiTest]
    public void TheWidestTwoDigitScoreFitsTheNarrowestColumn()
    {
        Assert.IsTrue(Near(72.5f, NarrowestScoreSpace(), 0.0001f), "88.5 minus the cell padding");
        Assert.IsTrue(Near(1.08784f * numberFontSize, WidestTwoDigitWidth(numberFontSize), 0.001f), "44 in Barlow Semi Condensed Bold");
        Assert.IsTrue(WidestTwoDigitWidth(numberFontSize) <= NarrowestScoreSpace(), "two digits fit twelve columns");
        Assert.IsTrue(WidestTwoDigitWidth(numberFontSize + 1f) > NarrowestScoreSpace(), "the size is the largest that fits");
        Assert.IsTrue(Near(TextWidth("44", numberFontSize), WidestTwoDigitWidth(numberFontSize), 0.0001f), "");
    }

    [TsukimiTest]
    public void BothTeamNamesShareTheSizeTheWiderFits()
    {
        Assert.AreEqual(64f, SharedNameFontSize("AWAY", "HOME"), "short names use the largest size");
        float shared = SharedNameFontSize("AWAY", "SUPERSTARS BASEBALL");
        Assert.IsTrue(shared < 64f, "a wider name shrinks both");
        Assert.AreEqual(shared, SharedNameFontSize("SUPERSTARS BASEBALL", "X"), "whichever team has it");
        Assert.IsTrue(TextWidth("SUPERSTARS BASEBALL", shared) <= 312f, "it fits between the paddings");
        Assert.IsTrue(TextWidth("SUPERSTARS BASEBALL", shared + 1f) > 312f, "and is the largest size that does");
        Assert.IsTrue(SharedNameFontSize("WWWWWWWWWWW", "H") < SharedNameFontSize("IIIIIIIIIII", "H"), "the drawn width decides, not the length");
        Assert.AreEqual(24f, SharedNameFontSize("A", "AVERYVERYVERYVERYLONGTEAMNAME"), "never below the smallest size");
        awayTeamName = "TOKYO";
        homeTeamName = "NORTHERN LIGHTS";
        ApplyNames();
        Assert.AreEqual(SharedNameFontSize("TOKYO", "NORTHERN LIGHTS"), GetNameFontSize());
    }

    [TsukimiTest]
    public void CellTextsAreScoresBlanksAndX()
    {
        Assert.AreEqual("0", CodeText(0));
        Assert.AreEqual("7", CodeText(7));
        Assert.AreEqual("99", CodeText(99));
        Assert.AreEqual("", CodeText(CellBlank));
        Assert.AreEqual("X", CodeText(CellX));
    }

    // ---- What the board shows ----

    [TsukimiTest]
    public void NothingIsShownBeforeTheMatchIsReady()
    {
        _OnMatchChanged();
        Assert.AreEqual(9, GetShownColumns());
        Assert.AreEqual(1, GetHeaderCode(0));
        Assert.AreEqual(9, GetHeaderCode(8));
        Assert.AreEqual(CellBlank, GetInningCode(Away, 0), "the controller is not ready: no score");
        Assert.AreEqual(CellBlank, GetTotalCode(Away, 0));
        Assert.AreEqual(-1, GetOffenseShown());
        Assert.AreEqual(-1, GetCurrentColumn());

        int[] m = MatchRules.NewState();
        m[MatchRules.Revision] = 1;
        Draw(m, true, 9);
        Assert.AreEqual(CellBlank, GetInningCode(Away, 0), "a match not started shows no score either");
        Assert.AreEqual(CellBlank, GetTotalCode(Home, 2));
        Assert.AreEqual(-1, GetOffenseShown());
    }

    [TsukimiTest]
    public void StartTopAndBottomOfTheFirst()
    {
        int[] m = InProgressForTest(1, MatchRules.HalfTop);
        DrawForTest(m);
        Assert.AreEqual(0, GetInningCode(Away, 0), "the half being played shows its runs, 0 included");
        Assert.AreEqual(CellBlank, GetInningCode(Home, 0), "Home has not batted yet");
        Assert.AreEqual(CellBlank, GetInningCode(Away, 1), "an inning not reached is blank");
        Assert.AreEqual(0, GetTotalCode(Away, 0));
        Assert.AreEqual(0, GetTotalCode(Home, 1));
        Assert.AreEqual(Away, GetOffenseShown(), "top: Away bats");
        Assert.AreEqual(0, GetCurrentColumn());

        GameFlowRules.EndHalf(m, 9, 12, true);
        DrawForTest(m);
        Assert.AreEqual(0, GetInningCode(Away, 0), "a scoreless inning that was played shows 0");
        Assert.AreEqual(0, GetInningCode(Home, 0));
        Assert.AreEqual(Home, GetOffenseShown(), "bottom: Home bats");
        Assert.AreEqual(0, GetCurrentColumn());

        GameFlowRules.EndHalf(m, 9, 12, true);
        DrawForTest(m);
        Assert.AreEqual(0, GetInningCode(Away, 1));
        Assert.AreEqual(CellBlank, GetInningCode(Home, 1));
        Assert.AreEqual(Away, GetOffenseShown());
        Assert.AreEqual(1, GetCurrentColumn(), "the highlight follows the inning");
    }

    [TsukimiTest]
    public void LiveRunsHitsAndErrorsShowFromTheEffectiveState()
    {
        int[] m = InProgressForTest(1, MatchRules.HalfTop);
        m[MatchRules.Runners] = 15;
        m[MatchRules.Runners + 1] = 16;
        m[MatchRules.Runners + 2] = 17;
        int[] roles = new int[RosterRules.RoleTableLength];
        int[] batters = new int[RosterRules.OrderTableLength];
        for (int r = 0; r < 9; r++)
        {
            roles[RosterRules.RoleSlot(Away, r)] = 11 + r; batters[RosterRules.OrderSlot(Away, r)] = 11 + r;
            roles[RosterRules.RoleSlot(Home, r)] = 21 + r; batters[RosterRules.OrderSlot(Home, r)] = 21 + r;
        }
        int[] p = PlayRules.NewPlay();
        double[] t = PlayRules.NewTimes();
        PlayRules.TakeRoster(p, roles, batters);
        Assert.AreEqual(0, PlayRules.Begin(p, t, m, 1, 100.0, 1, 1, 9));
        int[] e = MatchRules.NewState();
        PlayRules.Compose(m, p, e);
        DrawForTest(e);
        Assert.AreEqual(0, GetInningCode(Away, 0));

        for (int i = 0; i < 4; i++) PlayRules.Apply(p, t, m, PlayRules.JudgeBall, 0, 0, 1, i + 1, 101.0 + i, true);
        PlayRules.Apply(p, t, m, PlayRules.RulingHit, 0, Away, 1, 5, 106.0, true);
        PlayRules.Apply(p, t, m, PlayRules.RulingError, 0, Home, 1, 6, 107.0, true);
        PlayRules.Compose(m, p, e);
        DrawForTest(e);
        Assert.AreEqual(1, GetInningCode(Away, 0), "a run forced in during the live play");
        Assert.AreEqual(1, GetTotalCode(Away, 0));
        Assert.AreEqual(1, GetTotalCode(Away, 1), "a hit entered during the live play");
        Assert.AreEqual(1, GetTotalCode(Home, 2), "an error charged to Home");
        Assert.AreEqual(0, GetTotalCode(Home, 0));
    }

    [TsukimiTest]
    public void UnchangedCellsAreNotWrittenAgain()
    {
        int[] m = InProgressForTest(3, MatchRules.HalfBottom);
        m[MatchRules.InningRunsIndex(Away, 1)] = 2;
        DrawForTest(m);
        int writes = GetTextWriteCount();
        DrawForTest(m);
        Assert.AreEqual(writes, GetTextWriteCount(), "same state: no text is written");
        m[MatchRules.InningRunsIndex(Home, 3)] = 1;
        DrawForTest(m);
        Assert.AreEqual(writes + 2, GetTextWriteCount(), "one run: the inning cell and R only");
    }

    [TsukimiTest]
    public void ExtraInningsAddColumnsUpToTwelve()
    {
        int[] m = InProgressForTest(9, MatchRules.HalfBottom);
        DrawForTest(m);
        Assert.AreEqual(9, GetShownColumns());
        GameFlowRules.EndHalf(m, 9, 12, true);
        Assert.AreEqual(10, m[MatchRules.Inning], "tied after nine: the game goes on");
        DrawForTest(m);
        Assert.AreEqual(10, GetShownColumns());
        Assert.AreEqual(10, GetHeaderCode(9));
        Assert.AreEqual(9, GetCurrentColumn());
        Assert.AreEqual(0, GetInningCode(Away, 9));
        Assert.AreEqual(CellBlank, GetInningCode(Home, 9));
        Assert.AreEqual(0, GetInningCode(Home, 8), "the ninth stays shown");
        m[MatchRules.Inning] = 11;
        DrawForTest(m);
        Assert.AreEqual(11, GetShownColumns());
        m[MatchRules.Inning] = 12;
        DrawForTest(m);
        Assert.AreEqual(12, GetShownColumns());
        Assert.AreEqual(1, GetFirstShownInning());
        Assert.AreEqual(0, GetOverflowInning());
    }

    [TsukimiTest]
    public void PastTwelveTheBoardKeepsTwelveColumnsAndSaysSo()
    {
        int[] m = InProgressForTest(13, MatchRules.HalfTop);
        m[MatchRules.InningRunsIndex(Away, 1)] = 3;
        DrawForTest(m);
        Assert.AreEqual(12, GetShownColumns(), "twelve columns at most");
        Assert.AreEqual(2, GetFirstShownInning(), "the last twelve innings");
        Assert.AreEqual(2, GetHeaderCode(0));
        Assert.AreEqual(13, GetHeaderCode(11));
        Assert.AreEqual(11, GetCurrentColumn());
        Assert.AreEqual(13, GetOverflowInning(), "a diagnostic names the inning");
        Assert.AreEqual(3, GetTotalCode(Away, 0), "R still counts every inning");
    }

    [TsukimiTest]
    public void XOnlyWhenHomeDidNotNeedToBat()
    {
        int[] m = InProgressForTest(9, MatchRules.HalfTop);
        m[MatchRules.InningRunsIndex(Home, 3)] = 2;
        GameFlowRules.EndHalf(m, 9, 12, true);
        Assert.AreEqual(MatchRules.PhaseEnded, m[MatchRules.Phase], "Home leads after the top of the ninth");
        DrawForTest(m);
        Assert.AreEqual(0, GetInningCode(Away, 8));
        Assert.AreEqual(CellX, GetInningCode(Home, 8), "the bottom of the ninth was not needed");
        Assert.AreEqual(2, GetTotalCode(Home, 0));
        Assert.AreEqual(-1, GetOffenseShown(), "no team bats after the end");
        Assert.AreEqual(-1, GetCurrentColumn());

        int[] walkOff = InProgressForTest(9, MatchRules.HalfBottom);
        walkOff[MatchRules.InningRunsIndex(Away, 2)] = 1;
        walkOff[MatchRules.InningRunsIndex(Home, 9)] = 2;
        GameFlowRules.EndGame(walkOff, MatchRules.ResultHomeWin);
        DrawForTest(walkOff);
        Assert.AreEqual(2, GetInningCode(Home, 8), "Home won by batting: its runs, not X");

        int[] awayWin = InProgressForTest(9, MatchRules.HalfBottom);
        awayWin[MatchRules.InningRunsIndex(Away, 2)] = 1;
        GameFlowRules.EndHalf(awayWin, 9, 12, true);
        DrawForTest(awayWin);
        Assert.AreEqual(0, GetInningCode(Home, 8), "an Away win after the bottom shows the bottom's 0");

        int[] ended = InProgressForTest(5, MatchRules.HalfTop);
        ended[MatchRules.InningRunsIndex(Away, 1)] = 4;
        GameFlowRules.EndGame(ended, MatchRules.ResultAwayWin);
        DrawForTest(ended);
        Assert.AreEqual(CellBlank, GetInningCode(Home, 4), "ended by the umpire in the top with Away ahead: blank, not X");
    }

    [TsukimiTest]
    public void CorrectionsRecoveryAndLateJoinRedrawFromTheState()
    {
        int[] m = InProgressForTest(11, MatchRules.HalfBottom);
        m[MatchRules.InningRunsIndex(Away, 2)] = 3;
        m[MatchRules.InningRunsIndex(Home, 10)] = 12;
        m[MatchRules.Hits] = 5;
        m[MatchRules.Errors + Home] = 1;
        DrawForTest(m);
        Assert.AreEqual(11, GetShownColumns(), "a late joiner gets the whole state in one draw");
        Assert.AreEqual(12, GetInningCode(Home, 9));
        Assert.AreEqual(Home, GetOffenseShown());

        Draw(m, false, 9);
        Assert.AreEqual(9, GetShownColumns(), "not ready (an Owner recovering): nothing shown as a score");
        Assert.AreEqual(CellBlank, GetInningCode(Away, 1));
        Assert.AreEqual(CellBlank, GetTotalCode(Away, 1));
        Assert.AreEqual(-1, GetOffenseShown());

        DrawForTest(m);
        Assert.AreEqual(3, GetInningCode(Away, 1), "recovered: the same state again");
        Assert.AreEqual(5, GetTotalCode(Away, 1));

        int[] corrected = InProgressForTest(9, MatchRules.HalfTop);
        corrected[MatchRules.InningRunsIndex(Away, 2)] = 2;
        corrected[MatchRules.Hits] = 4;
        DrawForTest(corrected);
        Assert.AreEqual(9, GetShownColumns(), "a correction back to the ninth removes the extra columns");
        Assert.AreEqual(2, GetInningCode(Away, 1));
        Assert.AreEqual(4, GetTotalCode(Away, 1));
        Assert.AreEqual(0, GetTotalCode(Away, 2));
        Assert.AreEqual(CellBlank, GetInningCode(Home, 8));
        Assert.AreEqual(Away, GetOffenseShown());
    }
}
