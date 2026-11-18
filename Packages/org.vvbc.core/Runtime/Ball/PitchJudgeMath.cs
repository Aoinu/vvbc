using UnityEngine;

// Pure rules of the batter's pitch judgement (docs/development/manual-pitch-sync.md): the
// delay verdict, the remote release detection on one hand segment, and the status texts.
// Every time here is on one client's monotonic clock (Time.realtimeSinceStartup); server
// time is never mixed in.
public static class PitchJudgeMath
{
    public const int StatusNone = 0;
    public const int StatusPending = 1;
    public const int StatusValid = 2;
    public const int StatusInvalid = 3;
    // The pitcher withdrew the pitch without a confirmed batter verdict.
    public const int StatusCancelled = 4;

    public const int ReasonNone = 0;
    // The pitch data arrived later than the threshold after the release the batter saw.
    public const int ReasonDelay = 1;
    // The pitch data arrived but no release motion was seen within the wait.
    public const int ReasonMotionNotDetected = 2;
    // The pitch referred to prepared data this batter never received.
    public const int ReasonPrepMissing = 3;
    // Cancelled by the pitcher: the batter the pitch was prepared for left or changed.
    public const int ReasonBatterChanged = 4;
    public const int ReasonVerdictMissing = 5;

    public const float DefaultThresholdMs = 100f;
    public const float MinThresholdMs = 0f;
    public const float MaxThresholdMs = 500f;
    public const float ThresholdStepMs = 10f;

    // delayMs = (data received - visual release) x 1000, both on the same local clock.
    public static float DelayMs(float receivedAt, float visualReleaseAt)
    {
        return (receivedAt - visualReleaseAt) * 1000f;
    }

    // Valid at or below the threshold (100 ms exactly is valid); a negative delay (the data was
    // first) is valid. A delay or threshold that is not a number is never valid.
    public static int Classify(float delayMs, float thresholdMs)
    {
        if (float.IsNaN(delayMs) || float.IsNaN(thresholdMs)) return StatusInvalid;
        return delayMs <= thresholdMs ? StatusValid : StatusInvalid;
    }

    // Threshold settings are kept inside 0..500 ms on the 10 ms grid; anything else is the default.
    public static float ClampThreshold(float thresholdMs)
    {
        if (!(thresholdMs <= float.MaxValue && thresholdMs >= -float.MaxValue)) return DefaultThresholdMs;
        float stepped = Mathf.Round(thresholdMs / ThresholdStepMs) * ThresholdStepMs;
        return Mathf.Clamp(stepped, MinThresholdMs, MaxThresholdMs);
    }

    // Where on one segment the hand is closest to the ideal release, as the unclamped fraction
    // of the segment: <= 0 rising from its start, >= 1 still falling at its end, between them a
    // minimum inside it. rel0/rel1 are hand - ideal at both ends. A segment without motion (a
    // paused hand) is NaN: neither a minimum nor a fall.
    public static float ClosestFraction(Vector3 rel0, Vector3 rel1)
    {
        Vector3 motion = rel1 - rel0;
        float lengthSquared = motion.sqrMagnitude;
        if (!(lengthSquared > 0.0000001f)) return float.NaN;
        return -Vector3.Dot(rel0, motion) / lengthSquared;
    }

    // The distance is still falling at the end of this segment.
    public static bool FallingThrough(Vector3 rel0, Vector3 rel1)
    {
        return ClosestFraction(rel0, rel1) >= 1f;
    }

    // A release candidate on one segment of the remote throwing hand: the distance to the ideal
    // release falls and then rises (a closest approach, not a threshold crossing), within
    // captureRadius, while the hand moves toward the target fast enough and within
    // maxAngleDegrees of the pitch direction. A minimum on the sample shared with the previous
    // segment counts here only when that segment was falling through (previousFalling), so one
    // pass is reported once. Returns the fraction of this segment, or -1.
    public static float ReleaseCandidate(Vector3 hand0, Vector3 hand1, Vector3 ideal0, Vector3 ideal1, float dt,
        Vector3 pitchDirection, float captureRadius, float minSpeed, float maxAngleDegrees, bool previousFalling)
    {
        if (!(dt > 0f)) return -1f;
        float u = ClosestFraction(hand0 - ideal0, hand1 - ideal1);
        if (float.IsNaN(u) || u >= 1f) return -1f;
        if (u <= 0f)
        {
            if (!previousFalling) return -1f;
            u = 0f;
        }
        Vector3 rel = Vector3.Lerp(hand0 - ideal0, hand1 - ideal1, u);
        if (!(rel.magnitude <= captureRadius)) return -1f;
        Vector3 velocity = (hand1 - hand0) / dt;
        float speed = velocity.magnitude;
        float along = Vector3.Dot(velocity, pitchDirection);
        if (!(along >= minSpeed) || !(speed > 0f)) return -1f;
        float angle = Mathf.Acos(Mathf.Clamp(along / speed, -1f, 1f)) * Mathf.Rad2Deg;
        return angle <= maxAngleDegrees ? u : -1f;
    }

    public static string StatusName(int status)
    {
        if (status == StatusPending) return "pending";
        if (status == StatusValid) return "valid";
        if (status == StatusInvalid) return "invalid";
        if (status == StatusCancelled) return "cancelled";
        return "none";
    }

    public static string ReasonName(int reason)
    {
        if (reason == ReasonDelay) return "sync delay";
        if (reason == ReasonMotionNotDetected) return "release motion not detected";
        if (reason == ReasonPrepMissing) return "pitch data not prepared";
        if (reason == ReasonBatterChanged) return "batter changed";
        if (reason == ReasonVerdictMissing) return "batter verdict unavailable";
        return "";
    }

    // The message every player sees; the delay is shown only when it was measured.
    public static string MessageText(int status, int reason, float delayMs, float thresholdMs)
    {
        if (status == StatusInvalid)
        {
            string text = "INVALID PITCH: " + ReasonName(reason);
            if (reason == ReasonDelay && !float.IsNaN(delayMs))
                text += " " + Mathf.RoundToInt(delayMs) + " ms (limit " + Mathf.RoundToInt(thresholdMs) + " ms)";
            return text + ". Pitch again.";
        }
        if (status == StatusCancelled) return "PITCH CANCELLED: " + ReasonName(reason) + ". Pitch again.";
        return "";
    }
}
