using Tsukimi;
using UnityEngine;

public partial class BallFlightBehaviour
{
    private BallResultSnapshot ResultExample()
    {
        BallResultSnapshot s = new BallResultSnapshot();
        s.contactPosition = new Vector3(0.3f, 0.1f, 0f);
        s.batCoreVelocity = new Vector3(2f, 1f, 5f);
        s.batAngularVelocity = new Vector3(1f, 2f, 3f);
        s.incomingVelocity = new Vector3(2f, 0f, -20f);
        s.axisX = Vector3.right;
        s.axisY = Vector3.down;
        s.normal = Vector3.back;
        s.centerDirection = Vector3.forward;
        s.worldUp = Vector3.up;
        s.normalizedY = 0.4f;
        s.quality = 0.8f;
        s.power = 0.7f;
        s.sprayScale = 1f;
        s.launchScale = 1f;
        return s;
    }

    [TsukimiTest]
    public void ResultAxisReversalPreservesPhysicalOutput()
    {
        BallResultSnapshot s = ResultExample();
        BallResult a = BallResultMath.Calculate(s);
        s.axisX = -s.axisX;
        s.axisY = -s.axisY;
        s.normalizedY = -s.normalizedY;
        BallResult b = BallResultMath.Calculate(s);
        Assert.IsTrue(a.valid && b.valid);
        Assert.IsTrue((a.linearVelocity - b.linearVelocity).magnitude < 0.001f);
        Assert.IsTrue((a.angularVelocity - b.angularVelocity).magnitude < 0.001f);
    }

    [TsukimiTest]
    public void ResultUsesWorldAxesInSpecifiedRotationOrder()
    {
        BallResultSnapshot s = ResultExample();
        BallResult actual = BallResultMath.Calculate(s);
        Vector3 reflected = s.incomingVelocity - 2f * Vector3.Dot(s.incomingVelocity, s.normal) * s.normal;
        float spray = Mathf.Atan2(Vector3.Dot(s.axisY, Vector3.Cross(s.centerDirection, reflected.normalized)),
            Vector3.Dot(s.centerDirection, reflected.normalized)) * Mathf.Rad2Deg;
        Vector3 localVelocity = s.batCoreVelocity + Vector3.Cross(s.batAngularVelocity, s.contactPosition - s.batCorePosition);
        float attack = Mathf.Atan2(localVelocity.y, new Vector2(localVelocity.x, localVelocity.z).magnitude) * Mathf.Rad2Deg;
        float launch = -38f * s.normalizedY - (12f + 0.35f * attack);
        float sr = spray * Mathf.Deg2Rad;
        float lr = launch * Mathf.Deg2Rad;
        Vector3 expected = new Vector3(-Mathf.Sin(sr), -Mathf.Sin(lr) * Mathf.Cos(sr), Mathf.Cos(lr) * Mathf.Cos(sr));
        Vector3 reversed = new Vector3(-Mathf.Sin(sr) * Mathf.Cos(lr), -Mathf.Sin(lr), Mathf.Cos(lr) * Mathf.Cos(sr));
        Assert.IsTrue(actual.valid);
        Assert.IsTrue((actual.linearVelocity.normalized - expected).magnitude < 0.001f);
        Assert.IsTrue((expected - reversed).magnitude > 0.001f);
    }

    [TsukimiTest]
    public void ResultRejectsNonFiniteInputsAndHandlesZeroIncomingVelocity()
    {
        BallResultSnapshot s = ResultExample();
        s.incomingVelocity = Vector3.zero;
        BallResult result = BallResultMath.Calculate(s);
        Assert.IsTrue(result.valid);
        Assert.IsTrue(result.linearVelocity.magnitude > 0f);
        s.power = float.NaN;
        Assert.IsFalse(BallResultMath.Calculate(s).valid);
    }

    private Vector3 MirrorResultVector(Vector3 v) { return new Vector3(-v.x, v.y, v.z); }

    [TsukimiTest]
    public void ResultMirrorsObliqueContactIncludingAxialSpin()
    {
        BallResultSnapshot s = ResultExample();
        s.axisX = new Vector3(0.8f, 0.5f, 0.2f).normalized;
        s.incomingVelocity = new Vector3(3f, -2f, -20f);
        s.axisY = Vector3.Cross(s.incomingVelocity, s.axisX).normalized;
        s.normal = Vector3.Cross(s.axisX, s.axisY).normalized;
        BallResult a = BallResultMath.Calculate(s);
        s.contactPosition = MirrorResultVector(s.contactPosition);
        s.batCorePosition = MirrorResultVector(s.batCorePosition);
        s.batCoreVelocity = MirrorResultVector(s.batCoreVelocity);
        s.batAngularVelocity = -MirrorResultVector(s.batAngularVelocity);
        s.incomingVelocity = MirrorResultVector(s.incomingVelocity);
        s.axisX = MirrorResultVector(s.axisX);
        s.axisY = -MirrorResultVector(s.axisY);
        s.normal = MirrorResultVector(s.normal);
        s.normalizedY = -s.normalizedY;
        BallResult b = BallResultMath.Calculate(s);
        Assert.IsTrue(a.valid && b.valid);
        Assert.IsTrue((MirrorResultVector(a.linearVelocity) - b.linearVelocity).magnitude < 0.001f);
        Assert.IsTrue((-MirrorResultVector(a.angularVelocity) - b.angularVelocity).magnitude < 0.001f);
    }

    [TsukimiTest]
    public void ResultScalesAndPowerAffectOnlyTheirSpecifiedOutputs()
    {
        BallResultSnapshot s = ResultExample();
        s.sprayScale = 0f;
        s.launchScale = 0f;
        BallResult a = BallResultMath.Calculate(s);
        Assert.IsTrue((a.linearVelocity.normalized - s.centerDirection).magnitude < 0.0001f);
        s.power = 0.2f;
        BallResult b = BallResultMath.Calculate(s);
        Assert.IsTrue(b.valid && b.linearVelocity.magnitude < a.linearVelocity.magnitude);
        Assert.IsTrue((a.linearVelocity.normalized - b.linearVelocity.normalized).magnitude < 0.0001f);
        Assert.IsTrue((a.angularVelocity - b.angularVelocity).magnitude < 0.0001f);
    }

    [TsukimiTest]
    public void ResultLaunchUsesFullContactOffsetForAttack()
    {
        BallResultSnapshot s = ResultExample();
        s.batCorePosition = Vector3.zero;
        s.batCoreVelocity = Vector3.forward * 4f;
        s.batAngularVelocity = Vector3.forward * 4f;
        s.contactPosition = new Vector3(0.3f, 0f, 0f);
        BallResult a = BallResultMath.Calculate(s);
        s.contactPosition.y = 0.5f;
        BallResult b = BallResultMath.Calculate(s);
        Assert.IsTrue(a.valid && b.valid);
        Assert.IsTrue((a.linearVelocity.normalized - b.linearVelocity.normalized).magnitude > 0.001f);
        Assert.IsTrue((a.angularVelocity - b.angularVelocity).magnitude < 0.0001f);
    }

    [TsukimiTest]
    public void ResultSpinTracksSignedPlaneOffsetAndBatAxis()
    {
        BallResultSnapshot s = ResultExample();
        s.normalizedY = 1f;
        BallResult a = BallResultMath.Calculate(s);
        s.normalizedY = -1f;
        BallResult b = BallResultMath.Calculate(s);
        Assert.IsTrue(a.valid && b.valid);
        Assert.IsTrue(Mathf.Abs(a.angularVelocity.magnitude - 2000f * 2f * Mathf.PI / 60f) < 0.001f);
        Assert.IsTrue((a.angularVelocity + b.angularVelocity).magnitude < 0.001f);
        Assert.IsTrue(Vector3.Cross(a.angularVelocity, s.axisX).magnitude < 0.0001f);
        Assert.IsTrue(a.linearVelocity.y > 0f && b.linearVelocity.y < 0f);
    }

    [TsukimiTest]
    public void ResultIndividualAngleScalesAreIndependent()
    {
        BallResultSnapshot s = ResultExample();
        s.sprayScale = 0f;
        BallResult launchOnly = BallResultMath.Calculate(s);
        s.launchScale = 0f;
        BallResult baseline = BallResultMath.Calculate(s);
        s.sprayScale = 1f;
        BallResult sprayOnly = BallResultMath.Calculate(s);
        Assert.IsTrue(launchOnly.valid && baseline.valid && sprayOnly.valid);
        Assert.IsTrue((baseline.linearVelocity.normalized - s.centerDirection).magnitude < 0.0001f);
        Assert.IsTrue((launchOnly.linearVelocity.normalized - baseline.linearVelocity.normalized).magnitude > 0.001f);
        Assert.IsTrue((sprayOnly.linearVelocity.normalized - baseline.linearVelocity.normalized).magnitude > 0.001f);
    }

    [TsukimiTest]
    public void ResultDegenerateSprayKeepsFiniteLaunch()
    {
        BallResultSnapshot s = ResultExample();
        s.axisX = Vector3.right;
        s.axisY = Vector3.forward;
        s.normal = Vector3.down;
        BallResult result = BallResultMath.Calculate(s);
        Assert.IsTrue(result.valid);
        Assert.IsTrue(Mathf.Abs(result.linearVelocity.magnitude - (8f + 37f * s.power * s.quality)) < 0.001f);
        Assert.IsTrue((result.linearVelocity.normalized - s.centerDirection).magnitude > 0.001f);
    }

    [TsukimiTest]
    public void ResultIsFrozenAndPowerReferenceDoesNotDependOnContactPosition()
    {
        BallResultSnapshot snapshot = ResultExample();
        BallResult before = BallResultMath.Calculate(snapshot);
        snapshot.contactPosition += Vector3.right * 0.3f;
        BallResult changedPosition = BallResultMath.Calculate(snapshot);
        Assert.IsTrue(before.valid && changedPosition.valid);
        Assert.IsTrue(Mathf.Abs(before.linearVelocity.magnitude - changedPosition.linearVelocity.magnitude) < 0.0001f);
        Assert.IsTrue(Mathf.Abs(before.linearVelocity.magnitude - (8f + 37f * 0.7f * 0.8f)) < 0.0001f);
    }

    [TsukimiTest]
    public void ResultClampsAttackAngleAndTreatsVerySlowContactAsZero()
    {
        BallResultSnapshot s = ResultExample();
        s.contactPosition = s.batCorePosition;
        s.batAngularVelocity = Vector3.zero;
        s.batCoreVelocity = new Vector3(0f, 10f, 10f);
        BallResult steep = BallResultMath.Calculate(s);
        s.batCoreVelocity = new Vector3(0f, 0.4663077f, 1f);
        BallResult capped = BallResultMath.Calculate(s);
        Assert.IsTrue((steep.linearVelocity - capped.linearVelocity).magnitude < 0.001f);
        s.batCoreVelocity = new Vector3(0f, -10f, 10f);
        BallResult steepDown = BallResultMath.Calculate(s);
        s.batCoreVelocity = new Vector3(0f, -0.4663077f, 1f);
        BallResult cappedDown = BallResultMath.Calculate(s);
        Assert.IsTrue((steepDown.linearVelocity - cappedDown.linearVelocity).magnitude < 0.001f);
        s.batCoreVelocity = Vector3.up * 0.05f;
        BallResult slow = BallResultMath.Calculate(s);
        s.batCoreVelocity = Vector3.zero;
        BallResult zero = BallResultMath.Calculate(s);
        Assert.IsTrue(slow.valid && zero.valid);
        Assert.IsTrue((slow.linearVelocity - zero.linearVelocity).magnitude < 0.0001f);
    }

    [TsukimiTest]
    public void ResultHandlesVerticalBatWithUnitDirectionAndSpinOnItsAxis()
    {
        BallResultSnapshot s = ResultExample();
        s.axisX = Vector3.up;
        s.axisY = Vector3.right;
        s.normal = Vector3.back;
        s.centerDirection = Vector3.forward;
        BallResult result = BallResultMath.Calculate(s);
        Assert.IsTrue(result.valid);
        Assert.IsTrue(Mathf.Abs(result.linearVelocity.normalized.magnitude - 1f) < 0.0001f);
        Assert.IsTrue(Vector3.Cross(result.angularVelocity, s.axisX).magnitude < 0.0001f);
    }

    [TsukimiTest]
    public void ResultMirrorsHorizontalBatAndSpin()
    {
        BallResultSnapshot s = ResultExample();
        BallResult first = BallResultMath.Calculate(s);
        s.contactPosition = MirrorResultVector(s.contactPosition);
        s.batCorePosition = MirrorResultVector(s.batCorePosition);
        s.batCoreVelocity = MirrorResultVector(s.batCoreVelocity);
        s.batAngularVelocity = -MirrorResultVector(s.batAngularVelocity);
        s.incomingVelocity = MirrorResultVector(s.incomingVelocity);
        s.axisX = MirrorResultVector(s.axisX);
        s.axisY = -MirrorResultVector(s.axisY);
        s.normal = MirrorResultVector(s.normal);
        s.normalizedY = -s.normalizedY;
        BallResult second = BallResultMath.Calculate(s);
        Assert.IsTrue(first.valid && second.valid);
        Assert.IsTrue((MirrorResultVector(first.linearVelocity) - second.linearVelocity).magnitude < 0.001f);
        Assert.IsTrue((-MirrorResultVector(first.angularVelocity) - second.angularVelocity).magnitude < 0.001f);
        Assert.IsTrue(Mathf.Abs(first.linearVelocity.y - second.linearVelocity.y) < 0.001f);
    }

    [TsukimiTest]
    public void ResultUsesCoreVelocityForTranslationAndCrossProductForRotation()
    {
        BallResultSnapshot s = ResultExample();
        s.sprayScale = 0f;
        s.normalizedY = 0f;
        s.batAngularVelocity = Vector3.zero;
        s.batCoreVelocity = new Vector3(0f, 1f, 4f);
        BallResult translation = BallResultMath.Calculate(s);
        float attack = Mathf.Atan2(1f, 4f) * Mathf.Rad2Deg;
        float launch = (-12f - 0.35f * attack) * Mathf.Deg2Rad;
        Vector3 expected = new Vector3(0f, -Mathf.Sin(launch), Mathf.Cos(launch));
        Assert.IsTrue((translation.linearVelocity.normalized - expected).magnitude < 0.0001f);
        s.contactPosition += Vector3.one;
        BallResult translatedElsewhere = BallResultMath.Calculate(s);
        Assert.IsTrue((translation.linearVelocity - translatedElsewhere.linearVelocity).magnitude < 0.0001f);
        s.contactPosition = new Vector3(0.3f, 0.1f, 0f);
        s.batCorePosition = Vector3.zero;
        s.batCoreVelocity = Vector3.zero;
        s.batAngularVelocity = Vector3.forward * 4f;
        BallResult rotation = BallResultMath.Calculate(s);
        launch = (-12f - 0.35f * 25f) * Mathf.Deg2Rad;
        expected = new Vector3(0f, -Mathf.Sin(launch), Mathf.Cos(launch));
        Assert.IsTrue(rotation.valid && (rotation.linearVelocity.normalized - expected).magnitude < 0.0001f);
    }
}
