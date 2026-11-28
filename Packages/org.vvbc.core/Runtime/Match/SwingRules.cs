using UnityEngine;

// Swing recognition for the batter's pitch result (docs/development/automatic-umpire.md#swing).
// Pure: the batter's client measures its own bat around the pitch's plate crossing from the bat's
// existing motion history and hands the numbers in; nothing here reads a Behaviour or a clock.
// A swing is independent of contact: a check swing that stays slow, short or never comes forward
// is not one, and a bat swung with no pitch in the air is never reported at all.
public static class SwingRules
{
    // A swing: the sweet spot fast enough, the bat turning fast enough, a sweep of the bat's
    // horizontal direction at least minSweepDegrees within the window, and (when asked) the sweet
    // spot moving toward the pitcher by at least minForward metres.
    public static bool IsSwing(SwingMeasure m, float minSpeed, float minAngularSpeed, float minSweepDegrees, float minForward, bool requireForward)
    {
        if (!m.measured) return false;
        if (!(m.peakSpeed >= minSpeed) || !(m.peakAngularSpeed >= minAngularSpeed)) return false;
        if (m.poseKnown)
        {
            if (!(SweepDegrees(m.axisStart, m.axisEnd) >= minSweepDegrees)) return false;
            if (requireForward && !(Vector3.Dot(m.sweetEnd - m.sweetStart, Flat(m.towardPitcher)) >= minForward)) return false;
        }
        return true;
    }

    // The angle between two bat axes on the horizontal plane, in degrees (0 when either is vertical).
    public static float SweepDegrees(Vector3 a, Vector3 b)
    {
        Vector3 fa = Flat(a), fb = Flat(b);
        if (fa.sqrMagnitude < 0.0001f || fb.sqrMagnitude < 0.0001f) return 0f;
        return Vector3.Angle(fa, fb);
    }

    private static Vector3 Flat(Vector3 v) { return new Vector3(v.x, 0f, v.z); }
}

// What the batter's client measured of its bat around one pitch's crossing.
public partial struct SwingMeasure
{
    public bool measured;          // the bat's motion history covered the window
    public float peakSpeed;        // sweet spot, m/s
    public float peakAngularSpeed; // degrees per second
    public bool poseKnown;         // the bat poses at the window's start and end were sampled
    public Vector3 axisStart;      // bat axis (root to tip) at the start of the window
    public Vector3 axisEnd;
    public Vector3 sweetStart;     // sweet spot position at the start of the window
    public Vector3 sweetEnd;
    public Vector3 towardPitcher;  // horizontal direction from the plate toward the pitcher
}
