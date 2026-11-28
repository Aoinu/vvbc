using UnityEngine;

// Swing recognition for the batter's pitch result (docs/development/automatic-umpire.md#swing).
// Pure: the batter's client measures its own bat around the pitch's plate crossing from the bat's
// existing motion history and hands the numbers in; nothing here reads a Behaviour or a clock.
// A swing is independent of contact: a check swing that stays slow, short or never comes forward
// is not one, and a bat swung with no pitch in the air is never reported at all.
//
// Three answers. A missing measurement never loosens the rule (a fast bat whose sweep was not
// seen is not a swing by speed alone) and never becomes "no swing" by default either: a no swing
// needs a measurement that rules the swing out, a swing one that shows every condition.
public static class SwingRules
{
    public const int SwingNo = 0;
    public const int SwingYes = 1;
    public const int SwingUnknown = 2;

    // A bat history this close to the window's ends still covers it (one sample apart), seconds.
    public const float CoverageSlackSeconds = 0.05f;

    // A swing: the sweet spot fast enough, the bat turning fast enough, a sweep of the bat's
    // horizontal direction at least minSweepDegrees within the window, and (when asked) the sweet
    // spot moving toward the pitcher by at least minForward metres.
    //   - speeds below a threshold over a history covering the window: no swing (whatever the poses);
    //   - speeds met and the poses at both ends known: swing or no swing by the sweep and forward
    //     motion, but a short sweep over a window the poses did not cover is not known;
    //   - otherwise (no bat, no history, a window not covered): not known.
    public static int Classify(SwingMeasure m, float minSpeed, float minAngularSpeed, float minSweepDegrees, float minForward, bool requireForward)
    {
        if (!m.measured) return SwingUnknown;
        bool fastEnough = m.peakSpeed >= minSpeed && m.peakAngularSpeed >= minAngularSpeed;
        if (!fastEnough) return m.speedCovers ? SwingNo : SwingUnknown;
        if (!m.poseKnown) return SwingUnknown;
        bool swept = SweepDegrees(m.axisStart, m.axisEnd) >= minSweepDegrees;
        bool forward = !requireForward || Vector3.Dot(m.sweetEnd - m.sweetStart, Flat(m.towardPitcher)) >= minForward;
        if (swept && forward) return SwingYes;
        return m.poseCovers ? SwingNo : SwingUnknown;
    }

    // Whether a history from oldest to latest covers [start, end].
    public static bool Covers(float oldest, float latest, float start, float end)
    {
        return latest > oldest && oldest <= start + CoverageSlackSeconds && latest >= end - CoverageSlackSeconds;
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
    public bool measured;          // the bat's motion history had samples in the window
    public bool speedCovers;       // ... and covered the whole window (the peaks are the window's)
    public float peakSpeed;        // sweet spot, m/s
    public float peakAngularSpeed; // degrees per second
    public bool poseKnown;         // the bat poses at the window's start and end were sampled
    public bool poseCovers;        // ... at the window's own ends (not a shorter part of it)
    public Vector3 axisStart;      // bat axis (root to tip) at the start of the window
    public Vector3 axisEnd;
    public Vector3 sweetStart;     // sweet spot position at the start of the window
    public Vector3 sweetEnd;
    public Vector3 towardPitcher;  // horizontal direction from the plate toward the pitcher
}
