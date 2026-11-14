using Tsukimi;
using VRC.SDKBase;

public partial class BattingPitcherBehaviour
{
    [TsukimiTest]
    public void ThePlaylistAndStartReachTheOtherClient()
    {
        VRCPlayerApi me = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        _stateKnown = true;
        RequestSaveItem(0, 2, 1 | 256);
        RequestSaveItem(0, 5, 16);
        RequestSequence(true);
        RequestRunning(true);
        SubmitSettings(140f, 1800f, 5f, true);
        Mimic.Deliver();
        Mimic.Become(other);
        Assert.IsTrue(StateKnown());
        Assert.AreEqual(2, PlaylistCount());
        Assert.AreEqual(2, PitchPlaylist.Type(PlaylistItem(0)));
        Assert.AreEqual(1 | 256, PitchPlaylist.Mask(PlaylistItem(0)));
        Assert.AreEqual(5, PitchPlaylist.Type(PlaylistItem(1)));
        Assert.IsTrue(IsRunning());
        Assert.IsTrue(IsSequence());
        Assert.IsTrue(IsResetOnTimer());
        Assert.AreEqual(140f, speedKmh);
        Assert.AreEqual(1800f, spinRpm);
        Assert.AreEqual(5f, resetSeconds);
        Assert.AreEqual(PitchPlaylist.Id(PlaylistItem(1)) + 1, _nextItemId, "the id counter travels with the list");
    }

    [TsukimiTest]
    public void ANonOwnerEditTakesOwnershipAndEveryoneConverges()
    {
        VRCPlayerApi me = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        _stateKnown = true;
        RequestSaveItem(0, 0, 16);
        RequestRunning(true);
        int first = SavedItemId();
        Mimic.Deliver();
        Mimic.Become(other);
        int token = RequestSaveItem(0, 3, 2);
        Assert.IsTrue(Networking.IsOwner(other, gameObject), "editing takes Ownership");
        Assert.AreEqual(SaveDone, SaveResult(token));
        RequestMoveItem(SavedItemId(), -1);
        RequestRunning(false);
        Mimic.Deliver();
        Mimic.Become(me);
        Assert.AreEqual(2, PlaylistCount());
        Assert.AreEqual(3, PitchPlaylist.Type(PlaylistItem(0)), "the moved item is on top everywhere");
        Assert.AreEqual(first, PitchPlaylist.Id(PlaylistItem(1)));
        Assert.IsFalse(IsRunning(), "Stop from the other client reaches the first one");
        Assert.AreNotEqual(PitchPlaylist.Id(PlaylistItem(0)), PitchPlaylist.Id(PlaylistItem(1)));
    }

    [TsukimiTest]
    public void ConcurrentEditsConvergeToTheLastSentState()
    {
        VRCPlayerApi me = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        _stateKnown = true;
        RequestSaveItem(0, 0, 16);
        Mimic.Deliver();
        // the other client edits and sends
        Mimic.Become(other);
        RequestSaveItem(0, 1, 1);
        Mimic.Deliver(me);
        // this client edits next, from the state it holds
        Mimic.Become(me);
        RequestSaveItem(0, 2, 4);
        Mimic.Deliver(other);
        int count = PlaylistCount();
        int a = PlaylistItem(0), b = PlaylistItem(1), c = PlaylistItem(2);
        bool running = IsRunning();
        Mimic.Become(other);
        Assert.AreEqual(count, PlaylistCount());
        Assert.AreEqual(3, count);
        Assert.AreEqual(a, PlaylistItem(0));
        Assert.AreEqual(b, PlaylistItem(1));
        Assert.AreEqual(c, PlaylistItem(2));
        Assert.AreEqual(running, IsRunning());
        Assert.IsTrue(Networking.IsOwner(me, gameObject));
    }

    [TsukimiTest]
    public void ALateJoinerRestoresThePlaylistAndHistory()
    {
        VRCPlayerApi me = Mimic.Join();
        _stateKnown = true;
        RequestSequence(true);
        RequestSaveItem(0, 0, 16);
        RequestSaveItem(0, 6, 32);
        RequestRunning(true);
        _reservedItemId = PitchPlaylist.Id(ChooseNextItem());
        MarkReservedItem();
        RequestSerialization();
        Mimic.Deliver();
        VRCPlayerApi late = Mimic.Join();
        Mimic.Become(late);
        Assert.IsTrue(StateKnown());
        Assert.AreEqual(2, PlaylistCount());
        Assert.IsTrue(PlaylistDone(0), "the done bits arrive with the list");
        Assert.AreEqual(1, NextIndex());
        Assert.IsTrue(IsRunning());
        Assert.AreNotEqual(PhaseStopped, CyclePhaseAt(0.0), "a running state is not reported as Stopped");
    }

    [TsukimiTest]
    public void AClientThatMissedAnUpdateCatchesUpWithTheNextOne()
    {
        VRCPlayerApi me = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        VRCPlayerApi third = Mimic.Join();
        _stateKnown = true;
        RequestRunning(true);
        Mimic.Deliver(other);
        Mimic.Become(third);
        Assert.IsFalse(IsRunning(), "the third client missed the Start");
        Mimic.Become(me);
        RequestSaveItem(0, 4, 8);
        Mimic.Deliver();
        Mimic.Become(third);
        Assert.IsTrue(IsRunning(), "the whole state is sent, so the next send repairs it");
        Assert.AreEqual(1, PlaylistCount());
    }
}
