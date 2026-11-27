using Tsukimi;
using UnityEngine;

// One tag contact plays once per client: at once for the fielder, after the Owner's announcement
// for everyone else, never twice, never for a stale announcement.
public partial class TagContactFeedbackBehaviour
{
    private static readonly Vector3 At = new Vector3(20f, 1f, 20f);

    private void ResetFeedbackForTest()
    {
        effect = null;
        sound = null;
        maxAgeSeconds = 1.5f;
        for (int i = 0; i < Remembered; i++) { _seenPlay[i] = 0; _seenSource[i] = 0; _seenSeq[i] = 0; }
        _seenNext = 0;
        _played = 0;
        _skippedDuplicate = 0;
        _skippedLate = 0;
    }

    [TsukimiTest]
    public void TheFielderPlaysAtOnceAndSkipsTheAnnouncement()
    {
        ResetFeedbackForTest();
        PlayLocal(5, 21, 1, At);
        Assert.AreEqual(1, GetPlayedCount());
        Assert.IsFalse(PlayConfirmed(5, 21, 1, At, 100.0, 21, 100.2), "the same event announced back");
        Assert.AreEqual(1, GetPlayedCount());
        Assert.AreEqual(1, GetSkippedDuplicateCount());
    }

    [TsukimiTest]
    public void OthersPlayOnceOnTheAnnouncement()
    {
        ResetFeedbackForTest();
        // A runner, a spectator or the Owner itself (when it is not the fielder): same path.
        Assert.IsTrue(PlayConfirmed(5, 21, 1, At, 100.0, 11, 100.3));
        Assert.AreEqual(At, GetLastPosition(), "at the recorded contact position");
        Assert.IsFalse(PlayConfirmed(5, 21, 1, At, 100.0, 11, 100.4), "a second announcement (Owner change)");
        Assert.IsTrue(PlayConfirmed(5, 21, 2, At, 101.0, 11, 101.2), "another tag of the same runner");
        Assert.IsTrue(PlayConfirmed(6, 21, 1, At, 201.0, 11, 201.2), "the same sequence in another play");
        Assert.AreEqual(3, GetPlayedCount());
    }

    [TsukimiTest]
    public void AStaleAnnouncementShowsNothing()
    {
        ResetFeedbackForTest();
        Assert.IsFalse(PlayConfirmed(5, 21, 1, At, 100.0, 11, 103.0));
        Assert.AreEqual(1, GetSkippedLateCount());
        Assert.AreEqual(0, GetPlayedCount());
        Assert.IsFalse(PlayConfirmed(5, 21, 1, At, 100.0, 11, 100.1), "it was still remembered");
    }
}
