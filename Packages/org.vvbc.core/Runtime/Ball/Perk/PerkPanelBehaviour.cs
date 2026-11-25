using TMPro;
using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.Udon;

// Provisional perk panel (docs/development/player-perks.md; PerkPanel prefab): category tabs, one
// row per perk of the category, Clear All, a status line and the current ability values. It only
// shows and requests: every rule and rejection reason comes from the local player's
// PlayerPerkBehaviour, so a future menu page can drive the same API.
//
// Nothing here is synced. The display is rebuilt on a press, on _OnPerksChanged and when the
// panel is enabled; nothing runs per frame, and a hidden panel does not rebuild.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class PerkPanelBehaviour : TsukimiBehaviour
{
    public const int RowCount = 12;
    public const int RowSelected = 0;
    public const int RowAvailable = 1;
    public const int RowBlocked = 2;
    public const int RowNotImplemented = 3;

    [Header("UI")]
    public Button[] categoryButtons = new Button[0];
    public Image[] categoryImages = new Image[0];
    public Button[] rowButtons = new Button[0];
    public Image[] rowImages = new Image[0];
    public TextMeshProUGUI[] rowTitleTexts = new TextMeshProUGUI[0];
    public TextMeshProUGUI[] rowDetailTexts = new TextMeshProUGUI[0];
    public TextMeshProUGUI headerText;
    public TextMeshProUGUI statusText;
    public TextMeshProUGUI statsText;
    public Button clearButton;

    [Header("Look")]
    public Color categoryColor = new Color(0.25f, 0.27f, 0.3f, 1f);
    public Color categoryActiveColor = new Color(0.95f, 0.75f, 0.2f, 1f);
    public Color selectedColor = new Color(1f, 0.82f, 0.25f, 1f);
    public Color availableColor = new Color(0.95f, 0.95f, 0.92f, 1f);
    public Color blockedColor = new Color(0.7f, 0.72f, 0.72f, 1f);
    public Color notImplementedColor = new Color(0.45f, 0.45f, 0.47f, 1f);

    private PlayerPerkBehaviour _perks;
    private int _category = PerkCatalog.CategoryBatting;
    private int[] _rowIds = new int[RowCount];
    private string _status = "";
    private int _searches;

    void Start()
    {
        FindPerks();
        Refresh();
    }

    void OnEnable()
    {
        Refresh();
    }

    // The local PlayerObject can appear after this panel starts: retry once a second for a while.
    public void FindPerks()
    {
        if (_perks != null) return;
        _perks = PerkLookup.Local();
        if (_perks != null)
        {
            _perks.AddChangeListener(GetComponent<UdonBehaviour>());
            Refresh();
            return;
        }
        if (_searches++ < 60) SendCustomEventDelayedSeconds(nameof(FindPerks), 1f);
    }

    public void _OnPerksChanged()
    {
        Refresh();
    }

    // The applied values changed without a selection change (a system became ready).
    public void _OnPerkStatsChanged()
    {
        Refresh();
    }

    // ---- Buttons ----

    public void _Category0() { SelectCategory(0); } public void _Category1() { SelectCategory(1); }
    public void _Category2() { SelectCategory(2); } public void _Category3() { SelectCategory(3); }
    public void _Category4() { SelectCategory(4); }

    public void _Row0() { PressRow(0); } public void _Row1() { PressRow(1); } public void _Row2() { PressRow(2); }
    public void _Row3() { PressRow(3); } public void _Row4() { PressRow(4); } public void _Row5() { PressRow(5); }
    public void _Row6() { PressRow(6); } public void _Row7() { PressRow(7); } public void _Row8() { PressRow(8); }
    public void _Row9() { PressRow(9); } public void _Row10() { PressRow(10); } public void _Row11() { PressRow(11); }

    public void _Clear()
    {
        if (_perks == null) return;
        int result = _perks.ClearPerks();
        _status = result == PerkRules.Ok ? "All perks removed." : PerkRules.ResultText(result, 0, 0);
        Refresh();
    }

    public void SelectCategory(int category)
    {
        if (category < 0 || category >= PerkCatalog.CategoryCount) return;
        _category = category;
        _status = "";
        Refresh();
    }

    public void PressRow(int row)
    {
        if (_perks == null || row < 0 || row >= RowCount) return;
        int id = _rowIds[row];
        if (id == 0) return;
        _status = Toggle(id);
        Refresh();
    }

    // Removes a selected perk, adds another; returns the status line.
    private string Toggle(int id)
    {
        if (_perks.HasPerk(id))
        {
            int removed = _perks.TryRemovePerk(id);
            return removed == PerkRules.Ok ? PerkCatalog.Name(id) + " removed." : PerkRules.ResultText(removed, id, 0);
        }
        int result = _perks.TryAddPerk(id);
        if (result == PerkRules.Ok) return PerkCatalog.Name(id) + " added.";
        return PerkRules.ResultText(result, id, _perks.ConflictingPerk(id));
    }

    // ---- Display ----

    public void Refresh()
    {
        if (!gameObject.activeInHierarchy) return;
        FillRowIds(_category, _rowIds);
        for (int i = 0; i < categoryImages.Length && i < PerkCatalog.CategoryCount; i++)
            if (categoryImages[i] != null) categoryImages[i].color = i == _category ? categoryActiveColor : categoryColor;
        for (int row = 0; row < RowCount; row++) ShowRow(row, _rowIds[row]);
        if (headerText != null)
            headerText.text = "PERKS - " + PerkCatalog.CategoryName(_category) + (_perks == null ? "  (waiting for your PlayerObject)" : "  (" + _perks.GetSelectedCount() + " selected)");
        if (statusText != null) statusText.text = _status;
        if (statsText != null) statsText.text = _perks == null ? "" : StatsText(_perks);
    }

    private void ShowRow(int row, int id)
    {
        bool used = id != 0;
        if (row < rowButtons.Length && rowButtons[row] != null)
        {
            rowButtons[row].gameObject.SetActive(used);
            rowButtons[row].interactable = used && _perks != null && _perks.CanEdit() && PerkCatalog.IsImplemented(id);
        }
        if (!used) return;
        int state = RowState(id);
        if (row < rowImages.Length && rowImages[row] != null) rowImages[row].color = StateColor(state);
        if (row < rowTitleTexts.Length && rowTitleTexts[row] != null) rowTitleTexts[row].text = RowTitle(id, state);
        if (row < rowDetailTexts.Length && rowDetailTexts[row] != null) rowDetailTexts[row].text = RowDetail(id, state);
    }

    private int RowState(int id)
    {
        if (!PerkCatalog.IsImplemented(id)) return RowNotImplemented;
        if (_perks == null) return RowAvailable;
        if (_perks.HasPerk(id)) return RowSelected;
        return _perks.CheckAdd(id) == PerkRules.Ok ? RowAvailable : RowBlocked;
    }

    private Color StateColor(int state)
    {
        if (state == RowSelected) return selectedColor;
        if (state == RowBlocked) return blockedColor;
        if (state == RowNotImplemented) return notImplementedColor;
        return availableColor;
    }

    private string RowDetail(int id, int state)
    {
        string detail = PerkCatalog.Description(id);
        if (state == RowNotImplemented) return detail + "\nNot implemented: " + PerkCatalog.NotImplementedReason(id);
        if (state == RowBlocked && _perks != null) return detail + "\nConflicts with " + PerkCatalog.Name(_perks.ConflictingPerk(id));
        return detail;
    }

    // ---- Pure helpers (tested) ----

    // The perks of a category in catalog order, 0 for unused rows.
    public static void FillRowIds(int category, int[] rowIds)
    {
        int row = 0;
        for (int i = 0; i < rowIds.Length; i++) rowIds[i] = 0;
        for (int i = 0; i < PerkCatalog.PerkCount && row < rowIds.Length; i++)
        {
            int id = PerkCatalog.IdAt(i);
            if (PerkCatalog.Category(id) != category) continue;
            rowIds[row] = id;
            row++;
        }
    }

    public static string StateLabel(int state)
    {
        if (state == RowSelected) return "SELECTED";
        if (state == RowBlocked) return "CONFLICT";
        if (state == RowNotImplemented) return "NOT IMPLEMENTED";
        return "";
    }

    public static string RowTitle(int id, int state)
    {
        string kind = PerkCatalog.IsDebuff(id) ? "DEBUFF" : "BUFF";
        string label = StateLabel(state);
        return PerkCatalog.Name(id) + "  [" + PerkCatalog.StageText(id) + " / " + kind + "]  " + PerkCatalog.EffectText(id)
            + (label == "" ? "" : "  - " + label);
    }

    // "Name: base -> final unit (+x%)" for a percent ability; the state says whether it is applied.
    public static string PercentLine(string name, float baseValue, float finalValue, float modifier, string unit, string format, int state)
    {
        if (state == PlayerPerkBehaviour.StatNotReported) return name + ": not reported (no PerkEffects for this player)";
        if (state == PlayerPerkBehaviour.StatUnavailable && !(baseValue > 0f)) return name + ": not available (system not connected)";
        string line = name + ": " + baseValue.ToString(format) + " -> " + finalValue.ToString(format) + " " + unit + " (" + SignedPercent(modifier) + ")";
        return state == PlayerPerkBehaviour.StatUnavailable ? line + " - not applied" : line;
    }

    public static string SignedPercent(float modifier)
    {
        float percent = modifier * 100f;
        if (percent > -0.05f && percent < 0.05f) return "+0.0%";
        return (percent > 0f ? "+" : "") + percent.ToString("F1") + "%";
    }

    public static string StatsText(PlayerPerkBehaviour perks)
    {
        string text = "CURRENT ABILITIES (base -> final)";
        text += "\n" + PercentLine("Max Speed", perks.GetStatBase(PerkCatalog.AbilityMaxSpeed), perks.GetStatFinal(PerkCatalog.AbilityMaxSpeed),
            perks.GetModifier(PerkCatalog.AbilityMaxSpeed), "m/s", "F2", perks.GetStatState(PerkCatalog.AbilityMaxSpeed));
        text += "\n" + PercentLine("Jump Height", perks.GetStatBase(PerkCatalog.AbilityJumpHeight), perks.GetStatFinal(PerkCatalog.AbilityJumpHeight),
            perks.GetModifier(PerkCatalog.AbilityJumpHeight), "m", "F2", perks.GetStatState(PerkCatalog.AbilityJumpHeight));
        text += "\n" + PercentLine("Catch Radius", perks.GetStatBase(PerkCatalog.AbilityCatchRadius) * 100f, perks.GetStatFinal(PerkCatalog.AbilityCatchRadius) * 100f,
            perks.GetModifier(PerkCatalog.AbilityCatchRadius), "cm", "F1", perks.GetStatState(PerkCatalog.AbilityCatchRadius));
        text += "\nHoming: " + (perks.GetHomingRate() * 100f).ToString("F0") + "%   Bunt: " + (perks.IsFlagOn(PerkCatalog.AbilityBunt) ? "On" : "Off");
        int fade = perks.GetFadeLevel();
        text += "\nPitch Fade: " + (fade == 0 ? "Off" : fade == 1 ? "Fade I (2 rings)" : "Fade II (4 rings)")
            + "   Landing Indicator: " + (perks.IsFlagOn(PerkCatalog.AbilityLandingIndicator) ? "On" : "Off");
        float acceleration = perks.GetStatBase(PerkCatalog.AbilityAcceleration);
        text += "\nAcceleration: " + (acceleration > 0f ? acceleration.ToString("F2") + " m/s²" : "-") + " (perks not connected)";
        text += "\nPitch/Throw Speed Cap, Spin, Control: perks not connected";
        return text;
    }
}
