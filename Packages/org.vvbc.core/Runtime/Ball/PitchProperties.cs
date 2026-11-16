using UnityEngine;

// Launch inputs of one pitch, fixed when the pitch is prepared and never recomputed in flight.
// Directions are pitch-local (x right, y up, z forward, as PitchSpin) for a right-handed pitcher:
// +x is the arm side, -x the glove side.
public partial struct PitchProperties
{
    public float speedMps;
    public float spinRpm;
    // Unit spin axis; kept unit even when spinRpm is 0.
    public Vector3 spinAxisLocal;
    public WakeProfile wakeLocal;
}
