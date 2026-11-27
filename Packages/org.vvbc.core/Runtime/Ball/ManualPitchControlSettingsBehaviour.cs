using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

// Timing cap of one mound, shared by everyone in the instance: the control score cap of a
// release outside the ideal timing. The size of the control error is the pitcher's own (the
// Control perk and the release's score), never a mound setting. Its own Manual-sync object,
// apart from the Ball's pitch state: a slider edit takes Ownership, sets the value and requests a
// serialization; others and late joiners receive the owner's value in OnDeserialization. The
// mound reads it once at each release on the pitcher's client, so a change never reaches a pitch
// in flight and receivers never use it. See docs/development/manual-pitching.md.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class ManualPitchControlSettingsBehaviour : TsukimiBehaviour
{
    public const float CapStep = 5f;

    [Tooltip("Score cap of every release outside the ideal timing (early, auto, late), 0..100. Default 60.")]
    [UdonSynced] public float timingCap = 60f;

    [Header("UI (optional)")]
    public Slider capSlider;
    public Text capText;

    private void Start()
    {
        FindControls();
        ConfigureSliders();
        ShowValues();
    }

    // Scene instances store their own variable tables: unset controls are this object's
    // children of the same names.
    private void FindControls()
    {
        if (capSlider == null)
        {
            Transform child = transform.Find("CapSlider");
            if (child != null) capSlider = child.GetComponent<Slider>();
        }
        if (capText == null)
        {
            Transform child = transform.Find("CapLabel");
            if (child != null) capText = child.GetComponent<Text>();
        }
    }

    private void ConfigureSliders()
    {
        if (capSlider != null) { capSlider.minValue = 0f; capSlider.maxValue = 100f; capSlider.wholeNumbers = false; }
    }

    // Slider callback (On Value Changed). Values shown by ShowValues never call back.
    public void _CapChanged() { if (capSlider != null) Submit(capSlider.value); }

    // Takes Ownership when needed and publishes the snapped, clamped value.
    public void Submit(float cap)
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        if (!Utilities.IsValid(local)) return;
        if (!Networking.IsOwner(local, gameObject)) Networking.SetOwner(local, gameObject);
        timingCap = Snap(cap, 0f, 100f, CapStep, timingCap);
        RequestSerialization();
        ShowValues();
    }

    public override void OnDeserialization()
    {
        // A value from the network is clamped like a local edit; a non-finite one falls back to 60.
        timingCap = Snap(timingCap, 0f, 100f, CapStep, 60f);
        ShowValues();
    }

    public static float Snap(float value, float min, float max, float step, float fallback)
    {
        if (!ManualPitchMath.Finite(value)) return fallback;
        float clamped = Mathf.Clamp(value, min, max);
        return step > 0f ? Mathf.Clamp(Mathf.Round(clamped / step) * step, min, max) : clamped;
    }

    private void ShowValues()
    {
        if (capSlider != null) capSlider.SetValueWithoutNotify(timingCap);
        if (capText != null) capText.text = CapLabel();
    }

    public string CapLabel() { return "Timing cap (not ideal)  " + Mathf.RoundToInt(timingCap); }
}
