using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// The PlayerObject event stream: ids, plays, the ring that never drops an unsettled event, the
// receiver's table settling events, and the synced copy other clients read. The clock and the
// live play are passed in (RecordAt / ReconcileAt), as the wrappers do with the real ones.
public partial class PlayerEventCollectorBehaviour
{
    private static readonly Vector3 Here = new Vector3(20f, 0.1f, 20f);

    private void ResetCollectorForTest(int source)
    {
        // The test world places a controller, a receiver and a highlight of its own; these tests
        // pass their answers in.
        match = null;
        receiver = null;
        highlight = null;
        _sourceId = source;
        for (int i = 0; i < Capacity; i++)
        {
            _status[i] = ContactEvents.SlotFree;
            _order[i] = 0;
            _invalidReason[i] = 0;
        }
        for (int i = 0; i < _events.Length; i++) _events[i] = 0;
        _orderNext = 0;
        _seq = 0;
        _seqPlayId = 0;
        _overflowCount = 0;
        _evictedUnassigned = 0;
        _refusedNoPlay = 0;
        _touchMask = 0;
        unassignedTimeoutSeconds = 3f;
    }

    private int Touch(int play, double time) { return RecordAt(ContactRules.TypeBaseTouchEnter, 0, 1, ContactRules.PartLeftFoot, Here, 0, 0, time, play, 100.0); }
    private int Tag(int play, double time) { return RecordAt(ContactRules.TypeTagContactEnter, 11, ContactRules.NoBase, ContactRules.PartCapsule, Here, ContactRules.HoldGlove, 4, time, play, 100.0); }
    private int Seq(int slot) { return GetRecordField(slot, ContactRules.RecSeq); }
    private int PlayOf(int slot) { return GetRecordField(slot, ContactRules.RecPlay); }

    private static int[] TableSettling(int source, int upTo)
    {
        int[] t = new int[ContactEvents.TableLength];
        for (int s = 1; s <= upTo; s++) ContactEvents.Settle(t, source, s);
        return t;
    }

    [TsukimiTest]
    public void SequencesStartAtOnePerPlayForEveryType()
    {
        ResetCollectorForTest(21);
        int a = Touch(5, 101.0);
        int b = Tag(5, 102.0);
        int c = RecordAt(ContactRules.TypeBaseTouchExit, 0, 1, 0, Here, 0, 0, 103.0, 5, 100.0);
        Assert.AreEqual(1, Seq(a));
        Assert.AreEqual(2, Seq(b), "touches and tags share one sequence");
        Assert.AreEqual(3, Seq(c));
        Assert.AreEqual(5, PlayOf(b));
        int d = Touch(6, 201.0);
        Assert.AreEqual(1, Seq(d), "a new play starts again at 1");
        Assert.AreEqual(6, PlayOf(d));
        Assert.AreEqual(ContactEvents.SlotRecorded, GetSlotStatus(d));
        Assert.AreEqual(102.0, GetRecordTimes()[b], "the detection time, never a receive time");
        Assert.AreEqual(ContactRules.HoldGlove, GetRecordField(b, ContactRules.RecHoldMode));
        Assert.AreEqual(11, GetRecordField(b, ContactRules.RecTarget));
    }

    [TsukimiTest]
    public void ATagNeedsALivePlayAndNothingIsPlacedBeforeItsStart()
    {
        ResetCollectorForTest(21);
        Assert.AreEqual(-1, Tag(0, 101.0), "no live play: a tag is never guessed into one");
        Assert.AreEqual(-1, Touch(5, 80.0), "earlier than the play's start by more than the skew");
        Assert.AreEqual(2, GetRefusedNoPlayCount());
        Assert.AreEqual(0, GetCurrentSequence(), "nothing refused used a sequence");
    }

    [TsukimiTest]
    public void AWaitingEventIsPlacedOnlyInAPlayThatStartedBeforeIt()
    {
        ResetCollectorForTest(21);
        int early = Touch(0, 98.0);
        int late = Touch(0, 103.0);
        Assert.AreEqual(ContactEvents.SlotUnassigned, GetSlotStatus(early));
        Assert.AreEqual(0, PlayOf(early), "no id while it waits");
        Assert.IsFalse(ReconcileAt(0, 0.0, 4, 100.0), "still within the timeout");
        // Play 5 turns out to have started at 100: the touch at 98 was before it, 103 in it.
        Assert.IsTrue(ReconcileAt(5, 100.0, 4, 104.0));
        Assert.AreEqual(ContactEvents.SlotInvalid, GetSlotStatus(early));
        Assert.AreEqual(ContactEvents.InvalidNoPlay, GetInvalidReason(early));
        Assert.AreEqual(5, PlayOf(late));
        Assert.AreEqual(1, Seq(late));
        Assert.AreEqual(ContactRules.FlagInvalid, GetRecordField(early, ContactRules.RecFlags), "the receiver never reads it");
        int waiting = Touch(0, 300.0);
        Assert.IsTrue(ReconcileAt(0, 0.0, 5, 304.0), "no play came within the timeout");
        Assert.AreEqual(ContactEvents.SlotInvalid, GetSlotStatus(waiting));
    }

    [TsukimiTest]
    public void OpenEventsOfACommittedPlayAreWithdrawn()
    {
        ResetCollectorForTest(21);
        int a = Touch(5, 101.0);
        Assert.IsFalse(ReconcileAt(5, 100.0, 4, 102.0));
        Assert.IsTrue(ReconcileAt(0, 0.0, 5, 110.0), "play 5 was committed");
        Assert.AreEqual(ContactEvents.SlotInvalid, GetSlotStatus(a));
        Assert.AreEqual(ContactEvents.InvalidStalePlay, GetInvalidReason(a));
    }

    [TsukimiTest]
    public void AnUnsettledEventIsNeverOverwritten()
    {
        ResetCollectorForTest(21);
        for (int i = 0; i < Capacity; i++) Assert.IsTrue(Touch(5, 101.0 + i) >= 0);
        Assert.AreEqual(-1, Touch(5, 130.0), "the ring is full of unsettled events");
        Assert.AreEqual(1, GetOverflowCount());
        Assert.AreEqual(Capacity, GetCurrentSequence(), "the refused event took no sequence");
        // The receiver settled 1..4.
        Assert.AreEqual(4, ApplyAcks(5, TableSettling(21, 4)));
        Assert.AreEqual(4, CountSlots(ContactEvents.SlotAccepted));
        int reused = Touch(5, 131.0);
        Assert.AreEqual(0, reused, "the oldest settled slot is reused");
        Assert.AreEqual(Capacity + 1, Seq(reused));
        for (int i = 0; i < Capacity; i++)
            if (i != reused && Seq(i) > 4) Assert.AreEqual(ContactEvents.SlotRecorded, GetSlotStatus(i), "unsettled events stay");
    }

    [TsukimiTest]
    public void AWaitingDetectionGivesWayBeforeAnEventIsRefused()
    {
        ResetCollectorForTest(21);
        for (int i = 0; i < Capacity - 1; i++) Touch(5, 101.0 + i);
        int waiting = Touch(0, 120.0);
        Assert.AreEqual(ContactEvents.SlotUnassigned, GetSlotStatus(waiting));
        int next = Touch(5, 121.0);
        Assert.AreEqual(waiting, next, "the detection without an id is dropped first");
        Assert.AreEqual(1, GetEvictedUnassignedCount());
        Assert.AreEqual(0, GetOverflowCount());
    }

    [TsukimiTest]
    public void OnlyThisSourceAndPlayAreSettled()
    {
        ResetCollectorForTest(21);
        int a = Touch(5, 101.0);
        int b = Touch(5, 102.0);
        Assert.AreEqual(0, ApplyAcks(5, TableSettling(22, 2)), "another source");
        Assert.AreEqual(0, ApplyAcks(4, TableSettling(21, 2)), "another play");
        int[] table = new int[ContactEvents.TableLength];
        ContactEvents.Settle(table, 21, 2);
        Assert.AreEqual(1, ApplyAcks(5, table), "settled out of order");
        Assert.AreEqual(ContactEvents.SlotRecorded, GetSlotStatus(a));
        Assert.AreEqual(ContactEvents.SlotAccepted, GetSlotStatus(b));
        Assert.AreEqual(0, ApplyAcks(5, table), "a repeated table settles nothing twice");
        Assert.AreEqual(1, CountOpen(5), "seq 1 is still to be resent");
    }

    [TsukimiTest]
    public void OtherClientsReceiveTheSameIdsAndAResendKeepsThem()
    {
        VRCPlayerApi owner = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        ResetCollectorForTest(owner.playerId);
        int slot = Touch(5, 101.0);
        RequestSerialization();
        Mimic.Deliver();
        Assert.AreEqual(ContactEvents.SlotSent, GetSlotStatus(slot), "a successful send marks it sent");
        Mimic.Become(other);
        Assert.AreEqual(owner.playerId, GetSourceId());
        Assert.AreEqual(1, GetRecordField(slot, ContactRules.RecSeq));
        Assert.AreEqual(5, GetRecordField(slot, ContactRules.RecPlay));
        Assert.AreEqual(101.0, GetRecordTimes()[slot]);
        Mimic.Become(owner);
        RequestSerialization();      // a resend
        Mimic.Deliver();
        Mimic.Become(other);
        Assert.AreEqual(1, GetRecordField(slot, ContactRules.RecSeq), "a resend never renumbers");
    }

    [TsukimiTest]
    public void ALateJoinerSeesTheCurrentContactOnly()
    {
        VRCPlayerApi owner = Mimic.Join();
        ResetCollectorForTest(owner.playerId);
        Touch(5, 101.0);
        SetTouchMaskLocal(2);
        RequestSerialization();
        Mimic.Deliver();
        VRCPlayerApi late = Mimic.Join();
        Mimic.Become(late);
        Assert.AreEqual(2, GetTouchMask(), "1B is touched now");
        Assert.AreEqual(1, GetRecordField(0, ContactRules.RecSeq), "the history is data, not a replay");
    }
}
