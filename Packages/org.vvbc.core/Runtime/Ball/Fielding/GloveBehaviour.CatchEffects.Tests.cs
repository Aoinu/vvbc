using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

public partial class GloveBehaviour
{
    [TsukimiTest]
    public void OnlyAPitchAtOrAboveTheThresholdUsesTheFastPitchClip()
    {
        Assert.AreEqual(130f, fastPitchThresholdKmh);
        Assert.AreEqual(CatchClipNormal, CatchClipChoice(true, 129f, true, true), "pitch 129");
        Assert.AreEqual(CatchClipFastPitch, CatchClipChoice(true, 130f, true, true), "pitch 130 is included");
        Assert.AreEqual(CatchClipFastPitch, CatchClipChoice(true, 145f, true, true), "pitch 145");
        Assert.AreEqual(CatchClipNormal, CatchClipChoice(false, 145f, true, true), "hit, throw or unknown flight at 145");
        Assert.AreEqual(CatchClipNormal, CatchClipChoice(true, float.NaN, true, true), "an invalid speed never selects the fast clip");
    }

    // A bounced pitch keeps its flight kind; only the speed at the catch decides.
    [TsukimiTest]
    public void ABouncedPitchIsJudgedByItsSpeedAtTheCatch()
    {
        Assert.AreEqual(CatchClipFastPitch, CatchClipChoice(true, CatchSpeedKmh(36.2f), true, true), "130.3 km/h after the bounce");
        Assert.AreEqual(CatchClipNormal, CatchClipChoice(true, CatchSpeedKmh(30f), true, true), "108 km/h after the bounce");
    }

    [TsukimiTest]
    public void MissingClipsFallBackToNormalThenToSilence()
    {
        Assert.AreEqual(CatchClipNormal, CatchClipChoice(true, 150f, false, true), "no fast-pitch clip");
        Assert.AreEqual(CatchClipNone, CatchClipChoice(true, 150f, false, false), "no clip at all");
        Assert.AreEqual(CatchClipNone, CatchClipChoice(false, 50f, true, false));
        Assert.IsNull(CatchClip(true, 150f), "unassigned clips select nothing");
    }

    [TsukimiTest]
    public void SpeedConvertsToKmhAndInvalidSpeedsBecomeZero()
    {
        Assert.IsTrue(Mathf.Abs(CatchSpeedKmh(10f) - 36f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(CatchSpeedKmh(130f / 3.6f) - 130f) < 0.001f);
        Assert.AreEqual(0f, CatchSpeedKmh(0f));
        Assert.AreEqual(0f, CatchSpeedKmh(-1f), "unknown");
        Assert.AreEqual(0f, CatchSpeedKmh(float.NaN));
        Assert.AreEqual(0f, CatchSpeedKmh(float.PositiveInfinity));
    }

    [TsukimiTest]
    public void VolumeAndPitchRiseWithSpeedInsideTheirLimits()
    {
        Assert.AreEqual(catchMinVolume, CatchVolume(0f), "0 km/h");
        Assert.AreEqual(catchMinPitch, CatchPitch(0f));
        Assert.AreEqual(catchMaxVolume, CatchVolume(200f));
        Assert.AreEqual(catchMaxPitch, CatchPitch(200f));
        float midVolume = CatchVolume(90f);
        float midPitch = CatchPitch(90f);
        Assert.IsTrue(Mathf.Abs(midVolume - (catchMinVolume + catchMaxVolume) * 0.5f) < 0.0001f, "halfway between 30 and 150 km/h");
        Assert.IsTrue(Mathf.Abs(midPitch - (catchMinPitch + catchMaxPitch) * 0.5f) < 0.0001f);
        Assert.IsTrue(CatchVolume(140f) > CatchVolume(100f), "faster is louder");
        Assert.IsTrue(CatchPitch(140f) > CatchPitch(100f), "faster is higher");
    }

    [TsukimiTest]
    public void OutOfRangeAndInvalidSettingsNeverReachTheAudioSource()
    {
        catchMinVolume = -2f;
        catchMaxVolume = 5f;
        catchMinPitch = 0f;
        catchMaxPitch = 9f;
        Assert.AreEqual(0f, CatchVolume(0f));
        Assert.AreEqual(1f, CatchVolume(500f));
        Assert.AreEqual(MinCatchPitch, CatchPitch(0f));
        Assert.AreEqual(MaxCatchPitch, CatchPitch(500f));
        catchMaxVolume = float.NaN;
        catchMaxPitch = float.PositiveInfinity;
        float volume = CatchVolume(150f);
        float pitch = CatchPitch(150f);
        Assert.IsTrue(volume >= 0f && volume <= 1f, "finite volume");
        Assert.IsTrue(pitch >= MinCatchPitch && pitch <= MaxCatchPitch, "finite pitch");
        catchSlowSpeedKmh = 100f;
        catchFastSpeedKmh = 100f;
        Assert.IsTrue(FieldingMath.Finite(CatchVolume(100f)), "an empty speed range stays finite");
    }

    [TsukimiTest]
    public void HapticsUseTheHandOppositeTheThrowingHand()
    {
        Assert.AreEqual(VRC_Pickup.PickupHand.Left, GloveHapticHand(HandRight), "right-handed thrower wears the glove on the left");
        Assert.AreEqual(VRC_Pickup.PickupHand.Right, GloveHapticHand(HandLeft));
    }

    // IsUserInVR is not modelled by the runner, so the desktop skip is checked in ClientSim/VR.
    [TsukimiTest]
    public void ARemoteOrDisabledCatchDoesNotVibrate()
    {
        Mimic.Join();
        Start();
        Assert.IsTrue(IsLocalOwner());
        catchHaptics = false;
        PlayCatch(Vector3.one, 40f, true, true);
        Assert.AreEqual(0, catchHapticsPlayed, "haptics disabled");
        catchHaptics = true;
        PlayCatch(Vector3.one, 40f, true, false);
        Assert.AreEqual(0, catchHapticsPlayed, "remote catch");
        Assert.AreEqual(0, catchSoundsPlayed, "no clip or source assigned: skipped safely");
    }
}
