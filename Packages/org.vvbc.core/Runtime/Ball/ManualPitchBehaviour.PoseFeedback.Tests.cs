using Tsukimi;
using UnityEngine;

// The pose feedback of a release: arrows from where the pose was judged, the rings at the
// good and bad angles, the quality colour.
public partial class ManualPitchBehaviour
{
    [TsukimiTest]
    public void TheArrowsShowTheJudgedIdealAndMeasuredPalm()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        SelectPitchTypeAt(PitchType.Slider, TestGravity, 0.02f);
        LockForTest(new Vector3(0f, 0.75f, 0f));
        // Released 0.3 m past the plate (after the crossing): the arrows still start at the crossing.
        Assert.IsTrue(CurveReleaseForTest(new Vector3(0f, 0f, 0.3f), TwistHand(IdealTwist(PitchType.Slider) + 40f), TowardPlate(), 0.97f));
        Assert.IsTrue(PoseFeedbackShown());
        Assert.AreEqual(_crossPoint, PoseOrigin(), "from the crossing, where the pose was judged");
        Assert.IsTrue(Vector3.Distance(PoseOrigin(), _resultHand) > 0.25f, "not the hand at the release");
        Vector3 ideal = IdealPalmTip() - PoseOrigin();
        Vector3 actual = ActualPalmTip() - PoseOrigin();
        Assert.IsTrue(Near(ideal.magnitude, poseArrowLength, 0.0001f) && Near(actual.magnitude, poseArrowLength, 0.0001f), "arrow length");
        Quaternion frame = TestFrame(TestChest);
        Vector3 expected = ManualPitchQuality.PalmAtTwist(Vector3.up, Vector3.forward, IdealTwist(PitchType.Slider), false);
        Assert.IsTrue(Vector3.Angle(ideal, ManualPitchMath.Rotate(frame, expected)) < 0.05f, "the ideal twist about these fingers");
        Assert.IsTrue(PoseLabelText(_resultQuality).Contains("Ideal Sup -45°") && PoseLabelText(_resultQuality).Contains("Actual Sup -5°") &&
            PoseLabelText(_resultQuality).Contains("Error 40°"), PoseLabelText(_resultQuality));
        Assert.IsTrue(FormText().Contains("twist ideal 回外 -45°   actual 回外 -5°"), FormText());
        Assert.IsTrue(Near(Vector3.Angle(ideal, actual), 40f, 0.1f), "the measured palm, 40 deg off: " + Vector3.Angle(ideal, actual));
        Assert.IsTrue(Near(debugPoseAngle, Vector3.Angle(ideal, actual), 0.1f), "the angle judged");
        // A throw shows none; showPoseFeedback or showReleaseFeedback off shows none.
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsFalse(ReleaseForTest(TestChest, Vector3.zero, IdealHand(TestChest), Vector3.back * 0.5f));
        Assert.IsFalse(PoseFeedbackShown(), "not a pitch");
        Assert.IsTrue(TypeReleaseForTest(PitchType.Slider, TwistHand(IdealTwist(PitchType.Slider))));
        showPoseFeedback = false;
        Assert.IsFalse(PoseFeedbackShown());
        showPoseFeedback = true;
        showReleaseFeedback = false;
        Assert.IsFalse(PoseFeedbackShown());
    }

    // An early release judges the pose at the release: the arrows start at the hand there.
    [TsukimiTest]
    public void AnEarlyReleasesArrowsStartAtTheHand()
    {
        Mimic.Join();
        Assert.IsTrue(PlateReadyForTest());
        SwingForTest(Vector2.zero, 10f, 0.48f, 3, 71);
        Vector3 hand = PlatePointForTest(Vector2.zero, -0.15f);
        Assert.IsTrue(TrackedReleaseForTest(3, hand, 71, InputTrigger), debugReason);
        Assert.AreEqual(ReleasePlateMath.CrossPredicted, _resultCross);
        Assert.AreEqual(hand, PoseOrigin());
        Assert.IsTrue(PoseFeedbackShown(), "the 4-Seam is drawn too (grey: not evaluated)");
    }

    [TsukimiTest]
    // The arcs lie about the finger axis, at the twists they stand for, both hands.
    public void TheArcsLieAboutTheFingersAtTheirTwists()
    {
        Vector3 origin = new Vector3(1f, 2f, 3f);
        Quaternion frame = TestFrame(TestChest);
        int[] hands = { GloveBehaviour.HandRight, GloveBehaviour.HandLeft };
        Vector3[] fingers = { Vector3.up, new Vector3(0.7f, 0.6f, -0.2f).normalized };
        float[] twists = { -60f, -45f, 0f, 30f, 120f };
        for (int h = 0; h < hands.Length; h++)
            for (int f = 0; f < fingers.Length; f++)
            {
                _activeHand = hands[h];
                PitchQuality q = new PitchQuality();
                q.fingerAxis = fingers[f];
                q.referencePalm = ManualPitchQuality.ReferencePalm(fingers[f]);
                for (int t = 0; t < twists.Length; t++)
                {
                    Vector3 local = ManualPitchMath.Rotate(ManualPitchMath.Inverse(frame), ArcPoint(origin, frame, q, twists[t], 0.8f) - origin);
                    Assert.IsTrue(Near(local.magnitude, 0.8f * poseArrowLength, 0.0001f), "at 0.8 of the radius");
                    Assert.IsTrue(Mathf.Abs(Vector3.Dot(local, fingers[f])) < 0.0001f, "perpendicular to the fingers");
                    float twist = ManualPitchQuality.Twist(fingers[f], q.referencePalm, local, hands[h] == GloveBehaviour.HandLeft);
                    Assert.IsTrue(Near(twist, twists[t], 0.05f), "hand " + h + " fingers " + f + " twist " + twists[t] + ": " + twist);
                }
            }
    }

    [TsukimiTest]
    public void TheMeasuredPalmTurnsFromGoodThroughWarningToBad()
    {
        Assert.AreEqual(goodPalmColor, QualityColor(1f));
        Assert.AreEqual(warnPalmColor, QualityColor(0.5f));
        Assert.AreEqual(badPalmColor, QualityColor(0f));
        Assert.AreEqual(badPalmColor, QualityColor(float.NaN));
        Color quarter = QualityColor(0.25f);
        Assert.IsTrue(Near(quarter.g, 0.5f * (badPalmColor.g + warnPalmColor.g), 0.0001f), "between bad and warning");
    }
}
