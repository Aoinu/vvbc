using UnityEngine;

// Seam-shifted wake of one pitch, fixed before the release and never changed in flight.
// Only a pitch carries one; batted balls, throws and drops always use None.
public struct WakeProfile
{
    // BallAerodynamics.WakeNone, WakeFixed or WakeRotating.
    public int mode;
    // Acceleration per unit speed, in inverse seconds; clamped to BallAerodynamics.MaxWakeCoefficient.
    public float coefficient;
    // Fixed: the force direction. Rotating: the axis the force direction turns about.
    // World space on the Ball; pitch-local (x right, y up, z forward) before the aim is solved.
    public Vector3 direction;
    // Rotating only: angle between the axis and the force direction, in radians.
    public float coneAngle;
    // Rotating only: angle of the force direction around the axis at flight time 0, in radians.
    public float initialPhase;
}
