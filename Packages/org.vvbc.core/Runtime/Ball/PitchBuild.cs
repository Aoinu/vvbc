// One customised pitch: its type and the normalized (0-1) parameters A, B and C. Parameters
// the type does not use stay at DefaultParameter. Saved per playlist item.
public struct PitchBuild
{
    public const float DefaultParameter = 0.5f;

    public int pitchTypeId;
    public float parameterA;
    public float parameterB;
    public float parameterC;
}
