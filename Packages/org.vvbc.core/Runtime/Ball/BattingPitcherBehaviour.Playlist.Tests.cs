using Tsukimi;
using UnityEngine;

public partial class BattingPitcherBehaviour
{
    private int AddItem(int type, int mask)
    {
        int token = RequestSaveItem(0, type, mask);
        return SaveResult(token) == SaveDone ? SavedItemId() : -1;
    }

    // Marks the next item as pitched the way a confirmed reservation does.
    private int PitchNext()
    {
        int item = ChooseNextItem();
        _reservedItemId = PitchPlaylist.Id(item);
        MarkReservedItem();
        return _reservedItemId;
    }

    [TsukimiTest]
    public void PackedItemsRoundTripAndKeepTheReservedBitsClear()
    {
        int item = PitchPlaylist.Pack(65535, 6, 511);
        Assert.AreEqual(65535, PitchPlaylist.Id(item));
        Assert.AreEqual(6, PitchPlaylist.Type(item));
        Assert.AreEqual(511, PitchPlaylist.Mask(item));
        Assert.AreEqual(0, item >> 29, "bits 29-31 are reserved");
        // out-of-range inputs cannot spill into neighbouring fields
        int clipped = PitchPlaylist.Pack(1, 3, 0xFFFF);
        Assert.AreEqual(511, PitchPlaylist.Mask(clipped));
        Assert.AreEqual(3, PitchPlaylist.Type(clipped));
        Assert.AreEqual(1, PitchPlaylist.Id(clipped));
        // unknown pitch types pitch as Four-Seam
        Assert.AreEqual(0, PitchPlaylist.SafeType(9, 7));
        Assert.AreEqual(6, PitchPlaylist.SafeType(6, 7));
    }

    [TsukimiTest]
    public void PackedStateKeepsItsFieldsApart()
    {
        int state = PitchPlaylist.WithFlag(0, PitchPlaylist.RunningFlag, true);
        state = PitchPlaylist.WithFlag(state, PitchPlaylist.TimerFlag, true);
        int[] items = new int[10];
        for (int i = 0; i < 10; i++) state = PitchPlaylist.Append(items, state, PitchPlaylist.Pack(i + 1, 0, 16));
        state = PitchPlaylist.WithHistory(state, 0x3FF);
        Assert.AreEqual(10, PitchPlaylist.Count(state));
        Assert.AreEqual(0x3FF, PitchPlaylist.History(state));
        Assert.IsTrue(PitchPlaylist.Has(state, PitchPlaylist.RunningFlag));
        Assert.IsTrue(PitchPlaylist.Has(state, PitchPlaylist.TimerFlag));
        Assert.IsFalse(PitchPlaylist.Has(state, PitchPlaylist.SequenceFlag));
        Assert.AreEqual(-1, PitchPlaylist.FirstPending(state));
    }

    [TsukimiTest]
    public void AnEmptyListPitchesTheFourSeamToTheCenter()
    {
        Mimic.Join();
        _stateKnown = true;
        int item = ChooseNextItem();
        Assert.AreEqual(0, PitchPlaylist.Id(item), "the fallback is not a list item");
        Assert.AreEqual(PitchPlaylist.DefaultType, PitchPlaylist.Type(item));
        Assert.AreEqual(PitchPlaylist.CenterMask, PitchPlaylist.Mask(item));
        Assert.AreEqual(0, PlaylistCount());
    }

    [TsukimiTest]
    public void TheListHoldsTenItemsAndReportsAFullSave()
    {
        Mimic.Join();
        _stateKnown = true;
        for (int i = 0; i < 10; i++) AddItem(i % 7, 1 << (i % 9));
        Assert.AreEqual(10, PlaylistCount());
        int token = RequestSaveItem(0, 2, 16);
        Assert.AreEqual(SaveFull, SaveResult(token));
        Assert.AreEqual(10, PlaylistCount());
        // ids are unique
        for (int i = 0; i < 10; i++)
            for (int j = i + 1; j < 10; j++)
                Assert.AreNotEqual(PitchPlaylist.Id(PlaylistItem(i)), PitchPlaylist.Id(PlaylistItem(j)));
    }

    [TsukimiTest]
    public void ACourseMaskOfZeroIsKeptAndPitchesToTheCenter()
    {
        Mimic.Join();
        _stateKnown = true;
        AddItem(1, 0);
        int item = ChooseNextItem();
        Assert.AreEqual(0, PitchPlaylist.Mask(item), "the item keeps its empty mask");
        Assert.AreEqual(PitchPlaylist.CenterZone, PitchPlaylist.SelectCourse(PitchPlaylist.Mask(item), 0));
    }

    [TsukimiTest]
    public void RandomPitchesEveryItemOncePerLap()
    {
        Mimic.Join();
        _stateKnown = true;
        int a = AddItem(0, 1);
        int b = AddItem(1, 2);
        int c = AddItem(2, 4);
        int seen = 0;
        for (int i = 0; i < 3; i++)
        {
            int id = PitchNext();
            int bit = id == a ? 1 : id == b ? 2 : id == c ? 4 : 0;
            Assert.AreNotEqual(0, bit, "only list items are pitched");
            Assert.AreEqual(0, seen & bit, "no item twice in one lap");
            seen |= bit;
        }
        Assert.AreEqual(7, seen);
        Assert.AreEqual(-1, NextIndex(), "every item done");
        // the next choice starts a new, reshuffled lap with the same items
        int next = PitchNext();
        Assert.IsTrue(next == a || next == b || next == c);
        Assert.AreEqual(3, PlaylistCount());
        Assert.AreEqual(1, PitchPlaylist.CountCourses(PitchPlaylist.History(_playlistState)));
    }

    [TsukimiTest]
    public void SequencePitchesTopToBottomAndWraps()
    {
        Mimic.Join();
        _stateKnown = true;
        RequestSequence(true);
        int a = AddItem(0, 1);
        int b = AddItem(1, 2);
        int c = AddItem(2, 4);
        Assert.AreEqual(a, PitchNext());
        Assert.AreEqual(b, PitchNext());
        Assert.AreEqual(c, PitchNext());
        Assert.AreEqual(a, PitchNext(), "wraps from the end to the top");
        Assert.AreEqual(a, PitchPlaylist.Id(PlaylistItem(0)), "Sequence never reorders");
    }

    [TsukimiTest]
    public void AnItemAddedMidLapIsPitchedInThatLap()
    {
        Mimic.Join();
        _stateKnown = true;
        int a = AddItem(0, 1);
        int b = AddItem(1, 2);
        PitchNext();
        PitchNext();
        Assert.AreEqual(-1, NextIndex());
        // still in the completed lap until the next choice; add before that
        int c = AddItem(2, 4);
        Assert.AreEqual(2, NextIndex(), "the new item is appended and not done");
        Assert.AreEqual(c, PitchNext());
        Assert.IsTrue(a != c && b != c);
    }

    [TsukimiTest]
    public void EditingKeepsTheIdAndTheDoneBit()
    {
        Mimic.Join();
        _stateKnown = true;
        RequestSequence(true);
        int a = AddItem(0, 1);
        AddItem(1, 2);
        Assert.AreEqual(a, PitchNext());
        int token = RequestSaveItem(a, 5, 0x1F0);
        Assert.AreEqual(SaveDone, SaveResult(token));
        Assert.AreEqual(a, PitchPlaylist.Id(PlaylistItem(0)));
        Assert.AreEqual(5, PitchPlaylist.Type(PlaylistItem(0)));
        Assert.AreEqual(0x1F0, PitchPlaylist.Mask(PlaylistItem(0)));
        Assert.IsTrue(PlaylistDone(0), "editing keeps the history");
        Assert.AreEqual(2, PlaylistCount());
    }

    [TsukimiTest]
    public void MovingCarriesTheDoneBitAndTheNextIsTheFirstNotDone()
    {
        Mimic.Join();
        _stateKnown = true;
        RequestSequence(true);
        int a = AddItem(0, 1);
        int b = AddItem(1, 2);
        int c = AddItem(2, 4);
        Assert.AreEqual(a, PitchNext());
        // move the pitched item to the bottom
        RequestMoveItem(a, 1);
        RequestMoveItem(a, 1);
        Assert.AreEqual(a, PitchPlaylist.Id(PlaylistItem(2)));
        Assert.IsTrue(PlaylistDone(2));
        Assert.IsFalse(PlaylistDone(0));
        Assert.AreEqual(b, PitchPlaylist.Id(PlaylistItem(0)));
        Assert.AreEqual(0, NextIndex());
        // moving an undone item above keeps it next
        RequestMoveItem(c, -1);
        Assert.AreEqual(c, PitchPlaylist.Id(PlaylistItem(0)));
        Assert.AreEqual(c, PitchNext());
        // moves past either end are ignored
        RequestMoveItem(c, -1);
        Assert.AreEqual(c, PitchPlaylist.Id(PlaylistItem(0)));
        RequestMoveItem(a, 1);
        Assert.AreEqual(a, PitchPlaylist.Id(PlaylistItem(2)));
    }

    [TsukimiTest]
    public void DeletingShiftsTheHistoryWithTheItems()
    {
        Mimic.Join();
        _stateKnown = true;
        RequestSequence(true);
        int a = AddItem(0, 1);
        int b = AddItem(1, 2);
        int c = AddItem(2, 4);
        PitchNext();
        PitchNext();
        RequestDeleteItem(a);
        Assert.AreEqual(2, PlaylistCount());
        Assert.AreEqual(b, PitchPlaylist.Id(PlaylistItem(0)));
        Assert.IsTrue(PlaylistDone(0), "b keeps its done bit after the shift");
        Assert.IsFalse(PlaylistDone(1));
        Assert.AreEqual(c, PitchNext());
        RequestDeleteItem(b);
        RequestDeleteItem(c);
        Assert.AreEqual(0, PlaylistCount());
        Assert.AreEqual(0, PitchPlaylist.History(_playlistState));
        // an empty list recovers: the fallback pitches, and a new item is usable at once
        Assert.AreEqual(0, PitchPlaylist.Id(ChooseNextItem()));
        int d = AddItem(3, 8);
        Assert.AreEqual(d, PitchNext());
    }

    [TsukimiTest]
    public void SavingAnItemDeletedMeanwhileAppendsItWithANewId()
    {
        Mimic.Join();
        _stateKnown = true;
        int a = AddItem(0, 1);
        AddItem(1, 2);
        RequestDeleteItem(a);
        int token = RequestSaveItem(a, 4, 32);
        Assert.AreEqual(SaveDone, SaveResult(token));
        Assert.AreEqual(2, PlaylistCount());
        int added = PlaylistItem(1);
        Assert.AreNotEqual(a, PitchPlaylist.Id(added), "a deleted id is never revived");
        Assert.AreEqual(SavedItemId(), PitchPlaylist.Id(added));
        Assert.AreEqual(4, PitchPlaylist.Type(added));
        Assert.AreEqual(32, PitchPlaylist.Mask(added));
    }

    [TsukimiTest]
    public void ModeChangesStartANewLap()
    {
        Mimic.Join();
        _stateKnown = true;
        int a = AddItem(0, 1);
        int b = AddItem(1, 2);
        int c = AddItem(2, 4);
        PitchNext();
        int top = PitchPlaylist.Id(PlaylistItem(0));
        int middle = PitchPlaylist.Id(PlaylistItem(1));
        RequestSequence(true);
        Assert.AreEqual(0, PitchPlaylist.History(_playlistState), "the history restarts");
        Assert.AreEqual(top, PitchPlaylist.Id(PlaylistItem(0)), "Random to Sequence keeps the order");
        Assert.AreEqual(middle, PitchPlaylist.Id(PlaylistItem(1)));
        PitchNext();
        RequestSequence(false);
        Assert.AreEqual(0, PitchPlaylist.History(_playlistState));
        Assert.AreEqual(3, PlaylistCount());
        int ids = 0;
        for (int i = 0; i < 3; i++)
        {
            int id = PitchPlaylist.Id(PlaylistItem(i));
            ids |= id == a ? 1 : id == b ? 2 : id == c ? 4 : 8;
        }
        Assert.AreEqual(7, ids, "a reshuffle keeps every item");
    }

    [TsukimiTest]
    public void StartAndStopAreSharedStateAndStopKeepsTheCommittedPitch()
    {
        Mimic.Join();
        _stateKnown = true;
        Assert.IsFalse(IsRunning(), "the pitcher starts stopped");
        Assert.IsFalse(IsSequence(), "the default mode is Random");
        Assert.IsFalse(IsResetOnTimer(), "the default reset is On Event");
        SetSyncedPitch(4, 10.0);
        Assert.IsTrue(AcceptPitch(1f));
        RequestRunning(true);
        Assert.IsTrue(IsRunning());
        _preparing = true;
        RequestRunning(false);
        Assert.IsFalse(IsRunning());
        Assert.IsFalse(_preparing, "Stop drops an unsent preparation");
        Assert.AreEqual(1, _appliedId, "the committed pitch is untouched");
        Assert.AreEqual(10.0, _appliedStart);
        // a reservation already sent completes
        _preparing = true;
        _reserving = true;
        StopPreparation();
        Assert.IsTrue(_preparing);
    }

    [TsukimiTest]
    public void AFailedReservationGivesItsItemBack()
    {
        Mimic.Join();
        _stateKnown = true;
        RequestSequence(true);
        int a = AddItem(0, 1);
        AddItem(1, 2);
        _reservedItemId = PitchPlaylist.Id(ChooseNextItem());
        MarkReservedItem();
        Assert.IsTrue(PlaylistDone(0));
        ReturnReservedItem();
        Assert.IsFalse(PlaylistDone(0));
        Assert.AreEqual(a, PitchPlaylist.Id(ChooseNextItem()), "the same item is offered again");
    }

    [TsukimiTest]
    public void EditsWaitForOwnershipAndAreDroppedWhenSomeoneElseWins()
    {
        Mimic.Join();
        _stateKnown = false;
        int token = RequestSaveItem(0, 0, 16);
        RequestRunning(true);
        Assert.AreEqual(SavePending, SaveResult(token));
        Assert.AreEqual(0, PlaylistCount());
        DropPendingEdits();
        Assert.AreEqual(SaveInterrupted, SaveResult(token), "the UI keeps the draft and can save again");
        _stateKnown = true;
        ApplyPendingEdits();
        Assert.IsFalse(IsRunning(), "a superseded Start is not applied later");
        Assert.AreEqual(0, PlaylistCount());
    }

    [TsukimiTest]
    public void QueuedEditsApplyInOrderOnceTheStateIsKnown()
    {
        Mimic.Join();
        _stateKnown = false;
        int first = RequestSaveItem(0, 1, 2);
        int second = RequestSaveItem(0, 2, 4);
        RequestRunning(true);
        _stateKnown = true;
        ApplyPendingEdits();
        Assert.AreEqual(SaveDone, SaveResult(second));
        Assert.AreEqual(SaveDone, SaveResult(first));
        Assert.AreEqual(2, PlaylistCount());
        Assert.IsTrue(IsRunning());
        Assert.AreEqual(1, PitchPlaylist.CountCourses(PitchPlaylist.Mask(PlaylistItem(0))));
    }

    [TsukimiTest]
    public void AMalformedSnapshotIsSanitized()
    {
        int[] items = new int[10];
        items[0] = PitchPlaylist.Pack(3, 0, 16);
        items[1] = PitchPlaylist.Pack(3, 1, 16);
        items[2] = PitchPlaylist.Pack(0, 1, 16);
        items[3] = PitchPlaylist.Pack(7, 2, 16);
        int state = PitchPlaylist.WithHistory(15 | PitchPlaylist.RunningFlag, 0x8);
        state = PitchPlaylist.Sanitize(items, state);
        Assert.AreEqual(2, PitchPlaylist.Count(state), "count clamped, duplicate and zero ids removed");
        Assert.AreEqual(3, PitchPlaylist.Id(items[0]));
        Assert.AreEqual(7, PitchPlaylist.Id(items[1]));
        Assert.IsTrue(PitchPlaylist.Done(state, 1), "the history follows the surviving item");
        Assert.IsTrue(PitchPlaylist.Has(state, PitchPlaylist.RunningFlag));
        Assert.AreEqual(4, PitchPlaylist.FreeId(items, state, 3), "a used id is skipped");
    }
}
