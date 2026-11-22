using Tsukimi;
using VRC.SDKBase;

public partial class PitchSettingsBehaviour
{
    [TsukimiTest]
    public void ApplyingStateDoesNotScheduleACommit()
    {
        applying = true;
        Edited();
        Assert.IsFalse(commitScheduled);
    }

    [TsukimiTest]
    public void CommitWaitsForTheLastEdit()
    {
        commitDelaySeconds = 0.5f;
        lastEditTime = 10f;
        Assert.IsTrue(CommitRemaining(10.2f) > 0.29f);
        Assert.IsTrue(CommitRemaining(10.5f) <= 0.01f);
        // a later edit pushes the commit back
        lastEditTime = 10.4f;
        Assert.IsTrue(CommitRemaining(10.5f) > 0.39f);
    }

    [TsukimiTest]
    public void CourseLabelsNameTheZonesInBatterViewOrder()
    {
        Assert.AreEqual("Zone 5 (default)", CourseLabel(0));
        Assert.AreEqual("Zone 5", CourseLabel(1 << 4));
        Assert.AreEqual("Zones 1 3 9", CourseLabel(1 | 4 | 256));
        Assert.AreEqual("All zones", CourseLabel(511));
    }

    [TsukimiTest]
    public void RowsNameTheTypeAndItsParameters()
    {
        Assert.AreEqual("4-Seam", BuildLabel(PitchType.FourSeam, PitchPlaylist.DefaultBuild));
        Assert.AreEqual("Slider  0 / 50 / 100", BuildLabel(PitchType.Slider, PitchPlaylist.PackBuild(PitchType.Slider, 0f, 0.5f, 1f)));
        Assert.AreEqual("Split  25", BuildLabel(PitchType.Split, PitchPlaylist.PackBuild(PitchType.Split, 0.25f, 0f, 0f)));
        Assert.AreEqual("4-Seam", BuildLabel(12, PitchPlaylist.DefaultBuild), "an unknown type reads as the 4-Seam");
    }

    [TsukimiTest]
    public void EditingAnItemRestoresItsBuildAndANewTypeStartsFromTheStandard()
    {
        int saved = PitchPlaylist.PackBuild(PitchType.Knuckle, 0.1f, 0.9f, 0.3f);
        BeginEdit(7, PitchType.Knuckle, 16, saved);
        Assert.AreEqual(PitchType.Knuckle, draftType);
        Assert.AreEqual(saved, DraftBuild(), "the saved parameters come back");
        draftParameters[0] = 1f;
        Assert.AreEqual(PitchPlaylist.PackBuild(PitchType.Knuckle, 1f, 0.9f, 0.3f), DraftBuild(), "slider edits stay in the draft");
        SelectType(PitchType.Knuckle);
        Assert.AreEqual(1f, draftParameters[0], "choosing the same type keeps the draft");
        SelectType(PitchType.Sweeper);
        Assert.AreEqual(PitchPlaylist.DefaultBuild, DraftBuild(), "another type starts from its standard build");
        SelectType(10);
        Assert.AreEqual(PitchType.Sweeper, draftType, "an unknown option is ignored");
        _CancelEdit();
        Assert.IsFalse(editing);
        SelectType(PitchType.Curve);
        Assert.AreEqual(PitchType.Sweeper, draftType, "nothing changes without an open draft");
        BeginEdit(7, PitchType.Knuckle, 16, saved);
        Assert.AreEqual(saved, DraftBuild(), "cancel discarded the edits");
    }

    [TsukimiTest]
    public void LongOwnerNamesAreShortened()
    {
        Assert.AreEqual("Short", Shorten("Short", 20));
        Assert.AreEqual("ABCD…", Shorten("ABCDEFGH", 5));
        Assert.AreEqual("", Shorten(null, 5));
    }

    [TsukimiTest]
    public void OnlyCommittedPhasesShowACountdown()
    {
        Assert.IsTrue(HasCountdown(BattingPitcherBehaviour.PhaseScheduled));
        Assert.IsTrue(HasCountdown(BattingPitcherBehaviour.PhaseAwaitingContact));
        Assert.IsTrue(HasCountdown(BattingPitcherBehaviour.PhaseResetting));
        Assert.IsFalse(HasCountdown(BattingPitcherBehaviour.PhasePreparing));
        Assert.IsFalse(HasCountdown(BattingPitcherBehaviour.PhaseBallHeld));
        Assert.IsFalse(HasCountdown(BattingPitcherBehaviour.PhaseSyncing));
        Assert.IsFalse(HasCountdown(BattingPitcherBehaviour.PhaseStopped));
        Assert.AreEqual("AWAITING CONTACT", PhaseTitle(BattingPitcherBehaviour.PhaseAwaitingContact));
        Assert.AreEqual("PITCH SCHEDULED", PhaseTitle(BattingPitcherBehaviour.PhaseScheduled));
        Assert.AreEqual("STOPPED", PhaseTitle(BattingPitcherBehaviour.PhaseStopped));
    }
}
