using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// Geometry used throughout: the plate's front-edge plane passes through the origin with its
// normal +Z toward the pitcher; the pitcher's chest is 17.6 m away, so the pitching frame's
// forward is -Z and its right (the right-hander's arm side) is world -X.
public partial class ManualPitchBehaviour
{
    private static readonly Vector3 TestPlate = Vector3.zero;
    private static readonly Vector3 TestNormal = Vector3.forward;
    private static readonly Vector3 TestGravity = new Vector3(0f, -9.81f, 0f);
    private static readonly Vector3 TestChest = new Vector3(0f, 1.40f, 17.6f);
    // Overhand right-hander: 0.25 m arm side, 0.45 m up, 0.30 m forward of the chest.
    private static readonly Vector3 Overhand = new Vector3(0.25f, 0.45f, 0.30f);
    // Calibrated controller rotation relative to the pitching frame (a mixed 3D rotation).
    private static readonly Quaternion TestPose = new Quaternion(-0.2574f, 0.0956f, 0.0185f, 0.9614f);
    // The test mound: its centre 18.44 m from the plate, the release anchor 0.6 m in front of it
    // at 1.8 m (its own lateral offset is ignored). A right-hander's fixed release point is 0.7 m
    // to the pitcher's right, world -X here; a left-hander's 0.7 m to world +X.
    private static readonly Vector3 TestCentre = new Vector3(0f, 0.25f, 18.44f);
    private static readonly Vector3 TestAnchor = new Vector3(0.3f, 1.80f, 17.84f);

    private Vector3 TestP0()
    {
        return FixedReleaseAt(TestCentre, TestAnchor, TestPlate);
    }

    private Quaternion TestFrame(Vector3 chest)
    {
        return ManualPitchMath.Frame(ManualPitchMath.HorizontalDirection(chest, TestPlate));
    }

    private Quaternion IdealHand(Vector3 chest)
    {
        return ManualPitchMath.Multiply(TestFrame(chest), TestPose);
    }

    private Vector3 HandAt(Vector3 chest, Vector3 relative)
    {
        return ManualPitchMath.FromFrame(TestFrame(chest), chest, relative);
    }

    private void ActiveForTest(int hand)
    {
        state = StateTargeting;
        _activeHand = hand;
        _restored = true;
        _hasCursor = false;
    }

    private bool CalibrateForTest(Vector3 relative)
    {
        BeginCalibration();
        if (!RecordCalibrationAt(TestChest, HandAt(TestChest, relative), TestPlate)) return false;
        return CommitCalibration();
    }

    // A lock without the solver: aims straight at the target from the ideal release.
    private void LockForTest(Vector3 target)
    {
        Quaternion frame = TestFrame(TestChest);
        _lockTarget = target;
        _lockRelease = ManualPitchMath.FromFrame(frame, TestChest, _calPosition);
        _lockDirection = (target - _lockRelease).normalized;
        _lockType = _selectedType;
        _lockBuild = BuildFor(_lockType);
        _lockSpeedMultiplier = PitchPropertiesCalculator.SpeedMultiplier(_lockBuild);
        _lockSolverSpeedKmh = ManualPitchMath.ScaledSpeedKmh(maxPitchSpeedKmh, solverSpeedRatio) * _lockSpeedMultiplier;
        _lockConverged = true;
        _lockIdeal = IdealFor(_lockType);
        state = StateLockedReady;
    }

    // A default course without the solver: aims straight at the centre from the ideal release.
    private void DefaultCourseForTest(Vector3 target)
    {
        Quaternion frame = TestFrame(TestChest);
        _defaultTarget = target;
        _defaultRelease = ManualPitchMath.FromFrame(frame, TestChest, _calPosition);
        _defaultDirection = (target - _defaultRelease).normalized;
        _defaultType = _selectedType;
        _defaultBuild = BuildFor(_defaultType);
        _defaultSpeedMultiplier = PitchPropertiesCalculator.SpeedMultiplier(_defaultBuild);
        _defaultSolverSpeedKmh = ManualPitchMath.ScaledSpeedKmh(maxPitchSpeedKmh, solverSpeedRatio) * _defaultSpeedMultiplier;
        _defaultConverged = true;
        _defaultSolverError = 0f;
        _defaultPredicted = target;
        _defaultIdeal = IdealFor(_defaultType);
        _defaultReady = true;
        _defaultSolving = false;
        _defaultDirty = false;
    }

    private bool ReleaseForTest(Vector3 chest, Vector3 handOffset, Quaternion rotation, Vector3 armVelocity)
    {
        Vector3 hand = HandAt(chest, _calPosition) + ManualPitchMath.Rotate(TestFrame(chest), handOffset);
        return CrossedReleaseAt(hand, rotation, hand, armVelocity, TestPlate, TestNormal, InputTrigger);
    }

    private Vector3 TowardPlate() { return Vector3.back * 20f; }

    // The test release plate: centred on the right- or left-hander's P0 of the test mound.
    private void PlateForTest()
    {
        SetReleasePlate(ReleasePlateMath.Build(TestP0(), TestCentre, TestPlate, releasePlateWidth, releasePlateHeight));
    }

    // An actual crossing of the current hold at `offset` on the plate (right, up) with this arm
    // velocity and controller rotation, as the per-frame plate check records it.
    private void CrossForTest(Vector2 offset, Vector3 armVelocity, Quaternion rotation)
    {
        PlateForTest();
        _crossed = true;
        _crossSession = _releaseHandSession;
        _crossFrame = _releaseFrame;
        _crossOffset = offset;
        _crossPoint = _plate.centre + _plate.right * offset.x + Vector3.up * offset.y;
        _crossArm = FieldingMath.GivenArmMotion(armVelocity);
        _crossRotation = rotation;
        _crossType = _selectedType;
        _crossCourse = CourseFor(_crossType);
        _autoPending = true;
    }

    // A release after a centre crossing made with the same arm velocity and rotation.
    private bool CrossedReleaseAt(Vector3 hand, Quaternion rotation, Vector3 ball, Vector3 armVelocity,
        Vector3 planePoint, Vector3 planeNormal, int input)
    {
        CrossForTest(Vector2.zero, armVelocity, rotation);
        return ResolveReleaseAt(hand, rotation, ball, armVelocity, planePoint, planeNormal, input);
    }

    private bool CrossedCurveReleaseAt(Vector3 hand, Quaternion rotation, Vector3 ball, Vector3 armVelocity,
        Vector3 planePoint, Vector3 planeNormal, int input, float curveOutput)
    {
        CrossForTest(Vector2.zero, armVelocity, rotation);
        return ResolveReleaseWithCurveOutputAt(hand, rotation, ball, armVelocity, planePoint, planeNormal, input, curveOutput);
    }


    // ---- Calibration ----

    [TsukimiTest]
    public void ARightHandedCalibrationIsSavedAndRestored()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Assert.IsTrue(CalibrationUsable());
        Assert.AreEqual(GloveBehaviour.HandRight, CalibratedHand());
        Assert.IsTrue(Vector3.Distance(CalibratedPosition(), Overhand) < 0.0001f);
        _hasCalibration = false;
        Assert.IsTrue(RestoreCalibration(CalibrationVersion, GloveBehaviour.HandRight, Overhand), "positions only: no rotation is stored");
        Assert.IsTrue(Vector3.Distance(CalibratedPosition(), Overhand) < 0.0001f);
        Assert.IsTrue(CalibrationUsable());
    }

    [TsukimiTest]
    public void ALeftHandedCalibrationIsKeptForTheLeftHandOnly()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandLeft);
        Vector3 left = new Vector3(-0.25f, 0.45f, 0.30f);
        Assert.IsTrue(CalibrateForTest(left));
        Assert.AreEqual(GloveBehaviour.HandLeft, CalibratedHand());
        Assert.IsTrue(Vector3.Distance(CalibratedPosition(), left) < 0.0001f);
        _activeHand = GloveBehaviour.HandRight;
        Assert.IsFalse(CalibrationUsable(), "the other hand never reuses it");
        state = StateLockedReady;
        Assert.IsFalse(ReleaseForTest(TestChest, Vector3.zero, IdealHand(TestChest), TowardPlate()));
        Assert.AreEqual("no calibration", debugReason);
    }

    [TsukimiTest]
    public void SaveIsRefusedBeforeThePlayerDataIsRestored()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        _restored = false;
        Assert.IsFalse(CalibrateForTest(Overhand));
        Assert.IsFalse(HasCalibration());
    }

    // The frame is built from the chest position and the plate only: there is no chest or head
    // rotation input, and a chest higher or lower does not tilt the frame.
    [TsukimiTest]
    public void TheFrameUsesOnlyTheChestPositionAndThePlate()
    {
        Quaternion level = TestFrame(TestChest);
        Quaternion higher = TestFrame(TestChest + Vector3.up * 0.4f);
        Assert.IsTrue(ManualPitchMath.AngleBetween(level, higher) < 0.001f);
        Assert.IsTrue(Vector3.Distance(ManualPitchMath.Rotate(level, Vector3.forward), Vector3.back) < 0.0001f);
        Assert.IsTrue(Vector3.Distance(ManualPitchMath.Rotate(level, Vector3.right), Vector3.left) < 0.0001f, "right-hander's arm side is world -X here");
    }

    // Avatars place chest and hand differently; the recorded offset is used as it is.
    [TsukimiTest]
    public void AvatarDifferencesAreStoredWithoutCorrection()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Vector3 tall = new Vector3(0.30f, 0.62f, 0.35f);
        Assert.IsTrue(CalibrateForTest(tall));
        Assert.IsTrue(Vector3.Distance(CalibratedPosition(), tall) < 0.0001f);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Assert.IsTrue(Vector3.Distance(CalibratedPosition(), Overhand) < 0.0001f);
    }

    [TsukimiTest]
    public void AnInvalidChestRecordsNothingAndInvalidTrackingPitchesNothing()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        BeginCalibration();
        Assert.IsFalse(RecordCalibrationAt(Vector3.zero, HandAt(TestChest, Overhand), TestPlate));
        Assert.IsFalse(RecordCalibrationAt(new Vector3(float.NaN, 1f, 1f), HandAt(TestChest, Overhand), TestPlate));
        CancelCalibration();
        Assert.IsTrue(CalibrateForTest(Overhand));
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Vector3 hand = new Vector3(float.NaN, 1f, 17f);
        Assert.IsFalse(CrossedReleaseAt(hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputGrip));
        Assert.AreEqual("invalid tracking", debugReason);
        Assert.IsFalse(CrossedReleaseAt(HandAt(TestChest, Overhand), new Quaternion(0f, 0f, 0f, 0f), hand, TowardPlate(), TestPlate,
            TestNormal, InputGrip));
        Assert.AreEqual("invalid tracking", debugReason);
        state = StateTargeting;
        _rayValid = true;
        _hasCursor = true;
        Assert.IsFalse(BeginLockAt(Vector3.zero, TestPlate, TestNormal, TestGravity, 0.02f), "no lock without a release point");
    }

    [TsukimiTest]
    public void InvalidStoredCalibrationsAreNotRestored()
    {
        Assert.IsFalse(RestoreCalibration(2, GloveBehaviour.HandRight, Overhand), "unknown version");
        Assert.IsFalse(RestoreCalibration(CalibrationVersion, 3, Overhand), "unknown hand");
        Assert.IsFalse(RestoreCalibration(CalibrationVersion, GloveBehaviour.HandRight, new Vector3(0f, float.NaN, 0f)), "not finite");
        Assert.IsFalse(HasCalibration());
        Assert.IsTrue(RestoreCalibration(CalibrationVersion, GloveBehaviour.HandLeft, Overhand));
    }

    // ---- 4-Seam axis (fixed per hand) ----

    // World Magnus force of the 4-Seam of a hand thrown at the plate (-Z).
    private static Vector3 MagnusOfHand(bool leftHanded)
    {
        Vector3 direction = Vector3.back;
        Vector3 spin = PitchSpin.ToWorld(ManualPitchMath.FixedFourSeamAxis(leftHanded) * (2500f * 2f * Mathf.PI / 60f), direction);
        return BallAerodynamics.Acceleration(direction * 38f, spin, 0f, 0.00063f);
    }

    [TsukimiTest]
    public void TheFourSeamAxisIsFixedPerHand()
    {
        Vector3 right = ManualPitchMath.FixedFourSeamAxis(false);
        Vector3 left = ManualPitchMath.FixedFourSeamAxis(true);
        Assert.IsTrue(Vector3.Distance(right, new Vector3(-0.8660254f, 0.5f, 0f)) < 0.000001f, "right-hander " + right);
        Assert.IsTrue(Vector3.Distance(left, new Vector3(-0.8660254f, -0.5f, 0f)) < 0.000001f, "left-hander " + left);
        Assert.IsTrue(Mathf.Abs(right.magnitude - 1f) < 0.00001f);
        // Lift with arm-side run: a right-hander's arm side is the pitcher's right (world -X
        // here), a left-hander's the pitcher's left (+X); both rise the same.
        Vector3 r = MagnusOfHand(false);
        Vector3 l = MagnusOfHand(true);
        Assert.IsTrue(r.y > 3f && r.x < -1f, "right-hander: rise and run to the arm side " + r);
        Assert.IsTrue(l.y > 3f && l.x > 1f, "left-hander: rise and run to the arm side " + l);
        Assert.IsTrue(Mathf.Abs(r.x + l.x) < 0.0001f && Mathf.Abs(r.y - l.y) < 0.0001f, "mirror images");
        Assert.IsTrue(Vector3.Distance(ManualPitchMath.WakeDirection(90f, 1f), -ManualPitchMath.WakeDirection(90f, -1f)) < 0.0001f);
    }

    // Overhand, sidearm and underhand calibrations and releases, any controller rotation: the
    // released spin is the fixed axis of the hand, the solver's spin too.
    [TsukimiTest]
    public void NoFormOrPoseChangesTheAxis()
    {
        Mimic.Join();
        Vector3[] forms = { Overhand, new Vector3(0.6f, 0f, 0.2f), new Vector3(0.3f, -0.5f, 0.3f) };
        int[] hands = { GloveBehaviour.HandRight, GloveBehaviour.HandLeft };
        for (int h = 0; h < hands.Length; h++)
        {
            for (int f = 0; f < forms.Length; f++)
            {
                ActiveForTest(hands[h]);
                Vector3 form = hands[h] == GloveBehaviour.HandLeft ? new Vector3(-forms[f].x, forms[f].y, forms[f].z) : forms[f];
                Assert.IsTrue(CalibrateForTest(form), "form " + f);
                Vector3 expected = ManualPitchMath.FixedFourSeamAxis(hands[h] == GloveBehaviour.HandLeft);
                Assert.IsTrue(Vector3.Distance(IdealFor(PitchType.FourSeam).spinAxisLocal, expected) < 0.00001f, "the solver's axis " + h + "/" + f);
                Quaternion[] poses = { IdealHand(TestChest), ManualPitchMath.Multiply(IdealHand(TestChest), ManualPitchMath.AxisAngle(new Vector3(0.6f, 0.5f, 0.62f), 70f)) };
                for (int p = 0; p < poses.Length; p++)
                {
                    LockForTest(new Vector3(0.2f, 0.75f, 0f));
                    Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, poses[p], TowardPlate()), debugReason);
                    Assert.IsTrue(Vector3.Distance(debugSpinAxis, expected) < 0.00001f, "released axis " + h + "/" + f + "/" + p + ": " + debugSpinAxis);
                    Assert.IsTrue(Vector3.Distance(releaseSpin.normalized, PitchSpin.ToWorld(expected, releaseVelocity).normalized) < 0.00001f,
                        "world spin along the launch direction's frame");
                }
            }
        }
    }


    // ---- Pose quality ----


    // ---- Course ----

    [TsukimiTest]
    public void TheHeadRayMeetsTheFrontEdgePlaneAndIsClamped()
    {
        Vector3 eye = new Vector3(0f, 1.6f, 18f);
        Assert.IsTrue(UpdateCursor(eye, (new Vector3(0.2f, 0.8f, 0f) - eye).normalized, TestPlate, TestNormal, 0.016f));
        Vector2 c = CursorCoordinates();
        Assert.IsTrue(Mathf.Abs(c.x - 0.2f) < 0.0001f && Mathf.Abs(c.y - 0.8f) < 0.0001f);
        _hasCursor = false;
        UpdateCursor(eye, (new Vector3(-0.9f, 0.3f, 0f) - eye).normalized, TestPlate, TestNormal, 0.016f);
        Assert.IsTrue(Mathf.Abs(CursorCoordinates().x + 0.9f) < 0.0001f, "outside the zone is allowed");
        _hasCursor = false;
        UpdateCursor(eye, (new Vector3(5f, 4f, 0f) - eye).normalized, TestPlate, TestNormal, 0.016f);
        Assert.AreEqual(new Vector2(1.70f, 2.50f), CursorCoordinates());
        _hasCursor = false;
        UpdateCursor(eye, (new Vector3(-5f, -2f, 0f) - eye).normalized, TestPlate, TestNormal, 0.016f);
        Assert.AreEqual(new Vector2(-1.70f, 0f), CursorCoordinates());
    }

    [TsukimiTest]
    public void TheLockUsesTheSmoothedCursorAndNeedsAValidRay()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Vector3 eye = new Vector3(0f, 1.6f, 18f);
        UpdateCursor(eye, (new Vector3(0f, 0.5f, 0f) - eye).normalized, TestPlate, TestNormal, 0.016f);
        UpdateCursor(eye, (new Vector3(1f, 1.5f, 0f) - eye).normalized, TestPlate, TestNormal, 0.016f);
        Vector2 smoothed = CursorCoordinates();
        Assert.IsTrue(smoothed.x > 0.05f && smoothed.x < 0.5f, "between the old and the new reading");
        Assert.IsFalse(UpdateCursor(eye, Vector3.forward, TestPlate, TestNormal, 0.016f), "looking away");
        Assert.AreEqual(smoothed, CursorCoordinates(), "the last cursor is kept");
        Assert.IsFalse(BeginLockAt(TestP0(), TestPlate, TestNormal, TestGravity, 0.02f), "no new lock without a ray");
        UpdateCursor(eye, (new Vector3(1f, 1.5f, 0f) - eye).normalized, TestPlate, TestNormal, 0f);
        Assert.AreEqual(smoothed, CursorCoordinates(), "dt 0 does not move it");
        Assert.IsTrue(BeginLockAt(TestP0(), TestPlate, TestNormal, TestGravity, 0.02f));
        Vector3 expected = TestPlate + Vector3.right * smoothed.x + Vector3.up * smoothed.y;
        Assert.IsTrue(Vector3.Distance(LockedTarget(), expected) < 0.0001f);
        UpdateCursor(eye, (new Vector3(-1f, 2f, 0f) - eye).normalized, TestPlate, TestNormal, 1f);
        Assert.IsTrue(Vector3.Distance(LockedTarget(), expected) < 0.0001f, "looking elsewhere does not move a lock");
    }

    [TsukimiTest]
    public void CalibratingDisablesTheLock()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        _rayValid = true;
        _hasCursor = true;
        BeginCalibration();
        Assert.IsFalse(BeginLockAt(TestP0(), TestPlate, TestNormal, TestGravity, 0.02f));
        Assert.AreEqual(StateTargeting, state);
    }

    [TsukimiTest]
    public void AnUnconvergedSolveIsAcceptedOnlyWithinTheTolerance()
    {
        state = StateSolving;
        CompleteSolve(false, 0.05f, Vector3.back, Vector3.zero);
        Assert.AreEqual(StateLockedReady, state);
        state = StateSolving;
        CompleteSolve(false, 0.30f, Vector3.back, Vector3.zero);
        Assert.AreEqual(StateTargeting, state, "lock failed");
        state = StateSolving;
        CompleteSolve(false, float.NaN, Vector3.back, Vector3.zero);
        Assert.AreEqual(StateTargeting, state);
        state = StateSolving;
        CompleteSolve(true, 0.01f, Vector3.back, Vector3.zero);
        Assert.AreEqual(StateLockedReady, state);
    }

    [TsukimiTest]
    public void AReleaseBeforeTheSolveEndsIsNotAPitchAndEndsTheLock()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        state = StateSolving;
        Assert.IsFalse(ReleaseForTest(TestChest, Vector3.zero, IdealHand(TestChest), TowardPlate()));
        Assert.AreEqual("not locked", debugReason);
        Vector3 hand = HandAt(TestChest, Overhand);
        Assert.IsFalse(ResolveTrackedReleaseAt(1f, hand, IdealHand(TestChest), hand, TestPlate, TestNormal, InputTrigger));
        Assert.AreEqual(StateTargeting, state, "the release ends the lock");
        LockForTest(new Vector3(0f, 0.75f, 0f));
        ResetArmSamples();
        Assert.IsFalse(ResolveTrackedReleaseAt(2f, hand, IdealHand(TestChest), hand, TestPlate, TestNormal, InputGrip),
            "one sample (the release): the arm is not measured, a throw");
        Assert.AreEqual("arm not measured", debugReason);
        Assert.AreEqual(StateTargeting, state, "a pitch or a throw returns to targeting");
    }

    // ---- Default course (strike-zone centre) ----

    [TsukimiTest]
    public void AReleaseWithNoCourseLockedPitchesToTheCentre()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Vector3 centre = new Vector3(0f, 0.75f, 0f);
        DefaultCourseForTest(centre);
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, IdealHand(TestChest), TowardPlate()), "targeting");
        Assert.IsTrue(_resultDefaultCourse);
        Assert.IsTrue(_resultHasTarget);
        Assert.IsTrue(Vector3.Distance(_resultTarget, centre) < 0.0001f);
        Assert.IsTrue(Vector3.Angle(releaseVelocity, _defaultDirection) < 0.01f, "launched along the default course");
        Assert.AreEqual(StateTargeting, state);
        Assert.IsTrue(ReleaseDetailText().Contains("centre: no course locked"), ReleaseDetailText());
        state = StateSolving;
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, IdealHand(TestChest), TowardPlate()), "a lock still solving");
        Assert.IsTrue(_resultDefaultCourse);
        Assert.IsTrue(Vector3.Distance(_resultTarget, centre) < 0.0001f, "not the target still being solved");
        Assert.IsTrue(_defaultReady, "the default course stays for the next release");
    }

    [TsukimiTest]
    public void ALockedCourseWinsOverTheDefaultCourse()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        DefaultCourseForTest(new Vector3(0f, 0.75f, 0f));
        Vector3 locked = new Vector3(0.4f, 0.5f, 0f);
        LockForTest(locked);
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, IdealHand(TestChest), TowardPlate()));
        Assert.IsFalse(_resultDefaultCourse);
        Assert.IsTrue(Vector3.Distance(_resultTarget, locked) < 0.0001f);
        Assert.IsTrue(Vector3.Angle(releaseVelocity, _lockDirection) < 0.01f);
    }

    [TsukimiTest]
    public void WithoutASolvedDefaultCourseAnUnlockedReleaseIsAThrow()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Assert.IsFalse(_defaultReady, "a new calibration leaves no default course");
        Assert.IsFalse(ReleaseForTest(TestChest, Vector3.zero, IdealHand(TestChest), TowardPlate()));
        Assert.AreEqual("not locked", debugReason);
        Assert.IsFalse(_resultDefaultCourse);
        DefaultCourseForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(CalibrateForTest(Overhand));
        Assert.IsFalse(_defaultReady, "a changed calibration solves the default course again");
        Assert.IsTrue(_defaultDirty);
    }

    [TsukimiTest(maxSteps: 60000000)]
    public void TheDefaultCourseIsSolvedTowardTheCentreAndALockInterruptsIt()
    {
        Mimic.Join();
        _ball = strikeZone != null ? strikeZone.ball : null;
        Assert.IsNotNull(aimSolver, "the runner wires the solver of this folder");
        Assert.IsNotNull(_ball, "the runner wires the ball of this folder");
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Vector3 centre = new Vector3(0f, 0.75f, 0f);
        Assert.IsTrue(BeginDefaultCourseAt(TestP0(), TestPlate, TestNormal, centre, TestGravity, 0.02f));
        Assert.AreEqual(StateTargeting, state, "the background solve does not lock");
        for (int i = 0; i < 200 && _defaultSolving; i++)
        {
            aimSolver.Advance();
            if (!aimSolver.solving) CompleteDefaultCourse(aimSolver.converged, aimSolver.errorMeters, aimSolver.launchDirection, aimSolver.predictedCrossing);
        }
        Assert.IsTrue(_defaultReady, "the centre is solved");
        Assert.IsTrue(Vector3.Distance(_defaultPredicted, centre) <= solverAcceptMeters, "the solver reaches the centre");
        // A lock started during a default solve takes the solver and leaves the default to redo.
        Assert.IsTrue(BeginDefaultCourseAt(TestP0(), TestPlate, TestNormal, centre, TestGravity, 0.02f));
        Assert.IsTrue(_defaultSolving);
        Vector3 eye = new Vector3(0f, 1.6f, 18f);
        Assert.IsTrue(UpdateCursor(eye, (new Vector3(0.3f, 0.6f, 0f) - eye).normalized, TestPlate, TestNormal, 0.016f));
        Assert.IsTrue(BeginLockAt(TestP0(), TestPlate, TestNormal, TestGravity, 0.02f));
        Assert.IsFalse(_defaultSolving);
        Assert.IsTrue(_defaultDirty, "solved again once the solver is free");
        Assert.IsFalse(BeginDefaultCourseAt(TestP0(), TestPlate, TestNormal, centre, TestGravity, 0.02f),
            "not while a lock is solved");
    }

    // ---- Recognition (throwing controller) ----

    // Recognition reads the release plate and the throwing controller's speed (with the
    // calibration, the course and valid tracking). Neither the release position, the arm's
    // direction (sidearm and underhand swings) nor the pose refuses a crossed pitch.
    [TsukimiTest]
    public void RecognitionNeedsTheCrossingAndArmSpeedButNotDirectionPositionOrPose()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Quaternion ideal = IdealHand(TestChest);
        // Far outside the former recognition ellipsoid (0.60, 0.55, 0.80) on every axis.
        Vector3[] far = { new Vector3(0.7f, 0f, 0f), new Vector3(-1.5f, 0f, 0f), new Vector3(0f, 1.2f, 0f),
            new Vector3(0f, -1.4f, 0f), new Vector3(0f, 0f, 1.5f), new Vector3(0f, 0f, -1.5f), new Vector3(2f, -2f, 2f) };
        for (int i = 0; i < far.Length; i++)
        {
            LockForTest(new Vector3(0f, 0.75f, 0f));
            Assert.IsTrue(ReleaseForTest(TestChest, far[i], ideal, TowardPlate()), "offset " + i + ": " + debugReason);
        }
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsFalse(ReleaseForTest(TestChest, Vector3.zero, ideal, Vector3.back * 0.5f));
        Assert.AreEqual("arm speed", debugReason);
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, ideal, Vector3.left * 20f), "a sideways arm: " + debugReason);
        Assert.IsTrue(debugDirectionError > 80f, "measured and shown only: " + debugDirectionError);
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, new Vector3(1.5f, 0f, 0f), ideal, Vector3.up * 20f), "far and upward (underhand)");
        LockForTest(new Vector3(0f, 0.75f, 0f));
        // Without a crossing (and no plate ahead) the same release is a throw.
        PlateForTest();
        ClearCrossing();
        Assert.IsFalse(ResolveReleaseAt(HandAt(TestChest, Overhand), ideal, HandAt(TestChest, Overhand), TowardPlate(), TestPlate, TestNormal, InputTrigger));
        Assert.IsTrue(debugReason.StartsWith("plate: "), debugReason);
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, ManualPitchMath.Multiply(ideal, ManualPitchMath.AxisAngle(Vector3.right, 80f)), TowardPlate()), "a bad pose is still a pitch");
        Assert.AreEqual(1f, debugPoseQuality, "the 4-Seam's pose is not evaluated");
        SelectPitchTypeAt(PitchType.Slider, TestGravity, 0.02f);
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, TwistHand(IdealTwist(PitchType.Slider) + 120f), TowardPlate()), "a failed Slider is still a pitch");
        Assert.AreEqual(0f, debugPoseQuality);
    }

    // Feeds the controller samples of a straight motion of `speed` m/s along `direction`
    // ending at `end` at endTime, 0.02 s apart (the release samples `end` itself).
    // The release plate is placed halfway along the last segment (before `end`), so a motion
    // toward the batter crosses it at the release and one along the plate does not.
    private void ControllerMotionForTest(Vector3 end, Vector3 direction, float speed, float endTime)
    {
        ResetArmSamples();
        Vector3 d = direction.normalized;
        SetReleasePlate(ReleasePlateMath.Build(end - d * (speed * 0.01f), TestCentre, TestPlate, releasePlateWidth, releasePlateHeight));
        for (int i = 4; i >= 1; i--)
        {
            Vector3 p = end - d * (speed * i * 0.02f);
            SampleArmAt(endTime - i * 0.02f, p);
            TrackPlateAt(endTime - i * 0.02f, FrameAt(endTime - i * 0.02f), p, Quaternion.identity, _releaseHandSession);
        }
        _releaseFrame = FrameAt(endTime);
    }

    // The arm velocity is the throwing controller's own, from its samples: a ball centre that
    // swings with the wrist while the controller stays put is not an arm motion, and the
    // controller's speed is the one handed to the speed curve.
    [TsukimiTest]
    public void TheArmVelocityIsTheThrowingControllersOwn()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Vector3 target = new Vector3(0f, 0.75f, 0f);
        Quaternion ideal = IdealHand(TestChest);
        Vector3 hand = HandAt(TestChest, Overhand);
        Vector3 toward = (target - hand).normalized;
        // Wrist flick: the controller creeps (5 cm/s) while the ball centre swings 30 cm.
        LockForTest(target);
        ControllerMotionForTest(hand, toward, 0.05f, 1f);
        Vector3 swungBall = hand + toward * 0.3f + Vector3.up * 0.1f;
        Assert.IsFalse(ResolveTrackedReleaseAt(1f, hand, ManualPitchMath.Multiply(ideal, ManualPitchMath.AxisAngle(Vector3.right, 60f)),
            swungBall, TestPlate, TestNormal, InputTrigger));
        Assert.AreEqual("arm speed", debugReason);
        Assert.IsTrue(debugArmSpeed < 0.1f, "the controller's speed: " + debugArmSpeed);
        // The controller moving 6 m/s toward the target: a pitch, with that speed.
        LockForTest(target);
        ControllerMotionForTest(hand, toward, 6f, 2f);
        Assert.IsTrue(ResolveTrackedReleaseAt(2f, hand, ideal, hand + Vector3.down * 0.5f, TestPlate, TestNormal, InputTrigger), debugReason);
        Assert.IsTrue(Near(debugArmSpeed, 6f, 0.01f), "arm " + debugArmSpeed);
        Assert.IsTrue(debugDirectionError < 1f, "direction error " + debugDirectionError);
        Assert.IsTrue(SpeedText().Contains("arm 6.0 m/s"), SpeedText());
        // The same motion twice as fast hands the curve twice the speed.
        LockForTest(target);
        ControllerMotionForTest(hand, toward, 12f, 3f);
        Assert.IsTrue(ResolveTrackedReleaseAt(3f, hand, ideal, hand, TestPlate, TestNormal, InputTrigger));
        Assert.IsTrue(Near(debugArmSpeed, 12f, 0.01f), "arm " + debugArmSpeed);
        // A very fast arm (60 m/s, beyond the former 40 m/s "jump" limit) is never discarded.
        LockForTest(target);
        ControllerMotionForTest(hand, toward, 60f, 4f);
        Assert.IsTrue(ResolveTrackedReleaseAt(4f, hand, ideal, hand, TestPlate, TestNormal, InputTrigger), debugReason);
        Assert.IsTrue(Near(debugArmSpeed, 60f, 0.05f), "fast arm kept: " + debugArmSpeed);
    }

    private static int FrameAt(float time)
    {
        return (int)(time * 50f + 0.5f);
    }

    // Right- and left-handers: the motion toward the target crosses the plate and is a pitch
    // with the controller's speed, read in the pitching frame as forward; a motion along the
    // plate never crosses it and is a throw.
    [TsukimiTest]
    public void BothHandsReadTheControllerMotionTheSameWay()
    {
        Mimic.Join();
        int[] hands = { GloveBehaviour.HandRight, GloveBehaviour.HandLeft };
        Vector3[] forms = { Overhand, new Vector3(-0.25f, 0.45f, 0.30f) };
        Vector3 target = new Vector3(0f, 0.75f, 0f);
        for (int h = 0; h < hands.Length; h++)
        {
            ActiveForTest(hands[h]);
            Assert.IsTrue(CalibrateForTest(forms[h]), "hand " + h);
            Vector3 hand = HandAt(TestChest, forms[h]);
            Vector3 toward = (target - hand).normalized;
            LockForTest(target);
            ControllerMotionForTest(hand, toward, 5f, 10f + h);
            Assert.IsTrue(ResolveTrackedReleaseAt(10f + h, hand, IdealHand(TestChest), hand, TestPlate, TestNormal, InputTrigger),
                "hand " + h + ": " + debugReason);
            Assert.IsTrue(Near(debugArmSpeed, 5f, 0.01f), "speed " + h);
            Assert.IsTrue(_releaseArmVelocity.z > 4.8f, "forward in the pitching frame " + h + ": " + _releaseArmVelocity);
            Assert.AreEqual(_lockRelease, releasePosition, "launch point " + h);
            // Toward the pitcher's own arm side: not toward the target.
            Vector3 armSide = ManualPitchMath.Rotate(TestFrame(TestChest), Vector3.right * (_activeHand == GloveBehaviour.HandLeft ? -1f : 1f));
            LockForTest(target);
            ControllerMotionForTest(hand, armSide, 5f, 20f + h);
            Assert.IsFalse(ResolveTrackedReleaseAt(20f + h, hand, IdealHand(TestChest), hand, TestPlate, TestNormal, InputTrigger));
            Assert.IsTrue(debugReason.StartsWith("plate: "), debugReason);
            Assert.IsTrue(_releaseArmVelocity.x * (_activeHand == GloveBehaviour.HandLeft ? -1f : 1f) > 4.8f, "arm side in the pitching frame " + h + ": " + _releaseArmVelocity);
        }
    }

    // ---- Launch point ----

    // Every recognized pitch leaves from the solver's release point of its course and along
    // the solved direction (gains 0), wherever the controller, the wrist, the ball centre or
    // the chest is at the release.
    [TsukimiTest]
    public void APitchAlwaysLeavesFromTheSolversReleasePoint()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Vector3 target = new Vector3(0.1f, 0.7f, 0f);
        Quaternion ideal = IdealHand(TestChest);
        // At the ideal, far right/left/up/down, far forward/back.
        Vector3[] offsets = { Vector3.zero, new Vector3(0.8f, 0f, 0f), new Vector3(-0.8f, 0f, 0f), new Vector3(0f, 0.7f, 0f),
            new Vector3(0f, -0.9f, 0f), new Vector3(0f, 0f, 0.9f), new Vector3(0f, 0f, -0.9f) };
        for (int i = 0; i < offsets.Length; i++)
        {
            LockForTest(target);
            Vector3 solver = _lockRelease;
            Assert.IsTrue(ReleaseForTest(TestChest, offsets[i], ideal, TowardPlate()), "offset " + i);
            Assert.AreEqual(solver, releasePosition, "launch point " + i);
            Assert.IsTrue(Vector3.Distance(releaseVelocity.normalized, _lockDirection) < 0.000001f, "solved direction " + i);
        }
        // A turned wrist moves the ball centre with the grip; neither the launch point nor the
        // direction follows it.
        LockForTest(target);
        Vector3 hand = HandAt(TestChest, Overhand);
        Quaternion turned = ManualPitchMath.Multiply(ideal, ManualPitchMath.AxisAngle(Vector3.right, 40f));
        Vector3 ball = hand + ManualPitchMath.Rotate(turned, new Vector3(0f, 0.02f, 0.09f));
        Assert.IsTrue(CrossedReleaseAt(hand, turned, ball, TowardPlate(), TestPlate, TestNormal, InputTrigger));
        Assert.AreEqual(_lockRelease, releasePosition);
        Assert.IsTrue(Vector3.Distance(releaseVelocity.normalized, _lockDirection) < 0.000001f, "the ball centre never turns it");
        Assert.IsTrue(_releaseBallOffset.magnitude > 0.05f, "the ball centre is still shown: " + _releaseBallOffset);
        // The chest moved after the lock: the snapshot stays; only the controller error is
        // measured in the moved frame.
        LockForTest(target);
        Vector3 locked = _lockRelease;
        Vector3 lockedDirection = _lockDirection;
        Vector3 moved = TestChest + new Vector3(0.4f, -0.1f, -0.5f);
        Assert.IsTrue(ReleaseForTest(moved, Vector3.zero, IdealHand(moved), TowardPlate()));
        Assert.AreEqual(locked, releasePosition, "no body correction");
        Assert.IsTrue(Vector3.Distance(releaseVelocity.normalized, lockedDirection) < 0.000001f);
        // The Ball reports the point it launched from; the panel compares it with P0.
        RecordLaunch(releasePosition, releaseVelocity);
        Assert.IsTrue(releaseDetailText.Contains("pitch: from the fixed release point") && releaseDetailText.Contains("diff 0.0 cm"), releaseDetailText);
    }

    [TsukimiTest]
    public void APitchOnTheDefaultCourseLeavesFromItsReleasePoint()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        DefaultCourseForTest(new Vector3(0f, 0.75f, 0f));
        Vector3 solver = _defaultRelease;
        Assert.IsTrue(ReleaseForTest(TestChest, new Vector3(-0.6f, 0.5f, -0.7f), IdealHand(TestChest), TowardPlate()));
        Assert.IsTrue(_resultDefaultCourse);
        Assert.AreEqual(solver, releasePosition);
        Assert.IsTrue(Vector3.Distance(releaseVelocity.normalized, _defaultDirection) < 0.000001f);
        Vector3 moved = TestChest + new Vector3(-0.3f, 0f, 0.4f);
        Assert.IsTrue(ReleaseForTest(moved, Vector3.zero, IdealHand(moved), TowardPlate()));
        Assert.AreEqual(solver, releasePosition, "the default course's point after a chest step too");
    }

    // A release that is not a pitch has no pitch launch point: the Ball throws from the ball
    // centre, and the panel says so.
    [TsukimiTest]
    public void AThrowHasNoPitchLaunchPoint()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, IdealHand(TestChest), TowardPlate()));
        Assert.AreNotEqual(Vector3.zero, releasePosition);
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsFalse(ReleaseForTest(TestChest, Vector3.zero, IdealHand(TestChest), Vector3.back * 0.5f), "too slow");
        Assert.AreEqual(Vector3.zero, releasePosition, "no earlier pitch point is left behind");
        Assert.AreEqual(Vector3.zero, releaseVelocity);
        Vector3 ballCentre = new Vector3(-0.3f, 1.7f, 17.5f);
        RecordLaunch(ballCentre, new Vector3(-20f, 2f, 0f));
        Assert.IsTrue(releaseDetailText.Contains("throw: from the ball centre") && releaseDetailText.Contains("applied " + ballCentre.ToString("F2")), releaseDetailText);
    }

    // ---- Control ----

    [TsukimiTest]
    public void TheIdealReleaseAddsNoControlError()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        LockForTest(new Vector3(0.1f, 0.7f, 0f));
        Vector3 locked = LockedDirection();
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, IdealHand(TestChest), TowardPlate()));
        Assert.IsTrue(Vector3.Distance(releaseVelocity.normalized, locked) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(releaseVelocity.magnitude - 140f / 3.6f) < 0.001f);
    }

    // ---- Solver and flight ----

    // Flies the released state with the shared model (the Ball's drag and Magnus, the
    // release's spin and wake, 0.02 s steps) and returns where it crosses the plate plane.
    private Vector3 FlyToPlate(Vector3 position, Vector3 velocity, Vector3 spin, int wakeMode, float wakeCoefficient, Vector3 wakeDirection)
    {
        WakeProfile profile = new WakeProfile();
        profile.mode = wakeMode;
        profile.coefficient = wakeCoefficient;
        profile.direction = wakeDirection;
        BallWake wake = BallAerodynamics.PrepareWake(profile, spin);
        BallFlightState state = new BallFlightState();
        state.position = position;
        state.velocity = velocity;
        for (int i = 0; i < 200; i++)
        {
            Vector3 previous = state.position;
            state = BallFlightModel.Step(state, spin, TestGravity, _ball.dragCoefficient, _ball.magnusCoefficient, wake, 0.02f);
            if (state.position.z <= 0f)
                return previous + (state.position - previous) * (previous.z / (previous.z - state.position.z));
        }
        return state.position;
    }

    private bool SolveLock(Vector3 eyeTarget)
    {
        Vector3 eye = new Vector3(0f, 1.6f, 18f);
        state = StateTargeting;
        _hasCursor = false;
        if (!UpdateCursor(eye, (eyeTarget - eye).normalized, TestPlate, TestNormal, 0.016f)) return false;
        if (!BeginLockAt(TestP0(), TestPlate, TestNormal, TestGravity, 0.02f)) return false;
        for (int i = 0; i < 200 && state == StateSolving; i++)
        {
            aimSolver.Advance();
            if (!aimSolver.solving) CompleteSolve(aimSolver.converged, aimSolver.errorMeters, aimSolver.launchDirection, aimSolver.predictedCrossing);
        }
        return state == StateLockedReady;
    }

    // The solver aims the 4-Seam with its fixed SSW, and the released flight in the same
    // model (spin, wake) arrives where it was aimed.
    [TsukimiTest(maxSteps: 60000000)]
    public void TheSolvedPitchWithItsWakeArrivesAtTheLockedTarget()
    {
        Mimic.Join();
        _ball = strikeZone != null ? strikeZone.ball : null;
        Assert.IsNotNull(aimSolver, "the runner wires the solver of this folder");
        Assert.IsNotNull(_ball, "the runner wires the ball of this folder");
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Vector3[] targets = { new Vector3(0f, 0.72f, 0f), new Vector3(-0.3f, 0.5f, 0f), new Vector3(1.2f, 1.6f, 0f) };
        for (int t = 0; t < targets.Length; t++)
        {
            Assert.IsTrue(SolveLock(targets[t]), "solved " + t);
            Vector3 hand = HandAt(TestChest, Overhand);
            Assert.IsTrue(CrossedCurveReleaseAt(hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, solverSpeedRatio));
            Assert.AreEqual(BallAerodynamics.WakeFixed, releaseWakeMode, "the 4-Seam has its wake");
            Vector3 crossing = FlyToPlate(releasePosition, releaseVelocity, releaseSpin, releaseWakeMode, releaseWakeCoefficient, releaseWakeDirection);
            Assert.IsTrue(Vector3.Distance(crossing, LockedTarget()) <= aimSolver.tolerance + 0.005f, "target " + t);
            // The same flight without the wake misses: the solver and the flight share it.
            Vector3 dry = FlyToPlate(releasePosition, releaseVelocity, releaseSpin, BallAerodynamics.WakeNone, 0f, Vector3.zero);
            Assert.IsTrue(Vector3.Distance(dry, crossing) > 0.02f, "the wake matters " + t);
        }
    }

    // Control accuracy: launched from the solver's release point along the solved direction,
    // at the solver's speed with its spin and SSW and gains 0, the flight follows the solver's
    // predicted crossing wherever the controller is, however the wrist is turned (moving the
    // ball centre), and after a chest step since the lock. Nothing about the controller's
    // release enters the initial state.
    [TsukimiTest(maxSteps: 90000000)]
    public void WithGainsZeroTheFlightFollowsTheSolverWhateverTheRelease()
    {
        Mimic.Join();
        _ball = strikeZone != null ? strikeZone.ball : null;
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Assert.IsTrue(SolveLock(new Vector3(0.15f, 0.72f, 0f)));
        Vector3 predicted = debugPredictedTarget;
        Quaternion ideal = IdealHand(TestChest);
        Vector3[] chests = { TestChest, TestChest, TestChest, TestChest, TestChest + new Vector3(0.3f, -0.05f, -0.4f) };
        Vector3[] offsets = { Vector3.zero, new Vector3(0.5f, -0.4f, 0f), new Vector3(0f, 0f, -0.6f), Vector3.zero, Vector3.zero };
        Quaternion[] poses = { ideal, ideal, ideal, ManualPitchMath.Multiply(ideal, ManualPitchMath.AxisAngle(new Vector3(0.6f, 0.5f, 0.62f), 45f)), ideal };
        for (int i = 0; i < offsets.Length; i++)
        {
            state = StateLockedReady;
            Vector3 hand = HandAt(chests[i], Overhand) + ManualPitchMath.Rotate(TestFrame(chests[i]), offsets[i]);
            // The ball centre where the grip puts it for that wrist.
            Vector3 ball = hand + ManualPitchMath.Rotate(poses[i], new Vector3(0f, 0.02f, 0.09f));
            Assert.IsTrue(CrossedCurveReleaseAt(hand, poses[i], ball, TowardPlate(), TestPlate, TestNormal, InputTrigger, solverSpeedRatio), "release " + i);
            Assert.AreEqual(_lockRelease, releasePosition, "release " + i);
            Assert.IsTrue(Near(releaseVelocity.magnitude * 3.6f, _lockSolverSpeedKmh, 0.001f), "solver speed " + i);
            Vector3 crossing = FlyToPlate(releasePosition, releaseVelocity, releaseSpin, releaseWakeMode, releaseWakeCoefficient, releaseWakeDirection);
            Assert.IsTrue(Vector3.Distance(crossing, predicted) < 0.001f, "release " + i + ": " + Vector3.Distance(crossing, predicted) + " m from the solver's crossing");
            Assert.IsTrue(Vector3.Distance(crossing, LockedTarget()) <= aimSolver.tolerance, "release " + i);
        }
    }

    // Every client runs Update every frame; outside the mound it returns at once.
    [CostLimit(steps: 29, externs: 1)]
    [TsukimiTest]
    public void IdleUpdateOutsideTheMoundCost()
    {
        PostLateUpdate();
    }

    // The successful recall (and its publication) is tested on the Ball; the runner cannot run
    // another Behaviour's synchronisation from here.
    [TsukimiTest]
    public void TheRecallButtonIsRefusedOutsidePitcherMode()
    {
        Mimic.Join();
        _ball = strikeZone != null ? strikeZone.ball : null;
        Assert.IsNotNull(_ball);
        state = StateInactive;
        Assert.IsFalse(RecallBallAt(5.0));
        Assert.AreEqual(BallBehaviour.HoldFree, _ball.LocalHoldState(), "outside pitcher mode nothing moves");
        Assert.AreEqual("Stand on the mound in VR with the glove on.", statusText);
    }

    // ---- Triggers and panel clicks ----

    // Gives a throwing-hand press the capture it would take at trigger down (the runner has no
    // hand tracking): the throwing hand at `relative`.
    private void CaptureForTest(int hand, Vector3 relative)
    {
        int i = Slot(hand);
        _pressCaptured[i] = hand == _activeHand && _calibrating;
        _triggerChest = TestChest;
        _triggerHandPosition = HandAt(TestChest, relative);
        _triggerPlate = TestPlate;
    }

    private const int Glove = GloveBehaviour.HandLeft;
    private const int Throwing = GloveBehaviour.HandRight;

    // The glove-hand trigger releases a lock when it goes down, not at its release.
    [TsukimiTest]
    public void TheGloveHandTogglesTheLockWhenItsTriggerGoesDown()
    {
        Mimic.Join();
        ActiveForTest(Throwing);
        Assert.IsTrue(CalibrateForTest(Overhand));
        state = StateLockedReady;
        PressDown(Glove, 10);
        Assert.AreEqual(StateTargeting, state, "released at the press");
        PressUp(Glove, 20);
        Assert.AreEqual(StateTargeting, state, "the release does nothing more");
        state = StateLockedReady;
        PressDown(Throwing, 30);
        PressUp(Throwing, 31);
        Assert.AreEqual(StateLockedReady, state, "the throwing hand never toggles the lock");
    }

    // Every order of the two hands' presses and releases: the lock is toggled once, by the
    // glove hand's press, and the throwing hand's press is never taken for it.
    [TsukimiTest]
    public void BothHandsInEveryOrderToggleTheLockOnce()
    {
        Mimic.Join();
        ActiveForTest(Throwing);
        Assert.IsTrue(CalibrateForTest(Overhand));
        // 0 glove down, 1 glove up, 2 throwing down, 3 throwing up; each hand goes down first.
        int[] orders = { 0, 1, 2, 3, 0, 2, 1, 3, 0, 2, 3, 1, 2, 0, 1, 3, 2, 0, 3, 1, 2, 3, 0, 1 };
        for (int o = 0; o < 6; o++)
        {
            state = StateLockedReady;
            for (int k = 0; k < 4; k++)
            {
                int e = orders[o * 4 + k];
                int frame = 100 * (o + 1) + k * 5;
                if (e == 0) PressDown(Glove, frame);
                else if (e == 1) PressUp(Glove, frame);
                else if (e == 2) PressDown(Throwing, frame);
                else PressUp(Throwing, frame);
            }
            Assert.AreEqual(StateTargeting, state, "order " + o);
            Assert.IsFalse(_pressHeld[0] || _pressHeld[1], "both released " + o);
        }
        // Both hands in the very same frame.
        state = StateLockedReady;
        PressDown(Throwing, 900);
        PressDown(Glove, 900);
        PressUp(Glove, 901);
        PressUp(Throwing, 901);
        Assert.AreEqual(StateTargeting, state);
        // Alternating presses: each glove press toggles once, at its press.
        for (int k = 0; k < 3; k++)
        {
            state = StateLockedReady;
            PressDown(Glove, 910 + k * 10);
            Assert.AreEqual(StateTargeting, state, "glove " + k);
            PressUp(Glove, 911 + k * 10);
            state = StateLockedReady;
            PressDown(Throwing, 912 + k * 10);
            PressUp(Throwing, 913 + k * 10);
            Assert.AreEqual(StateLockedReady, state, "throwing " + k);
        }
    }

    // Real-client failure: clicking a panel button with the glove-hand trigger recorded the
    // resting throwing hand as the release pose, and Ball -> Glove toggled the lock.
    [TsukimiTest]
    public void APanelClickNeitherRecordsACalibrationNorTogglesTheLock()
    {
        Mimic.Join();
        ActiveForTest(Throwing);
        Assert.IsTrue(CalibrateForTest(Overhand));
        // Glove hand: the click comes after its release (or at it); the press-time toggle is undone.
        state = StateLockedReady;
        PressDown(Glove, 10);
        Assert.AreEqual(StateTargeting, state);
        PressUp(Glove, 20);
        CancelPressAt(21);
        Assert.AreEqual(StateLockedReady, state, "a click keeps the lock");
        // The click comes first in the frame of the release.
        PressDown(Glove, 30);
        CancelPressAt(40);
        PressUp(Glove, 40);
        Assert.AreEqual(StateLockedReady, state, "a click sent before the release keeps it too");
        // The click comes first in the frame of the press.
        CancelPressAt(50);
        PressDown(Glove, 50);
        Assert.AreEqual(StateLockedReady, state, "a click sent before the press");
        PressUp(Glove, 51);
        // A plain press is not cancelled by a later, unrelated click.
        PressDown(Glove, 60);
        PressUp(Glove, 61);
        CancelPressAt(70);
        Assert.AreEqual(StateTargeting, state, "a plain glove-hand press releases it");
        // Throwing hand while calibrating: the click's press neither records nor ends calibration.
        BeginCalibration();
        PressDown(Throwing, 80);
        CaptureForTest(Throwing, new Vector3(0.15f, -0.55f, 0.10f));
        PressUp(Throwing, 81);
        CancelPressAt(82);
        ApplyPress(Throwing);
        Assert.IsTrue(Calibrating());
        Assert.IsTrue(Vector3.Distance(CalibratedPosition(), Overhand) < 0.0001f);
    }

    // A click is matched to the hand that made it; the other hand's press keeps its effect.
    [TsukimiTest]
    public void AClickCancelsOnlyThePressThatMadeIt()
    {
        Mimic.Join();
        ActiveForTest(Throwing);
        Assert.IsTrue(CalibrateForTest(Overhand));
        BeginCalibration();
        // Throwing hand records while the glove hand clicks a button a few frames later.
        PressDown(Throwing, 10);
        CaptureForTest(Throwing, new Vector3(0.6f, 0f, 0.2f));
        PressDown(Glove, 12);
        PressUp(Throwing, 14);
        PressUp(Glove, 18);
        CancelPressAt(18);
        Assert.IsTrue(_pressCancelled[Slot(Glove)] && !_pressCancelled[Slot(Throwing)]);
        Assert.IsTrue(_pressCaptured[Slot(Throwing)], "the throwing hand's record is kept for its apply (which saves to PlayerData, not modelled here)");
        CancelCalibration();
        // The glove hand toggles while the throwing hand clicks.
        state = StateLockedReady;
        PressDown(Throwing, 30);
        PressDown(Glove, 40);
        PressUp(Throwing, 45);
        CancelPressAt(46);
        Assert.AreEqual(StateTargeting, state, "the glove hand's toggle stays");
        PressUp(Glove, 50);
        // A stale apply of the throwing hand while it is pressed again does nothing.
        BeginCalibration();
        PressDown(Throwing, 60);
        CaptureForTest(Throwing, Overhand);
        ApplyPress(Throwing);
        Assert.IsTrue(Calibrating() && _pressCaptured[Slot(Throwing)], "the held press keeps its capture");
        PressUp(Throwing, 61);
        Assert.IsTrue(_pressCaptured[Slot(Throwing)] && !_pressCancelled[Slot(Throwing)], "and applies it after its own release");
    }

    // An undo never restores a lock that a release or another toggle has since ended.
    [TsukimiTest]
    public void AnUndoNeverRestoresALockEndedSince()
    {
        Mimic.Join();
        ActiveForTest(Throwing);
        Assert.IsTrue(CalibrateForTest(Overhand));
        state = StateLockedReady;
        PressDown(Glove, 10);
        state = StateLockedReady;
        Unlock();
        PressUp(Glove, 11);
        CancelPressAt(11);
        Assert.AreEqual(StateTargeting, state, "the release ended the lock; the click does not bring it back");
    }

    // The glove hand never records; while calibrating it does not lock either.
    [TsukimiTest]
    public void OnlyTheEmptyThrowingHandRecordsTheCalibration()
    {
        Mimic.Join();
        ActiveForTest(Throwing);
        Assert.IsTrue(CalibrateForTest(Overhand));
        BeginCalibration();
        PressDown(Glove, 10);
        PressUp(Glove, 11);
        ApplyPress(Glove);
        Assert.IsTrue(Calibrating(), "glove hand: no record");
        Assert.AreEqual(StateTargeting, state, "and no lock while calibrating");
        // The throwing hand pressed while it held the ball: nothing was captured.
        PressDown(Throwing, 20);
        PressUp(Throwing, 21);
        ApplyPress(Throwing);
        Assert.IsTrue(Calibrating());
        Assert.AreEqual("Put the ball down: calibrate with an empty throwing hand.", statusText);
        Assert.IsTrue(Vector3.Distance(CalibratedPosition(), Overhand) < 0.0001f);
        // Recording itself (PlayerData is written by SaveCalibration and is not modelled here).
        Assert.IsTrue(RecordCalibrationAt(TestChest, HandAt(TestChest, new Vector3(0.6f, 0f, 0.2f)), TestPlate));
        Assert.IsTrue(CommitCalibration());
        Assert.IsFalse(Calibrating(), "one press records and ends calibration");
        Assert.IsTrue(Vector3.Distance(CalibratedPosition(), new Vector3(0.6f, 0f, 0.2f)) < 0.0001f, "the position is replaced");
    }

    [TsukimiTest]
    public void BallResetsKeepTheCalibration()
    {
        Mimic.Join();
        _ball = strikeZone != null ? strikeZone.ball : null;
        Assert.IsNotNull(_ball);
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        _ball.Stage(Vector3.zero);
        _ball.Stage(new Vector3(0f, 1f, 18f));
        Assert.IsTrue(CalibrationUsable(), "staging the ball does not touch the mound's calibration");
        Assert.IsTrue(Vector3.Distance(CalibratedPosition(), Overhand) < 0.0001f);
        LockForTest(new Vector3(0f, 0.7f, 0f));
        Vector3 hand = HandAt(TestChest, Overhand);
        Assert.IsFalse(ResolveTrackedReleaseAt(1f, hand, IdealHand(TestChest), hand, TestPlate, TestNormal, InputGrip));
        Assert.IsTrue(CalibrationUsable(), "nor does a release");
    }

    // ---- Release feedback ----

    [TsukimiTest]
    public void TheArrivalIsMeasuredWhereThePitchCrossesThePlatePlane()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, IdealHand(TestChest), TowardPlate()));
        BeginArrival(new Vector3(0f, 1.8f, 17f), 0f);
        Assert.IsTrue(ReleaseResultText(true).Contains("Course miss  measuring..."));
        // A new lock after the release does not move the target the result is measured against.
        LockForTest(new Vector3(1f, 1.5f, 0f));
        Assert.IsFalse(ArrivalStep(new Vector3(-0.05f, 1.0f, 1.0f), TestPlate, TestNormal));
        Assert.IsTrue(ArrivalStep(new Vector3(-0.15f, 0.6f, -1.0f), TestPlate, TestNormal));
        // Crossing at z = 0: x = -0.10, y = 0.80. The pitcher faces -Z, so world -X is their right.
        Assert.IsTrue(Mathf.Abs(arrivalError.x - 0.10f) < 0.0001f && Mathf.Abs(arrivalError.y - 0.05f) < 0.0001f);
        Assert.IsTrue(arrivalMeasured);
        Assert.AreEqual(ArrivalMeasured, arrivalStatus);
        string text = ReleaseResultText(true);
        Assert.IsTrue(text.StartsWith("PITCH  4-Seam") && text.Contains("Course miss  11.2 cm   right +10 cm   up +5 cm"), text);
        string detail = ReleaseDetailText();
        Assert.IsTrue(detail.Contains("quality 100%"), detail);
        Assert.IsTrue(detail.Contains("target   x +0.00  y 0.75 m") && detail.Contains("actual   x +0.10  y 0.80 m   miss 11.2 cm"), detail);
    }

    // Without a crossing nothing is shown as a 0 cm miss; each ending says why.
    [TsukimiTest]
    public void AnArrivalWithoutACrossingIsNotMeasured()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        int[] ends = { ArrivalHeld, ArrivalHit, ArrivalGrounded, ArrivalNextFlight, ArrivalTimeout, ArrivalStopped, ArrivalLeftMound };
        for (int i = 0; i < ends.Length; i++)
        {
            LockForTest(new Vector3(0f, 0.75f, 0f));
            Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, IdealHand(TestChest), TowardPlate()));
            BeginArrival(new Vector3(0f, 1.8f, 17f), 0f);
            arrivalMeasured = false;
            EndArrival(ends[i]);
            string text = releaseResultText + "\n" + releaseDetailText;
            Assert.IsFalse(arrivalMeasured);
            Assert.IsTrue(text.Contains("Course miss  not measured: ") && !text.Contains("Course miss  0") && !text.Contains("miss 0"), text);
            Assert.IsTrue(text.Contains("actual   not measured: "), text);
        }
        Assert.IsTrue(ArrivalStatusText(ArrivalGrounded).Contains("ground"));
        Assert.IsTrue(ArrivalStatusText(ArrivalHeld).Contains("caught"));
        Assert.IsTrue(ArrivalStatusText(ArrivalHit).Contains("hit"));
    }

    // The speed shown is the launched speed of this release, in km/h, pitch or throw.
    [TsukimiTest]
    public void TheResultShowsTheLaunchSpeedOfThisRelease()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, IdealHand(TestChest), TowardPlate()));
        Assert.IsTrue(ReleaseResultText(true).Contains("Release speed  140.0 km/h"), ReleaseResultText(true));
        // A throw: the pitch's velocity is cleared, and the speed is the throw's own once launched.
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsFalse(ReleaseForTest(TestChest, Vector3.zero, IdealHand(TestChest), Vector3.back * 0.5f));
        Assert.AreEqual(Vector3.zero, releaseVelocity, "no earlier pitch velocity is left behind");
        Assert.IsTrue(ReleaseResultText(false).Contains("Release speed  -"), "not known before the launch");
        RecordLaunch(new Vector3(0f, 1.8f, 17.5f), new Vector3(0f, 3f, -30f));
        Assert.IsTrue(releaseResultText.StartsWith("THROW - not a pitch: arm speed"), releaseResultText);
        Assert.IsTrue(releaseResultText.Contains("Release speed  108.5 km/h"), releaseResultText);
        Assert.IsTrue(releaseResultText.Contains("not measured: not a pitch"), releaseResultText);
    }

    // The analysis shows spin, SSW and the gains of this release, and the solver's predicted
    // crossing apart from the actual one.
    [TsukimiTest]
    public void TheAnalysisSeparatesTheSolverFromTheActualCrossing()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        LockForTest(new Vector3(0f, 0.75f, 0f));
        debugPredictedTarget = new Vector3(-0.02f, 0.76f, 0f);
        debugSolverError = 0.0224f;
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, ManualPitchMath.Multiply(IdealHand(TestChest), ManualPitchMath.AxisAngle(Vector3.up, 30f)), TowardPlate()));
        debugPredictedTarget = Vector3.zero;
        string detail = ReleaseDetailText();
        Assert.IsTrue(detail.Contains("pose evaluation off") && detail.Contains("TYPE  selected 4-Seam   pitched 4-Seam") && detail.Contains("rpm") && detail.Contains("SSW fixed 0.00"), detail);
        Assert.IsTrue(detail.Contains("solver   x +0.02  y 0.76 m   miss 2.2 cm (at lock, converged)"), detail);
        Assert.IsTrue(detail.Contains("actual   measuring...") || detail.Contains("actual   not measured"), detail);
    }

    // ---- Control gain and the flight ----

    // Pitcher-view (right, up) offset of a crossing from a reference crossing.
    private Vector2 ViewOffset(Vector3 crossing, Vector3 reference)
    {
        Vector3 local = ManualPitchMath.Rotate(ManualPitchMath.Inverse(TestFrame(TestChest)), crossing - reference);
        return new Vector2(local.x, local.y);
    }

    // The control turn is finite and unit, and a zero gain on both axes returns the solved
    // direction itself: no position-made change at all.
    [TsukimiTest]
    public void TheControlGainIsFiniteAndNeutralAtZero()
    {
        Vector3 d = new Vector3(0.05f, -0.08f, -1f).normalized;
        float[] gains = { 0f, 0.5f, 1f, 1.5f, 2f };
        for (int i = 0; i < gains.Length; i++)
        {
            Vector3 r = ManualPitchMath.ControlDirection(d, 0.1f, -0.08f, gains[i], gains[i], 17.5f);
            Assert.IsTrue(ManualPitchMath.FiniteVector(r) && Mathf.Abs(r.magnitude - 1f) < 0.0001f, "gain " + gains[i]);
        }
        Assert.AreEqual(d, ManualPitchMath.ControlDirection(d, 0.1f, -0.08f, 0f, 0f, 17.5f), "gain 0: exactly d");
        Assert.AreEqual(d, ManualPitchMath.ControlDirection(d, 2f, -3f, 0f, 0f, 17.5f), "however large the offset");
        Assert.AreEqual(d, ManualPitchMath.ControlDirection(d, 0f, 0f, 1.5f, 1.5f, 17.5f), "no offset");
        Assert.AreEqual(d, ManualPitchMath.ControlDirection(d, 0.1f, -0.08f, 1f, 1f, 0f), "no distance");
        Assert.AreEqual(d, ManualPitchMath.ControlDirection(d, float.NaN, float.NaN, 1f, 1f, 17.5f), "no finite offset");
        Vector3 right = Vector3.Cross(Vector3.up, d).normalized;
        Vector3 onlyY = ManualPitchMath.ControlDirection(d, 0.3f, 0.1f, 0f, 1f, 17.5f);
        Assert.IsTrue(Mathf.Abs(Vector3.Dot(onlyY, right)) < 0.000001f, "gain x 0: no lateral change");
        Vector3 onlyX = ManualPitchMath.ControlDirection(d, 0.1f, 0.3f, 1f, 0f, 17.5f);
        Assert.IsTrue(Near(Vector3.Dot(onlyX - d, right), 0.1f / 17.5f, 0.0001f), "gain 1: offset / L");
    }

    // Real-client verification defaults: no release-position or pose penalty. Recognition and
    // the diagnostic switch keep their values; the penalties are settings, not removed code.
    [TsukimiTest]
    public void ByDefaultThereIsNoPositionOrPosePenalty()
    {
        Assert.AreEqual(0f, armDirectionPenaltyPerDegree);
        Assert.IsFalse(PoseEvaluated(PitchType.FourSeam), "the 4-Seam's pose is not evaluated");
        Assert.IsFalse(diagnosticIdealSpin, "the diagnostic stays off");
        Assert.AreEqual(1f, minArmSpeed);
    }




    // A 4-Seam wake turned toward the top (more lift): the solver and the flight share it.
    [TsukimiTest(maxSteps: 60000000)]
    public void AnUpwardWakeIsAimedAndFlownTheSameWay()
    {
        Mimic.Join();
        _ball = strikeZone != null ? strikeZone.ball : null;
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        float[] clocks = { 0f, 30f };
        for (int c = 0; c < clocks.Length; c++)
        {
            wakeClockDegrees = clocks[c];
            Assert.IsTrue(SolveLock(new Vector3(0.2f, 0.8f, 0f)), "solved " + c);
            Vector3 hand = HandAt(TestChest, Overhand);
            Assert.IsTrue(CrossedCurveReleaseAt(hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, solverSpeedRatio));
            Assert.IsTrue(releaseWakeDirection.y > 0.8f, "mostly upward " + c);
            Vector3 crossing = FlyToPlate(releasePosition, releaseVelocity, releaseSpin, releaseWakeMode, releaseWakeCoefficient, releaseWakeDirection);
            Assert.IsTrue(Vector3.Distance(crossing, LockedTarget()) <= aimSolver.tolerance + 0.005f, "clock " + clocks[c]);
        }
    }

    // ---- Release speed (arm-speed curve, maximum and the solver's single speed) ----

    private bool CurveReleaseForTest(Vector3 handOffset, Quaternion rotation, Vector3 armVelocity, float curveOutput)
    {
        Vector3 hand = HandAt(TestChest, _calPosition) + ManualPitchMath.Rotate(TestFrame(TestChest), handOffset);
        return CrossedCurveReleaseAt(hand, rotation, hand, armVelocity, TestPlate, TestNormal, InputTrigger, curveOutput);
    }

    private bool Near(float a, float b, float tolerance) { return Mathf.Abs(a - b) <= tolerance; }

    [TsukimiTest]
    public void TheSpeedHelpersClampEveryInvalidInput()
    {
        Assert.AreEqual(0f, ManualPitchMath.CurveArmSpeed(-1f));
        Assert.AreEqual(0f, ManualPitchMath.CurveArmSpeed(float.NaN));
        Assert.AreEqual(ManualPitchMath.MaxCurveArmSpeed, ManualPitchMath.CurveArmSpeed(float.PositiveInfinity));
        Assert.AreEqual(5.8f, ManualPitchMath.CurveArmSpeed(5.8f));
        Assert.AreEqual(0f, ManualPitchMath.SpeedRatio(-0.2f));
        Assert.AreEqual(1f, ManualPitchMath.SpeedRatio(1.3f));
        Assert.AreEqual(0f, ManualPitchMath.SpeedRatio(float.NaN));
        Assert.AreEqual(1f, ManualPitchMath.SpeedRatio(float.PositiveInfinity));
        Assert.AreEqual(0.65f, ManualPitchMath.SpeedRatio(0.65f));
        Assert.AreEqual(0f, ManualPitchMath.SpeedSetting(-5f));
        Assert.AreEqual(0f, ManualPitchMath.SpeedSetting(float.NaN));
        Assert.AreEqual(0f, ManualPitchMath.SpeedSetting(float.PositiveInfinity));
        Assert.IsTrue(Near(ManualPitchMath.ScaledSpeedKmh(140f, 0.97f), 135.8f, 0.001f));
        Assert.AreEqual(0f, ManualPitchMath.ScaledSpeedKmh(float.NaN, 0.97f));
        Assert.AreEqual(140f, ManualPitchMath.ScaledSpeedKmh(140f, 2f));
    }

    [TsukimiTest]
    public void TheSpeedDefaultsAreTheMaximumAndTheSolverRatio()
    {
        Assert.AreEqual(140f, maxPitchSpeedKmh);
        Assert.AreEqual(0.97f, solverSpeedRatio);
        Assert.AreEqual(1f, minArmSpeed, "recognition keeps its own minimum");
    }

    // The release speed is maxPitchSpeedKmh x Clamp01(curve output), from the recognition's arm
    // sample; the same output gives the same speed, and pose and release position change nothing.
    [TsukimiTest]
    public void TheReleaseSpeedIsTheMaximumTimesTheCurveOutput()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Quaternion ideal = IdealHand(TestChest);
        Vector3 target = new Vector3(0f, 0.75f, 0f);
        float[] outputs = { 0.30f, 0.65f, 0.97f, 0.98f, 1f, 1.4f };
        float[] expected = { 42f, 91f, 135.8f, 137.2f, 140f, 140f };
        for (int i = 0; i < outputs.Length; i++)
        {
            LockForTest(target);
            Assert.IsTrue(CurveReleaseForTest(Vector3.zero, ideal, TowardPlate(), outputs[i]), "pitch " + i);
            Assert.IsTrue(Near(releaseVelocity.magnitude * 3.6f, expected[i], 0.01f), "output " + outputs[i] + ": " + releaseVelocity.magnitude * 3.6f);
        }
        LockForTest(target);
        Assert.IsTrue(CurveReleaseForTest(Vector3.zero, ideal, TowardPlate(), 0.97f));
        Vector3 first = releaseVelocity;
        LockForTest(target);
        Assert.IsTrue(CurveReleaseForTest(Vector3.zero, ideal, TowardPlate(), 0.97f));
        Assert.AreEqual(first, releaseVelocity, "the same output gives the same release");
        // Position and pose errors never change the speed (and at gain 0 not the direction).
        LockForTest(target);
        Assert.IsTrue(CurveReleaseForTest(new Vector3(0.3f, -0.2f, 0.2f),
            ManualPitchMath.Multiply(ideal, ManualPitchMath.AxisAngle(Vector3.right, 40f)), TowardPlate(), 0.97f));
        Assert.IsTrue(Vector3.Distance(releaseVelocity.normalized, first.normalized) < 0.000001f);
        Assert.IsTrue(Near(releaseVelocity.magnitude * 3.6f, 135.8f, 0.01f), "speed " + releaseVelocity.magnitude * 3.6f);
        // An invalid maximum gives no speed rather than NaN.
        maxPitchSpeedKmh = float.NaN;
        LockForTest(target);
        Assert.IsTrue(CurveReleaseForTest(Vector3.zero, ideal, TowardPlate(), 0.97f));
        Assert.AreEqual(Vector3.zero, releaseVelocity);
    }

    // Without keys the curve is not used: every pitch leaves at the maximum, and the panel says so.
    [TsukimiTest]
    public void ACurveWithoutKeysPitchesAtTheMaximum()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        armSpeedCurve = null;
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, IdealHand(TestChest), Vector3.back * 1.5f));
        Assert.IsTrue(Near(releaseVelocity.magnitude * 3.6f, 140f, 0.01f));
        Assert.IsTrue(ReleaseDetailText().Contains("curve has no keys: full speed"), ReleaseDetailText());
    }

    // Below the recognition's minimum it is the existing throw; the curve is not read, and no
    // new speed condition refuses a slow pitch.
    [TsukimiTest]
    public void ASlowArmIsAThrowAndASlowPitchIsStillAPitch()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsFalse(CurveReleaseForTest(Vector3.zero, IdealHand(TestChest), Vector3.back * 0.5f, 0.97f));
        Assert.AreEqual("arm speed", debugReason);
        Assert.AreEqual(Vector3.zero, releaseVelocity);
        Assert.IsTrue(ReleaseDetailText().Contains("SPEED  not a pitch: arm-speed curve not applied"), ReleaseDetailText());
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(CurveReleaseForTest(Vector3.zero, IdealHand(TestChest), Vector3.back * 1.2f, 0.32f), "a slow pitch is still a pitch");
        Assert.IsTrue(Near(releaseVelocity.magnitude * 3.6f, 44.8f, 0.01f));
    }

    // The solver aims with maxPitchSpeedKmh x solverSpeedRatio taken at the lock; later setting
    // changes and the release's own speed never touch it, and the release does not solve again.
    [TsukimiTest(maxSteps: 60000000)]
    public void TheSolverUsesOneSpeedFixedAtTheLock()
    {
        Mimic.Join();
        _ball = strikeZone != null ? strikeZone.ball : null;
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Vector3 eye = new Vector3(0f, 1.6f, 18f);
        Assert.IsTrue(UpdateCursor(eye, (new Vector3(0f, 0.72f, 0f) - eye).normalized, TestPlate, TestNormal, 0.016f));
        Assert.IsTrue(BeginLockAt(TestP0(), TestPlate, TestNormal, TestGravity, 0.02f));
        Assert.IsTrue(Near(_lockSolverSpeedKmh, 135.8f, 0.001f), "solver speed " + _lockSolverSpeedKmh);
        maxPitchSpeedKmh = 90f;
        solverSpeedRatio = 0.5f;
        for (int i = 0; i < 200 && state == StateSolving; i++) AdvanceSolve();
        Assert.AreEqual(StateLockedReady, state);
        Assert.IsTrue(Near(_lockSolverSpeedKmh, 135.8f, 0.001f), "setting changes after the lock are not used");
        // The failure flight's aim follows in the background, before the pitch.
        FinishLockFailureForTest();
        Assert.IsTrue(_lockFailureReady);
        Vector3 lockDirection = _lockDirection;
        int iterations = aimSolver.iterations;
        Vector3 hand = HandAt(TestChest, Overhand);
        Assert.IsTrue(CrossedCurveReleaseAt(hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, 0.5f));
        Assert.IsTrue(Near(releaseVelocity.magnitude * 3.6f, 45f, 0.01f), "the release reads the maximum at the release");
        Assert.IsFalse(aimSolver.solving, "the release does not solve again");
        Assert.AreEqual(iterations, aimSolver.iterations);
        Assert.AreEqual(lockDirection, _lockDirection);
        Assert.IsTrue(ReleaseDetailText().Contains("solver 135.8 km/h"), ReleaseDetailText());
    }

    // Actual speed = solver speed with the ideal spin and SSW, the ideal release and ball centre
    // and no control penalty: the flight follows the solver's prediction. A faster or slower
    // release keeps the solved direction (no aim correction for speed) and arrives higher or
    // lower; a deliberately slow pitch misses the course by much more.
    [TsukimiTest(maxSteps: 90000000)]
    public void SpeedDifferencesFromTheSolverAreNotCorrected()
    {
        Mimic.Join();
        _ball = strikeZone != null ? strikeZone.ball : null;
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        diagnosticIdealSpin = true;
        Assert.IsTrue(SolveLock(new Vector3(0f, 0.72f, 0f)));
        Vector3 hand = HandAt(TestChest, Overhand);
        Assert.IsTrue(CrossedCurveReleaseAt(hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, solverSpeedRatio));
        Vector3 matched = FlyToPlate(releasePosition, releaseVelocity, releaseSpin, releaseWakeMode, releaseWakeCoefficient, releaseWakeDirection);
        Vector3 direction = releaseVelocity.normalized;
        Assert.IsTrue(Vector3.Distance(matched, debugPredictedTarget) < 0.001f, "the flight follows the solver's prediction: " + Vector3.Distance(matched, debugPredictedTarget));
        Assert.IsTrue(Vector3.Distance(matched, LockedTarget()) <= aimSolver.tolerance, "and arrives at the target");
        Assert.IsTrue(Vector3.Distance(matched, FlyToPlate(releasePosition, releaseVelocity, releaseSpin, releaseWakeMode, releaseWakeCoefficient, releaseWakeDirection)) == 0f,
            "the same initial state flies the same way (as a receiver's catch-up does)");

        state = StateLockedReady;
        Assert.IsTrue(CrossedCurveReleaseAt(hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, 1f));
        Assert.IsTrue(Vector3.Distance(releaseVelocity.normalized, direction) < 0.000001f, "faster: same direction");
        Vector3 fast = FlyToPlate(releasePosition, releaseVelocity, releaseSpin, releaseWakeMode, releaseWakeCoefficient, releaseWakeDirection);
        Assert.IsTrue(fast.y > matched.y + 0.02f, "faster arrives higher: " + (fast.y - matched.y));

        state = StateLockedReady;
        Assert.IsTrue(CrossedCurveReleaseAt(hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, 95f / 140f));
        Assert.IsTrue(Vector3.Distance(releaseVelocity.normalized, direction) < 0.000001f, "slower: same direction");
        Vector3 slow = FlyToPlate(releasePosition, releaseVelocity, releaseSpin, releaseWakeMode, releaseWakeCoefficient, releaseWakeDirection);
        Assert.IsTrue(slow.y < matched.y - 0.3f, "a 95 km/h pitch drops well below the course: " + (slow.y - matched.y));
        Assert.IsTrue(matched.y - slow.y > (fast.y - matched.y) * 5f, "the slow miss is much larger than the fast one");
    }

    // The panel shows the solver's and the actual speed apart, the signed difference and the
    // curve output, all as snapshotted at this release.
    [TsukimiTest]
    public void TheSpeedResultIsASnapshotOfThisRelease()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(CurveReleaseForTest(Vector3.zero, IdealHand(TestChest), TowardPlate(), 1f));
        string text = SpeedText();
        Assert.IsTrue(text.Contains("arm 20.0 m/s   curve 100.0%   max 140.0 km/h"), text);
        Assert.IsTrue(text.Contains("solver 135.8 km/h   release 140.0 km/h   delta +4.2 km/h"), text);
        maxPitchSpeedKmh = 100f;
        solverSpeedRatio = 0.5f;
        Assert.AreEqual(text, SpeedText(), "later setting changes do not rewrite the result");

        maxPitchSpeedKmh = 140f;
        solverSpeedRatio = 0.97f;
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(CurveReleaseForTest(Vector3.zero, IdealHand(TestChest), TowardPlate(), 0.68f));
        text = SpeedText();
        Assert.IsTrue(text.Contains("curve 68.0%") && text.Contains("release 95.2 km/h   delta -40.6 km/h"), text);
        RecordLaunch(releasePosition, releaseVelocity);
        Assert.IsTrue(releaseDetailText.Contains("delta -40.6 km/h"), "the launched speed is the release's: " + releaseDetailText);

        // A throw after a pitch shows none of the pitch's speed model.
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsFalse(CurveReleaseForTest(Vector3.zero, IdealHand(TestChest), Vector3.back * 0.5f, 1f));
        text = ReleaseDetailText();
        Assert.IsTrue(text.Contains("SPEED  not a pitch") && !text.Contains("delta") && !text.Contains("solver 135.8"), text);
    }

    // ---- Arm motion: controller samples, speed and direction ----

    private readonly float[] _testTimes = new float[8];
    private readonly Vector3[] _testPositions = new Vector3[8];
    private readonly int[] _testState = new int[FieldingMath.MotionStateLength];

    // Segment velocities on uneven intervals use the real sample times; the speed is the
    // weighted mean of the segment speeds (1, 2, 3, oldest first).
    [TsukimiTest]
    public void TheArmSpeedUsesRealSampleTimesAndNewestWeights()
    {
        FieldingMath.ResetMotion(_testState);
        // 2 m/s over 0.011 s, 4 m/s over 0.014 s, 6 m/s over 0.007 s.
        FieldingMath.SampleMotion(_testTimes, _testPositions, _testState, 0f, Vector3.zero, 0.1f);
        FieldingMath.SampleMotion(_testTimes, _testPositions, _testState, 0.011f, new Vector3(0.022f, 0f, 0f), 0.1f);
        FieldingMath.SampleMotion(_testTimes, _testPositions, _testState, 0.025f, new Vector3(0.078f, 0f, 0f), 0.1f);
        FieldingMath.SampleMotion(_testTimes, _testPositions, _testState, 0.032f, new Vector3(0.12f, 0f, 0f), 0.1f);
        ArmMotion motion = FieldingMath.EstimateArmMotion(_testTimes, _testPositions, _testState);
        Assert.AreEqual(3, motion.segments);
        Assert.IsTrue(Near(motion.newestSpeed, 6f, 0.01f) && Near(motion.middleSpeed, 4f, 0.01f) && Near(motion.oldestSpeed, 2f, 0.01f),
            motion.newestSpeed + " " + motion.middleSpeed + " " + motion.oldestSpeed);
        Assert.IsTrue(Near(motion.speed, (2f + 8f + 18f) / 6f, 0.01f), "weighted speed " + motion.speed);
        Assert.IsTrue(Near(motion.span, 0.032f, 0.0001f), "span " + motion.span);
        Assert.IsTrue(Vector3.Distance(motion.direction, Vector3.right) < 0.0001f);
    }

    // A turning arm keeps its speed: the vectors are averaged only for the direction.
    [TsukimiTest]
    public void ATurningArmIsNotReadSlower()
    {
        FieldingMath.ResetMotion(_testState);
        Vector3 p = Vector3.zero;
        Vector3[] steps = { Vector3.forward, Vector3.right, Vector3.back };
        FieldingMath.SampleMotion(_testTimes, _testPositions, _testState, 0f, p, 0.1f);
        for (int i = 0; i < steps.Length; i++)
        {
            p += steps[i] * 0.1f;
            FieldingMath.SampleMotion(_testTimes, _testPositions, _testState, 0.02f * (i + 1), p, 0.1f);
        }
        ArmMotion motion = FieldingMath.EstimateArmMotion(_testTimes, _testPositions, _testState);
        Assert.IsTrue(Near(motion.speed, 5f, 0.001f), "every segment is 5 m/s: " + motion.speed);
        Assert.IsTrue(motion.velocity.magnitude < 3f, "the averaged vector is shorter: " + motion.velocity.magnitude);
        Assert.IsTrue(Vector3.Dot(motion.direction, Vector3.right) > 0.6f, "direction follows the newer segments: " + motion.direction);
    }

    // Non-finite positions and times are not stored, time never runs backwards, a repeated time
    // replaces the newest sample; one sample is "not measured", two equal ones a measured 0.
    [TsukimiTest]
    public void InvalidSamplesAreSafeAndUnmeasuredIsNotZero()
    {
        FieldingMath.ResetMotion(_testState);
        Assert.AreEqual(-1, FieldingMath.SampleMotion(_testTimes, _testPositions, _testState, 0f, new Vector3(float.NaN, 0f, 0f), 0.1f));
        Assert.AreEqual(-1, FieldingMath.SampleMotion(_testTimes, _testPositions, _testState, float.PositiveInfinity, Vector3.zero, 0.1f));
        Assert.AreEqual(-1, FieldingMath.SampleMotion(_testTimes, _testPositions, _testState, 0f, new Vector3(0f, float.NegativeInfinity, 0f), 0.1f));
        Assert.AreEqual(0, _testState[FieldingMath.MotionCount]);
        FieldingMath.SampleMotion(_testTimes, _testPositions, _testState, 1f, Vector3.zero, 0.1f);
        ArmMotion one = FieldingMath.EstimateArmMotion(_testTimes, _testPositions, _testState);
        Assert.AreEqual(0, one.segments, "one sample: not measured");
        FieldingMath.SampleMotion(_testTimes, _testPositions, _testState, 1f, Vector3.one, 0.1f);
        Assert.AreEqual(1, _testState[FieldingMath.MotionCount], "the same time replaces, no zero-time segment");
        Assert.AreEqual(-1, FieldingMath.SampleMotion(_testTimes, _testPositions, _testState, 0.99f, Vector3.zero, 0.1f), "older is ignored");
        FieldingMath.SampleMotion(_testTimes, _testPositions, _testState, 1.02f, Vector3.one, 0.1f);
        ArmMotion still = FieldingMath.EstimateArmMotion(_testTimes, _testPositions, _testState);
        Assert.AreEqual(1, still.segments, "measured");
        Assert.AreEqual(0f, still.speed, "a measured standstill");
        Assert.AreEqual(Vector3.zero, still.direction);
        Assert.IsTrue(FieldingMath.GivenArmMotion(new Vector3(float.NaN, 0f, 0f)).segments == 0);
    }

    // Switching the throwing hand (or entering pitcher mode) forgets the other hand's samples.
    [TsukimiTest]
    public void SwitchingTheHandClearsTheSamples()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        for (int i = 0; i < 4; i++) SampleArmAt(0.02f * i, HandAt(TestChest, Overhand) + Vector3.back * (0.1f * i));
        Assert.AreEqual(3, ControllerMotion().segments);
        SwitchThrowingHand(GloveBehaviour.HandLeft);
        Assert.AreEqual(0, ControllerMotion().segments, "nothing of the right hand is left");
    }

    // Every finite stored calibration is used again, including one taken outside the former
    // 0.05-1.5 m / 45-90 deg range; only an unreadable value is refused.
    [TsukimiTest]
    public void AnyFiniteStoredCalibrationIsRestored()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(RestoreCalibration(CalibrationVersion, GloveBehaviour.HandRight, new Vector3(0.05f, 0.1f, 0.6f)),
            "in front of the body (formerly refused)");
        Assert.IsTrue(CalibrationUsable());
        Assert.IsTrue(RestoreCalibration(CalibrationVersion, GloveBehaviour.HandRight, new Vector3(0f, 0f, -2.5f)),
            "far behind the chest");
        Assert.IsTrue(RestoreCalibration(CalibrationVersion, GloveBehaviour.HandRight, Vector3.zero), "at the chest");
        Assert.IsFalse(RestoreCalibration(CalibrationVersion, GloveBehaviour.HandRight, new Vector3(float.NaN, 0f, 0f)), "NaN");
        Assert.IsFalse(RestoreCalibration(CalibrationVersion, GloveBehaviour.HandRight, new Vector3(float.PositiveInfinity, 0f, 0f)),
            "infinity");
        Assert.IsFalse(RestoreCalibration(2, GloveBehaviour.HandRight, Overhand), "unknown version");
    }

    // Any finite position is recorded and saved: no distance or angle limit.
    [TsukimiTest]
    public void EveryFiniteReleasePoseIsRecorded()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Vector3[] poses = { new Vector3(0f, 0f, 1.4f), new Vector3(0.2f, 0.3f, -0.6f), new Vector3(2.5f, 0.5f, 0f),
            new Vector3(0.01f, 0f, 0f), new Vector3(0f, -1.2f, 0.9f) };
        for (int i = 0; i < poses.Length; i++)
        {
            Assert.IsTrue(CalibrateForTest(poses[i]), "pose " + i + ": " + statusText);
            Assert.IsTrue(Vector3.Distance(poses[i], CalibratedPosition()) < 0.0001f, "pose " + i);
        }
    }

    // ---- Fixed release point and its range ----

    // Test mound: F = -Z, the pitcher's right R = world -X; the mound centre (the pitcher's
    // plate) is at y 0.25, z 18.44; the anchor (0.3, 1.8, 17.84) has a 0.6 m extension and is
    // 1.55 m above it. Without a calibrated release the offset is zero and is limited to the
    // range: extension and height kept, 0.1 m to the arm side.
    private static readonly Vector3 RightUncalibrated = new Vector3(0.2f, 1.80f, 17.84f);
    private static readonly Vector3 LeftUncalibrated = new Vector3(0.4f, 1.80f, 17.84f);

    private bool CalibratedOffsetForTest(int hand, Vector3 offset)
    {
        ActiveForTest(hand);
        if (!RestoreCalibration(CalibrationVersion, hand, Overhand)) return false;
        return RestoreReleaseOffset(offset.x, offset.y, offset.z);
    }

    [TsukimiTest]
    public void TheReleaseRangeDefaultsAreExtensionHeightAndArmSide()
    {
        Assert.AreEqual(0.5f, releaseForwardMin);
        Assert.AreEqual(2.3f, releaseForwardMax);
        Assert.AreEqual(0.1f, releaseHeightMin);
        Assert.AreEqual(2.0f, releaseHeightMax);
        Assert.AreEqual(0.1f, releaseSideMin);
        Assert.AreEqual(0.6f, releaseSideMax);
    }

    // Without a calibrated release the anchor itself is limited to the range; a right-hander
    // releases to the pitcher's right (the batter's left), a left-hander the mirror.
    [TsukimiTest]
    public void WithoutACalibrationTheAnchorIsLimitedToTheRange()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Vector3 right = TestP0();
        Assert.IsTrue(Vector3.Distance(right, RightUncalibrated) < 0.0001f, "right-hander " + right);
        ActiveForTest(GloveBehaviour.HandLeft);
        Vector3 left = TestP0();
        Assert.IsTrue(Vector3.Distance(left, LeftUncalibrated) < 0.0001f, "left-hander " + left);
        Vector3 pitcherRight = Vector3.Cross(Vector3.up, Vector3.back);
        Assert.IsTrue(Vector3.Dot(right - TestAnchor, pitcherRight) > 0.099f, "right-hander toward the pitcher's right");
        Assert.IsTrue(Vector3.Dot(left - TestAnchor, pitcherRight) < -0.099f, "left-hander toward the pitcher's left");
    }

    // Each axis is limited on its own, edges included: extension and height from the mound
    // centre, side from the anchor; a left-hander's side is mirrored.
    [TsukimiTest]
    public void ACalibratedReleaseIsLimitedOnEachAxis()
    {
        Mimic.Join();
        Assert.IsTrue(CalibratedOffsetForTest(GloveBehaviour.HandRight, new Vector3(1.0f, 0.2f, 0.3f)));
        Assert.IsTrue(Vector3.Distance(TestP0(), new Vector3(0f, 2.0f, 16.84f)) < 0.0001f, "inside: unchanged " + TestP0());
        Assert.IsTrue(CalibratedOffsetForTest(GloveBehaviour.HandRight, new Vector3(0.2f, 0.2f, 0.3f)));
        Assert.IsTrue(Mathf.Abs(TestP0().z - 17.64f) < 0.0001f, "0.8 m extension from the mound centre: inside");
        Assert.IsTrue(CalibratedOffsetForTest(GloveBehaviour.HandRight, new Vector3(-0.4f, 0.2f, 0.3f)));
        Assert.IsTrue(Mathf.Abs(TestP0().z - 17.94f) < 0.0001f, "extension at least 0.5 m from the mound centre: " + TestP0());
        Assert.IsTrue(CalibratedOffsetForTest(GloveBehaviour.HandRight, new Vector3(3.0f, 0.2f, 0.3f)));
        Assert.IsTrue(Mathf.Abs(TestP0().z - 16.14f) < 0.0001f, "extension at most 2.3 m from the mound centre: " + TestP0());
        Assert.IsTrue(CalibratedOffsetForTest(GloveBehaviour.HandRight, new Vector3(1.0f, -1.8f, 0.3f)));
        Assert.IsTrue(Mathf.Abs(TestP0().y - 0.35f) < 0.0001f, "at least 0.1 m above the ground: " + TestP0());
        Assert.IsTrue(CalibratedOffsetForTest(GloveBehaviour.HandRight, new Vector3(1.0f, 1.0f, 0.3f)));
        Assert.IsTrue(Mathf.Abs(TestP0().y - 2.25f) < 0.0001f, "at most 2.0 m above the ground: " + TestP0());
        Assert.IsTrue(CalibratedOffsetForTest(GloveBehaviour.HandRight, new Vector3(1.0f, 0.2f, -0.3f)));
        Assert.IsTrue(Mathf.Abs(TestP0().x - 0.2f) < 0.0001f, "at least 0.1 m to the arm side (a glove-side release too)");
        Assert.IsTrue(CalibratedOffsetForTest(GloveBehaviour.HandRight, new Vector3(1.0f, 0.2f, 0.9f)));
        Assert.IsTrue(Mathf.Abs(TestP0().x + 0.3f) < 0.0001f, "at most 0.6 m to the arm side");
        Assert.IsTrue(CalibratedOffsetForTest(GloveBehaviour.HandLeft, new Vector3(1.0f, 0.2f, 0.9f)));
        Assert.IsTrue(Mathf.Abs(TestP0().x - 0.9f) < 0.0001f, "a left-hander's arm side is the mirror");
        ReleaseRange range = ReleaseRange();
        Assert.IsTrue(ManualPitchMath.InReleaseRange(new Vector3(0.5f, 2.0f, 0.1f), Vector2.zero, range), "edges included");
        Assert.IsTrue(ManualPitchMath.InReleaseRange(new Vector3(2.3f, 0.1f, 0.6f), Vector2.zero, range), "edges included");
        Assert.IsFalse(ManualPitchMath.InReleaseRange(new Vector3(1.71f, 0f, 0.3f), new Vector2(0.6f, 1.55f), range), "2.31 m extension");
        Assert.IsTrue(ManualPitchMath.InReleaseRange(new Vector3(-0.1f, 0f, 0.3f), new Vector2(0.6f, 1.55f), range), "0.5 m extension");
        Assert.IsFalse(ManualPitchMath.InReleaseRange(new Vector3(1f, 0.5f, 0.3f), new Vector2(0.6f, 1.55f), range), "2.05 m high");
        Assert.IsFalse(ManualPitchMath.InReleaseRange(new Vector3(1f, 0f, 0.05f), new Vector2(0.6f, 1.55f), range));
    }

    // The range is the Inspector's, read at each placement; a swapped minimum and maximum is
    // read in order.
    [TsukimiTest]
    public void TheRangeComesFromTheInspector()
    {
        Mimic.Join();
        Assert.IsTrue(CalibratedOffsetForTest(GloveBehaviour.HandRight, new Vector3(1.0f, 0.2f, 0.3f)));
        releaseForwardMin = 1.8f;
        Assert.IsTrue(Mathf.Abs(TestP0().z - 16.64f) < 0.0001f, "new minimum extension");
        releaseForwardMin = 0.5f;
        releaseSideMin = 0.4f;
        releaseSideMax = 0.2f;
        Assert.IsTrue(Mathf.Abs(TestP0().x - 0f) < 0.0001f, "0.3 inside 0.2..0.4");
        releaseSideMin = 0.5f;
        releaseSideMax = 0.6f;
        Assert.IsTrue(Mathf.Abs(TestP0().x + 0.2f) < 0.0001f, "0.3 raised to 0.5");
        releaseHeightMax = float.NaN;
        ReleaseRange range = ReleaseRange();
        Assert.AreEqual(0f, range.heightMin, "a non-finite value reads as 0");
        Assert.AreEqual(0.1f, range.heightMax);
    }

    // A turned lane (another mound and plate direction) uses its own axes, not world X.
    [TsukimiTest]
    public void TheFixedReleasePointFollowsATurnedLane()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Vector3 plate = new Vector3(50f, 0f, -20f);
        Quaternion yaw = ManualPitchMath.AxisAngle(Vector3.up, 90f);
        Vector3 centre = plate + ManualPitchMath.Rotate(yaw, TestCentre - TestPlate);
        Vector3 anchor = plate + ManualPitchMath.Rotate(yaw, TestAnchor - TestPlate);
        Vector3 turned = FixedReleaseAt(centre, anchor, plate);
        Vector3 expected = plate + ManualPitchMath.Rotate(yaw, RightUncalibrated - TestPlate);
        Assert.IsTrue(Vector3.Distance(turned, expected) < 0.0001f, turned + " vs " + expected);
        Assert.AreEqual(Vector3.zero, FixedReleaseAt(plate + Vector3.up * 3f, anchor, plate), "a centre above the plate has no direction");
        Assert.AreEqual(Vector3.zero, FixedReleaseAt(new Vector3(float.NaN, 0f, 0f), anchor, plate));
    }

    // The solver aims from P0 and the release leaves from that same P0, wherever the player,
    // the head, the chest, the controller or the ball is.
    [TsukimiTest]
    public void TheSolverAndThePitchShareTheFixedReleasePoint()
    {
        Mimic.Join();
        _ball = null;
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Vector3 p0 = TestP0();
        _lockTarget = new Vector3(0f, 0.75f, 0f);
        _lockRelease = p0;
        _lockDirection = (_lockTarget - p0).normalized;
        _lockSolverSpeedKmh = ManualPitchMath.ScaledSpeedKmh(maxPitchSpeedKmh, solverSpeedRatio);
        _lockConverged = true;
        state = StateLockedReady;
        Vector3[] hands = { HandAt(TestChest, Overhand), HandAt(TestChest, Overhand) + new Vector3(1.2f, -0.4f, 0.8f),
            new Vector3(3f, 2f, 15f) };
        for (int i = 0; i < hands.Length; i++)
        {
            _lockRelease = p0;
            state = StateLockedReady;
            Assert.IsTrue(CrossedCurveReleaseAt(hands[i], IdealHand(TestChest), hands[i] + Vector3.up * 0.1f, TowardPlate(),
                TestPlate, TestNormal, InputTrigger, solverSpeedRatio), "hand " + i + ": " + debugReason);
            Assert.AreEqual(p0, releasePosition, "hand " + i);
            Assert.AreEqual(p0, _resultSolverRelease, "the solver's point " + i);
        }
    }

    // The calibration sets P0 on all three axes, stored relative to ReleaseAnchor (no fixed
    // side offset any more). Overhand at TestChest is world (-0.25, 1.85, 17.30): 0.54 m toward
    // the plate from the anchor (a 1.14 m extension), 0.05 m up and 0.55 m toward the
    // right-hander's arm side, all inside the range.
    [TsukimiTest]
    public void TheCalibratedReleaseSetsP0OnEveryAxis()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(Vector3.Distance(TestP0(), RightUncalibrated) < 0.0001f, "no calibration");
        BeginCalibration();
        Assert.IsTrue(RecordCalibrationWithAnchorAt(TestChest, HandAt(TestChest, Overhand), TestPlate,
            true, TestCentre, TestAnchor));
        Assert.IsFalse(_draftReleaseLimited, "inside the range");
        Assert.IsTrue(CommitCalibration());
        Assert.IsTrue(HasCalibratedRelease());
        Vector3 offset = CalibratedReleaseOffset();
        Assert.IsTrue(Vector3.Distance(offset, new Vector3(0.54f, 0.05f, 0.55f)) < 0.0001f, "offset " + offset);
        Vector3 p0 = TestP0();
        Assert.IsTrue(Vector3.Distance(p0, HandAt(TestChest, Overhand)) < 0.0001f, "calibrated P0 is the hand " + p0);
        // Relative to the anchor: a moved anchor keeps the same offset.
        Vector3 moved = FixedReleaseAt(TestCentre, TestAnchor + Vector3.up * 0.2f, TestPlate);
        Assert.IsTrue(Vector3.Distance(moved, new Vector3(-0.25f, 2.05f, 17.30f)) < 0.0001f, "moved anchor " + moved);
        // The other hand has no usable calibration: the limited anchor, mirrored.
        ActiveForTest(GloveBehaviour.HandLeft);
        Assert.IsTrue(Vector3.Distance(TestP0(), LeftUncalibrated) < 0.0001f, "left hand: the anchor");
        // A calibration outside the range is recorded as it is and limited, and the preview says so.
        ActiveForTest(GloveBehaviour.HandRight);
        BeginCalibration();
        Assert.IsTrue(RecordCalibrationWithAnchorAt(TestChest, HandAt(TestChest, Overhand) + new Vector3(0f, 0f, 1.0f),
            TestPlate, true, TestCentre, TestAnchor));
        Assert.IsTrue(_draftReleaseLimited, "0.14 m extension");
        Assert.IsTrue(PreviewText().Contains("outside the release range"), PreviewText());
        Assert.IsTrue(CommitCalibration());
        Assert.IsTrue(Mathf.Abs(CalibratedReleaseOffset().x + 0.46f) < 0.0001f, "stored as measured (from the anchor)");
        Assert.IsTrue(Mathf.Abs(TestP0().z - 17.94f) < 0.0001f, "placed at a 0.5 m extension");
    }

    // The player moving after the calibration does not move P0, and releases leave from it.
    [TsukimiTest]
    public void TheCalibratedP0DoesNotFollowThePlayer()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        BeginCalibration();
        Assert.IsTrue(RecordCalibrationWithAnchorAt(TestChest, HandAt(TestChest, Overhand), TestPlate,
            true, TestCentre, TestAnchor));
        Assert.IsTrue(CommitCalibration());
        Vector3 p0 = TestP0();
        _lockTarget = new Vector3(0f, 0.75f, 0f);
        _lockRelease = p0;
        _lockDirection = (_lockTarget - p0).normalized;
        _lockConverged = true;
        state = StateLockedReady;
        Vector3 elsewhere = HandAt(TestChest + new Vector3(0.8f, 0.3f, -0.6f), Overhand);
        Assert.IsTrue(CrossedCurveReleaseAt(elsewhere, IdealHand(TestChest), elsewhere, TowardPlate(), TestPlate, TestNormal,
            InputTrigger, solverSpeedRatio), debugReason);
        Assert.AreEqual(p0, releasePosition);
        Assert.AreEqual(p0, TestP0(), "nothing of the release moves it");
    }

    // A calibration saved before the release offset existed uses the limited anchor; one saved
    // before the side existed reads its side as 0 (the anchor's side, limited to 0.1 m).
    [TsukimiTest]
    public void OldStoredCalibrationsAreLimitedToTheRange()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(RestoreCalibration(CalibrationVersion, GloveBehaviour.HandRight, Overhand));
        Assert.IsFalse(HasCalibratedRelease());
        Assert.IsTrue(Vector3.Distance(TestP0(), RightUncalibrated) < 0.0001f);
        Assert.IsFalse(RestoreReleaseOffset(float.NaN, 0f, 0f), "a non-finite offset is ignored");
        Assert.IsFalse(RestoreReleaseOffset(0.54f, 0.05f, float.NaN));
        Assert.IsTrue(RestoreReleaseOffset(0.54f, 0.05f, 0f));
        Assert.IsTrue(Vector3.Distance(TestP0(), new Vector3(0.2f, 1.85f, 17.30f)) < 0.0001f, "no stored side " + TestP0());
        Assert.IsTrue(RestoreReleaseOffset(0.54f, 0.05f, 0.55f));
        Assert.IsTrue(Vector3.Distance(TestP0(), new Vector3(-0.25f, 1.85f, 17.30f)) < 0.0001f);
        Assert.IsTrue(RestoreCalibration(CalibrationVersion, GloveBehaviour.HandRight, Overhand));
        Assert.IsFalse(HasCalibratedRelease(), "restoring a calibration starts without an offset");
    }
}
