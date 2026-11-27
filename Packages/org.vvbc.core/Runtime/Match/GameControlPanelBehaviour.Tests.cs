using Tsukimi;

public partial class GameControlPanelBehaviour
{
    [TsukimiTest]
    public void EndResultFollowsTheScore()
    {
        Assert.AreEqual(MatchRules.ResultAwayWin, ResultByScore(3, 2));
        Assert.AreEqual(MatchRules.ResultHomeWin, ResultByScore(2, 3));
        Assert.AreEqual(MatchRules.ResultTie, ResultByScore(4, 4));
    }

    [TsukimiTest]
    public void CorrectionFieldsStayInTheirRanges()
    {
        int[] m = MatchRules.NewState();
        StepField(m, FieldOuts, 5, 1);
        Assert.AreEqual(2, m[MatchRules.Outs]);
        StepField(m, FieldBalls, -1, 1);
        Assert.AreEqual(0, m[MatchRules.Balls]);
        StepField(m, FieldHalf, 1, 1);
        Assert.AreEqual(MatchRules.HalfBottom, m[MatchRules.Half]);
        StepField(m, FieldAwayCursor, -1, 1);
        Assert.AreEqual(9, FieldValue(m, FieldAwayCursor, 1), "the order wraps 1 -> 9");
        StepField(m, FieldHomeRuns, 2, 1);
        Assert.AreEqual(2, FieldValue(m, FieldHomeRuns, 1));
        Assert.AreEqual(2, MatchRules.TeamRuns(m, 1), "inning runs are what the total is made of");
        StepField(m, FieldAwayRuns, -3, 1);
        Assert.AreEqual(0, FieldValue(m, FieldAwayRuns, 1), "never negative");
        m[MatchRules.Attention] = 5;
        StepField(m, FieldAttention, 1, 1);
        Assert.AreEqual(5, m[MatchRules.Attention], "+ does nothing");
        StepField(m, FieldAttention, -1, 1);
        Assert.AreEqual(0, m[MatchRules.Attention], "- clears it");
    }

    [TsukimiTest]
    public void PlayerFieldsEditBatterAndBases()
    {
        int[] m = MatchRules.NewState();
        SetPlayerField(m, FieldBatter, 7);
        Assert.AreEqual(7, PlayerField(m, FieldBatter));
        Assert.AreEqual(1, m[MatchRules.PaId], "a batter needs a plate appearance id");
        SetPlayerField(m, Field2B, 8);
        Assert.AreEqual(8, m[MatchRules.Runners + 1]);
        Assert.IsTrue(IsPlayerField(Field3B));
        Assert.IsFalse(IsPlayerField(FieldOuts));
        Assert.AreEqual(0, MatchRules.FindViolation(m));
    }

    [TsukimiTest]
    public void ScoreboardShowsOnlyInningsReached()
    {
        int[] m = MatchRules.NewState();
        m[MatchRules.Phase] = MatchRules.PhaseInProgress;
        m[MatchRules.Inning] = 2;
        m[MatchRules.InningRunsIndex(0, 1)] = 1;
        m[MatchRules.Hits] = 3;
        string lines = ScoreLines(m, 3);
        Assert.AreEqual("       1  2  3|  R  H  E\nAWAY   1  0   |  1  3  0\nHOME   0      |  0  0  0", lines);
        Assert.AreEqual("Top 2   B 0  S 0  O 0", SituationLine(m));
        Assert.AreEqual("Final: Home wins", PhaseName(MatchRules.PhaseEnded, MatchRules.ResultHomeWin));
    }
}
