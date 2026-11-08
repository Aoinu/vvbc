using UnityEngine;

public partial class BatMotionBehaviour
{
    [Header("Bat Geometry")]
    public float batLength = 0.855f;
    public AnimationCurve radiusCurve;
    public float sweetSpotNormalized = 0.680f / 0.855f;
    public float contactStartNormalized = 0.100f / 0.855f;
    public float contactEndNormalized = 1f;
    public float contactMargin;

    [Header("Batting")]
    public AnimationCurve powerCurve;
    public float minimumQuality = 0.25f;
    public float timingYawDegrees = 30f;

    public float SweetSpotLocalY() { return BatGeometry.NormalizedToLocalY(batLength, sweetSpotNormalized); }
    public float ContactStartLocalY() { return BatGeometry.NormalizedToLocalY(batLength, contactStartNormalized); }
    public float ContactEndLocalY() { return BatGeometry.NormalizedToLocalY(batLength, contactEndNormalized); }

    public float ContactPlaneDistanceToLocalY(float distance)
    {
        float scale = body != null ? Mathf.Abs(body.transform.lossyScale.y) : 1f;
        if (!BatGeometry.Finite(distance) || !BatGeometry.Finite(scale) || scale <= 0.000001f) return float.NaN;
        return distance / scale;
    }

    public float PlaneDistanceFromLocalY(float localY)
    {
        float scale = body != null ? Mathf.Abs(body.transform.lossyScale.y) : 1f;
        if (!BatGeometry.Finite(localY) || !BatGeometry.Finite(scale) || scale <= 0.000001f) return float.NaN;
        return localY * scale;
    }

    public float ContactWidthAtLocalY(float localY, float ballRadiusWorld)
    {
        if (!GeometrySettingsValid() || !BatGeometry.Finite(localY) ||
            !BatGeometry.Finite(ballRadiusWorld) || ballRadiusWorld < 0f) return 0f;
        float radialScale = 1f;
        if (body != null)
        {
            Vector3 scale = body.transform.lossyScale;
            radialScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            if (!BatGeometry.Finite(radialScale) || radialScale <= 0.000001f) return 0f;
        }
        return BatGeometry.ContactWidth(radiusCurve, batLength, localY, ballRadiusWorld, contactMargin, radialScale);
    }

    // A feedback Scan is synchronous. Snapshot validated inputs once for its
    // samples; direct field and in-place curve changes are observed next Scan.
    private bool _feedbackGeometryValid;
    private float _feedbackLength, _feedbackStartY, _feedbackEndY, _feedbackMargin;
    private float _feedbackAxisScale, _feedbackRadialScale, _feedbackBallRadius;
    private AnimationCurve _feedbackRadiusCurve, _feedbackCurveSnapshot;
    private bool _feedbackShapeKnown;
    private int _feedbackGeometryRevision;

    public int FeedbackGeometryRevision() { return _feedbackGeometryRevision; }

    public void PrepareFeedbackGeometry(float ballRadiusWorld)
    {
        _feedbackGeometryValid = false;
        if (!GeometrySettingsValid() || !BatGeometry.Finite(ballRadiusWorld) || ballRadiusWorld < 0f) return;
        Vector3 scale = body != null ? body.transform.lossyScale : Vector3.one;
        float axial = Mathf.Abs(scale.y);
        float radial = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
        if (!BatGeometry.Finite(axial) || axial <= 0.000001f ||
            !BatGeometry.Finite(radial) || radial <= 0.000001f) return;
        float startY = ContactStartLocalY(), endY = ContactEndLocalY();
        bool curveChanged = _feedbackCurveSnapshot == null ||
            !radiusCurve.Equals(_feedbackCurveSnapshot) ||
            radiusCurve.preWrapMode != _feedbackCurveSnapshot.preWrapMode ||
            radiusCurve.postWrapMode != _feedbackCurveSnapshot.postWrapMode;
        if (!_feedbackShapeKnown || curveChanged || _feedbackLength != batLength || _feedbackStartY != startY ||
            _feedbackEndY != endY || _feedbackMargin != contactMargin || _feedbackAxisScale != axial ||
            _feedbackRadialScale != radial || _feedbackBallRadius != ballRadiusWorld)
        {
            if (curveChanged) _feedbackCurveSnapshot = CloneCurve(radiusCurve);
            _feedbackShapeKnown = true;
            _feedbackGeometryRevision++;
        }
        _feedbackLength = batLength; _feedbackRadiusCurve = radiusCurve;
        _feedbackStartY = startY; _feedbackEndY = endY;
        _feedbackMargin = contactMargin; _feedbackAxisScale = axial;
        _feedbackRadialScale = radial; _feedbackBallRadius = ballRadiusWorld;
        _feedbackGeometryValid = true;
    }

    public float FeedbackSampleWidth(float planeDistance)
    {
        if (!_feedbackGeometryValid || !BatGeometry.Finite(planeDistance)) return 0f;
        float localY = planeDistance / _feedbackAxisScale;
        if (!BatGeometry.Finite(localY)) return 0f;
        localY = Mathf.Clamp(localY, _feedbackStartY, _feedbackEndY);
        return FeedbackLocalWidth(localY);
    }

    // Contour vertices must retain the original x / axialScale rounding; unlike
    // a candidate position they are not clamped to the contact range first.
    public float FeedbackOutlineWidth(float planeDistance)
    {
        if (!_feedbackGeometryValid || !BatGeometry.Finite(planeDistance)) return 0f;
        float localY = planeDistance / _feedbackAxisScale;
        if (!BatGeometry.Finite(localY)) return 0f;
        return FeedbackLocalWidth(localY);
    }

    private float FeedbackLocalWidth(float localY)
    {
        float normalized = Mathf.Clamp01(localY / _feedbackLength);
        float radius = _feedbackRadiusCurve.Evaluate(normalized);
        if (!BatGeometry.Finite(radius)) return 0f;
        return Mathf.Max(0f, radius) * _feedbackRadialScale + _feedbackBallRadius + _feedbackMargin;
    }

    public bool IsInsideContactRange(float localY)
    {
        if (!GeometrySettingsValid() || !BatGeometry.Finite(localY)) return false;
        return localY >= ContactStartLocalY() && localY <= ContactEndLocalY();
    }

    public float ContactQualityAtLocalY(float localY)
    {
        if (!GeometrySettingsValid() || !BatGeometry.Finite(localY)) return 0f;
        return BatGeometry.ContactQuality(BatGeometry.LocalYToNormalized(batLength, localY), contactStartNormalized,
            contactEndNormalized, sweetSpotNormalized, minimumQuality);
    }

    private bool GeometrySettingsValid()
    {
        return BatGeometry.IsValidGeometrySettings(batLength, radiusCurve, sweetSpotNormalized,
            contactStartNormalized, contactEndNormalized, contactMargin, minimumQuality);
    }

    public float EvaluateRawPower(float speed)
    {
        if (float.IsNaN(speed) || speed <= 0f || speed > float.MaxValue) return 0f;
        if (powerCurve == null) powerCurve = CreateDefaultPowerCurve();
        if (powerCurve.length == 0) return 0f;
        powerCurve.preWrapMode = WrapMode.ClampForever;
        powerCurve.postWrapMode = WrapMode.ClampForever;
        float power = powerCurve.Evaluate(speed);
        return float.IsNaN(power) ? 0f : Mathf.Clamp01(power);
    }

    private AnimationCurve CreateDefaultPowerCurve()
    {
        AnimationCurve curve = AnimationCurve.Linear(0f, 0f, 2.5f, 0f);
        curve.AddKey(2.75f, 0.03437588f);
        curve.AddKey(3f, 0.0657586f);
        curve.AddKey(3.5f, 0.1214268f);
        curve.AddKey(4f, 0.16951798f);
        curve.AddKey(5f, 0.25f);
        curve.AddKey(7.5f, 0.3962406f);
        curve.AddKey(10f, 0.5f);
        curve.AddKey(15f, 0.6462406f);
        curve.AddKey(20f, 0.75f);
        curve.AddKey(30f, 0.8962406f);
        curve.AddKey(40f, 1f);
        for (int i = 0; i < curve.length; i++) curve.SmoothTangents(i, i == 1 ? 1f : 0f);
        return curve;
    }

    private bool _battingDefaultsCaptured;
    private AnimationCurve _defaultPowerCurve;
    private AnimationCurve _defaultRadiusCurve;
    private float _defaultBatLength, _defaultSweetSpotNormalized, _defaultContactStartNormalized;
    private float _defaultContactEndNormalized, _defaultContactMargin, _defaultMinimumQuality, _defaultTimingYaw;
    private readonly float[] _defaultCapSettings = new float[14];

    public void Start()
    {
        CaptureBattingDefaults();
        // The captured default is an independent curve and can also detect
        // in-place edits without allocating a second copy on the first Scan.
        _feedbackCurveSnapshot = _defaultRadiusCurve;
    }

    private void CaptureBattingDefaults()
    {
        if (_battingDefaultsCaptured) return;
        _defaultPowerCurve = CloneCurve(powerCurve);
        _defaultBatLength = batLength; _defaultRadiusCurve = CloneCurve(radiusCurve);
        _defaultSweetSpotNormalized = sweetSpotNormalized;
        _defaultContactStartNormalized = contactStartNormalized; _defaultContactEndNormalized = contactEndNormalized;
        _defaultContactMargin = contactMargin; _defaultMinimumQuality = minimumQuality;
        _defaultTimingYaw = timingYawDegrees;
        _defaultCapSettings[0] = invalidWindowSeconds;
        _defaultCapSettings[1] = oneHandMovingDistance; _defaultCapSettings[2] = oneHandStillDistance;
        _defaultCapSettings[3] = wristStillDistance; _defaultCapSettings[4] = wristRotationDegrees;
        _defaultCapSettings[5] = pushDistance; _defaultCapSettings[6] = pushMaximumRotation;
        _defaultCapSettings[7] = handSpikeSpeed; _defaultCapSettings[8] = wristMinimumPower;
        _defaultCapSettings[9] = pushMinimumDirectionDot;
        _defaultCapSettings[10] = oneHandPowerCap; _defaultCapSettings[11] = wristPowerCap;
        _defaultCapSettings[12] = pushPowerCap; _defaultCapSettings[13] = spikePowerCap;
        _battingDefaultsCaptured = true;
    }

    public bool SetBattingPowerCurve(AnimationCurve curve)
    {
        if (curve == null) return false;
        Keyframe[] keys = curve.keys;
        CaptureBattingDefaults();
        powerCurve = new AnimationCurve(keys);
        return true;
    }

    public bool SetBatGeometry(float length, AnimationCurve curve, float sweetSpot,
        float contactStart, float contactEnd, float margin, float quality)
    {
        if (!BatGeometry.IsValidBatGeometry(length, curve, sweetSpot, contactStart, contactEnd, margin, quality)) return false;
        CaptureBattingDefaults();
        bool measurementPointChanged = batLength != length || sweetSpotNormalized != sweetSpot;
        batLength = length; radiusCurve = CloneCurve(curve);
        sweetSpotNormalized = sweetSpot; contactStartNormalized = contactStart;
        contactEndNormalized = contactEnd; contactMargin = margin; minimumQuality = quality;
        if (measurementPointChanged) ResetContactHistory();
        return true;
    }

    public bool SetBattingTiming(float yawDegrees)
    {
        if (!ContactFinite(yawDegrees) || yawDegrees < 0f || yawDegrees > 90f) return false;
        CaptureBattingDefaults();
        timingYawDegrees = yawDegrees;
        return true;
    }

    // IDs are documented in bat-contact.md. Invalid settings leave all values unchanged.
    public bool SetBattingCapSetting(int setting, float value)
    {
        if (setting < 0 || setting >= 14 || !ContactFinite(value) || value < 0f ||
            (setting == 0 && value <= 0f) ||
            ((setting == 4 || setting == 6) && value > 180f) ||
            (setting >= 8 && value > 1f)) return false;
        CaptureBattingDefaults();
        ApplyBattingCapSetting(setting, value);
        return true;
    }

    private void ApplyBattingCapSetting(int setting, float value)
    {
        if (setting == 0) invalidWindowSeconds = value;
        else if (setting == 1) oneHandMovingDistance = value;
        else if (setting == 2) oneHandStillDistance = value;
        else if (setting == 3) wristStillDistance = value;
        else if (setting == 4) wristRotationDegrees = value;
        else if (setting == 5) pushDistance = value;
        else if (setting == 6) pushMaximumRotation = value;
        else if (setting == 7) handSpikeSpeed = value;
        else if (setting == 8) wristMinimumPower = value;
        else if (setting == 9) pushMinimumDirectionDot = value;
        else if (setting == 10) oneHandPowerCap = value;
        else if (setting == 11) wristPowerCap = value;
        else if (setting == 12) pushPowerCap = value;
        else if (setting == 13) spikePowerCap = value;
    }

    public void ResetBattingSettings()
    {
        CaptureBattingDefaults();
        powerCurve = CloneCurve(_defaultPowerCurve);
        bool measurementPointChanged = batLength != _defaultBatLength || sweetSpotNormalized != _defaultSweetSpotNormalized;
        batLength = _defaultBatLength; radiusCurve = CloneCurve(_defaultRadiusCurve);
        sweetSpotNormalized = _defaultSweetSpotNormalized;
        contactStartNormalized = _defaultContactStartNormalized; contactEndNormalized = _defaultContactEndNormalized;
        contactMargin = _defaultContactMargin; minimumQuality = _defaultMinimumQuality;
        if (measurementPointChanged) ResetContactHistory();
        timingYawDegrees = _defaultTimingYaw;
        for (int i = 0; i < _defaultCapSettings.Length; i++) ApplyBattingCapSetting(i, _defaultCapSettings[i]);
    }

    private AnimationCurve CloneCurve(AnimationCurve curve)
    {
        if (curve == null) return null;
        return new AnimationCurve(curve.keys) { preWrapMode = curve.preWrapMode, postWrapMode = curve.postWrapMode };
    }
}
