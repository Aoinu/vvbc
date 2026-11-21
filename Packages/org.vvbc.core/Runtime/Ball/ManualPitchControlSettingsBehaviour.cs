using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

// Control balance of one mound, shared by everyone in the instance: the strength of the
// random control error, the score cap of a release outside the ideal timing and the control
// error's sigma at score 0. Its own Manual-sync object, apart from the Ball's pitch state: a slider edit takes
// Ownership, sets the values and requests a serialization; others and late joiners receive the
// owner's values in OnDeserialization. The mound reads them once at each release on the
// pitcher's client, so a change never reaches a pitch in flight and receivers never use them.
// See docs/development/manual-pitching.md.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public partial class ManualPitchControlSettingsBehaviour : TsukimiBehaviour
{
    public const float SigmaLimitMeters = 1f;
    public const float StrengthStep = 0.05f;
    public const float CapStep = 5f;
    public const float SigmaStep = 0.01f;

    [Tooltip("Strength of the random control error, 0..1 (shown in %). Default 0: none.")]
    [UdonSynced] public float penaltyStrength = 0f;
    [Tooltip("Score cap of every release outside the ideal timing (early, auto, late), 0..100. Default 60.")]
    [UdonSynced] public float timingCap = 60f;
    [Tooltip("Control error sigma (1 sigma of the normal draw) at score 0, in metres (0..1). Provisional default 0.40.")]
    [UdonSynced] public float controlSigma = 0.40f;

    [Header("UI (optional)")]
    public Slider strengthSlider;
    public Slider capSlider;
    public Slider maxErrorSlider;
    public Text strengthText;
    public Text capText;
    public Text maxErrorText;

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
        if (strengthSlider == null) strengthSlider = ChildSlider("StrengthSlider");
        if (capSlider == null) capSlider = ChildSlider("CapSlider");
        if (maxErrorSlider == null) maxErrorSlider = ChildSlider("MaxErrorSlider");
        if (strengthText == null) strengthText = ChildText("StrengthLabel");
        if (capText == null) capText = ChildText("CapLabel");
        if (maxErrorText == null) maxErrorText = ChildText("MaxErrorLabel");
    }

    private Slider ChildSlider(string name)
    {
        Transform child = transform.Find(name);
        return child != null ? child.GetComponent<Slider>() : null;
    }

    private Text ChildText(string name)
    {
        Transform child = transform.Find(name);
        return child != null ? child.GetComponent<Text>() : null;
    }

    private void ConfigureSliders()
    {
        if (strengthSlider != null) { strengthSlider.minValue = 0f; strengthSlider.maxValue = 1f; strengthSlider.wholeNumbers = false; }
        if (capSlider != null) { capSlider.minValue = 0f; capSlider.maxValue = 100f; capSlider.wholeNumbers = false; }
        if (maxErrorSlider != null) { maxErrorSlider.minValue = 0f; maxErrorSlider.maxValue = SigmaLimitMeters; maxErrorSlider.wholeNumbers = false; }
    }

    // Slider callbacks (On Value Changed). Values shown by ShowValues never call back.
    public void _StrengthChanged() { if (strengthSlider != null) Submit(strengthSlider.value, timingCap, controlSigma); }
    public void _CapChanged() { if (capSlider != null) Submit(penaltyStrength, capSlider.value, controlSigma); }
    public void _MaxErrorChanged() { if (maxErrorSlider != null) Submit(penaltyStrength, timingCap, maxErrorSlider.value); }

    // Takes Ownership when needed and publishes the snapped, clamped values.
    public void Submit(float strength, float cap, float maxError)
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        if (!Utilities.IsValid(local)) return;
        if (!Networking.IsOwner(local, gameObject)) Networking.SetOwner(local, gameObject);
        penaltyStrength = Snap(strength, 0f, 1f, StrengthStep, penaltyStrength);
        timingCap = Snap(cap, 0f, 100f, CapStep, timingCap);
        controlSigma = Snap(maxError, 0f, SigmaLimitMeters, SigmaStep, controlSigma);
        RequestSerialization();
        ShowValues();
    }

    public override void OnDeserialization()
    {
        // Values from the network are clamped like local edits; a non-finite one keeps the last.
        penaltyStrength = Snap(penaltyStrength, 0f, 1f, StrengthStep, 0f);
        timingCap = Snap(timingCap, 0f, 100f, CapStep, 60f);
        controlSigma = Snap(controlSigma, 0f, SigmaLimitMeters, SigmaStep, 0.40f);
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
        if (strengthSlider != null) strengthSlider.SetValueWithoutNotify(penaltyStrength);
        if (capSlider != null) capSlider.SetValueWithoutNotify(timingCap);
        if (maxErrorSlider != null) maxErrorSlider.SetValueWithoutNotify(controlSigma);
        if (strengthText != null) strengthText.text = StrengthLabel();
        if (capText != null) capText.text = CapLabel();
        if (maxErrorText != null) maxErrorText.text = MaxErrorLabel();
    }

    public string StrengthLabel() { return "Control penalty  " + Mathf.RoundToInt(penaltyStrength * 100f) + "%"; }
    public string CapLabel() { return "Timing cap (not ideal)  " + Mathf.RoundToInt(timingCap); }
    public string MaxErrorLabel() { return "Error 1 sigma at score 0  " + Mathf.RoundToInt(controlSigma * 100f) + " cm"; }
}
