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
    public void TheDropdownListsEverySupportedPitchOnceInTheSpecifiedOrder()
    {
        Assert.AreEqual(7, pitchTypeOrder.Length);
        int seen = 0;
        for (int i = 0; i < pitchTypeOrder.Length; i++)
        {
            Assert.AreEqual(0, seen & (1 << pitchTypeOrder[i]), "no type twice");
            seen |= 1 << pitchTypeOrder[i];
        }
        Assert.AreEqual(127, seen);
        Assert.AreEqual("Four-Seam", TypeName(pitchTypeOrder[0]));
        Assert.AreEqual("Slider", TypeName(pitchTypeOrder[1]));
        Assert.AreEqual("Curveball", TypeName(pitchTypeOrder[2]));
        Assert.AreEqual("Sinker", TypeName(pitchTypeOrder[3]));
        Assert.AreEqual("Splitter", TypeName(pitchTypeOrder[4]));
        Assert.AreEqual("Screwball", TypeName(pitchTypeOrder[5]));
        Assert.AreEqual("Sweeper", TypeName(pitchTypeOrder[6]));
        Assert.AreEqual("Four-Seam", TypeName(12), "an unknown type reads as the default");
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
