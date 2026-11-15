using UnityEngine;

// A WakeProfile prepared once at the release (BallAerodynamics.PrepareWake), so a flight
// step needs no normalization and, for Fixed, no trigonometry. The default value is None.
public struct BallWake
{
    public int mode;
    public float coefficient;
    // Fixed: unit force direction. Rotating: axis * cos(coneAngle).
    public Vector3 direction;
    // Rotating: sin(coneAngle) times the two unit vectors perpendicular to the axis.
    public Vector3 basisX, basisY;
    // Rotating: phase(t) = phase + rate * t, in radians.
    public float phase, rate;
}
