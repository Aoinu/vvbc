using Tsukimi;
using UnityEngine;

// The pitcher's perks on a hand pitch (docs/development/player-perks.md): Ball Speed (4-Seam
// limit), Spin (profile) and Control (sigma at score 0). The perks are given as the values the
// mound reads from them (SetPerkAbilities), as at binding and on each change.
public partial class ManualPitchBehaviour
{
    private const float Rpm = 60f / (2f * Mathf.PI);

    private float CurveReleaseSpeedKmh(float curveOutput)
    {
        LockForTest(new Vector3(0f, 0.75f, 0f));
        if (!CurveReleaseForTest(Vector3.zero, IdealHand(TestChest), TowardPlate(), curveOutput)) return -1f;
        return releaseVelocity.magnitude * 3.6f;
    }

    private bool PerkPitcherForTest()
    {
        ActiveForTest(GloveBehaviour.HandRight);
        diagnosticIdealSpin = true;
        return CalibrateForTest(Overhand);
    }

    // ---- Ball Speed ----

    [TsukimiTest]
    public void TheBallSpeedPerkSetsTheFourSeamLimit()
    {
        Mimic.Join();
        Assert.IsTrue(PerkPitcherForTest());
        Assert.IsTrue(Near(CurveReleaseSpeedKmh(1f), 120f, 0.01f), "no perk system: the mound's 120 km/h, the same as the standard");
        SetPerkAbilities(120f, 0, 0.9f);
        Assert.IsTrue(Near(CurveReleaseSpeedKmh(1f), 120f, 0.01f), "perk system, no perk: the 120 km/h standard");
        Assert.IsTrue(Near(_lockSolverSpeedKmh, 120f * 0.97f, 0.01f), "the solver aims with the same limit");
        float[] caps = { 105f, 140f, 150f, 160f };
        for (int i = 0; i < caps.Length; i++)
        {
            SetPerkAbilities(caps[i], 0, 0.9f);
            Assert.IsTrue(Near(CurveReleaseSpeedKmh(1f), caps[i], 0.01f), "full arm: the limit " + caps[i]);
            Assert.IsTrue(Near(_lockSolverSpeedKmh, caps[i] * 0.97f, 0.01f));
            Assert.IsTrue(Near(CurveReleaseSpeedKmh(0.5f), caps[i] * 0.5f, 0.01f), "the arm-speed curve scales it as before");
        }
        Assert.AreEqual(120f, maxPitchSpeedKmh, "the Inspector setting is not changed");
    }

    [TsukimiTest]
    public void TheTypeMultiplierStaysOnTopOfTheLimit()
    {
        Mimic.Join();
        Assert.IsTrue(PerkPitcherForTest());
        SetPerkAbilities(160f, 0, 0.9f);
        SelectPitchTypeAt(PitchType.Curve, Vector3.zero, 0f);
        float multiplier = PitchPropertiesCalculator.SpeedMultiplier(BuildFor(PitchType.Curve));
        Assert.IsTrue(Near(CurveReleaseSpeedKmh(1f), 160f * multiplier, 0.01f), "a Curve is 160 x its multiplier, not 160");
        SelectPitchTypeAt(PitchType.Knuckle, Vector3.zero, 0f);
        Assert.IsTrue(Near(CurveReleaseSpeedKmh(1f), 160f * 0.65f, 0.05f));
    }

    [TsukimiTest]
    public void AReleaseUsesTheLimitItsCourseWasSolvedWith()
    {
        Mimic.Join();
        Assert.IsTrue(PerkPitcherForTest());
        SetPerkAbilities(140f, 0, 0.9f);
        LockForTest(new Vector3(0f, 0.75f, 0f));
        SetPerkAbilities(105f, 0, 0.9f);
        Assert.IsTrue(CurveReleaseForTest(Vector3.zero, IdealHand(TestChest), TowardPlate(), 1f));
        Assert.IsTrue(Near(releaseVelocity.magnitude * 3.6f, 140f, 0.01f), "the course's limit, never another one than the solver's");
    }

    [TsukimiTest]
    public void APerkChangeDropsEveryCourseSolvedBeforeIt()
    {
        Mimic.Join();
        Assert.IsTrue(PerkPitcherForTest());
        SetPerkAbilities(120f, 0, 0.9f);
        DefaultCourseForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(CourseFor(_selectedType).ready);
        SetPerkAbilities(150f, 2, 0.4f);
        OnPerksChangedAt(Vector3.zero, 0f);
        Assert.IsFalse(CourseFor(_selectedType).ready, "the default course is solved again");
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(CourseFor(_selectedType).ready);
        SetPerkAbilities(105f, 0, 0.9f);
        OnPerksChangedAt(TestGravity, 0.02f);
        Assert.IsFalse(state == StateLockedReady, "the lock is solved again (or dropped without a solver)");
        Assert.IsFalse(CourseFor(_selectedType).ready);
    }

    // ---- Spin ----

    [TsukimiTest]
    public void TheSpinProfileGivesEachTypeItsDesignValue()
    {
        Mimic.Join();
        Assert.IsTrue(PerkPitcherForTest());
        int[] stages = { -1, 0, 1, 2, 3 };
        float[] fourSeam = { 2000f, 2200f, 2300f, 2500f, 2800f };
        float[] split = { 1300f, 900f, 650f, 400f, 180f };
        float[] change = { 1800f, 1650f, 1550f, 1450f, 1350f };
        float[] knuckle = { 0.030f, 0.040f, 0.045f, 0.050f, 0.055f };
        float[] breaking = { 0.75f, 1f, 1.15f, 1.27f, 1.40f };
        for (int i = 0; i < stages.Length; i++)
        {
            SetPerkAbilities(120f, stages[i], 0.9f);
            Assert.IsTrue(Near(IdealFor(PitchType.FourSeam).spinRpm, fourSeam[i], 0.01f), "4-Seam " + stages[i]);
            Assert.IsTrue(Near(IdealFor(PitchType.Split).spinRpm, split[i], 0.01f), "Split " + stages[i]);
            Assert.IsTrue(Near(IdealFor(PitchType.Change).spinRpm, change[i], 0.01f), "Change " + stages[i]);
            PitchProperties k = IdealFor(PitchType.Knuckle);
            Assert.IsTrue(Near(k.wakeLocal.coefficient, knuckle[i], 0.00001f), "Knuckle SSW " + stages[i]);
            PitchProperties standardKnuckle = PitchPropertiesCalculator.Calculate(BuildFor(PitchType.Knuckle), PitchPropertiesCalculator.DefaultBaseSpeedMps, 2200f);
            Assert.IsTrue(Near(k.spinRpm, standardKnuckle.spinRpm, 0.001f), "the Knuckle's rpm is unchanged");
            Assert.IsTrue(Near(k.wakeLocal.coneAngle, standardKnuckle.wakeLocal.coneAngle, 0.00001f), "and its cone");
            int[] general = { PitchType.TwoSeam, PitchType.Sinker, PitchType.Cutter, PitchType.Slider, PitchType.Sweeper, PitchType.Curve };
            for (int t = 0; t < general.Length; t++)
            {
                PitchProperties standard = PitchPropertiesCalculator.Calculate(BuildFor(general[t]), PitchPropertiesCalculator.DefaultBaseSpeedMps, 2200f);
                PitchProperties p = IdealFor(general[t]);
                Assert.IsTrue(Near(p.spinRpm, standard.spinRpm * breaking[i], 0.01f), "type " + general[t] + " stage " + stages[i]);
                Assert.IsTrue(Vector3.Distance(p.spinAxisLocal, standard.spinAxisLocal) < 0.00001f, "the axis is the type's own");
                Assert.IsTrue(Near(p.wakeLocal.coefficient, standard.wakeLocal.coefficient, 0.00001f), "and its SSW");
            }
            Assert.IsTrue(Near(FailureFlight().spinRpm, failureSpinRatio * fourSeam[i], 0.01f), "a failed pitch keeps the pitcher's own 4-Seam");
        }
    }

    [TsukimiTest]
    public void WithoutThePerkSystemTheMoundSpinIsUsed()
    {
        Mimic.Join();
        Assert.IsTrue(PerkPitcherForTest());
        Assert.IsTrue(Near(IdealFor(PitchType.FourSeam).spinRpm, 2200f, 0.01f), "the mound's fourSeamSpinRpm, the standard 2200");
        Assert.IsTrue(Near(IdealFor(PitchType.Split).spinRpm, 2200f * 0.18f, 0.01f), "the calculator's own Split");
        PitchProperties batting = PitchPropertiesCalculator.Calculate(BuildFor(PitchType.Split), PitchPropertiesCalculator.DefaultBaseSpeedMps, 2500f);
        SetPerkAbilities(120f, 3, 0.9f);
        PitchProperties afterPerk = PitchPropertiesCalculator.Calculate(BuildFor(PitchType.Split), PitchPropertiesCalculator.DefaultBaseSpeedMps, 2500f);
        Assert.AreEqual(batting.spinRpm, afterPerk.spinRpm, "the batting pitcher's calculator is untouched");
    }

    [TsukimiTest]
    public void ALeftHandersProfileIsTheMirrorImage()
    {
        Mimic.Join();
        Assert.IsTrue(PerkPitcherForTest());
        SetPerkAbilities(120f, 2, 0.9f);
        PitchProperties right = IdealFor(PitchType.Slider);
        _activeHand = GloveBehaviour.HandLeft;
        PitchProperties left = IdealFor(PitchType.Slider);
        Assert.IsTrue(Near(left.spinRpm, right.spinRpm, 0.001f));
        Assert.IsTrue(Vector3.Distance(left.spinAxisLocal, new Vector3(right.spinAxisLocal.x, -right.spinAxisLocal.y, -right.spinAxisLocal.z)) < 0.00001f);
    }

    [TsukimiTest]
    public void TheSolverAndTheReleaseShareTheProfile()
    {
        Mimic.Join();
        Assert.IsTrue(PerkPitcherForTest());
        SetPerkAbilities(120f, 3, 0.9f);
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(Near(_lockIdeal.spinRpm, 2800f, 0.01f), "the lock is solved with the profile");
        Assert.IsTrue(CurveReleaseForTest(Vector3.zero, IdealHand(TestChest), TowardPlate(), 1f));
        Assert.IsTrue(Near(releaseSpin.magnitude * Rpm, 2800f, 0.5f), "and the pitch leaves with it");
        // A failed release blends toward the same pitcher's failure flight.
        diagnosticIdealSpin = false;
        SelectPitchTypeAt(PitchType.Slider, Vector3.zero, 0f);
        PitchProperties blended = ManualPitchQuality.Blend(IdealFor(PitchType.Slider), FailureFlight(), 0f);
        Assert.IsTrue(Near(blended.spinRpm, failureSpinRatio * 2800f, 0.01f));
    }

    // ---- Control ----

    // The crossing offset that gives control score 85 on the test plate (on the right edge's axis).
    private float Score85Offset()
    {
        float halfDiagonal = Mathf.Sqrt(releasePlateWidth * releasePlateWidth + releasePlateHeight * releasePlateHeight) * 0.5f;
        return 0.15f * halfDiagonal;
    }

    private float ControlErrorFor(Vector2 offset)
    {
        LockForTest(new Vector3(0f, 0.75f, 0f));
        SetTimingCap(60f);
        SetControlDraw(0.3f, 1f, 1f, 1f, 1f);
        Vector3 hand = HandAt(TestChest, _calPosition);
        CrossForTest(offset, TowardPlate(), IdealHand(TestChest));
        if (!ResolveReleaseWithCurveOutputAt(hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, 1f)) return -1f;
        return _resultDrawError.magnitude;
    }

    [TsukimiTest]
    public void TheControlPerkScalesTheErrorForTheSameScoreAndDraw()
    {
        Mimic.Join();
        Assert.IsTrue(PerkPitcherForTest());
        Vector2 offset = new Vector2(Score85Offset(), 0f);
        float sceneError = ControlErrorFor(offset);
        Assert.IsTrue(Near(_resultScore, 85f, 0.05f), "score " + _resultScore);
        Assert.IsTrue(Near(_resultSigma, 0.9f * 0.15f, 0.0005f), "no perk system: the mound's 0.90 m, the same as the standard");
        Assert.IsTrue(Near(sceneError, 0.135f * ReleasePlateMath.MaxSigmas, 0.0005f), "the sigma is drawn in full: no strength factor");
        Assert.IsTrue(ControlText().Contains("mound setting"), ControlText());
        float[] sigmas = { 1.30f, 0.90f, 0.60f, 0.40f, 0.18f };
        float[] at85 = { 0.195f, 0.135f, 0.090f, 0.060f, 0.027f };
        float previous = float.MaxValue;
        for (int i = 0; i < sigmas.Length; i++)
        {
            SetPerkAbilities(120f, i - 1, sigmas[i]);
            float error = ControlErrorFor(offset);
            Assert.IsTrue(Near(_resultSigma, at85[i], 0.0005f), "sigma at 85 for stage " + (i - 1) + ": " + _resultSigma);
            Assert.IsTrue(Near(error, at85[i] * ReleasePlateMath.MaxSigmas, 0.0005f), "the stage's sigma is applied as is");
            Assert.IsTrue(Near(error / sceneError, sigmas[i] / 0.9f, 0.01f), "same score and draw: the error scales with the sigma");
            Assert.IsTrue(error < previous, "every stage is more precise than the one below");
            previous = error;
            Assert.IsTrue(ControlText().Contains("Control perk"), ControlText());
        }
        SetPerkAbilities(120f, 0, 0.9f);
        ControlErrorFor(offset);
        Assert.IsTrue(ControlText().Contains("Control perk Standard"), "no Control perk: shown as Standard " + ControlText());
    }

    [TsukimiTest]
    public void OnlyAnIdealReleaseHasNoError()
    {
        Mimic.Join();
        Assert.IsTrue(PerkPitcherForTest());
        SetPerkAbilities(120f, -1, 1.30f);
        Assert.AreEqual(0f, ControlErrorFor(Vector2.zero), "score 100: no error even with the debuff");
        SetPerkAbilities(120f, 0, 0.9f);
        Assert.IsTrue(ControlErrorFor(new Vector2(Score85Offset(), 0f)) > 0.4f, "no Control perk, score 85: the standard error, never switched off");
        Assert.AreEqual(0.9f, controlSigmaMeters, "the Inspector sigma is never rewritten by the perk");
    }

    [TsukimiTest]
    public void TheScoreScalesThePerkSigmaLinearly()
    {
        Mimic.Join();
        Assert.IsTrue(PerkPitcherForTest());
        SetPerkAbilities(120f, 0, 0.9f);
        float halfDiagonal = Mathf.Sqrt(releasePlateWidth * releasePlateWidth + releasePlateHeight * releasePlateHeight) * 0.5f;
        float[] scores = { 0f, 50f, 85f, 100f };
        for (int i = 0; i < scores.Length; i++)
        {
            ControlErrorFor(new Vector2((1f - scores[i] / 100f) * halfDiagonal, 0f));
            Assert.IsTrue(Near(_resultScore, scores[i], 0.05f), "score " + _resultScore);
            Assert.IsTrue(Near(_resultSigma, 0.9f * (1f - scores[i] / 100f), 0.0005f), "sigma = 0.9 x (1 - score / 100) at " + scores[i]);
        }
    }
}
