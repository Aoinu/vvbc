using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

public partial class ManualPitchControlSettingsBehaviour
{
    [TsukimiTest]
    public void TheDefaultsAreNoPenaltyCap60AndSigma40Centimetres()
    {
        Assert.AreEqual(0f, penaltyStrength);
        Assert.AreEqual(60f, timingCap);
        Assert.AreEqual(0.40f, controlSigma);
        Assert.AreEqual("Control penalty  0%", StrengthLabel());
        Assert.AreEqual("Error 1 sigma at score 0  40 cm", MaxErrorLabel());
        Assert.AreEqual("Timing cap (not ideal)  60", CapLabel());
    }

    [TsukimiTest]
    public void AnEditIsSnappedClampedAndReachesTheOthers()
    {
        VRCPlayerApi me = Mimic.Join();
        VRCPlayerApi other = Mimic.Join();
        Submit(0.33f, 120f, 0.257f);
        Assert.IsTrue(Mathf.Abs(penaltyStrength - 0.35f) < 0.0001f, "5% steps: " + penaltyStrength);
        Assert.AreEqual(100f, timingCap, "clamped");
        Assert.IsTrue(Mathf.Abs(controlSigma - 0.26f) < 0.0001f, "1 cm steps: " + controlSigma);
        Mimic.Deliver();
        Mimic.Become(other);
        Assert.IsTrue(Mathf.Abs(penaltyStrength - 0.35f) < 0.0001f, "the other client: " + penaltyStrength);
        Assert.AreEqual(100f, timingCap);
        Submit(1f, 40f, float.NaN);
        Assert.IsTrue(Networking.IsOwner(other, gameObject), "an edit takes Ownership");
        Assert.IsTrue(Mathf.Abs(controlSigma - 0.26f) < 0.0001f, "a non-finite value keeps the last");
        Mimic.Deliver();
        Mimic.Become(me);
        Assert.AreEqual(1f, penaltyStrength);
        Assert.AreEqual(40f, timingCap);
    }

    [TsukimiTest]
    public void ALateJoinerReceivesTheCurrentSettings()
    {
        Mimic.Join();
        Submit(0.5f, 30f, 0.6f);
        Mimic.Deliver();
        VRCPlayerApi late = Mimic.Join();
        Mimic.Become(late);
        Assert.AreEqual(0.5f, penaltyStrength);
        Assert.AreEqual(30f, timingCap);
        Assert.IsTrue(Mathf.Abs(controlSigma - 0.6f) < 0.0001f);
    }
}
