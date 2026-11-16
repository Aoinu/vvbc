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
        _lockSpeed = 140f / 3.6f;
        _lockWakeLocal = ManualPitchMath.WakeDirection(wakeClockDegrees, ArmSide());
        state = StateLockedReady;
    }

    private bool ReleaseForTest(Vector3 chest, Vector3 handOffset, Quaternion rotation, Vector3 armVelocity)
    {
        Vector3 hand = HandAt(chest, _calPosition) + ManualPitchMath.Rotate(TestFrame(chest), handOffset);
        return ResolveReleaseAt(chest, hand, rotation, hand, armVelocity, TestPlate, TestNormal, InputTrigger);
    }

    private Vector3 TowardPlate() { return Vector3.back * 20f; }

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
            Assert.IsTrue(ResolveReleaseAt(TestChest, hand, IdealHand(TestChest), hand, TowardPlate(), TestPlate, TestNormal, InputTrigger));
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
        Assert.IsTrue(ResolveReleaseAt(moved, hand, IdealHand(moved), hand, TowardPlate(), TestPlate, TestNormal, InputGrip));
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

    // A press of `hand` captured with the throwing hand at `relative` (as taken at trigger down).
    private void PressWith(int hand, Vector3 relative)
    {
        _triggerHand = hand;
        _triggerCaptured = hand == _activeHand && _calibrating;
        _triggerChest = TestChest;
        _triggerHandPosition = HandAt(TestChest, relative);
        _triggerHandRotation = TestFrame(TestChest);
        _triggerPlate = TestPlate;
    }

    // Real-client failure: clicking a panel button with the glove-hand trigger recorded the
    // resting throwing hand as the release pose, and Ball -> Glove toggled the lock.
    [TsukimiTest]
    public void APanelClickNeitherRecordsACalibrationNorTogglesTheLock()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        BeginCalibration();
        PressWith(GloveBehaviour.HandRight, new Vector3(0.15f, -0.55f, 0.10f));
        CancelTriggerPress();
        ApplyTrigger();
        Assert.IsTrue(Calibrating(), "the click's press neither records nor ends calibration");
        Assert.IsTrue(Vector3.Distance(CalibratedPosition(), Overhand) < 0.0001f);
        CancelCalibration();
        state = StateLockedReady;
        PressWith(GloveBehaviour.HandLeft, Vector3.zero);
        CancelTriggerPress();
        ApplyTrigger();
        Assert.AreEqual(StateLockedReady, state, "a click keeps the lock");
        PressWith(GloveBehaviour.HandLeft, Vector3.zero);
        ApplyTrigger();
        Assert.AreEqual(StateTargeting, state, "a plain glove-hand press releases it");
    }

    // The glove hand never records; while calibrating it does not lock either.
    [TsukimiTest]
    public void OnlyTheEmptyThrowingHandRecordsTheCalibration()
    {
        Mimic.Join();
        ActiveForTest(GloveBehaviour.HandRight);
        Assert.IsTrue(CalibrateForTest(Overhand));
        BeginCalibration();
        PressWith(GloveBehaviour.HandLeft, new Vector3(0.6f, 0f, 0.2f));
        ApplyTrigger();
        Assert.IsTrue(Calibrating(), "glove hand: no record");
        Assert.AreEqual(StateTargeting, state, "and no lock while calibrating");
        // The throwing hand pressed while it held the ball: nothing was captured.
        PressWith(GloveBehaviour.HandRight, new Vector3(0.6f, 0f, 0.2f));
        _triggerCaptured = false;
        ApplyTrigger();
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
        Assert.IsFalse(ReleaseForTest(TestChest, new Vector3(0.7f, 0.1f, 0f), IdealHand(TestChest), TowardPlate()));
        Assert.IsTrue(_releaseHasPoints, "a rejected release still shows where it was");
        Assert.IsTrue(Vector3.Distance(_releaseIdealPoint, HandAt(TestChest, Overhand)) < 0.0001f);
        Assert.IsTrue(Vector3.Distance(_releaseActualPoint - _releaseIdealPoint,
            ManualPitchMath.Rotate(TestFrame(TestChest), new Vector3(0.7f, 0.1f, 0f))) < 0.0001f);
        Assert.IsTrue(_releaseRangeRatio > 1f, "outside the recognition range");
        string text = ReleaseResultText(false);
        Assert.IsTrue(text.StartsWith("THROW - not a pitch: release position"), text);
        Assert.IsTrue(text.Contains("right +70 cm") && text.Contains("up +10 cm") && text.Contains("forward +0 cm"), text);
        state = StateSolving;
        Assert.IsFalse(ReleaseForTest(TestChest, Vector3.zero, IdealHand(TestChest), TowardPlate()));
        Assert.AreEqual("not locked", debugReason);
        Assert.IsTrue(_releaseHasPoints, "an unlocked release is shown too");
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
        Assert.IsFalse(ArrivalStep(new Vector3(-0.05f, 1.0f, 1.0f), TestPlate, TestNormal));
        Assert.IsTrue(ArrivalStep(new Vector3(-0.15f, 0.6f, -1.0f), TestPlate, TestNormal));
        // Crossing at z = 0: x = -0.10, y = 0.80. The pitcher faces -Z, so world -X is their right.
        Assert.IsTrue(Mathf.Abs(arrivalError.x - 0.10f) < 0.0001f && Mathf.Abs(arrivalError.y - 0.05f) < 0.0001f);
        Assert.IsTrue(arrivalMeasured);
        string text = ReleaseResultText(true);
        Assert.IsTrue(text.StartsWith("PITCH (4-Seam)") && text.Contains("Arrival vs target  right +10 cm  up +5 cm"), text);
        Assert.IsTrue(text.Contains("Quality 100%"), text);
    }
}
