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
}
