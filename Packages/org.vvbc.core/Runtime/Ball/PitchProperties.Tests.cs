using Tsukimi;
using UnityEngine;

// Tests of PitchPropertiesCalculator, the physical definition of the pitch types.
public partial struct PitchProperties
{
    private const float BaseSpeed = 120f / 3.6f;
    private const float BaseSpin = 2500f;

    private static PitchProperties Build(int type, float a, float b, float c)
    {
        PitchBuild build = new PitchBuild();
        build.pitchTypeId = type;
        build.parameterA = a;
        build.parameterB = b;
        build.parameterC = c;
        return PitchPropertiesCalculator.Calculate(build, BaseSpeed, BaseSpin);
    }

    // Direction of the Magnus force on a forward flight, in the pitch-local x-y plane.
    private static Vector3 MagnusBreak(PitchProperties p)
    {
        return new Vector3(p.spinAxisLocal.y, -p.spinAxisLocal.x, 0f);
    }

    private static bool Same(PitchProperties x, PitchProperties y)
    {
        return x.speedMps == y.speedMps && x.spinRpm == y.spinRpm && x.spinAxisLocal == y.spinAxisLocal &&
            x.wakeLocal.mode == y.wakeLocal.mode && x.wakeLocal.coefficient == y.wakeLocal.coefficient &&
            x.wakeLocal.direction == y.wakeLocal.direction && x.wakeLocal.coneAngle == y.wakeLocal.coneAngle &&
            x.wakeLocal.initialPhase == y.wakeLocal.initialPhase;
    }

    private static bool Near(float x, float y) { return Mathf.Abs(x - y) < 0.0005f; }

    private static float Set(int parameter, int index, float value)
    {
        return parameter == index ? value : PitchBuild.DefaultParameter;
    }

    [TsukimiTest]
    public void EveryTypeGivesFiniteValidPropertiesAcrossItsParameterRange()
    {
        for (int type = 0; type < PitchType.Count; type++)
            for (int i = 0; i < 27; i++)
            {
                PitchProperties p = Build(type, (i % 3) * 0.5f, (i / 3 % 3) * 0.5f, (i / 9) * 0.5f);
                Assert.IsTrue(p.speedMps >= BaseSpeed * 0.65f - 0.001f && p.speedMps <= BaseSpeed + 0.001f, PitchType.Name(type) + " speed");
                Assert.IsTrue(p.spinRpm >= 0f && p.spinRpm <= PitchPropertiesCalculator.MaxSpinRpm, PitchType.Name(type) + " spin");
                Assert.IsTrue(Mathf.Abs(p.spinAxisLocal.magnitude - 1f) < 0.0001f, PitchType.Name(type) + " unit axis");
                Assert.IsTrue(p.wakeLocal.coefficient >= 0f && p.wakeLocal.coefficient <= BallAerodynamics.MaxWakeCoefficient,
                    PitchType.Name(type) + " wake cap");
                if (p.wakeLocal.mode != BallAerodynamics.WakeNone)
                {
                    Assert.IsTrue(Mathf.Abs(p.wakeLocal.direction.magnitude - 1f) < 0.0001f, PitchType.Name(type) + " unit wake");
                    Assert.AreNotEqual(BallAerodynamics.WakeNone,
                        BallAerodynamics.PrepareWake(p.wakeLocal, PitchPropertiesCalculator.LocalSpin(p)).mode, PitchType.Name(type) + " wake is usable");
                }
            }
    }

    [TsukimiTest]
    public void EveryParameterATypeUsesChangesItAndNoOtherDoes()
    {
        for (int type = 0; type < PitchType.Count; type++)
            for (int parameter = 0; parameter < PitchType.MaxParameters; parameter++)
            {
                PitchProperties low = Build(type, Set(parameter, 0, 0f), Set(parameter, 1, 0f), Set(parameter, 2, 0f));
                PitchProperties high = Build(type, Set(parameter, 0, 1f), Set(parameter, 1, 1f), Set(parameter, 2, 1f));
                bool used = PitchType.UsesParameter(type, parameter);
                Assert.AreEqual(used, !Same(low, high), PitchType.Name(type) + " parameter " + parameter);
                Assert.AreEqual(used, PitchType.ParameterName(type, parameter) != "", "a used parameter is named");
                Assert.AreEqual(used, PitchType.ParameterLow(type, parameter) != "" && PitchType.ParameterHigh(type, parameter) != "");
            }
    }

    [TsukimiTest]
    public void TheSameInputGivesTheSameOutput()
    {
        for (int type = 0; type < PitchType.Count; type++)
            Assert.IsTrue(Same(Build(type, 0.3f, 0.7f, 0.1f), Build(type, 0.3f, 0.7f, 0.1f)));
    }

    [TsukimiTest]
    public void InvalidInputsFallBackSafely()
    {
        PitchProperties standard = Build(PitchType.Slider, 0.5f, 0.5f, 0.5f);
        Assert.IsTrue(Same(standard, Build(PitchType.Slider, float.NaN, float.PositiveInfinity, float.NegativeInfinity)),
            "non-finite parameters read as the default");
        Assert.IsTrue(Same(Build(PitchType.Slider, 1f, 0f, 1f), Build(PitchType.Slider, 7f, -3f, 2f)), "parameters are clamped");
        Assert.IsTrue(Same(Build(PitchType.FourSeam, 0.5f, 0.5f, 0.5f), Build(42, 0.5f, 0.5f, 0.5f)), "an unknown type is the 4-Seam");
        Assert.IsTrue(Same(Build(PitchType.FourSeam, 0.5f, 0.5f, 0.5f), Build(-1, 0.5f, 0.5f, 0.5f)));
        PitchBuild build = new PitchBuild();
        PitchProperties p = PitchPropertiesCalculator.Calculate(build, float.NaN, float.NaN);
        Assert.AreEqual(PitchPropertiesCalculator.DefaultBaseSpeedMps, p.speedMps, "a non-finite base speed reads as the default");
        Assert.AreEqual(PitchPropertiesCalculator.DefaultBaseSpinRpm, p.spinRpm);
        Assert.AreEqual(PitchPropertiesCalculator.MaxBaseSpeedMps, PitchPropertiesCalculator.Calculate(build, 1000f, BaseSpin).speedMps, "the base speed is clamped");
        Assert.AreEqual(PitchPropertiesCalculator.MinBaseSpeedMps, PitchPropertiesCalculator.Calculate(build, 1f, BaseSpin).speedMps);
    }

    [TsukimiTest]
    public void ZeroSpinKeepsAUnitAxis()
    {
        PitchBuild build = new PitchBuild();
        for (int type = 0; type < PitchType.Knuckle; type++)
        {
            build.pitchTypeId = type;
            PitchProperties p = PitchPropertiesCalculator.Calculate(build, BaseSpeed, 0f);
            Assert.AreEqual(0f, p.spinRpm, PitchType.Name(type));
            Assert.IsTrue(Mathf.Abs(p.spinAxisLocal.magnitude - 1f) < 0.0001f);
            Assert.AreEqual(Vector3.zero, PitchPropertiesCalculator.LocalSpin(p));
        }
    }

    [TsukimiTest]
    public void FourSeamIsTheBaseWithPureBackspin()
    {
        PitchProperties p = Build(PitchType.FourSeam, 0.5f, 0.5f, 0.5f);
        Assert.AreEqual(BaseSpeed, p.speedMps);
        Assert.AreEqual(BaseSpin, p.spinRpm);
        Assert.AreEqual(Vector3.left, p.spinAxisLocal, "backspin");
        Assert.AreEqual(BallAerodynamics.WakeNone, p.wakeLocal.mode);
        PitchBuild build = new PitchBuild();
        Assert.AreEqual(PitchPropertiesCalculator.FourSeamMaxSpinRpm, PitchPropertiesCalculator.Calculate(build, BaseSpeed, 3000f).spinRpm, "4-Seam spin is capped");
    }

    [TsukimiTest]
    public void SpeedAndSpinFollowTheProvisionalTable()
    {
        Assert.IsTrue(Near(Build(PitchType.TwoSeam, 1f, 0.5f, 0.5f).speedMps, BaseSpeed * 0.95f));
        Assert.IsTrue(Near(Build(PitchType.TwoSeam, 0f, 0.5f, 0.5f).spinRpm, BaseSpin * 0.92f));
        Assert.IsTrue(Near(Build(PitchType.Sinker, 0f, 0f, 0.5f).speedMps, BaseSpeed * 0.95f));
        Assert.IsTrue(Near(Build(PitchType.Cutter, 1f, 0.5f, 0.5f).speedMps, BaseSpeed * 0.91f));
        Assert.IsTrue(Near(Build(PitchType.Slider, 0f, 0.5f, 0.5f).speedMps, BaseSpeed * 0.79f));
        Assert.IsTrue(Near(Build(PitchType.Slider, 1f, 0.5f, 0.5f).speedMps, BaseSpeed * 0.92f));
        Assert.IsTrue(Near(Build(PitchType.Sweeper, 0f, 0.5f, 0.5f).speedMps, BaseSpeed * 0.84f));
        Assert.IsTrue(Near(Build(PitchType.Curve, 0.5f, 1f, 0.5f).speedMps, BaseSpeed * 0.66f));
        Assert.IsTrue(Near(Build(PitchType.Split, 0.5f, 0.5f, 0.5f).speedMps, BaseSpeed * 0.89f));
        Assert.IsTrue(Near(Build(PitchType.Split, 0.5f, 0.5f, 0.5f).spinRpm, BaseSpin * 0.18f), "Split is low spin");
        Assert.IsTrue(Near(Build(PitchType.Change, 1f, 0.5f, 0.5f).speedMps, BaseSpeed * 0.73f), "Change slows down");
        Assert.IsTrue(Near(Build(PitchType.Change, 1f, 0.5f, 0.5f).spinRpm, BaseSpin * 0.75f));
        Assert.IsTrue(Near(Build(PitchType.Knuckle, 0.5f, 0.5f, 0.5f).speedMps, BaseSpeed * 0.65f));
    }

    private static float SpeedAt(float baseKmh, int type, float a, float b, float c)
    {
        PitchBuild build = new PitchBuild();
        build.pitchTypeId = type;
        build.parameterA = a;
        build.parameterB = b;
        build.parameterC = c;
        return PitchPropertiesCalculator.Calculate(build, baseKmh / 3.6f, BaseSpin).speedMps;
    }

    private static bool NearKmh(float speedMps, float kmh) { return Mathf.Abs(speedMps * 3.6f - kmh) < 0.01f; }

    // Only the base is limited; the release speed is exactly base x multiplier at every base, so
    // no speed slider collapses. A base below the range reads as its minimum (90 km/h).
    [TsukimiTest]
    public void ReleaseSpeedIsTheBaseTimesTheMultiplierAcrossTheBaseRange()
    {
        float[] requested = { 60f, 90f, 120f, 160f };
        for (int i = 0; i < requested.Length; i++)
        {
            float b = Mathf.Max(requested[i], PitchPropertiesCalculator.MinBaseSpeedKmh);
            Assert.IsTrue(NearKmh(SpeedAt(requested[i], PitchType.Curve, 0.5f, 0f, 0.5f), b * 0.81f), "Curve B=0");
            Assert.IsTrue(NearKmh(SpeedAt(requested[i], PitchType.Curve, 0.5f, 0.5f, 0.5f), b * 0.735f), "Curve B=0.5");
            Assert.IsTrue(NearKmh(SpeedAt(requested[i], PitchType.Curve, 0.5f, 1f, 0.5f), b * 0.66f), "Curve B=1");
            Assert.IsTrue(NearKmh(SpeedAt(requested[i], PitchType.Change, 0f, 0.5f, 0.5f), b * 0.88f), "Change A=0");
            Assert.IsTrue(NearKmh(SpeedAt(requested[i], PitchType.Change, 0.5f, 0.5f, 0.5f), b * 0.805f), "Change A=0.5");
            Assert.IsTrue(NearKmh(SpeedAt(requested[i], PitchType.Change, 1f, 0.5f, 0.5f), b * 0.73f), "Change A=1");
            Assert.IsTrue(NearKmh(SpeedAt(requested[i], PitchType.Slider, 0f, 0.5f, 0.5f), b * 0.79f), "Slider A=0");
            Assert.IsTrue(NearKmh(SpeedAt(requested[i], PitchType.Slider, 0.5f, 0.5f, 0.5f), b * 0.855f), "Slider A=0.5");
            Assert.IsTrue(NearKmh(SpeedAt(requested[i], PitchType.Slider, 1f, 0.5f, 0.5f), b * 0.92f), "Slider A=1");
            Assert.IsTrue(NearKmh(SpeedAt(requested[i], PitchType.Knuckle, 0.5f, 0.5f, 0.5f), b * 0.65f), "Knuckle");
            Assert.IsTrue(NearKmh(SpeedAt(requested[i], PitchType.FourSeam, 0.5f, 0.5f, 0.5f), b), "4-Seam is the base");
        }
    }

    // The slowest release over every type and parameter end at the minimum base stays above the
    // ~53 km/h reach floor of the mound-to-zone flight (BallAimSolverBehaviour tests fly it).
    [TsukimiTest]
    public void TheSlowestReleaseAtTheMinimumBaseIsAboveTheReachFloor()
    {
        float slowest = float.PositiveInfinity;
        for (int type = 0; type < PitchType.Count; type++)
            for (int i = 0; i < 8; i++)
                slowest = Mathf.Min(slowest, SpeedAt(PitchPropertiesCalculator.MinBaseSpeedKmh, type, i & 1, (i >> 1) & 1, (i >> 2) & 1));
        Assert.IsTrue(NearKmh(slowest, PitchPropertiesCalculator.MinBaseSpeedKmh * 0.65f), "the Knuckle is the slowest");
        Assert.IsTrue(slowest * 3.6f >= 58f, "58.5 km/h, about 5 km/h above the floor");
    }

    // A base below the allowed range reads as its minimum; the speed sliders still separate there.
    [TsukimiTest]
    public void SpeedSlidersStayEffectiveAtTheLowestBaseSpeed()
    {
        Assert.IsTrue(SpeedAt(60f, PitchType.Curve, 0.5f, 1f, 0.5f) < SpeedAt(60f, PitchType.Curve, 0.5f, 0.5f, 0.5f), "Curve tempo, slow half");
        Assert.IsTrue(SpeedAt(60f, PitchType.Curve, 0.5f, 0.5f, 0.5f) < SpeedAt(60f, PitchType.Curve, 0.5f, 0f, 0.5f), "Curve tempo, normal half");
        Assert.IsTrue(SpeedAt(60f, PitchType.Change, 1f, 0.5f, 0.5f) < SpeedAt(60f, PitchType.Change, 0f, 0.5f, 0.5f), "Change tempo");
        Assert.IsTrue(SpeedAt(60f, PitchType.Slider, 0f, 0.5f, 0.5f) < SpeedAt(60f, PitchType.Slider, 1f, 0.5f, 0.5f), "Slider velocity");
        Assert.IsTrue(SpeedAt(60f, PitchType.Knuckle, 0.5f, 0.5f, 0.5f) < SpeedAt(60f, PitchType.FourSeam, 0.5f, 0.5f, 0.5f), "Knuckle is slower than the base");
    }

    [TsukimiTest]
    public void FixedWakesPointTheIntendedWay()
    {
        PitchProperties twoSeam = Build(PitchType.TwoSeam, 1f, 0.5f, 0.5f);
        Assert.IsTrue(Near(twoSeam.wakeLocal.coefficient, 0.033f));
        Assert.IsTrue(twoSeam.wakeLocal.direction.x > 0.99f, "2-Seam: arm side");
        Assert.IsTrue(MagnusBreak(twoSeam).x > 0f && MagnusBreak(twoSeam).y > 0f, "2-Seam Magnus: arm side and ride");
        Assert.IsTrue(Build(PitchType.Sinker, 0.5f, 0f, 0.5f).wakeLocal.direction.x > 0.99f, "Sinker B=0: arm side");
        Assert.IsTrue(Build(PitchType.Sinker, 0.5f, 1f, 0.5f).wakeLocal.direction.y < -0.99f, "Sinker B=1: down");
        Vector3 mixed = Build(PitchType.Sinker, 0.5f, 0.5f, 0.5f).wakeLocal.direction;
        Assert.IsTrue(mixed.x > 0f && mixed.y < 0f && mixed.x < 0.8f && mixed.y > -0.8f, "run and sink trade off");
        Assert.IsTrue(Near(Build(PitchType.Sinker, 1f, 0.5f, 0.5f).wakeLocal.coefficient, 0.05f));
        Assert.IsTrue(Build(PitchType.Cutter, 0.5f, 0.5f, 0.5f).wakeLocal.direction.x < -0.99f, "Cutter: glove side");
        Assert.IsTrue(MagnusBreak(Build(PitchType.Cutter, 1f, 0.5f, 0.5f)).x < MagnusBreak(Build(PitchType.Cutter, 0f, 0.5f, 0.5f)).x,
            "more cut, more glove-side Magnus");
        PitchProperties sweep = Build(PitchType.Sweeper, 0f, 0.5f, 0.5f);
        PitchProperties lift = Build(PitchType.Sweeper, 1f, 0.5f, 0.5f);
        Assert.IsTrue(sweep.wakeLocal.direction.x < -0.99f, "Sweeper A=0: glove side");
        Assert.IsTrue(lift.wakeLocal.direction.y > 0.99f, "Sweeper A=1: up");
        Assert.AreEqual(sweep.wakeLocal.coefficient, lift.wakeLocal.coefficient, "only the direction trades off");
        Assert.IsTrue(MagnusBreak(sweep).x < -0.8f, "Sweeper Magnus: glove side");
        Assert.IsTrue(Build(PitchType.Change, 0.5f, 0f, 0.5f).wakeLocal.direction.x > 0.99f, "Change B=0: arm side");
        Assert.IsTrue(Build(PitchType.Change, 0.5f, 1f, 0.5f).wakeLocal.direction.y < -0.99f, "Change B=1: down");
        Assert.AreEqual(BallAerodynamics.WakeNone, Build(PitchType.Slider, 0.5f, 0.5f, 0.5f).wakeLocal.mode);
        Assert.AreEqual(BallAerodynamics.WakeNone, Build(PitchType.Curve, 0.5f, 0.5f, 0.5f).wakeLocal.mode);
        Assert.AreEqual(BallAerodynamics.WakeNone, Build(PitchType.Split, 0.5f, 0.5f, 0.5f).wakeLocal.mode);
    }

    [TsukimiTest]
    public void BreakingBallsTurnTheirSpinTheIntendedWay()
    {
        Vector3 sweepingSlider = MagnusBreak(Build(PitchType.Slider, 0.5f, 1f, 0f));
        Vector3 downSlider = MagnusBreak(Build(PitchType.Slider, 0.5f, 1f, 1f));
        Assert.IsTrue(sweepingSlider.x < -0.7f && Mathf.Abs(sweepingSlider.y) < 0.01f, "Slider C=0: glove side");
        Assert.IsTrue(downSlider.y < -0.7f && Mathf.Abs(downSlider.x) < 0.01f, "Slider C=1: down");
        PitchProperties gyro = Build(PitchType.Slider, 0.5f, 0f, 0.5f);
        PitchProperties active = Build(PitchType.Slider, 0.5f, 1f, 0.5f);
        Assert.AreEqual(gyro.spinRpm, active.spinRpm, "B changes the axis, not the total spin");
        Assert.IsTrue(MagnusBreak(active).magnitude > MagnusBreak(gyro).magnitude * 3f, "B adds active spin");
        Vector3 drop = MagnusBreak(Build(PitchType.Curve, 0f, 0.5f, 0.5f));
        Vector3 slurve = MagnusBreak(Build(PitchType.Curve, 1f, 0.5f, 0.5f));
        Assert.IsTrue(drop.y < -0.8f, "Curve A=0: down");
        Assert.IsTrue(slurve.x < drop.x && slurve.x < -0.7f, "Curve A=1: glove side");
        Vector3 split = MagnusBreak(Build(PitchType.Split, 0f, 0.5f, 0.5f));
        Vector3 runningSplit = MagnusBreak(Build(PitchType.Split, 1f, 0.5f, 0.5f));
        Assert.IsTrue(runningSplit.x > split.x && runningSplit.x > 0f, "Split A: arm side");
        Assert.IsTrue(MagnusBreak(Build(PitchType.Change, 0.5f, 0.5f, 0.5f)).x > 0f, "Change Magnus: arm side");
    }

    [TsukimiTest]
    public void KnuckleUsesARotatingWake()
    {
        PitchBuild build = new PitchBuild();
        build.pitchTypeId = PitchType.Knuckle;
        build.parameterB = 0.5f;
        build.parameterC = 0.5f;
        build.parameterA = 0f;
        PitchProperties slow = PitchPropertiesCalculator.Calculate(build, BaseSpeed, BaseSpin);
        build.parameterA = 1f;
        PitchProperties fast = PitchPropertiesCalculator.Calculate(build, BaseSpeed, BaseSpin);
        Assert.AreEqual(BallAerodynamics.WakeRotating, slow.wakeLocal.mode);
        Assert.AreEqual(0.040f, slow.wakeLocal.coefficient);
        Assert.AreEqual(PitchPropertiesCalculator.KnuckleMinRpm, slow.spinRpm, "absolute spin, not the base");
        Assert.AreEqual(PitchPropertiesCalculator.KnuckleMaxRpm, fast.spinRpm);
        Assert.AreEqual(0f, slow.wakeLocal.initialPhase, "the calculator draws no phase; the preparing pitcher does");
        BallWake wake = BallAerodynamics.PrepareWake(fast.wakeLocal, PitchPropertiesCalculator.LocalSpin(fast));
        Assert.AreEqual(BallAerodynamics.WakeRotating, wake.mode);
        Assert.IsTrue(Mathf.Abs(wake.rate - PitchPropertiesCalculator.KnuckleMaxRpm * 2f * Mathf.PI / 60f) < 0.001f, "the force turns with the spin");
        Assert.IsTrue(BallAerodynamics.PrepareWake(slow.wakeLocal, PitchPropertiesCalculator.LocalSpin(slow)).rate < wake.rate, "A sets the rate");
        build.parameterC = 0f;
        float small = PitchPropertiesCalculator.Calculate(build, BaseSpeed, BaseSpin).wakeLocal.coneAngle;
        build.parameterC = 1f;
        float large = PitchPropertiesCalculator.Calculate(build, BaseSpeed, BaseSpin).wakeLocal.coneAngle;
        Assert.IsTrue(large > small, "C widens the cone");
        build.parameterB = 0f;
        Vector3 glove = PitchPropertiesCalculator.Calculate(build, BaseSpeed, BaseSpin).wakeLocal.direction;
        build.parameterB = 1f;
        Vector3 arm = PitchPropertiesCalculator.Calculate(build, BaseSpeed, BaseSpin).wakeLocal.direction;
        Assert.IsTrue(glove.x < -0.4f && arm.x > 0.4f && glove.z > 0.8f, "B tilts the axis from glove side to arm side");
    }

    [TsukimiTest]
    public void PhasesFromSeedsAreDeterministicAndInRange()
    {
        Assert.AreEqual(PitchPropertiesCalculator.PhaseFromSeed(123456789), PitchPropertiesCalculator.PhaseFromSeed(123456789));
        Assert.AreNotEqual(PitchPropertiesCalculator.PhaseFromSeed(123456789), PitchPropertiesCalculator.PhaseFromSeed(987654321));
        for (int seed = -5000000; seed < 5000000; seed += 777777)
        {
            float phase = PitchPropertiesCalculator.PhaseFromSeed(seed);
            Assert.IsTrue(phase >= 0f && phase < 2f * Mathf.PI);
        }
    }

    [TsukimiTest]
    public void TheTenTypesAreListedWithoutAScrewball()
    {
        Assert.AreEqual(10, PitchType.Count);
        Assert.AreEqual("4-Seam", PitchType.Name(PitchType.FourSeam));
        Assert.AreEqual("Sweeper", PitchType.Name(5), "id 5 is the Sweeper");
        Assert.AreEqual("Knuckle", PitchType.Name(PitchType.Knuckle));
        for (int type = 0; type < PitchType.Count; type++)
            Assert.IsFalse(PitchType.Name(type).Contains("Screw"));
        Assert.AreEqual("4-Seam", PitchType.Name(10), "an unknown id reads as the 4-Seam");
        Assert.AreEqual(0, PitchType.ParameterCount(PitchType.FourSeam));
        Assert.AreEqual(3, PitchType.ParameterCount(PitchType.Slider));
    }
}
