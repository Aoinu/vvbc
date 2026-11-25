using Tsukimi;
using UnityEngine;

public partial class BatContactBehaviour
{
    [TsukimiTest]
    public void InvalidPowerSpeedsReturnZeroBeforeReadingCurve()
    {
        Assert.AreEqual(0f, EvaluateRawPower(0f));
        Assert.AreEqual(0f, EvaluateRawPower(-1f));
        Assert.AreEqual(0f, EvaluateRawPower(float.NaN));
        Assert.AreEqual(0f, EvaluateRawPower(float.PositiveInfinity));
        Assert.AreEqual(0f, EvaluateRawPower(float.NegativeInfinity));
    }

    [TsukimiTest]
    public void ShutdownDisablesContactWithOrWithoutHomePlate()
    {
        strikeZone = null;
        Assert.IsTrue(CanUseContact());
        _shutdown = true;
        Assert.IsFalse(CanUseContact());
    }

    [TsukimiTest]
    public void ContactPlaneUsesIncomingVelocityAndIsOrthonormal()
    {
        Vector3 x = new Vector3(1f, 0.4f, 0.2f).normalized;
        Vector3 velocity = new Vector3(3f, -2f, -20f);
        Assert.IsTrue(BuildPlane(x, velocity, Vector3.forward));
        Assert.IsTrue((_y - Vector3.Cross(velocity, x).normalized).magnitude < 0.0001f);
        Assert.IsTrue(Mathf.Abs(_axis.magnitude - 1f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(_y.magnitude - 1f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(Vector3.Dot(_axis, _y)) < 0.0001f);
        Assert.IsTrue((Vector3.Cross(_axis, _y) - _normal).magnitude < 0.0001f);
    }

    [TsukimiTest]
    public void FallbackPlaneReversesBothAxesForReversedBat()
    {
        Assert.IsTrue(BuildPlane(Vector3.right, Vector3.right, Vector3.forward));
        Vector3 y = _y; Vector3 normal = _normal;
        Assert.IsTrue(BuildPlane(Vector3.left, Vector3.left, Vector3.forward));
        Assert.IsTrue((_y + y).magnitude < 0.0001f);
        Assert.IsTrue((_normal - normal).magnitude < 0.0001f);
        Assert.IsFalse(BuildPlane(Vector3.zero, Vector3.back, Vector3.forward));
    }

    [TsukimiTest]
    public void TimingUsesOnlyHorizontalYawAndContactQualityUsesNormalizedPosition()
    {
        Assert.IsTrue(ValidTiming(new Vector3(1f, 2f, 0f), Vector3.forward, 30f));
        Assert.IsFalse(ValidTiming(Vector3.forward, Vector3.forward, 30f));
        float start = 0.1f / 0.855f, sweet = 0.68f / 0.855f;
        Assert.IsTrue(BatGeometry.ContactQuality(sweet, start, 1f, sweet, 0.25f) >
            BatGeometry.ContactQuality(start, start, 1f, sweet, 0.25f));
    }

    [TsukimiTest]
    public void FallbackProjectsPriorAxisAndRejectsNonFiniteValues()
    {
        _hasPreviousY = true;
        _previousY = new Vector3(1f, 1f, 0f).normalized;
        Assert.IsTrue(BuildPlane(Vector3.right, Vector3.right, Vector3.forward));
        Assert.IsTrue((_y - Vector3.up).magnitude < 0.0001f);
        Vector3 normal = _normal;
        _previousY = -_previousY;
        Assert.IsTrue(BuildPlane(Vector3.left, Vector3.left, Vector3.forward));
        Assert.IsTrue((_y - Vector3.down).magnitude < 0.0001f);
        Assert.IsTrue((_normal - normal).magnitude < 0.0001f);
        Assert.IsFalse(BuildPlane(new Vector3(float.NaN, 0f, 0f), Vector3.back, Vector3.forward));
        Assert.IsFalse(BuildPlane(Vector3.right, new Vector3(float.PositiveInfinity, 0f, 0f), Vector3.forward));
    }

    [TsukimiTest]
    public void QualityPeaksAtSweetSpotAndFallsLinearlyToBothEnds()
    {
        float start = 0.1f / 0.855f, sweet = 0.68f / 0.855f;
        Assert.IsTrue(Mathf.Abs(BatGeometry.ContactQuality(sweet, start, 1f, sweet, 0.25f) - 1f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(BatGeometry.ContactQuality(start, start, 1f, sweet, 0.25f) - 0.25f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(BatGeometry.ContactQuality(1f, start, 1f, sweet, 0.25f) - 0.25f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(BatGeometry.ContactQuality(0.39f / 0.855f, start, 1f, sweet, 0.25f) - 0.625f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(BatGeometry.ContactQuality(0.7675f / 0.855f, start, 1f, sweet, 0.25f) - 0.625f) < 0.0001f);
    }

    [TsukimiTest]
    public void ContactShapeAndQualityRemainConsistentForEitherWorldAxisDirection()
    {
        float localY = 0.32f, normalized = localY / 0.855f;
        float start = 0.1f / 0.855f, sweet = 0.68f / 0.855f;
        float physicalRadius = 0.01568f;
        float width = BatGeometry.ContactWidthFromRadius(physicalRadius, 0.0366f, 0f, 1f);
        float quality = BatGeometry.ContactQuality(normalized, start, 1f, sweet, 0.25f);
        float reverseWidth = BatGeometry.ContactWidthFromRadius(physicalRadius, 0.0366f, 0f, 1f);
        float reverseQuality = BatGeometry.ContactQuality(normalized, start, 1f, sweet, 0.25f);
        Assert.IsTrue(Mathf.Abs(reverseWidth - width) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(reverseQuality - quality) < 0.0001f);
        Assert.AreEqual(Mathf.Abs(0.04f) <= width, Mathf.Abs(-0.04f) <= reverseWidth);
    }

    [TsukimiTest]
    public void MissingSpinCurveFallsBackToQuadratic()
    {
        spinCurve = null;
        Assert.AreEqual(0f, EvaluateSpinRatio(0f));
        Assert.IsTrue(Mathf.Abs(EvaluateSpinRatio(0.5f) - 0.25f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(EvaluateSpinRatio(-0.75f) - 0.5625f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(EvaluateSpinRatio(2f) - 1f) < 0.0001f);
    }

    // ---- Bunt and Homing ----
    // AnimationCurve cannot be built in the test runner, so standard widths are
    // given explicitly for a 1 m Bat with radius 0.01 + 0.02 t and a 0.04 m Ball:
    // width(localY) = 0.05 + 0.02 * localY. Sweet spot 0.8, range 0.1..1.
    private float TestWidth(float localY) { return BatGeometry.ContactWidthFromRadius(0.01f + 0.02f * localY, 0.04f, 0f, 1f); }

    private void SetupAssist()
    {
        motion.buntEnabled = true;
        motion.buntPatternARawPowerThreshold = 0.2f;
        motion.buntUpperGripThreshold = 0.65f;
        motion.buntContactWidthMultiplier = 1.5f;
        motion.buntPowerCap = 0.3f;
        motion.buntPullThreshold = -0.1f;
        motion.homingEnabled = true;
        motion.homingRate = 0.5f;
        _candidatePattern = BattingAssist.BuntNone;
        _buntEntryState = BattingAssist.EntryUnknown;
    }

    [TsukimiTest]
    public void BuntPatternsAreIndependentAndCombine()
    {
        int inside = BattingAssist.EntrySweetSpotInside;
        Assert.AreEqual(BattingAssist.BuntPatternA, BattingAssist.BuntPattern(inside, 0.1f, 0.2f, float.NaN, 0.65f));
        Assert.AreEqual(BattingAssist.BuntPatternB, BattingAssist.BuntPattern(BattingAssist.EntryUnknown, 0.9f, 0.2f, 0.7f, 0.65f));
        Assert.AreEqual(BattingAssist.BuntPatternBoth, BattingAssist.BuntPattern(inside, 0.1f, 0.2f, 0.7f, 0.65f));
        Assert.AreEqual(BattingAssist.BuntNone, BattingAssist.BuntPattern(inside, 0.5f, 0.2f, 0.5f, 0.65f));
    }

    [TsukimiTest]
    public void BuntPatternARequiresWaitingSweetSpotAndLowRawPower()
    {
        // Set up after the Ball entered: the sweet spot was outside just before entry.
        Assert.AreEqual(BattingAssist.BuntNone, BattingAssist.BuntPattern(BattingAssist.EntrySweetSpotOutside, 0.05f, 0.2f, float.NaN, 0.65f));
        Assert.AreEqual(BattingAssist.BuntNone, BattingAssist.BuntPattern(BattingAssist.EntryHistoryMissing, 0.05f, 0.2f, float.NaN, 0.65f));
        Assert.AreEqual(BattingAssist.BuntNone, BattingAssist.BuntPattern(BattingAssist.EntryUnknown, 0.05f, 0.2f, float.NaN, 0.65f));
        Assert.AreEqual(BattingAssist.BuntNone, BattingAssist.BuntPattern(BattingAssist.EntrySweetSpotInside, 0.2f, 0.2f, float.NaN, 0.65f), "raw at threshold");
        Assert.AreEqual(BattingAssist.BuntPatternA, BattingAssist.BuntPattern(BattingAssist.EntrySweetSpotInside, 0.199f, 0.2f, float.NaN, 0.65f));
    }

    [TsukimiTest]
    public void BuntPatternBUsesUpperGripThresholdInclusively()
    {
        // Lower grip at 0.1 m on a 1 m Bat: 0.55 m between the hands reaches 65%.
        float at = BattingAssist.UpperGripFraction(0.1f, 0.55f, 1f);
        float below = BattingAssist.UpperGripFraction(0.1f, 0.549f, 1f);
        Assert.IsTrue(Mathf.Abs(at - 0.65f) < 0.00001f);
        Assert.AreEqual(BattingAssist.BuntPatternB, BattingAssist.BuntPattern(BattingAssist.EntryUnknown, 1f, 0.2f, 0.65f, 0.65f));
        Assert.AreEqual(BattingAssist.BuntNone, BattingAssist.BuntPattern(BattingAssist.EntryUnknown, 1f, 0.2f, below, 0.65f), "64.9%");
        // Lock Grip and one-hand poses record no two-hand grip (NaN).
        Assert.AreEqual(BattingAssist.BuntNone, BattingAssist.BuntPattern(BattingAssist.EntryUnknown, 1f, 0.2f, float.NaN, 0.65f));
        Assert.IsTrue(float.IsNaN(BattingAssist.UpperGripFraction(0.1f, -1f, 1f)));
        Assert.IsTrue(float.IsNaN(BattingAssist.UpperGripFraction(0.1f, 0.5f, 0f)));
    }

    [TsukimiTest]
    public void BuntEntryStateIsClearedPerPitchAndOnHistoryReset()
    {
        _buntEntryState = BattingAssist.EntrySweetSpotInside;
        _hasLastOutside = true; _lastOutsideTime = 3f;
        ResetBuntEntry();
        Assert.AreEqual(BattingAssist.EntryUnknown, _buntEntryState);
        Assert.IsFalse(_hasLastOutside);
        // Before entry a reset only forgets the outside time; after entry the evidence is gone.
        _hasLastOutside = true; _enteredZone = false;
        OnBuntHistoryReset();
        Assert.AreEqual(BattingAssist.EntryUnknown, _buntEntryState);
        Assert.IsFalse(_hasLastOutside);
        _buntEntryState = BattingAssist.EntrySweetSpotInside; _enteredZone = true;
        OnBuntHistoryReset();
        Assert.AreEqual(BattingAssist.EntryHistoryMissing, _buntEntryState);
        // No outside sample before the inside one: the entry cannot be located.
        ResetBuntEntry();
        ResolveZoneEntry(1f);
        Assert.AreEqual(BattingAssist.EntryHistoryMissing, _buntEntryState);
        ResetBuntEntry();
        _hasLastOutside = true; _lastOutsideTime = 1f;
        ResolveZoneEntry(1f);
        Assert.AreEqual(BattingAssist.EntryHistoryMissing, _buntEntryState);
    }

    [TsukimiTest]
    public void BuntWidensTheWholeStandardWidth()
    {
        float normal = TestWidth(0.8f);
        Assert.IsTrue(Mathf.Abs(normal - 0.066f) < 0.00001f);
        _candidateY = 0.06f;
        Assert.IsTrue(WithinContactWidth(normal, 1f), "standard width");
        Assert.IsTrue(WithinContactWidth(normal, 1.5f));
        // Between 1.0x and 1.5x: a miss for a swing, contact for a Bunt.
        _candidateY = 0.09f;
        Assert.IsFalse(WithinContactWidth(normal, 1f));
        Assert.IsTrue(WithinContactWidth(normal, 1.5f));
        Assert.IsTrue(Mathf.Abs(_candidateWidth - 0.099f) < 0.00001f);
        Assert.AreEqual(normal, _candidateNormalWidth);
        _candidateY = -0.098f;
        Assert.IsTrue(WithinContactWidth(normal, 1.5f));
        // Beyond 1.5x.
        _candidateY = 0.1f;
        Assert.IsFalse(WithinContactWidth(normal, 1.5f));
        // An invalid standard width (outside the curve, bad settings) stays a miss.
        _candidateY = 0f;
        Assert.IsFalse(WithinContactWidth(0f, 1.5f));
        Assert.IsFalse(WithinContactWidth(normal, float.NaN));
    }

    [TsukimiTest]
    public void BuntKeepsPhysicalCoordinatesSoRangeAndQualityAreUnchanged()
    {
        SetupAssist();
        _buntEntryState = BattingAssist.EntrySweetSpotInside;
        ClassifyCandidate(1f, 0.05f, 0.07f, 0.1f, 0.04f);
        Assert.AreEqual(BattingAssist.BuntPatternA, _candidatePattern);
        Assert.AreEqual(0f, _candidateHomingRate);
        // Range and quality read the same Bat position as an uncorrected swing.
        Assert.AreEqual(0.05f, _candidateX);
        Assert.AreEqual(0.07f, _candidateY);
        Assert.AreEqual(motion.ContactPlaneDistanceToLocalY(0.05f), _candidateLocalY);
    }

    [TsukimiTest]
    public void BuntPowerCombinesCapsAndPullingGivesZero()
    {
        SetupAssist();
        Vector3 center = Vector3.forward;
        _coreVelocity = center * 2f; _batOmega = Vector3.zero;
        Assert.IsTrue(Mathf.Abs(BuntPower(0.8f, Vector3.right, center) - 0.3f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(BuntPower(Mathf.Min(0.8f, 0.2f), Vector3.right, center) - 0.2f) < 0.00001f, "existing cap is stricter");
        Assert.IsTrue(Mathf.Abs(BuntPower(0.1f, Vector3.right, center) - 0.1f) < 0.00001f);
        Assert.IsFalse(_candidatePulled);
        // Small backward tracking noise is tolerated.
        _coreVelocity = center * -0.05f;
        Assert.IsTrue(Mathf.Abs(BuntPower(0.8f, Vector3.right, center) - 0.3f) < 0.00001f);
        Assert.IsFalse(_candidatePulled);
        _coreVelocity = center * -0.5f;
        Assert.AreEqual(0f, BuntPower(0.8f, Vector3.right, center));
        Assert.IsTrue(_candidatePulled);
        // Rotation alone pulls the contact point back: omega +Y at offset +X moves it toward -Z.
        _coreVelocity = Vector3.zero; _batOmega = Vector3.up;
        Assert.AreEqual(0f, BuntPower(0.8f, Vector3.right, center));
        Assert.IsTrue(Mathf.Abs(_candidateForwardSpeed + 1f) < 0.00001f);
        _batOmega = Vector3.down;
        Assert.IsTrue(Mathf.Abs(BuntPower(0.8f, Vector3.right, center) - 0.3f) < 0.00001f);
    }

    [TsukimiTest]
    public void ZeroPowerContactKeepsTheSharedMinimumExitSpeed()
    {
        BallResultSnapshot s = new BallResultSnapshot();
        s.contactPosition = new Vector3(0f, 1f, 0.8f); s.batCorePosition = new Vector3(0f, 1f, 0f);
        s.batCoreVelocity = Vector3.back * 0.5f; s.batAngularVelocity = Vector3.zero;
        s.incomingVelocity = Vector3.back * 30f;
        s.axisX = Vector3.forward; s.axisY = Vector3.down; s.normal = Vector3.Cross(s.axisX, s.axisY);
        s.centerDirection = Vector3.right; s.worldUp = Vector3.up;
        s.normalizedY = 0f; s.quality = 1f; s.power = 0f;
        s.sprayScale = 1f; s.launchScale = 1f; s.maxSpinRpm = 2000f; s.spinRatio = 0f;
        BallResult r = BallResultMath.Calculate(s);
        Assert.IsTrue(r.valid);
        Assert.IsTrue(Mathf.Abs(r.linearVelocity.magnitude - 8f) < 0.0001f);
        s.power = 0.3f;
        Assert.IsTrue(Mathf.Abs(BallResultMath.Calculate(s).linearVelocity.magnitude - (8f + 37f * 0.3f)) < 0.001f);
    }

    [TsukimiTest]
    public void HomingMovesTowardSweetSpotAndPlaneCentre()
    {
        Assert.IsTrue(Mathf.Abs(BattingAssist.HomingX(0.4f, 0.8f, 0.25f) - 0.5f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(BattingAssist.HomingX(0.4f, 0.8f, 0.5f) - 0.6f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(BattingAssist.HomingX(0.4f, 0.8f, 0.75f) - 0.7f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(BattingAssist.HomingX(0.9f, 0.8f, 0.5f) - 0.85f) < 0.00001f, "from the tip side too");
        Assert.AreEqual(0.4f, BattingAssist.HomingX(0.4f, 0.8f, 0f));
        Assert.IsTrue(Mathf.Abs(BattingAssist.HomingY(0.08f, 0.25f) - 0.06f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(BattingAssist.HomingY(-0.08f, 0.5f) + 0.04f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(BattingAssist.HomingY(0.08f, 0.75f) - 0.02f) < 0.00001f);
    }

    [TsukimiTest]
    public void HomingOffKeepsTheExistingCoordinatesExactly()
    {
        SetupAssist();
        _ballPosition = new Vector3(0.3f, 1.1f, 0.2f);
        ApplyHoming(0.37f, 0.051f, 0.8f, 0f);
        Assert.AreEqual(0.37f, _candidateX);
        Assert.AreEqual(0.051f, _candidateY);
        Assert.AreEqual(0f, _candidateHomingRate);
        Assert.IsTrue(CalculationContactPosition(0.37f, 0.051f) == _ballPosition);
        Assert.IsTrue(WithinContactWidth(TestWidth(0.37f), 1f));
        Assert.AreEqual(TestWidth(0.37f), _candidateWidth);
        motion.homingEnabled = false;
        Assert.AreEqual(0f, motion.EffectiveHomingRate());
        // Disabled Homing reaches the plane resolution as rate 0.
        ClassifyCandidate(1f, 0.37f, 0.051f, 0.9f, 0.04f);
        Assert.AreEqual(0f, _candidateHomingRate);
        Assert.AreEqual(0.37f, _candidateX);
    }

    [TsukimiTest]
    public void HomingTurnsAMissIntoContactAndKeepsTheNormal()
    {
        // Without Homing: width at 0.3 is 0.056, so Y 0.08 misses.
        ApplyHoming(0.3f, 0.08f, 0.8f, 0f);
        Assert.IsFalse(WithinContactWidth(TestWidth(_candidateX), 1f));
        ApplyHoming(0.3f, 0.08f, 0.8f, 0.5f);
        Assert.IsTrue(Mathf.Abs(_candidateX - 0.55f) < 0.00001f);
        Assert.IsTrue(Mathf.Abs(_candidateY - 0.04f) < 0.00001f);
        Assert.IsTrue(WithinContactWidth(TestWidth(_candidateX), 1f));
        _axis = Vector3.up; _y = Vector3.right; _normal = Vector3.Cross(_axis, _y);
        _core = new Vector3(1f, 0f, 2f);
        _ballPosition = _core + _axis * 0.3f + _y * 0.08f + _normal * 0.013f;
        Vector3 d = CalculationContactPosition(0.3f, 0.08f) - _core;
        Assert.IsTrue(Mathf.Abs(Vector3.Dot(d, _axis) - 0.55f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(Vector3.Dot(d, _y) - 0.04f) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(Vector3.Dot(d, _normal) - 0.013f) < 0.0001f, "normal coordinate unchanged");
        // An already valid contact is corrected too, and quality rises toward the sweet spot.
        ApplyHoming(0.5f, 0.02f, 0.8f, 0.5f);
        Assert.IsTrue(Mathf.Abs(_candidateX - 0.65f) < 0.00001f);
        Assert.IsTrue(BatGeometry.ContactQuality(_candidateX, 0.1f, 1f, 0.8f, 0.25f) >
            BatGeometry.ContactQuality(0.5f, 0.1f, 1f, 0.8f, 0.25f));
    }

    [TsukimiTest]
    public void HomingReducesLaunchAndSpinThroughTheSharedResult()
    {
        BallResultSnapshot s = new BallResultSnapshot();
        s.contactPosition = new Vector3(0f, 1f, 0.8f); s.batCorePosition = new Vector3(0f, 1f, 0f);
        s.batCoreVelocity = Vector3.right * 20f; s.batAngularVelocity = Vector3.zero;
        s.incomingVelocity = Vector3.left * 30f;
        s.axisX = Vector3.forward; s.axisY = Vector3.down; s.normal = Vector3.Cross(s.axisX, s.axisY);
        s.centerDirection = Vector3.right; s.worldUp = Vector3.up;
        s.quality = 1f; s.power = 0.8f; s.sprayScale = 1f; s.launchScale = 1f; s.maxSpinRpm = 2000f;
        spinCurve = null;
        float rawY = 0.8f, correctedY = BattingAssist.HomingY(rawY, 0.5f);
        s.normalizedY = rawY; s.spinRatio = EvaluateSpinRatio(rawY);
        BallResult raw = BallResultMath.Calculate(s);
        s.normalizedY = correctedY; s.spinRatio = EvaluateSpinRatio(correctedY);
        BallResult corrected = BallResultMath.Calculate(s);
        Assert.IsTrue(raw.valid && corrected.valid);
        Assert.IsTrue(corrected.angularVelocity.magnitude < raw.angularVelocity.magnitude);
        Assert.IsTrue(Mathf.Abs(corrected.linearVelocity.y) < Mathf.Abs(raw.linearVelocity.y), "flatter launch");
    }

    [TsukimiTest]
    public void BuntDisablesHomingAndANormalSwingUsesIt()
    {
        SetupAssist();
        _buntEntryState = BattingAssist.EntrySweetSpotInside;
        ClassifyCandidate(1f, 0.3f, 0.07f, 0.1f, 0.04f);
        Assert.AreEqual(BattingAssist.BuntPatternA, _candidatePattern);
        Assert.AreEqual(0f, _candidateHomingRate);
        Assert.AreEqual(0.3f, _candidateX);
        // Same position at full raw Power: a normal swing with Homing.
        ClassifyCandidate(1f, 0.3f, 0.07f, 0.9f, 0.04f);
        Assert.AreEqual(BattingAssist.BuntNone, _candidatePattern);
        Assert.AreEqual(0.5f, _candidateHomingRate);
        // Bunt disabled: never a Bunt.
        motion.buntEnabled = false;
        ClassifyCandidate(1f, 0.3f, 0.07f, 0.1f, 0.04f);
        Assert.AreEqual(BattingAssist.BuntNone, _candidatePattern);
        Assert.AreEqual(0.5f, _candidateHomingRate);
    }

}
