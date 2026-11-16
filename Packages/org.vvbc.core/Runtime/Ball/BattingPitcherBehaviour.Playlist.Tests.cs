using Tsukimi;
using UnityEngine;

public partial class BattingPitcherBehaviour
{
    private int AddItem(int type, int mask)
    {
        return AddBuild(type, mask, PitchPlaylist.DefaultBuild);
    }

    private int AddBuild(int type, int mask, int build)
    {
        int token = RequestSaveItem(0, type, mask, build);
        return SaveResult(token) == SaveDone ? SavedItemId() : -1;
    }

    // The item the next preparation takes, or the fallback for an empty list.
    private int ChooseNextItem()
    {
        int index = ChooseNextIndex();
        return index < 0 ? FallbackItem : _playlist[index];
    }

    private int BuildOf(int itemId)
    {
        return PlaylistBuild(IndexOfItem(itemId));
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
        // unknown pitch types pitch as the 4-Seam
        Assert.AreEqual(PitchType.FourSeam, PitchPlaylist.Build(PitchPlaylist.Pack(1, 12, 16), PitchPlaylist.DefaultBuild).pitchTypeId);
        Assert.AreEqual(PitchType.Knuckle, PitchPlaylist.Build(PitchPlaylist.Pack(1, 9, 16), PitchPlaylist.DefaultBuild).pitchTypeId);
    }

    [TsukimiTest]
    public void PackedBuildsRoundTripAndKeepUnusedParametersAtTheDefault()
    {
        int build = PitchPlaylist.PackBuild(PitchType.Slider, 0f, 0.5f, 1f);
        Assert.AreEqual(0f, PitchPlaylist.Parameter(build, 0));
        Assert.AreEqual(0.5f, PitchPlaylist.Parameter(build, 1), "the default is exact");
        Assert.AreEqual(1f, PitchPlaylist.Parameter(build, 2));
        Assert.AreEqual(0, build >> 30, "bits 30-31 stay clear");
        float quarter = PitchPlaylist.Parameter(PitchPlaylist.PackBuild(PitchType.Slider, 0.25f, 0f, 0f), 0);
        Assert.IsTrue(Mathf.Abs(quarter - 0.25f) < 0.001f, "10-bit steps");
        Assert.AreEqual(PitchPlaylist.DefaultBuild, PitchPlaylist.PackBuild(PitchType.FourSeam, 0f, 1f, 0.2f),
            "the 4-Seam has no parameters");
        int sinker = PitchPlaylist.PackBuild(PitchType.Sinker, 1f, 0f, 0f);
        Assert.AreEqual(1f, PitchPlaylist.Parameter(sinker, 0));
        Assert.AreEqual(0f, PitchPlaylist.Parameter(sinker, 1));
        Assert.AreEqual(0.5f, PitchPlaylist.Parameter(sinker, 2), "C is unused by the Sinker");
        Assert.AreEqual(PitchPlaylist.PackBuild(PitchType.Sinker, 1f, 0f, 0.5f),
            PitchPlaylist.NormalizeBuild(PitchType.Sinker, PitchPlaylist.PackBuild(PitchType.Slider, 1f, 0f, 0.9f)));
        Assert.AreEqual(1f, PitchPlaylist.Parameter(0x3FF, 0), "an out-of-range step is clamped");
        Assert.AreEqual(PitchPlaylist.PackBuild(PitchType.Knuckle, 0.5f, 0.5f, 0.5f),
            PitchPlaylist.PackBuild(PitchType.Knuckle, float.NaN, 0.5f, 0.5f), "a non-finite value is the default");
        PitchBuild unpacked = PitchPlaylist.Build(PitchPlaylist.Pack(4, PitchType.Knuckle, 16), PitchPlaylist.PackBuild(PitchType.Knuckle, 0f, 1f, 0.5f));
        Assert.AreEqual(PitchType.Knuckle, unpacked.pitchTypeId);
        Assert.AreEqual(0f, unpacked.parameterA);
        Assert.AreEqual(1f, unpacked.parameterB);
        Assert.AreEqual(0.5f, unpacked.parameterC);
    }

    [TsukimiTest]
    public void TheSameTypeIsSavedWithDifferentBuilds()
    {
        Mimic.Join();
        _stateKnown = true;
        int slow = PitchPlaylist.PackBuild(PitchType.Slider, 0f, 1f, 0f);
        int fast = PitchPlaylist.PackBuild(PitchType.Slider, 1f, 0.2f, 1f);
        int a = AddBuild(PitchType.Slider, 16, slow);
        int b = AddBuild(PitchType.Slider, 16, fast);
        Assert.AreEqual(2, PlaylistCount());
        Assert.AreEqual(slow, BuildOf(a));
        Assert.AreEqual(fast, BuildOf(b));
        // editing one replaces its build only; unused parameters are normalized on save
        int token = RequestSaveItem(a, PitchType.Sinker, 1, PitchPlaylist.PackBuild(PitchType.Slider, 0.25f, 0.75f, 0f));
        Assert.AreEqual(SaveDone, SaveResult(token));
        Assert.AreEqual(PitchPlaylist.PackBuild(PitchType.Sinker, 0.25f, 0.75f, 0.5f), BuildOf(a));
        Assert.AreEqual(PitchType.Sinker, PitchPlaylist.Type(PlaylistItem(IndexOfItem(a))));
        Assert.AreEqual(fast, BuildOf(b), "the other item keeps its build");
    }

    [TsukimiTest]
    public void BuildsMoveWithTheirItemsAndFreedSlotsAreCleared()
    {
        Mimic.Join();
        _stateKnown = true;
        RequestSequence(true);
        int buildA = PitchPlaylist.PackBuild(PitchType.Curve, 0f, 0f, 0.5f);
        int buildB = PitchPlaylist.PackBuild(PitchType.Curve, 1f, 1f, 0.5f);
        int buildC = PitchPlaylist.PackBuild(PitchType.Knuckle, 0.1f, 0.9f, 0.3f);
        int a = AddBuild(PitchType.Curve, 1, buildA);
        int b = AddBuild(PitchType.Curve, 2, buildB);
        int c = AddBuild(PitchType.Knuckle, 4, buildC);
        RequestMoveItem(a, 1);
        Assert.AreEqual(1, IndexOfItem(a));
        Assert.AreEqual(buildA, BuildOf(a), "Move Down");
        Assert.AreEqual(buildB, BuildOf(b));
        RequestMoveItem(c, -1);
        Assert.AreEqual(buildC, BuildOf(c), "Move Up");
        Assert.AreEqual(buildA, BuildOf(a));
        RequestDeleteItem(c);
        Assert.AreEqual(0, _playlistBuilds[2], "the freed slot holds no build");
        int d = AddItem(PitchType.Split, 8);
        Assert.AreEqual(PitchPlaylist.DefaultBuild, BuildOf(d), "a new item never inherits a deleted build");
        Assert.AreEqual(buildA, BuildOf(a));
        Assert.AreEqual(buildB, BuildOf(b));
    }

    [TsukimiTest]
    public void ShufflingKeepsEveryBuildWithItsItem()
    {
        Mimic.Join();
        _stateKnown = true;
        int[] ids = new int[10];
        for (int i = 0; i < 10; i++) ids[i] = AddBuild(i, 1 << (i % 9), PitchPlaylist.PackBuild(i, i * 0.1f, 1f - i * 0.1f, 0.05f * i));
        for (int lap = 0; lap < 3; lap++)
        {
            RequestSequence(true);
            RequestSequence(false);
            for (int i = 0; i < 10; i++)
            {
                int index = IndexOfItem(ids[i]);
                Assert.AreEqual(i, PitchPlaylist.Type(PlaylistItem(index)));
                Assert.AreEqual(PitchPlaylist.PackBuild(i, i * 0.1f, 1f - i * 0.1f, 0.05f * i), PlaylistBuild(index));
            }
        }
    }

    [TsukimiTest]
    public void PackedStateKeepsItsFieldsApart()
    {
        int state = PitchPlaylist.WithFlag(0, PitchPlaylist.RunningFlag, true);
        state = PitchPlaylist.WithFlag(state, PitchPlaylist.TimerFlag, true);
        int[] items = new int[10];
        int[] builds = new int[10];
        for (int i = 0; i < 10; i++) state = PitchPlaylist.Append(items, builds, state, PitchPlaylist.Pack(i + 1, 0, 16), PitchPlaylist.DefaultBuild);
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
        Assert.AreEqual(PitchType.FourSeam, PitchPlaylist.Type(item));
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
        int token = RequestSaveItem(0, 2, 16, PitchPlaylist.DefaultBuild);
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
        int token = RequestSaveItem(a, 5, 0x1F0, PitchPlaylist.DefaultBuild);
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
        int token = RequestSaveItem(a, 4, 32, PitchPlaylist.DefaultBuild);
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
        int token = RequestSaveItem(0, 0, 16, PitchPlaylist.DefaultBuild);
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
        int first = RequestSaveItem(0, 1, 2, PitchPlaylist.DefaultBuild);
        int second = RequestSaveItem(0, 2, 4, PitchPlaylist.DefaultBuild);
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
        int[] builds = new int[10];
        items[0] = PitchPlaylist.Pack(3, 0, 16);
        items[1] = PitchPlaylist.Pack(3, 1, 16);
        items[2] = PitchPlaylist.Pack(0, 1, 16);
        items[3] = PitchPlaylist.Pack(7, 2, 16);
        int state = PitchPlaylist.WithHistory(15 | PitchPlaylist.RunningFlag, 0x8);
        state = PitchPlaylist.Sanitize(items, builds, state);
        Assert.AreEqual(2, PitchPlaylist.Count(state), "count clamped, duplicate and zero ids removed");
        Assert.AreEqual(3, PitchPlaylist.Id(items[0]));
        Assert.AreEqual(7, PitchPlaylist.Id(items[1]));
        Assert.IsTrue(PitchPlaylist.Done(state, 1), "the history follows the surviving item");
        Assert.IsTrue(PitchPlaylist.Has(state, PitchPlaylist.RunningFlag));
        Assert.AreEqual(4, PitchPlaylist.FreeId(items, state, 3), "a used id is skipped");
    }
}
