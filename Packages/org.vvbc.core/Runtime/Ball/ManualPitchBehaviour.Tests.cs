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
        if (!RecordCalibrationAt(TestChest, HandAt(TestChest, relative), IdealHand(TestChest), TestPlate)) return false;
        return CommitCalibration();
    }

    // A lock without the solver: aims straight at the target from the ideal release.
    private void LockForTest(Vector3 target)
    {
        Quaternion frame = TestFrame(TestChest);
        _lockTarget = target;
        _lockRelease = ManualPitchMath.FromFrame(frame, TestChest, _calPosition);
        _lockDirection = (target - _lockRelease).normalized;
        _lockSolverSpeedKmh = ManualPitchMath.ScaledSpeedKmh(maxPitchSpeedKmh, solverSpeedRatio);
        _lockConverged = true;
        _lockWakeLocal = ManualPitchMath.WakeDirection(wakeClockDegrees, ArmSide());
        _lockWakeClock = wakeClockDegrees;
        state = StateLockedReady;
    }

    private bool ReleaseForTest(Vector3 chest, Vector3 handOffset, Quaternion rotation, Vector3 armVelocity)
    {
        Vector3 hand = HandAt(chest, _calPosition) + ManualPitchMath.Rotate(TestFrame(chest), handOffset);
        return ResolveReleaseAt(chest, hand, rotation, hand, armVelocity, TestPlate, TestNormal, InputTrigger);
    }

    private Vector3 TowardPlate() { return Vector3.back * 20f; }

    // The earlier tuning (before the zero defaults for real-client verification): tests of the
    // penalty mechanics set it, so they show the penalties still work once configured.
    private void EarlierPenaltiesForTest()
    {
        controlGainX = 0.5f;
        controlGainY = 0.5f;
        spinLossAtBadPose = 0.5f;
        gyroAtBadPose = 0.5f;
        axisTransferGain = 0.5f;
        wakeLossAtBadPose = 1f;
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
        Quaternion stored = CalibratedRotation();
        _hasCalibration = false;
        Assert.IsTrue(RestoreCalibration(CalibrationVersion, GloveBehaviour.HandRight, Overhand, stored));
        Assert.IsTrue(ManualPitchMath.AngleBetween(stored, CalibratedRotation()) < 0.01f);
        Assert.IsTrue(ManualPitchMath.AngleBetween(TestPose, stored) < 0.01f, "rotation relative to the pitching frame");
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

    [TsukimiTest]
    public void MovingTheChestKeepsTheReleaseQuality()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Vector3 moved = TestChest + new Vector3(0.4f, -0.1f, -0.5f);
        Assert.IsTrue(ReleaseForTest(moved, Vector3.zero, IdealHand(moved), TowardPlate()));
        Assert.IsTrue(debugPositionError.magnitude < 0.0001f, "the ideal release moves with the chest");
        Assert.AreEqual(1f, debugPoseQuality);
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
    public void AnInvalidChestRecordsAndPitchesNothing()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        BeginCalibration();
        Assert.IsFalse(RecordCalibrationAt(Vector3.zero, HandAt(TestChest, Overhand), IdealHand(TestChest), TestPlate));
        Assert.IsFalse(RecordCalibrationAt(new Vector3(float.NaN, 1f, 1f), HandAt(TestChest, Overhand), IdealHand(TestChest), TestPlate));
        CancelCalibration();
        Assert.IsTrue(CalibrateForTest(Overhand));
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Vector3 hand = HandAt(TestChest, Overhand);
        Assert.IsFalse(ResolveReleaseAt(Vector3.zero, hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputGrip));
        Assert.AreEqual("invalid tracking", debugReason);
        state = StateTargeting;
        _rayValid = true;
        _hasCursor = true;
        Assert.IsFalse(BeginLockAt(Vector3.zero, TestPlate, TestNormal, Vector3.zero, TestGravity, 0.02f), "no lock without a chest");
    }

    [TsukimiTest]
    public void InvalidStoredCalibrationsAreNotRestored()
    {
        Quaternion good = TestPose;
        Assert.IsFalse(RestoreCalibration(CalibrationVersion, GloveBehaviour.HandRight, Overhand, new Quaternion(0f, 0f, 0f, 0f)));
        Assert.IsFalse(RestoreCalibration(CalibrationVersion, GloveBehaviour.HandRight, Overhand, new Quaternion(float.NaN, 0f, 0f, 1f)));
        Assert.IsFalse(RestoreCalibration(CalibrationVersion, GloveBehaviour.HandRight, Overhand, new Quaternion(0f, 0f, 0f, 2f)));
        Assert.IsFalse(RestoreCalibration(2, GloveBehaviour.HandRight, Overhand, good), "unknown version");
        Assert.IsFalse(RestoreCalibration(CalibrationVersion, 3, Overhand, good), "unknown hand");
        Assert.IsFalse(RestoreCalibration(CalibrationVersion, GloveBehaviour.HandRight, Vector3.zero, good), "at the chest");
        Assert.IsFalse(RestoreCalibration(CalibrationVersion, GloveBehaviour.HandRight, Vector3.up * 5f, good), "out of reach");
        Assert.IsFalse(HasCalibration());
        Assert.IsTrue(RestoreCalibration(CalibrationVersion, GloveBehaviour.HandLeft, Overhand, good));
    }

    // ---- 4-Seam axis ----

    private Vector3 MagnusOf(Vector3 relative)
    {
        // World Magnus force of the ideal 4-Seam thrown at the plate (-Z).
        Vector3 direction = Vector3.back;
        Vector3 spin = PitchSpin.ToWorld(ManualPitchMath.FourSeamSpin(ManualPitchMath.FourSeamAxis(relative), 2500f, 2800f, 0f, 0.5f, 0.5f), direction);
        return BallAerodynamics.Acceleration(direction * 38f, spin, 0f, 0.00063f);
    }

    [TsukimiTest]
    public void OverhandSidearmAndUnderhandBreakUpArmSideAndDown()
    {
        Vector3 over = MagnusOf(Overhand);
        Vector3 side = MagnusOf(new Vector3(0.6f, 0f, 0.2f));
        Vector3 under = MagnusOf(new Vector3(0.3f, -0.5f, 0.3f));
        Assert.IsTrue(over.y > 3f && over.x < 0f, "overhand: lift (and some arm-side run, world -X)");
        Assert.IsTrue(side.y * side.y < 0.0001f && side.x < -3f, "sidearm: toward the arm side (world -X)");
        Assert.IsTrue(under.y < -3f, "underhand: drop");
        Vector3 overSpin = ManualPitchMath.FourSeamAxis(Overhand);
        Assert.IsTrue(overSpin.x < -0.8f, "overhand axis is mostly backspin (-x)");
    }

    [TsukimiTest]
    public void LeftAndRightHandersMirror()
    {
        Vector3[] forms = { Overhand, new Vector3(0.6f, 0f, 0.2f), new Vector3(0.3f, -0.5f, 0.3f) };
        for (int i = 0; i < forms.Length; i++)
        {
            Vector3 right = MagnusOf(forms[i]);
            Vector3 left = MagnusOf(new Vector3(-forms[i].x, forms[i].y, forms[i].z));
            Assert.IsTrue(Mathf.Abs(right.x + left.x) < 0.0001f && Mathf.Abs(right.y - left.y) < 0.0001f, "form " + i);
        }
        Assert.IsTrue(Vector3.Distance(ManualPitchMath.WakeDirection(90f, 1f), -ManualPitchMath.WakeDirection(90f, -1f)) < 0.0001f);
    }

    // ---- Pose quality ----

    [TsukimiTest]
    public void TheCalibratedPoseHasFullQualityAndErrorsDegradeItContinuously()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        EarlierPenaltiesForTest();
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, IdealHand(TestChest), TowardPlate()));
        Assert.AreEqual(1f, debugPoseQuality);
        Assert.IsTrue(Mathf.Abs(debugSpinRpm - 2500f) < 0.5f);
        Assert.AreEqual(wakeCoefficient, debugWake);
        float previousQuality = 1f;
        float previousRpm = debugSpinRpm;
        for (int angle = 2; angle <= 60; angle += 2)
        {
            LockForTest(new Vector3(0f, 0.75f, 0f));
            // A 3D error mixing all three axes, never a single Euler angle.
            Quaternion error = ManualPitchMath.AxisAngle(new Vector3(0.6f, 0.5f, 0.62f), angle);
            Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, ManualPitchMath.Multiply(IdealHand(TestChest), error), TowardPlate()), "pose never fails recognition");
            Assert.IsTrue(debugPoseQuality <= previousQuality + 0.00001f && previousQuality - debugPoseQuality < 0.12f, "continuous at " + angle);
            Assert.IsTrue(debugSpinRpm <= previousRpm + 0.01f, "rpm never rises");
            previousQuality = debugPoseQuality;
            previousRpm = debugSpinRpm;
        }
        Assert.AreEqual(0f, debugPoseQuality, "past 50 degrees");
        Assert.IsTrue(Mathf.Abs(debugSpinRpm - 1250f) < 1f);
        Assert.AreEqual(0f, debugWake, "the wake is lost with the pose");
        Assert.AreEqual(BallAerodynamics.WakeNone, releaseWakeMode);
    }

    [TsukimiTest]
    public void ZeroSpinGivesNoNaNOrJump()
    {
        Vector3 axis = ManualPitchMath.FourSeamAxis(Overhand);
        Assert.AreEqual(Vector3.zero, ManualPitchMath.FourSeamSpin(axis, 0f, 2800f, 0.5f, 0.5f, 0.5f));
        Vector3 tiny = ManualPitchMath.FourSeamSpin(axis, 0.001f, 2800f, 0f, 0.5f, 0.5f);
        Assert.IsTrue(ManualPitchMath.FiniteVector(tiny) && tiny.magnitude < 0.001f);
        Assert.IsTrue(ManualPitchMath.FiniteVector(ManualPitchMath.FourSeamSpin(axis, float.NaN, 2800f, float.NaN, 0.5f, 0.5f)));
        Assert.IsTrue(Vector3.Distance(ManualPitchMath.FourSeamAxis(Vector3.zero), Vector3.left) < 0.0001f, "no slot reads as overhand");
        Assert.IsTrue(Vector3.Distance(ManualPitchMath.TransferAxis(axis, new Quaternion(float.NaN, 0f, 0f, 1f), 0.5f), axis) < 0.0001f);
        Vector3 capped = ManualPitchMath.FourSeamSpin(axis, 5000f, 2800f, 0f, 0.5f, 0.5f);
        Assert.IsTrue(Mathf.Abs(capped.magnitude * 60f / (2f * Mathf.PI) - 2800f) < 0.5f, "capped at the maximum");
    }

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
        Assert.IsFalse(BeginLockAt(TestChest, TestPlate, TestNormal, Vector3.zero, TestGravity, 0.02f), "no new lock without a ray");
        UpdateCursor(eye, (new Vector3(1f, 1.5f, 0f) - eye).normalized, TestPlate, TestNormal, 0f);
        Assert.AreEqual(smoothed, CursorCoordinates(), "dt 0 does not move it");
        Assert.IsTrue(BeginLockAt(TestChest, TestPlate, TestNormal, Vector3.zero, TestGravity, 0.02f));
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
        Assert.IsFalse(BeginLockAt(TestChest, TestPlate, TestNormal, Vector3.zero, TestGravity, 0.02f));
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
        Assert.IsFalse(ResolveRelease(Vector3.zero, null, InputTrigger));
        Assert.AreEqual(StateTargeting, state, "the release ends the lock");
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsFalse(ResolveRelease(Vector3.zero, null, InputGrip), "without arm tracking it is a throw");
        Assert.AreEqual(StateTargeting, state, "a pitch or a throw returns to targeting");
    }

    // ---- Recognition and control ----

    [TsukimiTest]
    public void RecognitionNeedsPositionArmSpeedAndDirectionButNotPose()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Quaternion ideal = IdealHand(TestChest);
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsFalse(ReleaseForTest(TestChest, new Vector3(0.7f, 0f, 0f), ideal, TowardPlate()));
        Assert.AreEqual("release position", debugReason);
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, new Vector3(0.4f, 0.3f, 0.3f), ideal, TowardPlate()), "inside the ellipsoid (E2 = 0.89)");
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsFalse(ReleaseForTest(TestChest, Vector3.zero, ideal, Vector3.back * 0.5f));
        Assert.AreEqual("arm speed", debugReason);
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsFalse(ReleaseForTest(TestChest, Vector3.zero, ideal, Vector3.left * 20f));
        Assert.AreEqual("arm direction", debugReason);
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, ManualPitchMath.Multiply(ideal, ManualPitchMath.AxisAngle(Vector3.right, 80f)), TowardPlate()), "a bad pose is still a pitch");
        Assert.AreEqual(0f, debugPoseQuality);
    }

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

    [TsukimiTest]
    public void PositionErrorsAddControlErrorInTheirOwnDirection()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Quaternion ideal = IdealHand(TestChest);
        Quaternion frame = TestFrame(TestChest);
        controlGainX = 1f;
        controlGainY = 1f;
        LockForTest(new Vector3(0f, 0.7f, 0f));
        Vector3 locked = LockedDirection();
        Assert.IsTrue(ReleaseForTest(TestChest, new Vector3(0.1f, -0.1f, 0f), ideal, TowardPlate()));
        Assert.IsTrue(Vector3.Distance(releaseVelocity.normalized, locked) < 0.00001f, "gain 1: only the natural offset");
        controlGainX = 2f;
        controlGainY = 2f;
        LockForTest(new Vector3(0f, 0.7f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, new Vector3(0.1f, 0f, 0f), ideal, TowardPlate()));
        Vector3 change = releaseVelocity.normalized - locked;
        Assert.IsTrue(Vector3.Dot(change, ManualPitchMath.Rotate(frame, Vector3.right)) > 0.004f, "lateral error moves the pitch further the same way");
        Assert.IsTrue(Mathf.Abs(change.y) < 0.0005f);
        LockForTest(new Vector3(0f, 0.7f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, new Vector3(0f, 0.1f, 0f), ideal, TowardPlate()));
        change = releaseVelocity.normalized - locked;
        Assert.IsTrue(change.y > 0.004f, "a high release goes higher");
        Assert.IsTrue(Mathf.Abs(Vector3.Dot(change, ManualPitchMath.Rotate(frame, Vector3.right))) < 0.0005f);
    }

    [TsukimiTest]
    public void PoseAndPositionErrorsAreIndependent()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Quaternion ideal = IdealHand(TestChest);
        LockForTest(new Vector3(0f, 0.7f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, new Vector3(0.1f, 0f, 0f), ideal, TowardPlate()));
        Vector3 positionOnly = releaseVelocity;
        LockForTest(new Vector3(0f, 0.7f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, new Vector3(0.1f, 0f, 0f), ManualPitchMath.Multiply(ideal, ManualPitchMath.AxisAngle(Vector3.forward, 35f)), TowardPlate()));
        Assert.IsTrue(Vector3.Distance(positionOnly, releaseVelocity) < 0.0001f, "the pose does not change the direction");
        Assert.IsTrue(Vector3.Distance(debugPositionError, new Vector3(0.1f, 0f, 0f)) < 0.00001f, "the pose does not change the position error");
        Assert.IsTrue(debugPoseQuality < 1f, "it changes the quality instead");
    }

    // A chest step between lock and release turns the solved direction toward the target
    // from the new ideal release, without solving again.
    [TsukimiTest]
    public void ChestMovementRotatesTheLockedDirectionTowardTheTarget()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Vector3 target = new Vector3(0f, 0.7f, 0f);
        LockForTest(target);
        Vector3 moved = TestChest + new Vector3(0.3f, 0f, -0.4f);
        Assert.IsTrue(ReleaseForTest(moved, Vector3.zero, IdealHand(moved), TowardPlate()));
        Vector3 from = HandAt(moved, Overhand);
        Assert.IsTrue(Vector3.Distance(releaseVelocity.normalized, (target - from).normalized) < 0.00001f, "straight aims stay exact");
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
        if (!BeginLockAt(TestChest, TestPlate, TestNormal, Vector3.zero, TestGravity, 0.02f)) return false;
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
            Assert.IsTrue(ResolveReleaseWithCurveOutputAt(TestChest, hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, solverSpeedRatio));
            Assert.AreEqual(BallAerodynamics.WakeFixed, releaseWakeMode, "the 4-Seam has its wake");
            Vector3 crossing = FlyToPlate(hand, releaseVelocity, releaseSpin, releaseWakeMode, releaseWakeCoefficient, releaseWakeDirection);
            Assert.IsTrue(Vector3.Distance(crossing, LockedTarget()) <= aimSolver.tolerance + 0.005f, "target " + t);
            // The same flight without the wake misses: the solver and the flight share it.
            Vector3 dry = FlyToPlate(hand, releaseVelocity, releaseSpin, BallAerodynamics.WakeNone, 0f, Vector3.zero);
            Assert.IsTrue(Vector3.Distance(dry, crossing) > 0.02f, "the wake matters " + t);
        }
    }

    // The body correction is not a new solve; its residual after a chest step is bounded.
    [TsukimiTest(maxSteps: 30000000)]
    public void TheBodyCorrectionResidualAfterAChestStepIsSmall()
    {
        Mimic.Join();
        _ball = strikeZone != null ? strikeZone.ball : null;
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Assert.IsTrue(SolveLock(new Vector3(0f, 0.72f, 0f)));
        Vector3 moved = TestChest + new Vector3(0.3f, -0.05f, -0.4f);
        Vector3 hand = HandAt(moved, Overhand);
        Assert.IsTrue(ResolveReleaseWithCurveOutputAt(moved, hand, IdealHand(moved), hand, TowardPlate(), TestPlate, TestNormal, InputGrip, solverSpeedRatio));
        Vector3 crossing = FlyToPlate(hand, releaseVelocity, releaseSpin, releaseWakeMode, releaseWakeCoefficient, releaseWakeDirection);
        float residual = Vector3.Distance(crossing, LockedTarget());
        Assert.IsTrue(residual < 0.05f, "residual " + residual + " (13.4 mm measured)");
        // Without the correction the same step misses by about the step itself.
        Vector3 uncorrected = FlyToPlate(hand, LockedDirection() * releaseVelocity.magnitude, releaseSpin,
            releaseWakeMode, releaseWakeCoefficient, releaseWakeDirection);
        Assert.IsTrue(Vector3.Distance(uncorrected, LockedTarget()) > 0.25f);
    }

    // Every client runs Update every frame; outside the mound it returns at once.
    [CostLimit(steps: 29, externs: 1)]
    [TsukimiTest]
    public void IdleUpdateOutsideTheMoundCost()
    {
        Update();
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
        _triggerHandRotation = TestFrame(TestChest);
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
        Assert.IsTrue(RecordCalibrationAt(TestChest, HandAt(TestChest, new Vector3(0.6f, 0f, 0.2f)), TestFrame(TestChest), TestPlate));
        Assert.IsTrue(CommitCalibration());
        Assert.IsFalse(Calibrating(), "one press records and ends calibration");
        Assert.IsTrue(Vector3.Distance(CalibratedPosition(), new Vector3(0.6f, 0f, 0.2f)) < 0.0001f, "position and rotation replaced together");
        Assert.IsTrue(ManualPitchMath.AngleBetween(CalibratedRotation(), Quaternion.identity) < 0.01f);
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
        Assert.IsFalse(ResolveRelease(Vector3.zero, null, InputGrip));
        Assert.IsTrue(CalibrationUsable(), "nor does a release");
    }

    // ---- Release feedback ----

    [TsukimiTest]
    public void TheReleaseMarkersRecordIdealAndActualPointsEvenForAThrow()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsFalse(ReleaseForTest(TestChest, new Vector3(0.7f, 0.1f, -0.05f), IdealHand(TestChest), TowardPlate()));
        Assert.IsTrue(_releaseHasPoints, "a rejected release still shows where it was");
        Assert.IsTrue(Vector3.Distance(_releaseIdealPoint, HandAt(TestChest, Overhand)) < 0.0001f);
        Assert.IsTrue(Vector3.Distance(_releaseActualPoint - _releaseIdealPoint,
            ManualPitchMath.Rotate(TestFrame(TestChest), new Vector3(0.7f, 0.1f, -0.05f))) < 0.0001f);
        Assert.IsTrue(_releaseRangeRatio > 1f, "outside the recognition range");
        string text = ReleaseResultText(false);
        Assert.IsTrue(text.StartsWith("THROW - not a pitch: release position"), text);
        string detail = ReleaseDetailText();
        Assert.IsTrue(detail.Contains("throwing controller") && detail.Contains("right +70  up +10  forward -5 cm"), detail);
        state = StateSolving;
        Assert.IsFalse(ReleaseForTest(TestChest, Vector3.zero, IdealHand(TestChest), TowardPlate()));
        Assert.AreEqual("not locked", debugReason);
        Assert.IsTrue(_releaseHasPoints, "an unlocked release is shown too");
        Assert.IsTrue(ReleaseDetailText().Contains("no locked target"));
    }

    // The points of the last release are world positions taken at the release: a later chest
    // move changes only the live ideal marker, and only the next release replaces them.
    [TsukimiTest]
    public void TheLastReleasePointsStayUntilTheNextRelease()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, new Vector3(0.1f, 0f, 0f), IdealHand(TestChest), TowardPlate()));
        Vector3 ideal = _releaseIdealPoint, actual = _releaseActualPoint;
        // Moving, recalibration-free targeting and a new lock leave them alone.
        UpdateCursor(new Vector3(0f, 1.6f, 18f), Vector3.back, TestPlate, TestNormal, 0.016f);
        LockForTest(new Vector3(0.5f, 1.2f, 0f));
        Assert.AreEqual(ideal, _releaseIdealPoint);
        Assert.AreEqual(actual, _releaseActualPoint);
        Vector3 moved = TestChest + new Vector3(0.5f, 0f, -0.3f);
        Assert.IsTrue(ReleaseForTest(moved, Vector3.zero, IdealHand(moved), TowardPlate()));
        Assert.IsTrue(Vector3.Distance(_releaseIdealPoint, HandAt(moved, Overhand)) < 0.0001f, "replaced by the next release");
        Assert.IsTrue(Vector3.Distance(_releaseActualPoint, _releaseIdealPoint) < 0.0001f);
    }

    // The ellipsoid is display only: recognition is the same with it on or off.
    [TsukimiTest]
    public void TheRecognitionRangeDisplayDoesNotChangeRecognition()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Assert.IsFalse(showRecognitionRange, "off by default");
        Vector3[] offsets = { new Vector3(0.4f, 0.3f, 0.3f), new Vector3(0.7f, 0f, 0f), new Vector3(0f, 0.56f, 0f) };
        for (int i = 0; i < offsets.Length; i++)
        {
            showRecognitionRange = false;
            LockForTest(new Vector3(0f, 0.75f, 0f));
            bool off = ReleaseForTest(TestChest, offsets[i], IdealHand(TestChest), TowardPlate());
            showRecognitionRange = true;
            LockForTest(new Vector3(0f, 0.75f, 0f));
            Assert.AreEqual(off, ReleaseForTest(TestChest, offsets[i], IdealHand(TestChest), TowardPlate()), "offset " + i);
        }
    }

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
        RecordLaunch(new Vector3(0f, 3f, -30f));
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
        controlGainX = 0.5f;
        controlGainY = 0.5f;
        LockForTest(new Vector3(0f, 0.75f, 0f));
        debugPredictedTarget = new Vector3(-0.02f, 0.76f, 0f);
        debugSolverError = 0.0224f;
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, ManualPitchMath.Multiply(IdealHand(TestChest), ManualPitchMath.AxisAngle(Vector3.up, 30f)), TowardPlate()));
        controlGainX = 1.7f;
        debugPredictedTarget = Vector3.zero;
        string detail = ReleaseDetailText();
        Assert.IsTrue(detail.Contains("pose error 30 deg") && detail.Contains("rpm") && detail.Contains("SSW 0.00"), detail);
        Assert.IsTrue(detail.Contains("control gain x 0.50  y 0.50"), "the gains of the release, not later edits: " + detail);
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

    [TsukimiTest]
    public void TheControlGainIsFiniteAndNeutralAtOne()
    {
        Vector3 d = new Vector3(0.05f, -0.08f, -1f).normalized;
        float[] gains = { 0f, 0.5f, 1f, 1.5f, 2f };
        for (int i = 0; i < gains.Length; i++)
        {
            Vector3 r = ManualPitchMath.ControlDirection(d, 0.1f, -0.08f, gains[i], gains[i], 17.5f);
            Assert.IsTrue(ManualPitchMath.FiniteVector(r) && Mathf.Abs(r.magnitude - 1f) < 0.0001f, "gain " + gains[i]);
        }
        Assert.IsTrue(Vector3.Distance(ManualPitchMath.ControlDirection(d, 0.1f, -0.08f, 1f, 1f, 17.5f), d) < 0.000001f);
        Assert.IsTrue(Vector3.Distance(ManualPitchMath.ControlDirection(d, 0.1f, -0.08f, 0f, 0f, 0f), d) < 0.000001f, "no distance: unchanged");
    }

    // Real-client verification defaults: no release-position or pose penalty. Recognition and
    // the diagnostic switch keep their values; the penalties are settings, not removed code.
    [TsukimiTest]
    public void ByDefaultThereIsNoPositionOrPosePenalty()
    {
        Assert.AreEqual(0f, controlGainX);
        Assert.AreEqual(0f, controlGainY);
        Assert.AreEqual(0f, spinLossAtBadPose);
        Assert.AreEqual(0f, gyroAtBadPose);
        Assert.AreEqual(0f, axisTransferGain);
        Assert.AreEqual(0f, wakeLossAtBadPose);
        Assert.IsFalse(diagnosticIdealSpin, "the diagnostic stays off");
        Assert.AreEqual(new Vector3(0.60f, 0.55f, 0.80f), recognitionRadii);
        Assert.AreEqual(1f, minArmSpeed);
        Assert.AreEqual(60f, maxDirectionErrorDegrees);
    }

    // With the zero defaults a bad pose is still measured (quality, pose error) but leaves the
    // spin, its axis and the SSW as calibrated; the earlier tuning brings the changes back.
    [TsukimiTest]
    public void ABadPoseIsMeasuredButChangesNothingUntilPenaltiesAreSet()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Quaternion ideal = IdealHand(TestChest);
        Quaternion bad = ManualPitchMath.Multiply(ideal, ManualPitchMath.AxisAngle(new Vector3(0.6f, 0.5f, 0.62f), 35f));
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, ideal, TowardPlate()));
        Vector3 idealSpin = releaseSpin;
        float idealWake = releaseWakeCoefficient;
        Vector3 idealWakeDirection = releaseWakeDirection;
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, bad, TowardPlate()));
        Assert.IsTrue(debugPoseQuality < 0.7f, "quality still measured: " + debugPoseQuality);
        Assert.IsTrue(debugPoseAngle > 30f, "pose error still measured: " + debugPoseAngle);
        Assert.IsTrue(Vector3.Distance(idealSpin, releaseSpin) < 0.001f, "no spin loss, gyro or axis change");
        Assert.IsTrue(Mathf.Abs(releaseSpin.magnitude * 60f / (2f * Mathf.PI) - 2500f) < 0.5f, "2500 rpm");
        Assert.AreEqual(idealWake, releaseWakeCoefficient, "no SSW loss");
        Assert.IsTrue(Vector3.Distance(idealWakeDirection, releaseWakeDirection) < 0.00001f);
        Assert.IsTrue(ReleaseDetailText().Contains("pose error"), "the panel still shows it");

        EarlierPenaltiesForTest();
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, bad, TowardPlate()));
        Assert.IsTrue(releaseSpin.magnitude < idealSpin.magnitude - 1f, "spin loss returns");
        Assert.IsTrue(Vector3.Angle(idealSpin, releaseSpin) > 1f, "axis change and gyro return");
        Assert.IsTrue(releaseWakeCoefficient < idealWake, "SSW loss returns");
    }

    // Each coefficient alone brings back only its own change.
    [TsukimiTest]
    public void EachPoseCoefficientActsAlone()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Quaternion ideal = IdealHand(TestChest);
        Quaternion bad = ManualPitchMath.Multiply(ideal, ManualPitchMath.AxisAngle(new Vector3(0.6f, 0.5f, 0.62f), 35f));
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, ideal, TowardPlate()));
        Vector3 idealSpin = releaseSpin;
        float idealWake = releaseWakeCoefficient;

        spinLossAtBadPose = 0.5f;
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, bad, TowardPlate()));
        Assert.IsTrue(releaseSpin.magnitude < idealSpin.magnitude - 1f, "spin loss");
        Assert.IsTrue(Vector3.Angle(idealSpin, releaseSpin) < 0.01f, "same axis");
        Assert.AreEqual(idealWake, releaseWakeCoefficient);
        spinLossAtBadPose = 0f;

        gyroAtBadPose = 0.5f;
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, bad, TowardPlate()));
        Assert.IsTrue(Vector3.Angle(idealSpin, releaseSpin) > 1f, "gyro turns the axis");
        Assert.IsTrue(Mathf.Abs(releaseSpin.magnitude - idealSpin.magnitude) < 0.01f, "same rpm");
        gyroAtBadPose = 0f;

        axisTransferGain = 0.5f;
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, bad, TowardPlate()));
        Assert.IsTrue(Vector3.Angle(idealSpin, releaseSpin) > 1f, "the pose turns the axis");
        Assert.IsTrue(Mathf.Abs(releaseSpin.magnitude - idealSpin.magnitude) < 0.01f, "same rpm");
        axisTransferGain = 0f;

        wakeLossAtBadPose = 1f;
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, Vector3.zero, bad, TowardPlate()));
        Assert.IsTrue(releaseWakeCoefficient < idealWake, "SSW loss");
        Assert.IsTrue(Vector3.Distance(idealSpin, releaseSpin) < 0.001f, "spin unchanged");
    }

    // Gain 0 cancels the controller offset's own miss in the launch direction; the offset is
    // still measured and shown.
    [TsukimiTest]
    public void AZeroGainKeepsTheReleaseOffsetMeasured()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        LockForTest(new Vector3(0f, 0.7f, 0f));
        Assert.IsTrue(ReleaseForTest(TestChest, new Vector3(0.1f, -0.08f, 0f), IdealHand(TestChest), TowardPlate()));
        Assert.IsTrue(Vector3.Distance(debugPositionError, new Vector3(0.1f, -0.08f, 0f)) < 0.00001f, "position error measured");
        Assert.IsTrue(_releaseRangeRatio > 0f, "share of the recognition range measured");
        string detail = ReleaseDetailText();
        Assert.IsTrue(detail.Contains("RELEASE POSITION") && detail.Contains("control gain x 0.00  y 0.00"), detail);
    }

    // With the solver and the shared flight model, and the pose's spin changes switched off,
    // a release offset (right, up) moves the crossing by about gain x offset for every gain in
    // 0..2, in its own direction (both signs).
    [TsukimiTest(maxSteps: 90000000)]
    public void TheCrossingMovesByTheGainTimesTheReleaseOffset()
    {
        Mimic.Join();
        _ball = strikeZone != null ? strikeZone.ball : null;
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        diagnosticIdealSpin = true;
        Assert.IsTrue(SolveLock(new Vector3(0f, 0.72f, 0f)));
        Vector3 hand = HandAt(TestChest, Overhand);
        Assert.IsTrue(ResolveReleaseWithCurveOutputAt(TestChest, hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, solverSpeedRatio));
        Vector3 reference = FlyToPlate(hand, releaseVelocity, releaseSpin, releaseWakeMode, releaseWakeCoefficient, releaseWakeDirection);
        Assert.IsTrue(Vector3.Distance(reference, LockedTarget()) <= aimSolver.tolerance + 0.005f, "solver vs actual");
        Assert.IsTrue(Vector3.Distance(reference, debugPredictedTarget) < 0.01f, "the flight follows the solver's own prediction");
        Vector3 lockDirection = _lockDirection;
        int lockEpoch = _lockEpoch;
        float[] gains = { 0f, 0.5f, 1f, 1.5f, 2f };
        Vector3[] offsets = { new Vector3(0.08f, 0.06f, 0f), new Vector3(-0.08f, -0.06f, 0f) };
        for (int s = 0; s < offsets.Length; s++)
            for (int g = 0; g < gains.Length; g++)
            {
                controlGainX = gains[g];
                controlGainY = gains[g];
                state = StateLockedReady;
                Vector3 at = hand + ManualPitchMath.Rotate(TestFrame(TestChest), offsets[s]);
                Assert.IsTrue(ResolveReleaseWithCurveOutputAt(TestChest, at, IdealHand(TestChest), at, TowardPlate(), TestPlate, TestNormal, InputTrigger, solverSpeedRatio));
                Vector2 miss = ViewOffset(FlyToPlate(at, releaseVelocity, releaseSpin, releaseWakeMode, releaseWakeCoefficient, releaseWakeDirection), reference);
                Vector2 expected = new Vector2(offsets[s].x, offsets[s].y) * gains[g];
                Assert.IsTrue((miss - expected).magnitude < 0.01f,
                    "gain " + gains[g] + " offset " + s + ": miss " + miss.ToString("F4") + " expected " + expected.ToString("F4"));
            }
        Assert.AreEqual(lockDirection, _lockDirection);
        Assert.AreEqual(lockEpoch, _lockEpoch);
    }

    // The pose changes spin, axis and SSW and so moves the crossing; the diagnostic setting
    // removes exactly that part.
    [TsukimiTest(maxSteps: 60000000)]
    public void TheDiagnosticIdealSpinRemovesThePoseMadeMiss()
    {
        Mimic.Join();
        _ball = strikeZone != null ? strikeZone.ball : null;
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        Assert.IsFalse(diagnosticIdealSpin, "off by default");
        EarlierPenaltiesForTest();
        Assert.IsTrue(SolveLock(new Vector3(0f, 0.72f, 0f)));
        Vector3 hand = HandAt(TestChest, Overhand);
        Quaternion bad = ManualPitchMath.Multiply(IdealHand(TestChest), ManualPitchMath.AxisAngle(new Vector3(0.6f, 0.5f, 0.62f), 35f));
        Assert.IsTrue(ResolveReleaseWithCurveOutputAt(TestChest, hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, solverSpeedRatio));
        Vector3 ideal = FlyToPlate(hand, releaseVelocity, releaseSpin, releaseWakeMode, releaseWakeCoefficient, releaseWakeDirection);
        state = StateLockedReady;
        Assert.IsTrue(ResolveReleaseWithCurveOutputAt(TestChest, hand, bad, hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, solverSpeedRatio));
        Assert.IsTrue(debugPoseQuality < 0.7f);
        Vector3 posed = FlyToPlate(hand, releaseVelocity, releaseSpin, releaseWakeMode, releaseWakeCoefficient, releaseWakeDirection);
        diagnosticIdealSpin = true;
        state = StateLockedReady;
        Assert.IsTrue(ResolveReleaseWithCurveOutputAt(TestChest, hand, bad, hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, solverSpeedRatio));
        Assert.AreEqual(1f, debugPoseQuality);
        Vector3 diagnosed = FlyToPlate(hand, releaseVelocity, releaseSpin, releaseWakeMode, releaseWakeCoefficient, releaseWakeDirection);
        Assert.IsTrue(Vector3.Distance(diagnosed, ideal) < 0.001f, "the pose no longer moves the pitch");
        Vector2 poseMiss = ViewOffset(posed, ideal);
        Assert.IsTrue(poseMiss.magnitude > 0.05f, "a 35 deg pose error moves the crossing: " + poseMiss.ToString("F3"));
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
            Assert.IsTrue(ResolveReleaseWithCurveOutputAt(TestChest, hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, solverSpeedRatio));
            Assert.IsTrue(releaseWakeDirection.y > 0.8f, "mostly upward " + c);
            Vector3 crossing = FlyToPlate(hand, releaseVelocity, releaseSpin, releaseWakeMode, releaseWakeCoefficient, releaseWakeDirection);
            Assert.IsTrue(Vector3.Distance(crossing, LockedTarget()) <= aimSolver.tolerance + 0.005f, "clock " + clocks[c]);
        }
    }

    // ---- Release speed (arm-speed curve, maximum and the solver's single speed) ----

    private bool CurveReleaseForTest(Vector3 handOffset, Quaternion rotation, Vector3 armVelocity, float curveOutput)
    {
        Vector3 hand = HandAt(TestChest, _calPosition) + ManualPitchMath.Rotate(TestFrame(TestChest), handOffset);
        return ResolveReleaseWithCurveOutputAt(TestChest, hand, rotation, hand, armVelocity, TestPlate, TestNormal, InputTrigger, curveOutput);
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
        // Position and pose errors change the direction and the spin, never the speed.
        LockForTest(target);
        Assert.IsTrue(CurveReleaseForTest(new Vector3(0.3f, -0.2f, 0.2f),
            ManualPitchMath.Multiply(ideal, ManualPitchMath.AxisAngle(Vector3.right, 40f)), TowardPlate(), 0.97f));
        Assert.IsTrue(debugPoseQuality < 1f && Vector3.Distance(releaseVelocity.normalized, first.normalized) > 0.001f);
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
        Assert.IsTrue(BeginLockAt(TestChest, TestPlate, TestNormal, Vector3.zero, TestGravity, 0.02f));
        Assert.IsTrue(Near(_lockSolverSpeedKmh, 135.8f, 0.001f), "solver speed " + _lockSolverSpeedKmh);
        maxPitchSpeedKmh = 90f;
        solverSpeedRatio = 0.5f;
        for (int i = 0; i < 200 && state == StateSolving; i++) AdvanceSolve();
        Assert.AreEqual(StateLockedReady, state);
        Assert.IsTrue(Near(_lockSolverSpeedKmh, 135.8f, 0.001f), "setting changes after the lock are not used");
        Vector3 lockDirection = _lockDirection;
        int iterations = aimSolver.iterations;
        Vector3 hand = HandAt(TestChest, Overhand);
        Assert.IsTrue(ResolveReleaseWithCurveOutputAt(TestChest, hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, 0.5f));
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
        controlGainX = 0f;
        controlGainY = 0f;
        Assert.IsTrue(SolveLock(new Vector3(0f, 0.72f, 0f)));
        Vector3 hand = HandAt(TestChest, Overhand);
        Assert.IsTrue(ResolveReleaseWithCurveOutputAt(TestChest, hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, solverSpeedRatio));
        Assert.AreEqual(Vector3.zero, _releaseBallOffset, "the ball centre is the ideal one");
        Vector3 matched = FlyToPlate(hand, releaseVelocity, releaseSpin, releaseWakeMode, releaseWakeCoefficient, releaseWakeDirection);
        Vector3 direction = releaseVelocity.normalized;
        Assert.IsTrue(Vector3.Distance(matched, debugPredictedTarget) < 0.001f, "the flight follows the solver's prediction: " + Vector3.Distance(matched, debugPredictedTarget));
        Assert.IsTrue(Vector3.Distance(matched, LockedTarget()) <= aimSolver.tolerance, "and arrives at the target");
        Assert.IsTrue(Vector3.Distance(matched, FlyToPlate(hand, releaseVelocity, releaseSpin, releaseWakeMode, releaseWakeCoefficient, releaseWakeDirection)) == 0f,
            "the same initial state flies the same way (as a receiver's catch-up does)");

        state = StateLockedReady;
        Assert.IsTrue(ResolveReleaseWithCurveOutputAt(TestChest, hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, 1f));
        Assert.IsTrue(Vector3.Distance(releaseVelocity.normalized, direction) < 0.000001f, "faster: same direction");
        Vector3 fast = FlyToPlate(hand, releaseVelocity, releaseSpin, releaseWakeMode, releaseWakeCoefficient, releaseWakeDirection);
        Assert.IsTrue(fast.y > matched.y + 0.02f, "faster arrives higher: " + (fast.y - matched.y));

        state = StateLockedReady;
        Assert.IsTrue(ResolveReleaseWithCurveOutputAt(TestChest, hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger, 95f / 140f));
        Assert.IsTrue(Vector3.Distance(releaseVelocity.normalized, direction) < 0.000001f, "slower: same direction");
        Vector3 slow = FlyToPlate(hand, releaseVelocity, releaseSpin, releaseWakeMode, releaseWakeCoefficient, releaseWakeDirection);
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
        RecordLaunch(releaseVelocity);
        Assert.IsTrue(releaseDetailText.Contains("delta -40.6 km/h"), "the launched speed is the release's: " + releaseDetailText);

        // A throw after a pitch shows none of the pitch's speed model.
        LockForTest(new Vector3(0f, 0.75f, 0f));
        Assert.IsFalse(CurveReleaseForTest(Vector3.zero, IdealHand(TestChest), Vector3.left * 20f, 1f));
        text = ReleaseDetailText();
        Assert.IsTrue(text.Contains("SPEED  not a pitch") && !text.Contains("delta") && !text.Contains("solver 135.8"), text);
    }
}
