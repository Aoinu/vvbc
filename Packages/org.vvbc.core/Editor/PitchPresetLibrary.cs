using System;
using UnityEditor;
using UnityEngine;

[Serializable]
public class PitchPresetEntry
{
    public string name;
    // Pitch-local components: right, up, forward. -right is backspin.
    public Vector3 spinAxis;
    public Vector3 seamEuler;
    public float initialPhase;
    public PitchPresetEntry(string label, Vector3 axis) { name = label; spinAxis = axis; }
}

public class PitchPresetLibrary : ScriptableObject
{
    public PitchPresetEntry[] presets = {
        new PitchPresetEntry("Four-Seam FB", new Vector3(-1f, 0f, 0f)),
        new PitchPresetEntry("Curve", new Vector3(1f, 0f, 0f)),
        new PitchPresetEntry("Slider", new Vector3(0f, -0.55f, 0.83f)),
        new PitchPresetEntry("Sweeper", new Vector3(0f, -1f, 0f)),
        new PitchPresetEntry("Split", new Vector3(-0.12f, 0f, 0.99f)),
        new PitchPresetEntry("Screw", new Vector3(0.65f, 0.76f, 0f)),
        new PitchPresetEntry("Sinker", new Vector3(-0.65f, 0.55f, 0.52f))
    };

    private void OnValidate()
    {
        if (!Application.isPlaying) PitchPresetSynchronizer.Queue(this);
    }
}
