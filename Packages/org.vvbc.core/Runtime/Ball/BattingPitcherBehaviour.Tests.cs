using Tsukimi;

public partial class BattingPitcherBehaviour
{
    [TsukimiTest]
    public void ReleaseIsAcceptedOnceAndResetDeadlineUsesSnapshot()
    {
        phase = PitchMotion;
        pitchSequence = 7;
        _solvingSequence = 7;
        _resetSnapshot = 3f;
        Assert.IsTrue(AcceptRelease(10f, 0.385f));
        Assert.AreEqual(FollowThrough, phase);
        Assert.IsFalse(AcceptRelease(10.1f, 0.385f));
        resetSeconds = 12f;
        Assert.IsFalse(ResetDue(12.99f));
        Assert.IsTrue(ResetDue(13f));
    }

    [TsukimiTest]
    public void OldSolverGenerationAndEventsOutsideMotionAreRejected()
    {
        pitchSequence = 8;
        _solvingSequence = 7;
        phase = PitchMotion;
        Assert.IsFalse(AcceptRelease(1f, 0.385f));
        _solvingSequence = 8;
        phase = Preparing;
        Assert.IsFalse(AcceptRelease(1f, 0.385f));
        phase = FollowThrough;
        Assert.IsFalse(AcceptRelease(1f, 0.385f));
    }

    [TsukimiTest]
    public void ReleaseOutsidePitchWindowCannotStartNewCycleEarly()
    {
        pitchSequence = 9;
        _solvingSequence = 9;
        phase = PitchMotion;
        Assert.IsFalse(AcceptRelease(1f, 0.1f));
        Assert.IsTrue(AcceptRelease(2f, 0.385f));
    }

    [TsukimiTest]
    public void ResetCannotHappenBeforeARelease()
    {
        phase = PitchMotion;
        _released = false;
        Assert.IsFalse(ResetDue(100f));
        phase = FollowThrough;
        Assert.IsFalse(ResetDue(100f));
    }

    [TsukimiTest]
    public void CourseMaskSelectsOnlyEnabledEntriesAndEmptyMeansCenter()
    {
        int mask = (1 << 0) | (1 << 3) | (1 << 8);
        Assert.AreEqual(3, CountCourses(mask));
        Assert.AreEqual(0, SelectCourse(mask, 0));
        Assert.AreEqual(3, SelectCourse(mask, 1));
        Assert.AreEqual(8, SelectCourse(mask, 2));
        Assert.AreEqual(0, CountCourses(0));
        Assert.AreEqual(4, SelectCourse(0, 0));
        Assert.AreEqual(0, CountCourses(1 << 12));
    }
}
