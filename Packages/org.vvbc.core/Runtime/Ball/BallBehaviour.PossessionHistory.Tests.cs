using Tsukimi;

// The local history of accepted states that tag verification reads: newest first, every kind, a
// repeated state once, bounded, never for a LocalRig ball.
public partial class BallBehaviour
{
    private void AcceptForHistoryTest(int seq, int publisher, int kind, int hold, int holder, double time)
    {
        _acceptedSequence = seq;
        _acceptedPublisherId = publisher;
        _acceptedKind = kind;
        _acceptedHold = hold;
        _acceptedHolderId = holder;
        _acceptedServerTime = time;
        RecordAcceptedHistory();
    }

    [TsukimiTest]
    public void AcceptedStatesAreKeptNewestFirst()
    {
        localOnly = false;
        _possessionHistoryCount = 0;
        _possessionHistoryNext = 0;
        AcceptForHistoryTest(7, 21, PossessionState, HoldGlove, 21, 104.0);
        AcceptForHistoryTest(7, 21, PossessionState, HoldGlove, 21, 104.0);    // received again
        AcceptForHistoryTest(8, 21, ThrowState, HoldFree, 0, 106.0);
        Assert.AreEqual(2, GetPossessionHistoryCount(), "a repeated state is kept once");
        Assert.AreEqual(8, GetPossessionHistoryField(0, HistorySequence));
        Assert.AreEqual(0, GetPossessionHistoryField(0, HistoryIsPossession), "a throw ends the hold");
        Assert.AreEqual(0, GetPossessionHistoryField(0, HistoryHolder));
        Assert.AreEqual(1, GetPossessionHistoryField(1, HistoryIsPossession));
        Assert.AreEqual(HoldGlove, GetPossessionHistoryField(1, HistoryHold));
        Assert.AreEqual(21, GetPossessionHistoryField(1, HistoryHolder));
        Assert.AreEqual(104.0, GetPossessionHistoryTime(1));
        for (int s = 9; s < 9 + PossessionHistoryCapacity; s++) AcceptForHistoryTest(s, 22, PossessionState, HoldHand, 22, 100.0 + s);
        Assert.AreEqual(PossessionHistoryCapacity, GetPossessionHistoryCount(), "bounded");
        Assert.AreEqual(9 + PossessionHistoryCapacity - 1, GetPossessionHistoryField(0, HistorySequence));
        Assert.AreEqual(9, GetPossessionHistoryField(PossessionHistoryCapacity - 1, HistorySequence), "the oldest kept");
    }

    [TsukimiTest]
    public void ALocalRigBallKeepsNoHistory()
    {
        localOnly = true;
        _possessionHistoryCount = 0;
        AcceptForHistoryTest(3, 21, PossessionState, HoldGlove, 21, 104.0);
        Assert.AreEqual(0, GetPossessionHistoryCount());
        localOnly = false;
    }
}
