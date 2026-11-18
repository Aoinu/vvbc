using UnityEngine;

public struct PitchDecision
{
    public int author, sequence, pitcher, number, status, reason, revision;
    public float delay, threshold;
    public bool measured;
    public double time;
}

// The wire arrays contain up to eight whole records. A local positive revision pins a
// record until a serialization of that revision succeeds; received records are unpinned.
public static class PitchDecisionHistory
{
    public const int Capacity = 8;
    private const int KeysPerRecord = 5;

    public static bool Store(PitchDecision[] records, PitchDecision decision)
    {
        int slot = -1;
        for (int i = 0; i < records.Length; i++)
        {
            PitchDecision entry = records[i];
            if (entry.author == decision.author && entry.sequence == decision.sequence) return false;
            if (entry.sequence == 0) slot = i;
            else if (entry.revision == 0 && (slot < 0 || (records[slot].sequence != 0 && Older(entry, records[slot]))))
                slot = i;
        }
        if (slot < 0) return false;
        if (decision.revision == 0 && records[slot].sequence != 0 && !Older(records[slot], decision)) return false;
        records[slot] = decision;
        return true;
    }

    private static bool Older(PitchDecision a, PitchDecision b)
    {
        if (a.time != b.time) return a.time < b.time;
        if (a.author != b.author) return a.author < b.author;
        return a.sequence < b.sequence;
    }

    public static void MarkSent(PitchDecision[] records, int revision)
    {
        for (int i = 0; i < records.Length; i++)
        {
            PitchDecision entry = records[i];
            if (entry.revision == 0 || entry.revision > revision) continue;
            entry.revision = 0;
            records[i] = entry;
        }
    }

    public static int[] Keys(PitchDecision[] records)
    {
        int[] keys = new int[Count(records) * KeysPerRecord];
        int j = 0;
        for (int i = 0; i < records.Length; i++)
        {
            PitchDecision entry = records[i];
            if (entry.sequence == 0) continue;
            keys[j++] = entry.author;
            keys[j++] = entry.sequence;
            keys[j++] = entry.pitcher;
            keys[j++] = entry.number;
            keys[j++] = entry.status | (entry.reason << 3);
        }
        return keys;
    }

    public static double[] Timing(PitchDecision[] records, bool verdict)
    {
        double[] timing = new double[Count(records) * (verdict ? 3 : 1)];
        int j = 0;
        for (int i = 0; i < records.Length; i++)
        {
            PitchDecision entry = records[i];
            if (entry.sequence == 0) continue;
            timing[j++] = entry.time;
            if (!verdict) continue;
            timing[j++] = entry.measured ? entry.delay : double.NaN;
            timing[j++] = entry.threshold;
        }
        return timing;
    }

    private static int Count(PitchDecision[] records)
    {
        int count = 0;
        for (int i = 0; i < records.Length; i++)
            if (records[i].sequence > 0) count++;
        return count;
    }

    public static int WireCount(int[] keys, double[] timing, bool verdict)
    {
        if (keys == null || timing == null) return 0;
        int count = keys.Length / KeysPerRecord;
        return count <= Capacity && keys.Length == count * KeysPerRecord &&
            timing.Length == count * (verdict ? 3 : 1) ? count : 0;
    }

    public static PitchDecision Read(int[] keys, double[] timing, int index, bool verdict)
    {
        PitchDecision entry = new PitchDecision();
        int j = index * KeysPerRecord;
        entry.author = keys[j];
        entry.sequence = keys[j + 1];
        entry.pitcher = keys[j + 2];
        entry.number = keys[j + 3];
        entry.status = keys[j + 4] & 7;
        entry.reason = keys[j + 4] >> 3;
        j = index * (verdict ? 3 : 1);
        entry.time = timing[j];
        if (verdict)
        {
            entry.measured = !double.IsNaN(timing[j + 1]);
            if (entry.measured) entry.delay = (float)timing[j + 1];
            entry.threshold = (float)timing[j + 2];
        }
        return entry;
    }
}

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
