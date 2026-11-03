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
}
