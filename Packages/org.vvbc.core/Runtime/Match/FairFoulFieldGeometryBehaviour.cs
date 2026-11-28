using Tsukimi;
using UnityEngine;

// The field one game is played on, for the umpire engine's fair / foul and out-of-play decisions
// (docs/development/automatic-umpire.md#field-geometry). Placed once per field in the scene; the
// foul lines run from Home Plate through First Base and Third Base on the horizontal plane, so the
// field may be rotated or moved freely. Only read on the MatchManager Owner; nothing is synced.
//
// Out of play is an extension point: optional foul poles (vertical cylinders) and a radial
// out-of-play distance from home. A batted ball reaching either is never ruled automatically
// (home runs, ground-rule doubles and local rules go to the umpire).
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class FairFoulFieldGeometryBehaviour : TsukimiBehaviour
{
    [Header("Bases (centres, on the ground)")]
    [Tooltip("Home plate (the apex of the foul lines; its height is the ground height).")]
    public Transform homePlate;
    [Tooltip("First base: the first-base foul line runs from home through it.")]
    public Transform firstBase;
    [Tooltip("Third base: the third-base foul line runs from home through it.")]
    public Transform thirdBase;

    [Header("Ball")]
    [Tooltip("Ball radius in metres: a ball within it of a foul line touches the line (fair). Default 0.037.")]
    public float ballRadius = 0.037f;
    [Tooltip("Reach around first / third base within which a ball near the ground touches the bag (fair), in metres. Default 0.3.")]
    public float baseTouchReach = 0.3f;

    [Header("Out of play (optional)")]
    [Tooltip("Foul poles (base of each pole). Empty = none. A batted ball touching one goes to the umpire.")]
    public Transform[] foulPoles = new Transform[0];
    [Tooltip("Pole radius in metres. Default 0.15.")]
    public float foulPoleRadius = 0.15f;
    [Tooltip("Pole height above its base in metres. Default 20.")]
    public float foulPoleHeight = 20f;
    [Tooltip("Horizontal distance from home beyond which a batted ball is out of play (the fence line), in metres. 0 = not checked.")]
    public float outOfPlayDistance = 0f;

    // The frame (FairFoulRules) built from the transforms; false when a transform is missing or
    // the bases do not make a field.
    public bool BuildFrame(float[] frame)
    {
        if (homePlate == null || firstBase == null || thirdBase == null) return false;
        return FairFoulRules.BuildFrame(homePlate.position, firstBase.position, thirdBase.position, frame);
    }

    public float GroundHeight() { return homePlate != null ? homePlate.position.y : 0f; }

    // A batted ball at p is out of play (past the fence line, or on a pole).
    public bool OutOfPlay(float[] frame, Vector3 p)
    {
        if (outOfPlayDistance > 0f && FairFoulRules.DistanceFromHome(frame, p) > outOfPlayDistance) return true;
        for (int i = 0; i < foulPoles.Length; i++)
            if (foulPoles[i] != null && FairFoulRules.TouchesPole(foulPoles[i].position, foulPoleRadius, foulPoleHeight, p, ballRadius)) return true;
        return false;
    }
}
