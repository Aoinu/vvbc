using Tsukimi;
using UnityEngine;

// Base contact: five regions OR-ed per base, enter / exit only on the OR, bases independent.
// Geometry and regions are passed in (the zones and bones are read only in the event wrappers).
public partial class BaseTouchDetectorBehaviour
{
    private static readonly Vector3 Far = new Vector3(50f, 0f, 50f);

    // 1B at (20, 0.1, 20), a 0.4 x 0.2 x 0.4 box; 2B at (0, 0.1, 40); no Home or 3B.
    private void ResetDetectorForTest()
    {
        for (int b = 0; b < ContactRules.BaseCount; b++)
        {
            _hasBase[b] = false;
            _capsule[b] = false;
            _parts[b] = 0;
            _enters[b] = 0;
            _exits[b] = 0;
        }
        for (int i = 0; i < ContactRules.SphereCount; i++) SetSphere(i, Far, true);
        _touchMask = 0;
        _queued = 0;
        _missedCapsuleExits = 0;
        sphereRadius = 0.05f;
        activationDistance = 4f;
        capsuleReleaseMargin = 1f;
        SetBaseGeometry(1, new Vector3(20f, 0.1f, 20f), Quaternion.identity, new Vector3(0.2f, 0.1f, 0.2f));
        SetBaseGeometry(2, new Vector3(0f, 0.1f, 40f), Quaternion.identity, new Vector3(0.2f, 0.1f, 0.2f));
    }

    private static readonly Vector3 OnFirst = new Vector3(20f, 0.1f, 20f);
    private static readonly Vector3 NearFirst = new Vector3(20.5f, 0f, 20f);

    [TsukimiTest]
    public void CapsuleAloneTouchesAndLeaves()
    {
        ResetDetectorForTest();
        SetCapsuleFlag(1, true);
        Assert.AreEqual(2, Evaluate(NearFirst), "1B touched");
        Assert.AreEqual(1, GetEnterCount(1));
        Assert.AreEqual(ContactRules.TypeBaseTouchEnter, GetLastEventType());
        Assert.AreEqual(ContactRules.PartCapsule, GetLastEventParts());
        SetCapsuleFlag(1, false);
        Assert.AreEqual(0, Evaluate(NearFirst));
        Assert.AreEqual(1, GetExitCount(1));
        Assert.AreEqual(ContactRules.TypeBaseTouchExit, GetLastEventType());
        Assert.AreEqual(2, GetQueuedCount(), "one enter and one exit for the collector");
    }

    [TsukimiTest]
    public void EachHandAndFootTouchesOnItsOwn()
    {
        int[] parts = { ContactRules.PartLeftHand, ContactRules.PartRightHand, ContactRules.PartLeftFoot, ContactRules.PartRightFoot };
        for (int i = 0; i < ContactRules.SphereCount; i++)
        {
            ResetDetectorForTest();
            SetSphere(i, OnFirst, true);
            Evaluate(NearFirst);
            Assert.AreEqual(1, GetEnterCount(1));
            Assert.AreEqual(parts[i], GetParts(1));
            SetSphere(i, Far, true);
            Evaluate(NearFirst);
            Assert.AreEqual(1, GetExitCount(1));
        }
    }

    [TsukimiTest]
    public void SeveralRegionsAreOneTouchAndSwappingRegionsSendsNothing()
    {
        ResetDetectorForTest();
        SetCapsuleFlag(1, true);
        SetSphere(3, OnFirst, true);   // right foot
        Evaluate(NearFirst);
        Assert.AreEqual(1, GetEnterCount(1), "two regions at once: one enter");
        Assert.AreEqual(ContactRules.PartCapsule | ContactRules.PartRightFoot, GetParts(1));
        SetSphere(0, OnFirst, true);   // left hand joins
        Evaluate(NearFirst);
        SetSphere(3, Far, true);       // right foot leaves
        SetCapsuleFlag(1, false);
        Evaluate(NearFirst);
        Assert.AreEqual(1, GetEnterCount(1));
        Assert.AreEqual(0, GetExitCount(1), "the left hand still touches");
        Assert.AreEqual(ContactRules.PartLeftHand, GetParts(1));
        SetSphere(0, Far, true);
        Evaluate(NearFirst);
        Assert.AreEqual(1, GetExitCount(1), "the last region left");
        SetSphere(2, OnFirst, true);
        Evaluate(NearFirst);
        Assert.AreEqual(2, GetEnterCount(1), "touching the same base again is a new enter");
    }

    [TsukimiTest]
    public void BasesAreIndependent()
    {
        ResetDetectorForTest();
        SetSphere(0, OnFirst, true);
        SetSphere(1, new Vector3(0f, 0.1f, 40f), true);
        Assert.AreEqual(2 | 4, Evaluate(NearFirst));
        Assert.AreEqual(1, GetEnterCount(1));
        Assert.AreEqual(1, GetEnterCount(2));
        SetSphere(1, Far, true);
        Assert.AreEqual(2, Evaluate(NearFirst));
        Assert.AreEqual(1, GetExitCount(2));
        Assert.AreEqual(0, GetExitCount(1));
        Assert.AreEqual(0, GetEnterCount(0), "no Home base configured");
    }

    [TsukimiTest]
    public void AMissingBoneDisablesOnlyItsSphere()
    {
        ResetDetectorForTest();
        SetSphere(0, OnFirst, false);          // left hand bone not available
        Evaluate(NearFirst);
        Assert.AreEqual(0, GetEnterCount(1));
        SetSphere(2, OnFirst, true);
        Evaluate(NearFirst);
        Assert.AreEqual(ContactRules.PartLeftFoot, GetParts(1));
        Assert.IsFalse(ContactRules.ValidPoint(Vector3.zero), "a zero bone position is a missing bone");
    }

    [TsukimiTest]
    public void ARotatedBoxIsTestedInItsOwnSpace()
    {
        ResetDetectorForTest();
        Quaternion turn = Quaternion.Euler(0f, 45f, 0f);
        SetBaseGeometry(1, new Vector3(20f, 0.1f, 20f), turn, new Vector3(0.2f, 0.1f, 0.2f));
        // (0.22, 0, 0.22) from the centre would touch an axis-aligned box (0.028 m from its
        // corner), but along the rotated box's local axis it is 0.311 m out, beyond 0.2 + 0.05.
        Assert.IsTrue(ContactRules.SphereTouchesBox(new Vector3(20.22f, 0.1f, 20.22f), 0.05f, new Vector3(20f, 0.1f, 20f), Quaternion.identity, new Vector3(0.2f, 0.1f, 0.2f)));
        SetSphere(0, new Vector3(20.22f, 0.1f, 20.22f), true);
        Evaluate(NearFirst);
        Assert.AreEqual(0, GetEnterCount(1));
        // (0.24, 0, 0) is 0.17 m along both local axes: inside the rotated box.
        SetSphere(0, new Vector3(20.24f, 0.1f, 20f), true);
        Evaluate(NearFirst);
        Assert.AreEqual(1, GetEnterCount(1));
    }

    [TsukimiTest]
    public void TouchingTheSurfaceCountsAndJustBeyondDoesNot()
    {
        ResetDetectorForTest();
        SetSphere(0, new Vector3(20.25f, 0.1f, 20f), true);   // 0.05 from the face: touches
        Evaluate(NearFirst);
        Assert.AreEqual(1, GetEnterCount(1));
        SetSphere(0, new Vector3(20.26f, 0.1f, 20f), true);   // 0.06: apart
        Evaluate(NearFirst);
        Assert.AreEqual(1, GetExitCount(1));
        Assert.IsTrue(ContactRules.SphereTouchesBox(new Vector3(0.25f, 0f, 0f), 0.05f, Vector3.zero, Quaternion.identity, new Vector3(0.2f, 0.1f, 0.2f)));
        Assert.IsFalse(ContactRules.SphereTouchesBox(new Vector3(0.2501f, 0.1501f, 0f), 0.05f, Vector3.zero, Quaternion.identity, new Vector3(0.2f, 0.1f, 0.2f)), "a corner is measured diagonally");
    }

    [TsukimiTest]
    public void ACapsuleLeftInsideIsClearedOnceThePlayerIsAway()
    {
        ResetDetectorForTest();
        SetCapsuleFlag(1, true);
        Evaluate(NearFirst);
        Assert.AreEqual(1, GetEnterCount(1));
        // The exit never came (teleport, respawn): the player is now 10 m away.
        Evaluate(new Vector3(30f, 0f, 20f));
        Assert.IsFalse(GetCapsule(1));
        Assert.AreEqual(1, GetExitCount(1));
        Assert.AreEqual(1, GetMissedCapsuleExitCount());
        Assert.AreEqual(0, GetTouchMask());
    }

    [TsukimiTest]
    public void SamplingRunsOnlyNearABaseOrWhileTouching()
    {
        ResetDetectorForTest();
        Assert.IsTrue(ShouldTrack(new Vector3(22f, 0f, 22f)), "within 4 m of 1B");
        Assert.IsFalse(ShouldTrack(new Vector3(10f, 0f, 10f)));
        SetCapsuleFlag(2, true);
        Assert.IsTrue(ShouldTrack(new Vector3(10f, 0f, 10f)), "a capsule inside keeps it running");
        SetCapsuleFlag(2, false);
        _touchMask = 2;
        Assert.IsTrue(ShouldTrack(new Vector3(10f, 0f, 10f)), "until the exit is sent");
    }

    // The zones' trigger stay sets the capsule flag again and again (or after a missed enter);
    // only the OR of the regions sends an event.
    [TsukimiTest]
    public void StaysFillInAMissedEnterAndNeverRepeatIt()
    {
        ResetDetectorForTest();
        for (int i = 0; i < 5; i++)
        {
            SetCapsuleFlag(1, true);      // stay, the enter was missed
            Evaluate(NearFirst);
        }
        Assert.AreEqual(1, GetEnterCount(1), "one enter however many stays");
        SetCapsuleFlag(1, false);         // exit
        Evaluate(NearFirst);
        Assert.AreEqual(1, GetExitCount(1));
        SetCapsuleFlag(1, true);          // a stay after coming back in
        Evaluate(NearFirst);
        Assert.AreEqual(2, GetEnterCount(1), "re-entering is a new touch");
        SetCapsuleFlag(1, false);
        SetCapsuleFlag(1, true);          // exit and stay out of order within one frame
        Evaluate(NearFirst);
        Assert.AreEqual(2, GetEnterCount(1), "no flicker between frames");
        Assert.AreEqual(1, GetExitCount(1));
    }
}
