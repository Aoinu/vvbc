using Tsukimi;
using UnityEngine;

// Tests of ManualPitchQuality (arm slot, ideal palm, pose quality, failure blend, Knuckle wake)
// and of the calculator's 4-Seam axis, speed multiplier and left-hand mirror.
public partial struct PitchQuality
{
    private const float Rpm4 = 2500f;
    private const float Wake4 = 0.006f;

    private static PitchBuild TypeBuild(int type)
    {
        PitchBuild build = new PitchBuild();
        build.pitchTypeId = type;
        build.parameterA = PitchBuild.DefaultParameter;
        build.parameterB = PitchBuild.DefaultParameter;
        build.parameterC = PitchBuild.DefaultParameter;
        return build;
    }

    private static PitchProperties Ideal(int type, bool left)
    {
        PitchProperties p = ManualPitchQuality.IdealProperties(TypeBuild(type), Rpm4, Wake4, 90f, left);
        if (p.wakeLocal.mode == BallAerodynamics.WakeRotating) p.wakeLocal.initialPhase = 0.9f;
        return p;
    }

    private static PitchProperties Failure(bool left)
    {
        return ManualPitchQuality.FailureProperties(Rpm4, 0.7f, Wake4, 90f, left);
    }

    private static Vector3 Spin(PitchProperties p)
    {
        return PitchPropertiesCalculator.LocalSpin(p);
    }

    // Pitch-local acceleration (Magnus + SSW, no gravity or drag) on a forward flight at time t.
    private static Vector3 Force(PitchProperties p, float t)
    {
        Vector3 v = Vector3.forward * 38f;
        Vector3 w = Spin(p);
        return BallAerodynamics.Acceleration(v, w, 0f, 0.00063f, BallAerodynamics.PrepareWake(p.wakeLocal, w), t);
    }

    private static Vector3 Mirror(Vector3 v) { return new Vector3(-v.x, v.y, v.z); }

    // ---- Calculator ----

    [TsukimiTest]
    public void TheCalculatorsFourSeamAxisIsTheManualOne()
    {
        PitchProperties p = PitchPropertiesCalculator.Calculate(TypeBuild(PitchType.FourSeam), 120f / 3.6f, 2500f);
        Assert.IsTrue(Vector3.Distance(p.spinAxisLocal, new Vector3(-0.8660254f, 0.5f, 0f)) < 0.000001f, "right-hander " + p.spinAxisLocal);
        Assert.IsTrue(Vector3.Distance(p.spinAxisLocal, ManualPitchMath.FixedFourSeamAxis(false)) < 0.000001f);
        PitchProperties left = PitchPropertiesCalculator.MirrorForLeftHand(p);
        Assert.IsTrue(Vector3.Distance(left.spinAxisLocal, new Vector3(-0.8660254f, -0.5f, 0f)) < 0.000001f, "left-hander " + left.spinAxisLocal);
        Assert.IsTrue(Vector3.Distance(left.spinAxisLocal, ManualPitchMath.FixedFourSeamAxis(true)) < 0.000001f);
    }

    [TsukimiTest]
    public void TheSpeedMultiplierIsTheTypesShareOfTheBase()
    {
        float[] expected = { 1f, 0.97f, 0.95f, 0.945f, 0.855f, 0.84f, 0.735f, 0.89f, 0.805f, 0.65f };
        for (int type = 0; type < PitchType.Count; type++)
        {
            float m = PitchPropertiesCalculator.SpeedMultiplier(TypeBuild(type));
            Assert.IsTrue(Mathf.Abs(m - expected[type]) < 0.0005f, PitchType.Name(type) + " x" + m);
            // Independent of any base inside the range: base x multiplier exactly.
            float at100 = PitchPropertiesCalculator.Calculate(TypeBuild(type), 100f / 3.6f, 2500f).speedMps;
            Assert.IsTrue(Mathf.Abs(at100 - 100f / 3.6f * m) < 0.001f, PitchType.Name(type) + " at 100 km/h");
        }
    }

    // Spin is an axial vector, a wake direction an ordinary one: the left-hander's flight is the
    // mirror image of the right-hander's at every moment, Knuckle included.
    [TsukimiTest]
    public void ALeftHandersFlightIsTheMirrorImageForEveryType()
    {
        for (int type = 0; type < PitchType.Count; type++)
        {
            PitchProperties right = Ideal(type, false);
            PitchProperties left = PitchPropertiesCalculator.MirrorForLeftHand(right);
            float[] times = { 0f, 0.1f, 0.25f, 0.4f };
            for (int i = 0; i < times.Length; i++)
            {
                Vector3 r = Force(right, times[i]);
                Vector3 l = Force(left, times[i]);
                Assert.IsTrue(Vector3.Distance(Mirror(r), l) < 0.0005f, PitchType.Name(type) + " t=" + times[i] + ": " + r + " vs " + l);
            }
            Assert.AreEqual(right.speedMps, left.speedMps);
            Assert.AreEqual(right.spinRpm, left.spinRpm);
        }
    }

    [TsukimiTest]
    public void TheIdealOfEveryTypeIsTheCalculatorsAndTheFourSeamKeepsTheMoundsSettings()
    {
        PitchProperties four = Ideal(PitchType.FourSeam, false);
        Assert.AreEqual(Rpm4, four.spinRpm);
        Assert.AreEqual(BallAerodynamics.WakeFixed, four.wakeLocal.mode);
        Assert.AreEqual(Wake4, four.wakeLocal.coefficient);
        Assert.IsTrue(Vector3.Distance(four.wakeLocal.direction, Vector3.right) < 0.00001f, "arm side");
        Assert.IsTrue(Vector3.Distance(Ideal(PitchType.FourSeam, true).wakeLocal.direction, Vector3.left) < 0.00001f, "left-hander's arm side");
        Assert.AreEqual(BallAerodynamics.WakeNone, ManualPitchQuality.IdealProperties(TypeBuild(0), Rpm4, 0f, 90f, false).wakeLocal.mode, "no SSW at 0");
        for (int type = 1; type < PitchType.Count; type++)
        {
            PitchProperties calc = PitchPropertiesCalculator.Calculate(TypeBuild(type), PitchPropertiesCalculator.DefaultBaseSpeedMps, Rpm4);
            PitchProperties ideal = ManualPitchQuality.IdealProperties(TypeBuild(type), Rpm4, Wake4, 90f, false);
            Assert.AreEqual(calc.spinRpm, ideal.spinRpm, PitchType.Name(type));
            Assert.AreEqual(calc.spinAxisLocal, ideal.spinAxisLocal, PitchType.Name(type));
            Assert.AreEqual(calc.wakeLocal.mode, ideal.wakeLocal.mode, PitchType.Name(type));
            Assert.AreEqual(calc.wakeLocal.coefficient, ideal.wakeLocal.coefficient, PitchType.Name(type));
        }
    }

    // ---- Arm slot and ideal palm ----

    [TsukimiTest]
    public void TheArmSlotIsMeasuredFromStraightUp()
    {
        Assert.IsTrue(Mathf.Abs(ManualPitchQuality.SlotAngle(new Vector3(0f, 0.5f, 0.3f))) < 0.001f, "overhead");
        Assert.IsTrue(Mathf.Abs(ManualPitchQuality.SlotAngle(new Vector3(0.5f, 0f, 0.3f)) - 90f) < 0.001f, "beside");
        Assert.IsTrue(Mathf.Abs(ManualPitchQuality.SlotAngle(new Vector3(0.5f, -0.5f, 0.3f)) - 135f) < 0.001f, "below");
        Assert.IsTrue(Mathf.Abs(ManualPitchQuality.SlotAngle(new Vector3(0.3f, 0.3f, 0f)) - 45f) < 0.001f);
        Assert.AreEqual(ManualPitchQuality.SlotAngle(new Vector3(0.3f, 0.2f, 0.4f)), ManualPitchQuality.SlotAngle(new Vector3(-0.3f, 0.2f, -0.9f)),
            "the side and the depth do not matter");
        Assert.AreEqual(0f, ManualPitchQuality.SlotAngle(Vector3.zero));
        Assert.AreEqual(0f, ManualPitchQuality.SlotAngle(new Vector3(float.NaN, 0f, 0f)));
    }

    [TsukimiTest]
    public void TheIdealPalmTurnsWithTheArmSlotAndIsMirroredForALeftHander()
    {
        Vector3 slider = new Vector3(-0.707f, 0f, 0.707f).normalized;
        Assert.IsTrue(Vector3.Distance(ManualPitchQuality.CorrectedPalm(slider, 0f, false), slider) < 0.00001f, "overhand: as set");
        // The arm turns from up toward the arm side: up becomes the arm side at 90 deg.
        Assert.IsTrue(Vector3.Distance(ManualPitchQuality.CorrectedPalm(Vector3.up, 90f, false), Vector3.right) < 0.00001f, "sidearm");
        Assert.IsTrue(Vector3.Distance(ManualPitchQuality.CorrectedPalm(Vector3.up, 180f, false), Vector3.down) < 0.00001f, "underhand");
        Assert.IsTrue(Vector3.Distance(ManualPitchQuality.CorrectedPalm(Vector3.forward, 70f, false), Vector3.forward) < 0.00001f,
            "a palm toward the plate is the axis itself");
        Vector3 side = ManualPitchQuality.CorrectedPalm(slider, 90f, false);
        Assert.IsTrue(Vector3.Distance(side, new Vector3(0f, 0.707f, 0.707f).normalized) < 0.001f, "a sidearm slider's palm faces up " + side);
        float[] slots = { 0f, 30f, 90f, 135f };
        Vector3[] palms = { slider, Vector3.up, new Vector3(0.5f, -0.17f, 0.85f) };
        for (int s = 0; s < slots.Length; s++)
            for (int p = 0; p < palms.Length; p++)
            {
                Vector3 r = ManualPitchQuality.CorrectedPalm(palms[p], slots[s], false);
                Vector3 l = ManualPitchQuality.CorrectedPalm(palms[p], slots[s], true);
                Assert.IsTrue(Vector3.Distance(Mirror(r), l) < 0.00001f, "mirror images at slot " + slots[s]);
            }
        Assert.IsTrue(Vector3.Distance(ManualPitchQuality.CorrectedPalm(Vector3.up, 90f, true), Vector3.left) < 0.00001f,
            "a left-hander's sidearm turns toward its own arm side");
        Assert.AreEqual(Vector3.zero, ManualPitchQuality.CorrectedPalm(Vector3.zero, 30f, false));
        Assert.IsTrue(Vector3.Distance(ManualPitchQuality.CorrectedPalm(slider, float.NaN, false), slider) < 0.00001f);
    }

    // ---- Pose and final quality ----

    [TsukimiTest]
    public void PoseQualityIsASmoothstepBetweenTheGoodAndBadAngles()
    {
        Assert.AreEqual(1f, ManualPitchQuality.PoseQuality(false, 170f, 25f, 60f), "not evaluated");
        Assert.AreEqual(1f, ManualPitchQuality.PoseQuality(true, 0f, 25f, 60f));
        Assert.AreEqual(1f, ManualPitchQuality.PoseQuality(true, 25f, 25f, 60f), "at the good angle");
        Assert.AreEqual(0f, ManualPitchQuality.PoseQuality(true, 60f, 25f, 60f), "at the bad angle");
        Assert.AreEqual(0f, ManualPitchQuality.PoseQuality(true, 120f, 25f, 60f));
        Assert.IsTrue(Mathf.Abs(ManualPitchQuality.PoseQuality(true, 42.5f, 25f, 60f) - 0.5f) < 0.0001f, "half way");
        float previous = 1f;
        for (float a = 25f; a <= 60f; a += 1f)
        {
            float q = ManualPitchQuality.PoseQuality(true, a, 25f, 60f);
            Assert.IsTrue(q <= previous + 0.000001f && previous - q < 0.05f, "monotone and continuous at " + a);
            previous = q;
        }
        // Not linear: slow near both ends.
        Assert.IsTrue(1f - ManualPitchQuality.PoseQuality(true, 28.5f, 25f, 60f) < 0.05f, "smooth start");
        // Bad not above good: a step at the good angle.
        Assert.AreEqual(1f, ManualPitchQuality.PoseQuality(true, 30f, 30f, 20f));
        Assert.AreEqual(0f, ManualPitchQuality.PoseQuality(true, 30.1f, 30f, 20f));
        Assert.AreEqual(0f, ManualPitchQuality.PoseQuality(true, float.NaN, 25f, 60f), "unknown palm");
        float unknown = ManualPitchQuality.PoseQuality(true, 10f, float.NaN, float.NaN);
        Assert.IsTrue(unknown == 0f || unknown == 1f, "NaN angles give a plain step, never NaN");
    }

    [TsukimiTest]
    public void TheTimingCapLimitsTheQualityOutsideTheIdealTimingOnly()
    {
        Assert.AreEqual(0.8f, ManualPitchQuality.FinalQuality(0.8f, true, 0.6f));
        Assert.AreEqual(0.6f, ManualPitchQuality.FinalQuality(0.8f, false, 0.6f));
        Assert.AreEqual(0.6f, ManualPitchQuality.FinalQuality(1f, false, 0.6f), "a perfect early release is capped");
        Assert.AreEqual(0.3f, ManualPitchQuality.FinalQuality(0.3f, false, 0.6f));
        Assert.AreEqual(0f, ManualPitchQuality.FinalQuality(1f, false, float.NaN));
        Assert.AreEqual(1f, ManualPitchQuality.FinalQuality(2f, true, 0.6f));
        Assert.IsTrue(ReleasePlateMath.QualityTimingIdeal(ReleasePlateMath.TimingIdeal));
        Assert.IsTrue(ReleasePlateMath.QualityTimingIdeal(ReleasePlateMath.TimingAuto), "the auto release is ideal for quality");
        Assert.IsFalse(ReleasePlateMath.QualityTimingIdeal(ReleasePlateMath.TimingEarly));
        Assert.IsFalse(ReleasePlateMath.QualityTimingIdeal(ReleasePlateMath.TimingLate));
        Assert.IsFalse(ReleasePlateMath.QualityTimingIdeal(ReleasePlateMath.TimingNone));
    }

    // ---- Failure blend ----

    [TsukimiTest]
    public void EveryTypeGoesFromItsIdealToTheCommonFailureMode()
    {
        bool[] hands = { false, true };
        for (int h = 0; h < hands.Length; h++)
        {
            PitchProperties failure = Failure(hands[h]);
            Assert.IsTrue(Mathf.Abs(failure.spinRpm - 1750f) < 0.01f, "70% of the 4-Seam rpm");
            Assert.IsTrue(Vector3.Distance(failure.spinAxisLocal, ManualPitchMath.FixedFourSeamAxis(hands[h])) < 0.000001f);
            Assert.AreEqual(BallAerodynamics.WakeFixed, failure.wakeLocal.mode);
            Assert.AreEqual(Wake4, failure.wakeLocal.coefficient);
            for (int type = 0; type < PitchType.Count; type++)
            {
                PitchProperties ideal = Ideal(type, hands[h]);
                PitchProperties best = ManualPitchQuality.Blend(ideal, failure, 1f);
                PitchProperties worst = ManualPitchQuality.Blend(ideal, failure, 0f);
                string name = PitchType.Name(type) + (hands[h] ? " L" : " R");
                Assert.IsTrue(Vector3.Distance(Force(best, 0.2f), Force(ideal, 0.2f)) < 0.00001f, name + ": quality 1 is the ideal");
                Assert.IsTrue(Vector3.Distance(Force(worst, 0.2f), Force(failure, 0.2f)) < 0.00001f, name + ": quality 0 is the failure");
                Assert.IsTrue(Mathf.Abs(worst.spinRpm - 1750f) < 0.01f, name + " rpm");
                Assert.IsTrue(Vector3.Distance(worst.spinAxisLocal, failure.spinAxisLocal) < 0.00001f, name + " axis");
                Assert.AreEqual(ideal.speedMps, worst.speedMps, name + ": the speed never changes");
                Assert.AreEqual(ideal.speedMps, ManualPitchQuality.Blend(ideal, failure, 0.4f).speedMps);
            }
        }
    }

    // Small quality steps make small force steps, through the whole range, for every type; the
    // axis stays unit and the rpm is interpolated on its own (never cancelled by the axes).
    [TsukimiTest(maxSteps: 20000000)]
    public void TheBlendIsContinuousForEveryTypeKnuckleIncluded()
    {
        PitchProperties failure = Failure(false);
        for (int type = 0; type < PitchType.Count; type++)
        {
            PitchProperties ideal = Ideal(type, false);
            Vector3 previous = Force(ManualPitchQuality.Blend(ideal, failure, 0f), 0.3f);
            float previousRpm = failure.spinRpm;
            for (int i = 1; i <= 50; i++)
            {
                float q = i / 50f;
                PitchProperties p = ManualPitchQuality.Blend(ideal, failure, q);
                Vector3 force = Force(p, 0.3f);
                string name = PitchType.Name(type) + " q=" + q;
                Assert.IsTrue(ManualPitchMath.FiniteVector(force), name);
                Assert.IsTrue(Mathf.Abs(p.spinAxisLocal.magnitude - 1f) < 0.0001f, name + " unit axis");
                Assert.IsTrue((force - previous).magnitude < 1.2f, name + " step " + (force - previous).magnitude);
                float expected = failure.spinRpm + (ideal.spinRpm - failure.spinRpm) * q;
                Assert.IsTrue(Mathf.Abs(p.spinRpm - expected) < 0.01f, name + " rpm");
                previous = force;
                previousRpm = p.spinRpm;
            }
        }
    }

    // The Knuckle's Rotating wake narrows its cone toward the failure's Fixed wake: at a quality
    // just above 0 its force is the Fixed one, and its flutter vanishes continuously.
    [TsukimiTest]
    public void TheKnuckleWakeTurnsIntoTheFourSeamWakeWithoutAJump()
    {
        PitchProperties ideal = Ideal(PitchType.Knuckle, false);
        PitchProperties failure = Failure(false);
        Assert.AreEqual(BallAerodynamics.WakeRotating, ideal.wakeLocal.mode);
        WakeProfile near = ManualPitchQuality.BlendWake(ideal.wakeLocal, failure.wakeLocal, 0.0001f);
        Assert.AreEqual(BallAerodynamics.WakeRotating, near.mode);
        Assert.IsTrue(near.coneAngle < 0.0001f);
        Vector3 v = Vector3.forward * 38f;
        float[] times = { 0f, 0.2f, 0.45f };
        for (int i = 0; i < times.Length; i++)
        {
            Vector3 a = BallAerodynamics.WakeAcceleration(BallAerodynamics.PrepareWake(near, Vector3.forward * 3f), v, times[i]);
            Vector3 b = BallAerodynamics.WakeAcceleration(BallAerodynamics.PrepareWake(failure.wakeLocal, Vector3.forward * 3f), v, times[i]);
            Assert.IsTrue(Vector3.Distance(a, b) < 0.001f, "the same force as the Fixed wake at t=" + times[i]);
        }
        // The turning part of the force (coefficient x sin(cone)) grows with the quality from 0.
        float previous = 0f;
        for (int i = 1; i <= 10; i++)
        {
            WakeProfile w = ManualPitchQuality.BlendWake(ideal.wakeLocal, failure.wakeLocal, i / 10f);
            Assert.IsTrue(Mathf.Abs(w.coneAngle - ideal.wakeLocal.coneAngle * i / 10f) < 0.00001f, "cone at " + i);
            float flutter = w.coefficient * Mathf.Sin(w.coneAngle);
            Assert.IsTrue(flutter > previous, "the flutter grows with the quality at " + i);
            previous = flutter;
        }
        Assert.IsTrue(Mathf.Abs(previous - ideal.wakeLocal.coefficient * Mathf.Sin(ideal.wakeLocal.coneAngle)) < 0.000001f, "the ideal Knuckle");
        Assert.AreEqual(ideal.wakeLocal.initialPhase, ManualPitchQuality.BlendWake(ideal.wakeLocal, failure.wakeLocal, 0.5f).initialPhase,
            "the phase the pitcher drew is kept");
    }

    [TsukimiTest]
    public void ATypeWithoutSswFadesTheFourSeamSswIn()
    {
        PitchProperties failure = Failure(false);
        PitchProperties slider = Ideal(PitchType.Slider, false);
        Assert.AreEqual(BallAerodynamics.WakeNone, slider.wakeLocal.mode);
        WakeProfile half = ManualPitchQuality.BlendWake(slider.wakeLocal, failure.wakeLocal, 0.5f);
        Assert.AreEqual(BallAerodynamics.WakeFixed, half.mode);
        Assert.IsTrue(Mathf.Abs(half.coefficient - Wake4 * 0.5f) < 0.000001f);
        Assert.IsTrue(Vector3.Distance(half.direction, failure.wakeLocal.direction) < 0.00001f);
        WakeProfile none = new WakeProfile();
        Assert.AreEqual(BallAerodynamics.WakeNone, ManualPitchQuality.BlendWake(none, none, 0.5f).mode, "no SSW on either side");
    }

    [TsukimiTest]
    public void TheGreatCircleBlendNeverBreaks()
    {
        Vector3 a = Vector3.right;
        Assert.IsTrue(Vector3.Distance(ManualPitchQuality.SlerpUnit(a, Vector3.up, 0.5f), new Vector3(1f, 1f, 0f).normalized) < 0.00001f);
        Assert.AreEqual(a, ManualPitchQuality.SlerpUnit(a, Vector3.up, 0f));
        Assert.IsTrue(Vector3.Distance(ManualPitchQuality.SlerpUnit(a, Vector3.up, 1f), Vector3.up) < 0.00001f);
        // Opposite: a fixed perpendicular way round, unit, never NaN or zero.
        Vector3 previous = a;
        for (int i = 1; i <= 20; i++)
        {
            Vector3 v = ManualPitchQuality.SlerpUnit(a, -a, i / 20f);
            Assert.IsTrue(ManualPitchMath.FiniteVector(v) && Mathf.Abs(v.magnitude - 1f) < 0.0001f, "opposite " + i);
            Assert.IsTrue(Vector3.Angle(previous, v) < 9.5f, "no jump at " + i);
            previous = v;
        }
        Assert.IsTrue(Vector3.Distance(previous, -a) < 0.0001f);
        Vector3 forward = ManualPitchQuality.SlerpUnit(Vector3.forward, Vector3.back, 0.5f);
        Assert.IsTrue(ManualPitchMath.FiniteVector(forward) && Mathf.Abs(forward.magnitude - 1f) < 0.0001f, "along the fallback axis");
        Assert.AreEqual(Vector3.up, ManualPitchQuality.SlerpUnit(Vector3.zero, Vector3.up, 0.3f));
        Assert.AreEqual(Vector3.up, ManualPitchQuality.SlerpUnit(Vector3.up, new Vector3(float.NaN, 0f, 0f), 0.3f));
    }
}
