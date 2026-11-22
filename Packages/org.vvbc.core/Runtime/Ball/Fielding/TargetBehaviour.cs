using TMPro;
using Tsukimi;
using UnityEngine;

// Settings of one throw target, placed on the target's own object (for example
// ThrowAim/Targets/FirstBase) and listed in ThrowAimBehaviour.targets. It holds data only:
// no per-frame work, no sync. ThrowAim reads it once at the first hold, places and shows
// the marker, culls by distance and selects among the usable targets. The component itself
// is never disabled to switch a target; targetEnabled and allowDuringPitcherMode decide.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class TargetBehaviour : TsukimiBehaviour
{
    [Header("Target")]
    [Tooltip("The point a throw is aimed at, placed at receiving height. Empty: the target is skipped.")]
    public Transform targetPoint;
    [Tooltip("Name written to the Label and used by the release feedback. Empty: the Target Point's name.")]
    public string displayName = "";

    [Header("Marker")]
    [Tooltip("Marker Root (with Reticle and Label children): positioned, turned toward the player and shown or hidden by ThrowAim. Empty: no marker is shown; the target is still selectable while in range.")]
    public Transform marker;
    [Tooltip("Reticle MeshRenderer (a quad with the shared reticle material). Its colour comes from Marker Color.")]
    public MeshRenderer reticleRenderer;
    // TextMeshPro rather than TextMesh: Udon exposes no UnityEngine.TextMesh member.
    [Tooltip("Label TextMeshPro; its text is set to the display name once. Its own colour is kept.")]
    public TextMeshPro labelText;
    [Tooltip("Reticle colour, applied once with a MaterialPropertyBlock (the material is shared). Default white.")]
    public Color markerColor = Color.white;

    [Header("Selection")]
    [Tooltip("Shortest horizontal (XZ) distance from the thrower at which the target is shown and selectable, in metres. 0: no minimum.")]
    public float minDistance = 0f;
    [Tooltip("Longest horizontal (XZ) distance from the thrower at which the target is shown and selectable, in metres. 0: no maximum. Must not be below the minimum when both are set.")]
    public float maxDistance = 0f;
    [Tooltip("Whether the target is used at all, in or out of pitcher mode. Default on.")]
    public bool targetEnabled = true;
    [Tooltip("Whether the target may be shown and selected while the local player is in pitcher mode on a mound (pickoff throws to the bases). Off for HomePlate: a pitch that is not recognized must not become a throw home. Ignored outside pitcher mode. Default on.")]
    public bool allowDuringPitcherMode = true;
    // The enable rule over these two settings is FieldingMath.TargetUsable; ThrowAim applies it
    // to the settings it read, so no call into this Behaviour is made per frame.
}
