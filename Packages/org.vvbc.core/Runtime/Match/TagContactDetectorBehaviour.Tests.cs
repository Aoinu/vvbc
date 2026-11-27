using Tsukimi;
using UnityEngine;

// Tag contact on the fielder's client: who may tag, the five runner regions, one event per
// continuous contact and the swept region. Runner geometry and the region are passed in.
public partial class TagContactDetectorBehaviour
{
    private const int Fielder = 21;        // home pitcher (home defends: slot 1 * 10 + 0)
    private const int Runner = 11;
    private static readonly Vector3 RunnerFeet = new Vector3(20f, 0f, 20f);

    private void ResetTagForTest()
    {
        match = null;
        ballQuery = null;
        feedback = null;
        for (int s = 0; s < Targets; s++) { _targets[s] = 0; _inContact[s] = false; _targetValid[s] = false; }
        _armed = false;
        _sensorPreviousValid = false;
        _contacts = 0;
        _releases = 0;
        _queued = 0;
        runnerSphereRadius = 0.05f;
        runnerCapsuleRadius = 0.2f;
        runnerCapsuleHeadMargin = 0.1f;
        releaseMargin = 0.03f;
        maxSweepDistance = 1.5f;
        SetTarget(0, Runner, Fielder);
        // Arms out sideways: left hand at x - 0.7, right hand at x + 0.7, feet under the body.
        Vector3[] spheres = new Vector3[SpheresPerTarget];
        bool[] valid = new bool[SpheresPerTarget];
        spheres[0] = RunnerFeet + new Vector3(-0.7f, 1.3f, 0f);
        spheres[1] = RunnerFeet + new Vector3(0.7f, 1.3f, 0f);
        spheres[2] = RunnerFeet + new Vector3(-0.1f, 0.1f, 0f);
        spheres[3] = RunnerFeet + new Vector3(0.1f, 0.1f, 0f);
        for (int i = 0; i < SpheresPerTarget; i++) valid[i] = true;
        SetRunnerGeometry(0, RunnerFeet, 1.5f, spheres, valid);
    }

    private void Frame(Vector3 center) { Step(center, 0.1f); }

    [TsukimiTest]
    public void OnlyADefenseFielderHoldingTheBallIsArmed()
    {
        ResetTagForTest();
        Assert.IsTrue(Arm(Fielder, 10, 1, ContactRules.HoldGlove, 3), "glove hold");
        ResetContacts();
        Assert.IsTrue(Arm(Fielder, 10, 1, ContactRules.HoldHand, 4), "hand hold");
        ResetContacts();
        Assert.IsFalse(Arm(Fielder, 10, 1, ContactRules.HoldFree, 0), "free ball (also in flight)");
        Assert.IsFalse(Arm(Fielder, 10, 1, ContactRules.HoldGlove, 0), "no confirmed hold sequence");
        Assert.IsFalse(Arm(12, 1, 1, ContactRules.HoldGlove, 3), "an offense player");
        Assert.IsFalse(Arm(40, -1, 1, ContactRules.HoldGlove, 3), "a spectator wearing a glove");
        Assert.IsFalse(Arm(30, 19, 1, ContactRules.HoldGlove, 3), "the defense's DH slot");
        SetTarget(0, 0, Fielder);
        Assert.IsFalse(Arm(Fielder, 10, 1, ContactRules.HoldGlove, 3), "no active runner");
        SetTarget(0, Fielder, Fielder);
        Assert.AreEqual(0, GetTarget(0), "the fielder is never their own target");
    }

    [TsukimiTest]
    public void CapsuleContactIsOneEvent()
    {
        ResetTagForTest();
        Frame(RunnerFeet + new Vector3(1f, 1f, 0f));
        Assert.AreEqual(0, GetContactCount(), "a metre away");
        Frame(RunnerFeet + new Vector3(0.25f, 1f, 0f));
        Assert.AreEqual(1, GetContactCount());
        Assert.AreEqual(ContactRules.PartCapsule, GetLastParts());
        Assert.AreEqual(Runner, GetLastTarget());
        for (int f = 0; f < 10; f++) Frame(RunnerFeet + new Vector3(0.2f, 1f + f * 0.01f, 0f));
        Assert.AreEqual(1, GetContactCount(), "a continuous contact never repeats");
        Assert.AreEqual(1, GetQueuedCount());
    }

    [TsukimiTest]
    public void HandsAndFeetAreRegionsOfTheRunner()
    {
        ResetTagForTest();
        Frame(RunnerFeet + new Vector3(0.75f, 1.3f, 0.5f));
        Frame(RunnerFeet + new Vector3(0.75f, 1.3f, 0.1f));
        Assert.AreEqual(1, GetContactCount(), "right hand");
        Assert.AreEqual(ContactRules.PartRightHand, GetLastParts());
        ResetTagForTest();
        Frame(RunnerFeet + new Vector3(-0.75f, 1.3f, 0.1f));
        Assert.AreEqual(ContactRules.PartLeftHand, GetLastParts());
        ResetTagForTest();
        Frame(RunnerFeet + new Vector3(0f, 0.1f, -0.1f));
        Assert.AreEqual(1, GetContactCount(), "foot and capsule at the ankle: one event");
        Assert.AreEqual(ContactRules.PartCapsule | ContactRules.PartLeftFoot | ContactRules.PartRightFoot, GetLastParts());
    }

    [TsukimiTest]
    public void AFullReleaseThenANewContactIsANewEvent()
    {
        ResetTagForTest();
        Frame(RunnerFeet + new Vector3(0.25f, 1f, 0f));
        Frame(RunnerFeet + new Vector3(0.32f, 1f, 0f));      // 0.02 beyond touching: within the margin
        Assert.IsTrue(IsInContact(0), "not released within the margin");
        Frame(RunnerFeet + new Vector3(0.6f, 1f, 0f));
        Assert.IsFalse(IsInContact(0));
        Assert.AreEqual(1, GetReleaseCount());
        Frame(RunnerFeet + new Vector3(0.25f, 1f, 0f));
        Assert.AreEqual(2, GetContactCount(), "touched again after a full release");
    }

    [TsukimiTest]
    public void AFastSwingThroughTheRunnerIsCaught()
    {
        ResetTagForTest();
        // Starts 0.7 m in front of the runner's right hand and ends 0.7 m behind it in one frame.
        Frame(RunnerFeet + new Vector3(1.2f, 1.3f, 0.7f));
        Frame(RunnerFeet + new Vector3(1.2f, 1.3f, -0.7f));
        Assert.AreEqual(0, GetContactCount(), "0.5 m past the hand: never touched");
        ResetTagForTest();
        Frame(RunnerFeet + new Vector3(0.7f, 1.3f, 0.7f));
        Frame(RunnerFeet + new Vector3(0.7f, 1.3f, -0.7f));
        Assert.AreEqual(1, GetContactCount(), "the sweep passed through the right hand");
        Assert.AreEqual(ContactRules.PartRightHand, GetLastParts());
        ResetTagForTest();
        Frame(RunnerFeet + new Vector3(0.7f, 1.3f, 1.0f));
        Frame(RunnerFeet + new Vector3(0.7f, 1.3f, -1.0f));   // 2 m in a frame: a teleport, not swept
        Assert.AreEqual(0, GetContactCount());
    }

    [TsukimiTest]
    public void OnlyTargetsWithKnownPositionsAreTested()
    {
        ResetTagForTest();
        _targetValid[0] = false;          // the runner's position could not be read this frame
        Frame(RunnerFeet + new Vector3(0.25f, 1f, 0f));
        Assert.AreEqual(0, GetContactCount());
        Assert.IsFalse(ContactRules.SweptSphereTouchesSphere(new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 1f), 0.1f, new Vector3(0.16f, 0f, 0.5f), 0.05f, 1.5f), "near is not touching");
    }
}
