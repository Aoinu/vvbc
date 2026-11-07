using UnityEngine;

public partial class BatMotionBehaviour
{
    [Header("Batting")]
    public AnimationCurve powerCurve;
    public float minContactX = 0.10f;
    public float maxContactX = 0.855f;
    public float sweetSpotX = 0.68f;
    public float widthAtGrip = 0.045f;
    public float widthAtBarrel = 0.070f;
    public float minimumQuality = 0.25f;
    public float timingYawDegrees = 30f;

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
    private float _defaultMinContactX, _defaultMaxContactX, _defaultSweetSpotX;
    private float _defaultWidthAtGrip, _defaultWidthAtBarrel, _defaultMinimumQuality, _defaultTimingYaw;
    private readonly float[] _defaultCapSettings = new float[14];

    public void Start() { CaptureBattingDefaults(); }

    private void CaptureBattingDefaults()
    {
        if (_battingDefaultsCaptured) return;
        _defaultPowerCurve = powerCurve;
        _defaultMinContactX = minContactX; _defaultMaxContactX = maxContactX;
        _defaultSweetSpotX = sweetSpotX; _defaultWidthAtGrip = widthAtGrip;
        _defaultWidthAtBarrel = widthAtBarrel; _defaultMinimumQuality = minimumQuality;
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

    public bool SetBattingGeometry(float minimum, float maximum, float sweetSpot,
        float gripWidth, float barrelWidth, float quality)
    {
        if (!ContactFinite(minimum) || !ContactFinite(maximum) || !ContactFinite(sweetSpot) ||
            !ContactFinite(gripWidth) || !ContactFinite(barrelWidth) || !ContactFinite(quality) ||
            maximum <= minimum || sweetSpot < minimum || sweetSpot > maximum ||
            gripWidth <= 0f || barrelWidth <= 0f || quality < 0f || quality > 1f) return false;
        CaptureBattingDefaults();
        minContactX = minimum; maxContactX = maximum; sweetSpotX = sweetSpot;
        widthAtGrip = gripWidth; widthAtBarrel = barrelWidth; minimumQuality = quality;
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
        powerCurve = _defaultPowerCurve;
        minContactX = _defaultMinContactX; maxContactX = _defaultMaxContactX;
        sweetSpotX = _defaultSweetSpotX; widthAtGrip = _defaultWidthAtGrip;
        widthAtBarrel = _defaultWidthAtBarrel; minimumQuality = _defaultMinimumQuality;
        timingYawDegrees = _defaultTimingYaw;
        for (int i = 0; i < _defaultCapSettings.Length; i++) ApplyBattingCapSetting(i, _defaultCapSettings[i]);
    }
}
