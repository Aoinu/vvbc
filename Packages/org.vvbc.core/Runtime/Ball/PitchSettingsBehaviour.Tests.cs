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
}
