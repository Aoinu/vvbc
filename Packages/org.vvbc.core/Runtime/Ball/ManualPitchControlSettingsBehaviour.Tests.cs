using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

public partial class ManualPitchControlSettingsBehaviour
{
    [TsukimiTest]
    public void TheDefaultIsCap60()
    {
        Assert.AreEqual(60f, timingCap);
        Assert.AreEqual("Timing cap (not ideal)  60", CapLabel());
    }

    [TsukimiTest]
    public void AnEditIsSnappedClampedAndReachesTheOthers()
    {
        VRCPlayerApi me = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        Submit(120f);
        Assert.AreEqual(100f, timingCap, "clamped");
        Submit(33f);
        Assert.AreEqual(35f, timingCap, "5-point steps");
        Mimic.Deliver();
        Mimic.Become(other);
        Assert.AreEqual(35f, timingCap, "the other client");
        Submit(40f);
        Assert.IsTrue(Networking.IsOwner(other, gameObject), "an edit takes Ownership");
        Submit(float.NaN);
        Assert.AreEqual(40f, timingCap, "a non-finite value keeps the last");
        Mimic.Deliver();
        Mimic.Become(me);
        Assert.AreEqual(40f, timingCap);
    }

    [TsukimiTest]
    public void ALateJoinerReceivesTheCurrentCap()
    {
        Mimic.Join();
        Submit(30f);
        Mimic.Deliver();
        VRCPlayerApi late = Mimic.Join();
        Mimic.Become(late);
        Assert.AreEqual(30f, timingCap);
    }
}
