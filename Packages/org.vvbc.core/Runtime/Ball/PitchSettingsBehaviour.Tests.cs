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
    public void ConsecutiveEditsShareOneCommit()
    {
        applying = false;
        draftMask = 0;
        ToggleCourse(1);
        Assert.IsTrue(commitScheduled);
        ToggleCourse(2);
        Assert.AreEqual(6, draftMask);
        Assert.IsTrue(commitScheduled);
    }
}
