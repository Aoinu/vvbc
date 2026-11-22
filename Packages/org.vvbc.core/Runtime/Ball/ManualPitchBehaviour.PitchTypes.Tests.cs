using Tsukimi;
using UnityEngine;

// Phase 2 pitch types on the mound: selection, the label above the plate, the type fixed by a
// release, the palm quality with the arm slot and the hands, the failure mode, the solver.
public partial class ManualPitchBehaviour
{
    // The rotation taking e1 to `first` and e2 to `second` (orthonormal, pitching or local axes).
    private static Quaternion BasisRotation(Vector3 first, Vector3 second)
    {
        Vector3 a = first.normalized;
        Vector3 b = (second - a * Vector3.Dot(second, a)).normalized;
        Vector3 c = Vector3.Cross(a, b);
        float m00 = a.x, m01 = b.x, m02 = c.x, m10 = a.y, m11 = b.y, m12 = c.y, m20 = a.z, m21 = b.z, m22 = c.z;
        float trace = m00 + m11 + m22;
        if (trace > 0f)
        {
            float s = 0.5f / Mathf.Sqrt(trace + 1f);
            return new Quaternion((m21 - m12) * s, (m02 - m20) * s, (m10 - m01) * s, 0.25f / s);
        }
        if (m00 > m11 && m00 > m22)
        {
            float s = 2f * Mathf.Sqrt(1f + m00 - m11 - m22);
            return new Quaternion(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
        }
        if (m11 > m22)
        {
            float s = 2f * Mathf.Sqrt(1f + m11 - m00 - m22);
            return new Quaternion((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
        }
        float t = 2f * Mathf.Sqrt(1f + m22 - m00 - m11);
        return new Quaternion((m02 + m20) / t, (m12 + m21) / t, 0.25f * t, (m10 - m01) / t);
    }

    // The world controller rotation whose finger axis (right/leftFingerLocal) and palm
    // (right/leftPalmLocal) point along `finger` and `palm`, in the pitching frame of the test chest.
    private Quaternion HandWith(Vector3 finger, Vector3 palm)
    {
        bool left = _activeHand == GloveBehaviour.HandLeft;
        Quaternion world = ManualPitchMath.Multiply(TestFrame(TestChest), BasisRotation(finger, palm));
        Quaternion local = BasisRotation(left ? leftFingerLocal : rightFingerLocal, left ? leftPalmLocal : rightPalmLocal);
        return ManualPitchMath.Multiply(world, ManualPitchMath.Inverse(local));
    }

    // A hand with the fingers along `finger` (pitching frame) twisted `twist` degrees
    // (pronation positive) from the palm facing the plate.
    private Quaternion TwistHandAt(Vector3 finger, float twist)
    {
        Vector3 f = finger.normalized;
        Vector3 palm = ManualPitchQuality.PalmAtTwist(f, ManualPitchQuality.ReferencePalm(f), twist, _activeHand == GloveBehaviour.HandLeft);
        return HandWith(f, palm);
    }

    // The same with the fingers straight up (an overhand release).
    private Quaternion TwistHand(float twist)
    {
        return TwistHandAt(Vector3.up, twist);
    }

    private bool TypeReleaseForTest(int type, Quaternion rotation)
    {
        SelectPitchTypeAt(type, TestGravity, 0.02f);
        LockForTest(new Vector3(0f, 0.75f, 0f));
        return CurveReleaseForTest(Vector3.zero, rotation, TowardPlate(), 0.97f);
    }

    private float ReleaseRpm()
    {
        return releaseSpin.magnitude * 60f / (2f * Mathf.PI);
    }

    // ---- Selection ----

    [TsukimiTest]
    public void DownSelectsTheNextTypeUpThePreviousAndBothWrap()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.AreEqual(PitchType.FourSeam, SelectedPitchType(), "the 4-Seam first");
        for (int i = 1; i <= PitchType.Count; i++)
        {
            Assert.IsTrue(PitchSelectInput(-1f), "down " + i);
            Assert.AreEqual(i % PitchType.Count, SelectedPitchType());
            Assert.AreEqual(PitchType.Name(i % PitchType.Count), SelectedPitchTypeName());
            PitchSelectInput(0f);
        }
        Assert.AreEqual(PitchType.FourSeam, SelectedPitchType(), "past the Knuckle back to the 4-Seam");
        Assert.IsTrue(PitchSelectInput(1f));
        Assert.AreEqual(PitchType.Knuckle, SelectedPitchType(), "up from the 4-Seam wraps to the Knuckle");
        PitchSelectInput(0f);
        Assert.IsTrue(PitchSelectInput(0.8f));
        Assert.AreEqual(PitchType.Change, SelectedPitchType());
        PitchSelectInput(0f);
        invertPitchSelect = true;
        Assert.IsTrue(PitchSelectInput(1f));
        Assert.AreEqual(PitchType.Knuckle, SelectedPitchType(), "inverted: up is the next type");
    }

    [TsukimiTest]
    public void OneDeflectionSwitchesOnceUntilTheStickIsBackAtNeutral()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsFalse(PitchSelectInput(-0.45f), "inside the dead zone");
        Assert.AreEqual(PitchType.FourSeam, SelectedPitchType());
        Assert.IsTrue(PitchSelectInput(-0.6f));
        Assert.IsFalse(PitchSelectInput(-1f), "held: no second switch");
        Assert.IsFalse(PitchSelectInput(-0.3f), "half way back is not neutral");
        Assert.IsFalse(PitchSelectInput(-0.9f));
        Assert.AreEqual(PitchType.TwoSeam, SelectedPitchType());
        Assert.IsFalse(PitchSelectInput(0.1f), "neutral re-arms");
        Assert.IsTrue(PitchSelectInput(-0.9f));
        Assert.AreEqual(PitchType.Sinker, SelectedPitchType());
        Assert.IsFalse(PitchSelectInput(float.NaN));
        PitchSelectInput(0f);
        // A swing from down straight to up switches only once, until neutral.
        Assert.IsTrue(PitchSelectInput(1f));
        Assert.IsFalse(PitchSelectInput(-1f));
        Assert.AreEqual(PitchType.TwoSeam, SelectedPitchType());
    }

    // Only in pitcher mode and not while calibrating; holding the ball is not needed (none here).
    [TsukimiTest]
    public void TheTypeIsSwitchedOnlyInPitcherModeAndNotWhileCalibrating()
    {
        Mimic.Join();
        Assert.AreEqual(StateInactive, state);
        Assert.IsFalse(PitchSelectInput(-1f), "off the mound");
        Assert.AreEqual(PitchType.FourSeam, SelectedPitchType());
        PitchSelectInput(0f);
        ActiveForTest(GloveBehaviour.HandRight);
        BeginCalibration();
        Assert.IsFalse(PitchSelectInput(-1f), "calibrating");
        Assert.AreEqual(PitchType.FourSeam, SelectedPitchType());
        PitchSelectInput(0f);
        CancelCalibration();
        Assert.IsTrue(PitchSelectInput(-1f), "without a ball");
        Assert.AreEqual(PitchType.TwoSeam, SelectedPitchType());
        // The selection is kept across releases (never reset to the 4-Seam).
        Assert.IsTrue(CalibrateForTest(Overhand));
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, TwistHand(IdealTwist(PitchType.TwoSeam)), TowardPlate()));
        Assert.AreEqual(PitchType.TwoSeam, SelectedPitchType());
    }

    [TsukimiTest]
    public void TheLabelSitsAboveThePlateAndFollowsItsSize()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        PlateForTest();
        Assert.IsTrue(PitchTypeLabelShown());
        Vector3 expected = TestP0() + Vector3.up * (0.25f + pitchTypeLabelOffset);
        Assert.IsTrue(Vector3.Distance(PitchTypeLabelPosition(), expected) < 0.0001f, "centred above the top edge " + PitchTypeLabelPosition());
        releasePlateHeight = 0.8f;
        pitchTypeLabelOffset = 0.1f;
        PlateForTest();
        Assert.IsTrue(Vector3.Distance(PitchTypeLabelPosition(), TestP0() + Vector3.up * 0.5f) < 0.0001f, "a taller plate");
        BeginCalibration();
        Assert.IsFalse(PitchTypeLabelShown(), "hidden while calibrating");
        CancelCalibration();
        showPitchTypeLabel = false;
        Assert.IsFalse(PitchTypeLabelShown());
        showPitchTypeLabel = true;
        state = StateInactive;
        Assert.IsFalse(PitchTypeLabelShown(), "off the mound");
    }

    // ---- Every type with its default build ----

    [TsukimiTest(maxSteps: 4000000)]
    public void EveryTypeIsPitchedWithItsDefaultBuildAtItsOwnSpeed()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        for (int type = 0; type < PitchType.Count; type++)
        {
            Assert.IsTrue(TypeReleaseForTest(type, TwistHand(IdealTwist(type))), PitchType.Name(type) + ": " + debugReason);
            string name = PitchType.Name(type);
            Assert.AreEqual(type, releasePitchType, name);
            Assert.AreEqual(1f, debugQuality, name + ": the ideal palm at the ideal timing");
            float multiplier = PitchPropertiesCalculator.SpeedMultiplier(BuildFor(type));
            Assert.IsTrue(Near(releaseVelocity.magnitude * 3.6f, 140f * 0.97f * multiplier, 0.01f), name + " speed " + releaseVelocity.magnitude * 3.6f);
            PitchProperties ideal = ManualPitchQuality.IdealProperties(BuildFor(type), FourSeamRpm(), wakeCoefficient, wakeClockDegrees, false);
            Assert.IsTrue(Near(ReleaseRpm(), ideal.spinRpm, 0.5f), name + " rpm " + ReleaseRpm());
            Assert.IsTrue(Vector3.Distance(debugSpinAxis, ideal.spinAxisLocal) < 0.0001f, name + " axis");
            Assert.AreEqual(ideal.wakeLocal.mode, releaseWakeMode, name + " SSW mode");
            Assert.IsTrue(ReleaseResultText(true).StartsWith("PITCH  " + name), ReleaseResultText(true));
        }
        Assert.AreEqual(PitchBuild.DefaultParameter, BuildFor(PitchType.Slider).parameterC, "the default build");
        Assert.IsTrue(Near(ReleaseRpm(), PitchPropertiesCalculator.KnuckleMinRpm + 0.5f * (PitchPropertiesCalculator.KnuckleMaxRpm - PitchPropertiesCalculator.KnuckleMinRpm), 0.5f),
            "the Knuckle's own rpm");
    }

    // A bad palm turns the type into the common failure mode: the 4-Seam axis, 70 % of the
    // 4-Seam rpm and the 4-Seam SSW, at the type's own speed and on the same launch direction.
    [TsukimiTest]
    public void AFailedPoseFallsToTheCommonFailureModeAtTheTypesSpeed()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Assert.IsTrue(TypeReleaseForTest(PitchType.Slider, TwistHand(IdealTwist(PitchType.Slider))));
        Vector3 idealVelocity = releaseVelocity;
        Assert.IsTrue(TypeReleaseForTest(PitchType.Slider, TwistHand(IdealTwist(PitchType.Slider) + 80f)));
        Assert.IsTrue(Near(debugPoseAngle, 80f, 0.1f), "palm error " + debugPoseAngle);
        Assert.AreEqual(0f, debugQuality);
        Assert.IsTrue(Near(ReleaseRpm(), 0.7f * 2500f, 0.5f), "70 % of the 4-Seam rpm: " + ReleaseRpm());
        Assert.IsTrue(Vector3.Distance(debugSpinAxis, FourSeamAxis()) < 0.0001f, "the 4-Seam axis");
        Assert.AreEqual(BallAerodynamics.WakeFixed, releaseWakeMode);
        Assert.AreEqual(wakeCoefficient, releaseWakeCoefficient, "the 4-Seam SSW");
        Assert.IsTrue(Vector3.Distance(releaseVelocity, idealVelocity) < 0.00001f, "the same speed and direction");
        // Half way between the good and bad angles: half quality, between the two.
        float middle = 0.5f * (PoseGoodAngle(PitchType.Slider) + PoseBadAngle(PitchType.Slider));
        Assert.IsTrue(TypeReleaseForTest(PitchType.Slider, TwistHand(IdealTwist(PitchType.Slider) + middle)));
        Assert.IsTrue(Near(debugQuality, 0.5f, 0.01f), "half quality " + debugQuality);
        float slider = 0.9f * 2500f;
        Assert.IsTrue(Near(ReleaseRpm(), 0.5f * (slider + 1750f), 2f), "rpm between " + ReleaseRpm());
        Assert.IsTrue(Vector3.Distance(releaseVelocity, idealVelocity) < 0.00001f, "quality never changes the speed");
        // Within the good angle: full quality, either way round.
        Assert.IsTrue(TypeReleaseForTest(PitchType.Slider, TwistHand(IdealTwist(PitchType.Slider) + PoseGoodAngle(PitchType.Slider) - 1f)));
        Assert.AreEqual(1f, debugQuality);
        Assert.IsTrue(TypeReleaseForTest(PitchType.Slider, TwistHand(IdealTwist(PitchType.Slider) - PoseGoodAngle(PitchType.Slider) + 1f)));
        Assert.AreEqual(1f, debugQuality);
    }

    [TsukimiTest]
    public void TheFourSeamIgnoresThePoseButNotTheTiming()
    {
        Mimic.Join();
        Assert.IsTrue(PlateReadyForTest());
        Assert.IsFalse(PoseEvaluated(PitchType.FourSeam));
        // Ideal timing, any palm: the 4-Seam's own flight.
        SwingForTest(Vector2.zero, 10f, 0.05f, 3, 21);
        Assert.IsTrue(TrackedReleaseForTest(4, PlatePointForTest(Vector2.zero, 0.2f), 21, InputTrigger), debugReason);
        Assert.AreEqual(ReleasePlateMath.TimingIdeal, _resultTiming);
        Assert.AreEqual(1f, debugQuality);
        Assert.IsTrue(Near(ReleaseRpm(), 2500f, 0.5f));
        Vector3 axis = releaseSpin.normalized;
        float speed = releaseVelocity.magnitude;
        // The auto release is the ideal timing for quality (the control score is capped).
        PlateForTest();
        ResetArmSamples();
        SwingForTest(Vector2.zero, 10f, 0.05f, 3, 22);
        Assert.IsTrue(TrackedReleaseForTest(8, PlatePointForTest(Vector2.zero, 0.3f), 22, InputAuto), debugReason);
        Assert.AreEqual(ReleasePlateMath.TimingAuto, _resultTiming);
        Assert.AreEqual(1f, debugQuality, "auto: full quality");
        Assert.AreEqual(60f, _resultTimingCap, "auto: the control cap stays");
        // Early: capped at qualityTimingCap even with the pose not evaluated.
        PlateForTest();
        ResetArmSamples();
        SwingForTest(Vector2.zero, 10f, 0.48f, 3, 23);
        Assert.IsTrue(TrackedReleaseForTest(3, PlatePointForTest(Vector2.zero, -0.15f), 23, InputTrigger), debugReason);
        Assert.AreEqual(ReleasePlateMath.TimingEarly, _resultTiming);
        Assert.AreEqual(qualityTimingCap, debugQuality, "early: the quality cap");
        Assert.IsTrue(Near(ReleaseRpm(), 1750f + 750f * qualityTimingCap, 0.5f), "rpm toward the failure " + ReleaseRpm());
        Assert.IsTrue(Vector3.Angle(axis, releaseSpin.normalized) < 2f, "the axis is the 4-Seam's either way");
        Assert.IsTrue(Near(releaseVelocity.magnitude, speed, 0.0001f), "the same speed");
        Assert.AreEqual(wakeCoefficient, releaseWakeCoefficient);
        // The control cap is its own setting.
        qualityTimingCap = 0.2f;
        PlateForTest();
        ResetArmSamples();
        SwingForTest(Vector2.zero, 10f, 0.48f, 3, 24);
        Assert.IsTrue(TrackedReleaseForTest(3, PlatePointForTest(Vector2.zero, -0.15f), 24, InputTrigger));
        Assert.AreEqual(0.2f, debugQuality);
        Assert.AreEqual(60f, _resultTimingCap, "the control cap is unchanged");
    }

    // ---- Arm slot, wrist and hands ----

    // The twist is measured about the fingers at the judged moment: the same twist is the same
    // hand shape (and the same quality) overhand, sidearm or underhand, whatever the palm's
    // direction in the world, and a palm facing the same world direction at another slot is not.
    [TsukimiTest]
    public void TheSameTwistIsJudgedTheSameAtEveryArmSlot()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        float slider = IdealTwist(PitchType.Slider);
        Vector3[] fingers = { Vector3.up, new Vector3(0.7f, 0.7f, -0.1f), Vector3.right, new Vector3(0.6f, -0.6f, 0.2f) };
        Vector3 axis = Vector3.zero;
        for (int i = 0; i < fingers.Length; i++)
        {
            Assert.IsTrue(TypeReleaseForTest(PitchType.Slider, TwistHandAt(fingers[i], slider)));
            Assert.IsTrue(Near(debugPoseAngle, 0f, 0.05f) && debugQuality == 1f, "slot " + i + ": " + debugPoseAngle);
            Assert.IsTrue(Near(_resultQuality.actualTwist, slider, 0.05f));
            if (i == 0) axis = debugSpinAxis;
            Assert.IsTrue(Vector3.Distance(axis, debugSpinAxis) < 0.00001f, "the spin axis never follows the slot");
        }
        // Sidearm with the overhand Slider's world palm: a 45 deg twist off.
        Vector3 overhandPalm = ManualPitchQuality.PalmAtTwist(Vector3.up, Vector3.forward, slider, false);
        Assert.IsTrue(TypeReleaseForTest(PitchType.Slider, HandWith(Vector3.right, overhandPalm - Vector3.right * Vector3.Dot(overhandPalm, Vector3.right))));
        Assert.IsTrue(debugPoseAngle > 40f && debugQuality < 0.7f, "the overhand world palm at sidearm " + debugPoseAngle);
    }

    // Only the twist about the finger axis counts: bending the wrist (about the thumb axis) or
    // turning it sideways (about the palm normal) leaves a palm-to-the-plate hand at 0 deg, a
    // turn about the fingers changes the twist by exactly that, and the fingers at the plate
    // leave no twist (no reward).
    [TsukimiTest]
    public void OnlyTheTwistAboutTheFingersIsJudged()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        float[] bends = { -40f, -15f, 20f, 45f };
        for (int i = 0; i < bends.Length; i++)
        {
            float b = bends[i] * Mathf.Deg2Rad;
            // Wrist bend about the thumb axis (x): fingers and palm turn together in the y-z plane.
            Vector3 finger = new Vector3(0f, Mathf.Cos(b), Mathf.Sin(b));
            Vector3 palm = new Vector3(0f, -Mathf.Sin(b), Mathf.Cos(b));
            Assert.IsTrue(TypeReleaseForTest(PitchType.Split, HandWith(finger, palm)));
            Assert.IsTrue(Near(_resultQuality.actualTwist, 0f, 0.05f), "wrist bend " + bends[i] + ": " + _resultQuality.actualTwist);
            // Sideways about the palm normal (z): the fingers turn in the x-y plane.
            Vector3 side = new Vector3(Mathf.Sin(b), Mathf.Cos(b), 0f);
            Assert.IsTrue(TypeReleaseForTest(PitchType.Split, HandWith(side, Vector3.forward)));
            Assert.IsTrue(Near(_resultQuality.actualTwist, 0f, 0.05f), "sideways " + bends[i] + ": " + _resultQuality.actualTwist);
        }
        for (int t = -170; t <= 170; t += 20)
        {
            Assert.IsTrue(TypeReleaseForTest(PitchType.Split, TwistHandAt(new Vector3(0.2f, 0.9f, -0.3f), t)));
            Assert.IsTrue(Near(_resultQuality.actualTwist, t, 0.05f), "twist " + t + ": " + _resultQuality.actualTwist);
        }
        Assert.IsTrue(TypeReleaseForTest(PitchType.Split, HandWith(new Vector3(0.05f, 0f, 1f), Vector3.up)));
        Assert.IsFalse(_resultQuality.twistDefined, "fingers at the plate");
        Assert.AreEqual(0f, debugPoseQuality);
        Assert.IsTrue(ReleaseDetailText().Contains("twist not measured"), ReleaseDetailText());
    }

    // A left-hander's mirror-image release has the same quality and a mirror-image flight.
    [TsukimiTest(maxSteps: 4000000)]
    public void ALeftHandersMirrorImageReleaseIsJudgedTheSame()
    {
        Mimic.Join();
        int[] types = { PitchType.Slider, PitchType.Sinker, PitchType.Curve, PitchType.Knuckle };
        Vector3[] forms = { Overhand, new Vector3(0.6f, 0f, 0.2f) };
        for (int f = 0; f < forms.Length; f++)
            for (int t = 0; t < types.Length; t++)
            {
                ActiveForTest(GloveBehaviour.HandRight);
                Assert.IsTrue(CalibrateForTest(forms[f]));
                Vector3 finger = f == 0 ? new Vector3(0.2f, 0.9f, -0.2f) : new Vector3(0.9f, 0.2f, -0.1f);
                Assert.IsTrue(TypeReleaseForTest(types[t], TwistHandAt(finger, IdealTwist(types[t]) + 40f)));
                float rightQuality = debugQuality;
                Vector3 rightAxis = debugSpinAxis;
                float rightRpm = ReleaseRpm();
                ActiveForTest(GloveBehaviour.HandLeft);
                Vector3 form = new Vector3(-forms[f].x, forms[f].y, forms[f].z);
                Assert.IsTrue(CalibrateForTest(form));
                Vector3 mirrored = new Vector3(-finger.x, finger.y, finger.z);
                Assert.IsTrue(TypeReleaseForTest(types[t], TwistHandAt(mirrored, IdealTwist(types[t]) + 40f)));
                string name = PitchType.Name(types[t]) + " form " + f;
                Assert.IsTrue(Near(debugQuality, rightQuality, 0.001f), name + ": " + rightQuality + " vs " + debugQuality);
                Assert.IsTrue(Vector3.Distance(debugSpinAxis, new Vector3(rightAxis.x, -rightAxis.y, -rightAxis.z)) < 0.001f,
                    name + ": the spin is mirrored as an axial vector");
                Assert.IsTrue(Near(ReleaseRpm(), rightRpm, 0.5f), name + " rpm");
            }
    }

    // Record Pose: the throwing hand's twist becomes the selected type's ideal for this session
    // (rounded to a degree) and the next release is judged against it; never with the fingers at
    // the plate.
    [TsukimiTest]
    public void RecordingAPoseSetsTheSelectedTypesIdealTwist()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        SelectPitchTypeAt(PitchType.Slider, TestGravity, 0.02f);
        Assert.IsTrue(RecordIdealTwistAt(TwistHandAt(new Vector3(0.3f, 0.9f, -0.2f), -32.4f), TestFrame(TestChest)));
        Assert.AreEqual(-32f, IdealTwist(PitchType.Slider), "rounded");
        Assert.AreEqual(-60f, IdealTwist(PitchType.Sweeper), "other types untouched");
        Assert.IsTrue(statusText.Contains("Slider ideal twist set to 回外 -32°") && statusText.Contains("idealTwistAngles[4] = -32"), statusText);
        Assert.IsTrue(TypeReleaseForTest(PitchType.Slider, TwistHand(-32f)));
        Assert.AreEqual(1f, debugQuality, "the recorded pose is now ideal");
        Assert.IsFalse(RecordIdealTwistAt(HandWith(Vector3.forward, Vector3.up), TestFrame(TestChest)), "fingers at the plate");
        Assert.AreEqual(-32f, IdealTwist(PitchType.Slider));
        // An older, shorter table grows to every type before it is written.
        idealTwistAngles = new float[2];
        SelectPitchTypeAt(PitchType.Knuckle, TestGravity, 0.02f);
        Assert.IsTrue(RecordIdealTwistAt(TwistHand(15f), TestFrame(TestChest)));
        Assert.AreEqual(PitchType.Count, idealTwistAngles.Length);
        Assert.AreEqual(15f, IdealTwist(PitchType.Knuckle));
        Assert.AreEqual("回内 +15°", TwistName(15f));
        Assert.AreEqual("Sup -45°", TwistLabel(-45f));
        Assert.AreEqual("0°", TwistName(0.3f));
    }

    // ---- The type fixed by the release ----

    // The crossing fixes the type and its course for the hold: switching afterwards (before the
    // auto release) changes neither the released type nor its flight.
    [TsukimiTest]
    public void TheCrossingFixesTheTypeOfTheHold()
    {
        Mimic.Join();
        Assert.IsTrue(PlateReadyForTest());
        SelectPitchTypeAt(PitchType.Slider, TestGravity, 0.02f);
        LockForTest(new Vector3(0.1f, 0.8f, 0f));
        Vector3 sliderDirection = _lockDirection;
        Assert.IsTrue(SwingForTest(Vector2.zero, 10f, 0.05f, 3, 31) >= 0, "crossed");
        Assert.AreEqual(PitchType.Slider, _crossType);
        SelectPitchTypeAt(PitchType.Curve, TestGravity, 0.02f);
        Assert.AreEqual(StateTargeting, state, "the lock could not be solved again here (no ball): it is released");
        Assert.IsTrue(TrackedReleaseForTest(5, PlatePointForTest(Vector2.zero, 0.3f), 31, InputAuto), debugReason);
        Assert.AreEqual(PitchType.Slider, releasePitchType, "the type of the crossing");
        Assert.IsTrue(Near(releaseVelocity.magnitude * 3.6f, 140f * PitchPropertiesCalculator.SpeedMultiplier(BuildFor(PitchType.Slider)), 0.01f),
            "the Slider's speed " + releaseVelocity.magnitude * 3.6f);
        Assert.IsTrue(Vector3.Distance(releaseVelocity.normalized, sliderDirection) < 0.00001f, "the Slider's own course");
        Assert.AreEqual(PitchType.Curve, SelectedPitchType(), "the selection stays");
    }

    // An early release takes the type selected at the release, and only a course solved for it.
    [TsukimiTest]
    public void AnEarlyReleaseUsesTheSelectionAndNeverAStaleCourse()
    {
        Mimic.Join();
        Assert.IsTrue(PlateReadyForTest());
        SelectPitchTypeAt(PitchType.Cutter, TestGravity, 0.02f);
        LockForTest(new Vector3(0f, 0.75f, 0f));
        SwingForTest(Vector2.zero, 10f, 0.48f, 3, 41);
        Assert.IsTrue(TrackedReleaseForTest(3, PlatePointForTest(Vector2.zero, -0.15f), 41, InputTrigger), debugReason);
        Assert.AreEqual(PitchType.Cutter, releasePitchType);
        // A course solved for another type is never used: no course for this type, no pitch.
        LockForTest(new Vector3(0f, 0.75f, 0f));
        _selectedType = PitchType.Split;
        PlateForTest();
        ResetArmSamples();
        SwingForTest(Vector2.zero, 10f, 0.48f, 3, 42);
        Assert.IsFalse(TrackedReleaseForTest(3, PlatePointForTest(Vector2.zero, -0.15f), 42, InputTrigger));
        Assert.AreEqual("not locked", debugReason);
        // Switching marks the default course stale too.
        SelectPitchTypeAt(PitchType.Sweeper, TestGravity, 0.02f);
        Assert.IsFalse(_defaultReady, "the centre course is solved again for the new type");
        Assert.IsFalse(CourseFor(PitchType.Sweeper).ready);
    }

    // The Knuckle's phase is drawn with the course (the solver aims with it) and published with
    // the release; the default course is solved again after it, for a new phase.
    [TsukimiTest]
    public void AKnucklePublishesThePhaseItWasAimedWith()
    {
        Mimic.Join();
        Assert.IsTrue(PlateReadyForTest());
        poseEvaluation = new bool[PitchType.Count];
        SelectPitchTypeAt(PitchType.Knuckle, TestGravity, 0.02f);
        DefaultCourseForTest(new Vector3(0f, 0.75f, 0f));
        float phase = _defaultIdeal.wakeLocal.initialPhase;
        SwingForTest(Vector2.zero, 10f, 0.05f, 3, 51);
        Assert.IsTrue(TrackedReleaseForTest(4, PlatePointForTest(Vector2.zero, 0.2f), 51, InputTrigger), debugReason);
        Assert.AreEqual(BallAerodynamics.WakeRotating, releaseWakeMode);
        Assert.AreEqual(phase, releaseWakePhase, "the solver's phase");
        Assert.IsTrue(releaseWakeConeAngle > 0.2f, "its cone");
        Assert.IsFalse(_defaultReady, "the next Knuckle gets a new phase");
        DefaultCourseForTest(new Vector3(0f, 0.75f, 0f));
        Assert.AreNotEqual(phase, _defaultIdeal.wakeLocal.initialPhase, "a new draw");
        // A non-rotating type publishes no cone or phase.
        SelectPitchTypeAt(PitchType.Sinker, TestGravity, 0.02f);
        DefaultCourseForTest(new Vector3(0f, 0.75f, 0f));
        PlateForTest();
        ResetArmSamples();
        SwingForTest(Vector2.zero, 10f, 0.05f, 3, 52);
        Assert.IsTrue(TrackedReleaseForTest(4, PlatePointForTest(Vector2.zero, 0.2f), 52, InputTrigger));
        Assert.AreEqual(BallAerodynamics.WakeFixed, releaseWakeMode);
        Assert.AreEqual(0f, releaseWakeConeAngle);
        Assert.AreEqual(0f, releaseWakePhase);
    }

    // ---- Solver ----

    // Flies the released state, Rotating wake included, and returns its plate-plane crossing.
    private Vector3 FlyReleaseToPlate()
    {
        WakeProfile profile = new WakeProfile();
        profile.mode = releaseWakeMode;
        profile.coefficient = releaseWakeCoefficient;
        profile.direction = releaseWakeDirection;
        profile.coneAngle = releaseWakeConeAngle;
        profile.initialPhase = releaseWakePhase;
        BallWake wake = BallAerodynamics.PrepareWake(profile, releaseSpin);
        BallFlightState flight = new BallFlightState();
        flight.position = releasePosition;
        flight.velocity = releaseVelocity;
        for (int i = 0; i < 300; i++)
        {
            Vector3 previous = flight.position;
            flight = BallFlightModel.Step(flight, releaseSpin, TestGravity, _ball.dragCoefficient, _ball.magnusCoefficient, wake, 0.02f);
            if (flight.position.z <= 0f)
                return previous + (flight.position - previous) * (previous.z / (previous.z - flight.position.z));
        }
        return flight.position;
    }

    // Each type is aimed with its own ideal flight (speed, spin, SSW) and, released ideally at
    // the solver's speed, arrives at the locked target.
    [TsukimiTest(seeds: 10, maxSteps: 90000000)]
    public void EveryTypeIsAimedWithItsOwnFlightAndArrives(int seed)
    {
        Mimic.Join();
        _ball = strikeZone != null ? strikeZone.ball : null;
        Assert.IsNotNull(_ball);
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        SelectPitchTypeAt(seed, TestGravity, 0.02f);
        Assert.IsTrue(SolveLock(new Vector3(0.1f, 0.75f, 0f)), PitchType.Name(seed) + " solved");
        Assert.AreEqual(seed, _lockType);
        Assert.IsTrue(Near(_lockSolverSpeedKmh, 140f * 0.97f * PitchPropertiesCalculator.SpeedMultiplier(BuildFor(seed)), 0.01f), "the type's solver speed");
        Vector3 hand = HandAt(TestChest, Overhand);
        Assert.IsTrue(CrossedCurveReleaseAt(hand, TwistHand(IdealTwist(seed)), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, solverSpeedRatio));
        Assert.AreEqual(1f, debugQuality);
        Vector3 crossing = FlyReleaseToPlate();
        Assert.IsTrue(Vector3.Distance(crossing, LockedTarget()) <= aimSolver.tolerance + 0.005f,
            PitchType.Name(seed) + " arrives " + Vector3.Distance(crossing, LockedTarget()));
        // The failure flight's aim is solved after the ideal one; a fully failed pitch (quality 0)
        // then arrives at the target too, as a failure.
        FinishLockFailureForTest();
        Assert.IsTrue(_lockFailureReady, PitchType.Name(seed) + " failure solved");
        if (!PoseEvaluated(seed)) return;
        state = StateLockedReady;
        Assert.IsTrue(CrossedCurveReleaseAt(hand, TwistHand(IdealTwist(seed) + 90f), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, solverSpeedRatio));
        Assert.AreEqual(0f, debugQuality);
        Assert.IsTrue(Near(ReleaseRpm(), 0.7f * 2500f, 0.5f), "the failure flight");
        Vector3 failed = FlyReleaseToPlate();
        Assert.IsTrue(Vector3.Distance(failed, LockedTarget()) <= aimSolver.tolerance + 0.005f,
            PitchType.Name(seed) + " failed arrives " + Vector3.Distance(failed, LockedTarget()));
    }

    // The default course is solved for the ideal and then the failure flight; a lock taking the
    // solver drops an unfinished failure solve, and the course is solved again later.
    [TsukimiTest(maxSteps: 90000000)]
    public void TheDefaultCourseIsSolvedForTheFailureFlightToo()
    {
        Mimic.Join();
        _ball = strikeZone != null ? strikeZone.ball : null;
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        SelectPitchTypeAt(PitchType.Slider, TestGravity, 0.02f);
        Vector3 centre = new Vector3(0f, 0.75f, 0f);
        Assert.IsTrue(BeginDefaultCourseAt(TestP0(), TestPlate, TestNormal, centre, TestGravity, 0.02f));
        for (int i = 0; i < 400 && _defaultSolving; i++)
        {
            aimSolver.Advance();
            if (aimSolver.solving) continue;
            if (_defaultFailurePhase) CompleteDefaultFailure(aimSolver.converged, aimSolver.errorMeters, aimSolver.launchDirection);
            else CompleteDefaultCourse(aimSolver.converged, aimSolver.errorMeters, aimSolver.launchDirection, aimSolver.predictedCrossing);
        }
        Assert.IsTrue(_defaultReady && _defaultFailureReady, "both aims");
        ManualPitchCourse course = CourseFor(PitchType.Slider);
        Assert.IsTrue(course.ready && course.failureReady);
        Assert.IsTrue(Vector3.Angle(course.direction, course.failureDirection) > 1f, "two different aims");
        // A lock in the middle of the failure solve takes the solver; the centre is solved again.
        Assert.IsTrue(BeginDefaultCourseAt(TestP0(), TestPlate, TestNormal, centre, TestGravity, 0.02f));
        for (int i = 0; i < 400 && !_defaultFailurePhase && _defaultSolving; i++)
        {
            aimSolver.Advance();
            if (!aimSolver.solving) CompleteDefaultCourse(aimSolver.converged, aimSolver.errorMeters, aimSolver.launchDirection, aimSolver.predictedCrossing);
        }
        Assert.IsTrue(_defaultFailurePhase && _defaultReady && !_defaultFailureReady, "failure solve in progress");
        Assert.IsFalse(CourseFor(PitchType.Slider).failureReady, "meanwhile the ideal aim for every quality");
        Assert.IsTrue(SolveLock(new Vector3(0.2f, 0.8f, 0f)));
        Assert.IsFalse(_defaultSolving, "the lock took the solver");
        Assert.IsTrue(_defaultDirty, "the centre is solved again later");
    }

    private void FinishLockFailureForTest()
    {
        for (int i = 0; i < 200 && _lockFailureSolving; i++) AdvanceLockFailure();
    }

    // Switching the type while locked solves the same target again for the new type, for its
    // ideal and its failure flight. A failed pose changes the flight but not the course: the
    // aim follows the quality (exact at 0 and 1). The diagnostic keeps the ideal flight.
    [TsukimiTest(maxSteps: 90000000)]
    public void ASwitchSolvesTheLockAgainAndTheAimFollowsTheQuality()
    {
        Mimic.Join();
        _ball = strikeZone != null ? strikeZone.ball : null;
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Assert.IsTrue(SolveLock(new Vector3(0f, 0.72f, 0f)));
        Vector3 target = LockedTarget();
        Vector3 fourSeam = _lockDirection;
        SelectPitchTypeAt(PitchType.Curve, TestGravity, 0.02f);
        Assert.AreEqual(StateSolving, state, "solved again");
        Assert.AreEqual(target, LockedTarget(), "the target is kept");
        for (int i = 0; i < 200 && state == StateSolving; i++)
        {
            aimSolver.Advance();
            if (!aimSolver.solving) CompleteSolve(aimSolver.converged, aimSolver.errorMeters, aimSolver.launchDirection, aimSolver.predictedCrossing);
        }
        Assert.AreEqual(StateLockedReady, state);
        Assert.AreEqual(PitchType.Curve, _lockType);
        Assert.IsTrue(Vector3.Angle(fourSeam, _lockDirection) > 0.5f, "the Curve's own direction");
        Assert.IsTrue(_lockFailureSolving && !_lockFailureReady, "the failure aim follows");
        FinishLockFailureForTest();
        Assert.IsTrue(_lockFailureReady);
        Assert.IsTrue(Vector3.Angle(_lockFailureDirection, _lockDirection) > 1f, "the failure flight has its own aim");
        Vector3 hand = HandAt(TestChest, Overhand);
        Assert.IsTrue(CrossedCurveReleaseAt(hand, TwistHand(IdealTwist(PitchType.Curve)), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, solverSpeedRatio));
        Vector3 ideal = FlyReleaseToPlate();
        Assert.IsTrue(Vector3.Distance(ideal, target) <= aimSolver.tolerance + 0.005f, "an ideal Curve arrives");
        state = StateLockedReady;
        Assert.IsTrue(CrossedCurveReleaseAt(hand, TwistHand(IdealTwist(PitchType.Curve) + 90f), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, solverSpeedRatio));
        Assert.AreEqual(0f, debugQuality);
        Assert.IsTrue(Vector3.Distance(releaseVelocity.normalized, _lockFailureDirection) < 0.00001f, "the failure aim");
        Vector3 failed = FlyToPlate(releasePosition, releaseVelocity, releaseSpin, releaseWakeMode, releaseWakeCoefficient, releaseWakeDirection);
        Assert.IsTrue(Vector3.Distance(failed, target) <= aimSolver.tolerance + 0.005f, "a hung Curve still reaches the course: " + Vector3.Distance(failed, target));
        Assert.IsTrue(ReleaseDetailText().Contains("aimed for quality 0% (between the ideal and the failure solve)"), ReleaseDetailText());
        // In between the aim is turned between the two (only approximately right).
        state = StateLockedReady;
        Assert.IsTrue(CrossedCurveReleaseAt(hand, TwistHand(IdealTwist(PitchType.Curve) + 40f), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, solverSpeedRatio));
        Assert.IsTrue(debugQuality > 0.1f && debugQuality < 0.9f, "in between " + debugQuality);
        Assert.IsTrue(Vector3.Distance(releaseVelocity.normalized,
            ManualPitchQuality.SlerpUnit(_lockFailureDirection, _lockDirection, debugQuality)) < 0.00001f, "turned by the quality");
        diagnosticIdealSpin = true;
        state = StateLockedReady;
        Assert.IsTrue(CrossedCurveReleaseAt(hand, TwistHand(IdealTwist(PitchType.Curve) + 90f), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, solverSpeedRatio));
        Assert.AreEqual(1f, debugQuality, "the diagnostic keeps the ideal flight");
        Assert.IsTrue(Vector3.Distance(FlyReleaseToPlate(), ideal) < 0.001f);
    }
}
