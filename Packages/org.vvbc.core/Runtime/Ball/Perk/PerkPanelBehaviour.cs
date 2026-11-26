using TMPro;
using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.Udon;

// Provisional perk panel (docs/development/player-perks.md; PerkPanel prefab): category tabs, one
// row per perk of the category in pages of RowCount (previous / next), Clear All, a status line
// and the category's current ability values. It only
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
    [Tooltip("Page buttons and label; hidden when the category fits one page.")]
    public Button pagePrevButton;
    public Button pageNextButton;
    public TextMeshProUGUI pageText;

    [Header("Look")]
    public Color categoryColor = new Color(0.25f, 0.27f, 0.3f, 1f);
    public Color categoryActiveColor = new Color(0.95f, 0.75f, 0.2f, 1f);
    public Color selectedColor = new Color(1f, 0.82f, 0.25f, 1f);
    public Color availableColor = new Color(0.95f, 0.95f, 0.92f, 1f);
    public Color blockedColor = new Color(0.7f, 0.72f, 0.72f, 1f);
    public Color notImplementedColor = new Color(0.45f, 0.45f, 0.47f, 1f);

    private PlayerPerkBehaviour _perks;
    private int _category = PerkCatalog.CategoryBatting;
    private int _page;
    private int _categoryCount;
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

    public void _PagePrev() { SelectPage(_page - 1); }
    public void _PageNext() { SelectPage(_page + 1); }

    public void SelectPage(int page)
    {
        int pages = PageCount(PerkCount(_category));
        if (page < 0 || page >= pages || page == _page) return;
        _page = page;
        Refresh();
    }

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
        _page = 0;
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
        _categoryCount = FillRowIds(_category, _page, _rowIds);
        int pages = PageCount(_categoryCount);
        if (pagePrevButton != null) { pagePrevButton.gameObject.SetActive(pages > 1); pagePrevButton.interactable = _page > 0; }
        if (pageNextButton != null) { pageNextButton.gameObject.SetActive(pages > 1); pageNextButton.interactable = _page + 1 < pages; }
        if (pageText != null) pageText.text = pages > 1 ? "Page " + (_page + 1) + " / " + pages + "  (" + _categoryCount + " perks)" : "";
        for (int i = 0; i < categoryImages.Length && i < PerkCatalog.CategoryCount; i++)
            if (categoryImages[i] != null) categoryImages[i].color = i == _category ? categoryActiveColor : categoryColor;
        for (int row = 0; row < RowCount; row++) ShowRow(row, _rowIds[row]);
        if (headerText != null)
            headerText.text = "PERKS - " + PerkCatalog.CategoryName(_category) + (_perks == null ? "  (waiting for your PlayerObject)" : "  (" + _perks.GetSelectedCount() + " selected)");
        if (statusText != null) statusText.text = _status;
        if (statsText != null) statsText.text = _perks == null ? "" : StatsText(_perks, _category);
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

    // The first page of a category in catalog order, 0 for unused rows.
    public static void FillRowIds(int category, int[] rowIds)
    {
        FillRowIds(category, 0, rowIds);
    }

    // One page (rowIds.Length rows) of a category's perks in catalog order, 0 for unused rows;
    // returns how many perks the whole category has.
    public static int FillRowIds(int category, int page, int[] rowIds)
    {
        int index = 0;
        int first = page * rowIds.Length;
        for (int i = 0; i < rowIds.Length; i++) rowIds[i] = 0;
        for (int i = 0; i < PerkCatalog.PerkCount; i++)
        {
            int id = PerkCatalog.IdAt(i);
            if (PerkCatalog.Category(id) != category) continue;
            int row = index - first;
            if (row >= 0 && row < rowIds.Length) rowIds[row] = id;
            index++;
        }
        return index;
    }

    public static int PerkCount(int category)
    {
        int count = 0;
        for (int i = 0; i < PerkCatalog.PerkCount; i++)
            if (PerkCatalog.Category(PerkCatalog.IdAt(i)) == category) count++;
        return count;
    }

    public static int PageCount(int perkCount)
    {
        return perkCount <= RowCount ? 1 : (perkCount + RowCount - 1) / RowCount;
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

    // The category's abilities as they are used now (applied values where a system reports them).
    public static string StatsText(PlayerPerkBehaviour perks, int category)
    {
        string text = PerkCatalog.CategoryName(category).ToUpper() + " ABILITIES (base -> final)";
        if (category == PerkCatalog.CategoryBatting) return text + BattingLines(perks);
        if (category == PerkCatalog.CategoryPitching) return text + PitchingLines(perks);
        if (category == PerkCatalog.CategoryRunning) return text + RunningLines(perks);
        if (category == PerkCatalog.CategoryFielding)
            return text + "\n" + PercentLine("Catch Radius", perks.GetStatBase(PerkCatalog.AbilityCatchRadius) * 100f, perks.GetStatFinal(PerkCatalog.AbilityCatchRadius) * 100f,
                perks.GetModifier(PerkCatalog.AbilityCatchRadius), "cm", "F1", perks.GetStatState(PerkCatalog.AbilityCatchRadius));
        int fade = perks.GetFadeLevel();
        return text + "\nPitch Fade: " + (fade == 0 ? "Off" : fade == 1 ? "Fade I (2 rings)" : "Fade II (4 rings)")
            + "\nLanding Indicator: " + (perks.IsFlagOn(PerkCatalog.AbilityLandingIndicator) ? "On" : "Off");
    }

    private static string RunningLines(PlayerPerkBehaviour perks)
    {
        string text = "\n" + AppliedLine(perks, "Max Speed", PerkCatalog.AbilityMaxSpeed, "m/s");
        text += "\n" + AppliedLine(perks, "Acceleration", PerkCatalog.AbilityAcceleration, "m/s²");
        text += "\n" + AppliedLine(perks, "Initial Speed", PerkCatalog.AbilityInitialSpeed, "m/s");
        text += "\n" + PercentLine("Jump Height", perks.GetStatBase(PerkCatalog.AbilityJumpHeight), perks.GetStatFinal(PerkCatalog.AbilityJumpHeight),
            perks.GetModifier(PerkCatalog.AbilityJumpHeight), "m", "F2", perks.GetStatState(PerkCatalog.AbilityJumpHeight));
        text += "\nFence jump zones: " + (perks.HasEffect(PerkCatalog.AbilityJumpHeight)
            ? PerkCatalog.JumpFenceHeight.ToString("F2") + " m" : "normal jump (no Jump perk)");
        return text;
    }

    private static string BattingLines(PlayerPerkBehaviour perks)
    {
        string text = "\nHoming: " + (perks.GetHomingRate() * 100f).ToString("F0") + "%   Bunt: " + (perks.IsFlagOn(PerkCatalog.AbilityBunt) ? "On" : "Off");
        text += "\n" + BattedSpeedLine(perks.GetValueOrStandard(PerkCatalog.AbilityBattedBallSpeed));
        text += "\nSweet Spot: gamma 1.00 -> " + perks.GetValueOrStandard(PerkCatalog.AbilitySweetSpot).ToString("F2") + " (exit speed uses quality^gamma)";
        float offset = perks.GetValueOrStandard(PerkCatalog.AbilityContactRange);
        text += "\nContact Range: half width " + (offset > 0f ? "+" : "") + offset.ToString("F1") + " cm" + (offset > 0f ? " (outer band: weak contact)" : "");
        int early = perks.GetStatState(PerkCatalog.AbilityTimingEarly);
        text += "\nTiming yaw: early " + YawText(perks, PerkCatalog.AbilityTimingEarly) + ", late " + YawText(perks, PerkCatalog.AbilityTimingLate)
            + (early == PlayerPerkBehaviour.StatApplied ? "" : " - not reported (no Bat)");
        return text;
    }

    public static string BattedSpeedLine(float value)
    {
        float standard = PerkCatalog.Standard(PerkCatalog.AbilityBattedBallSpeed);
        if (value > standard) return "Batted Ball Speed: up to " + value.ToString("F0") + " km/h (std " + standard.ToString("F0") + "; bonus from Power 0.85 and quality 0.90)";
        if (value < standard) return "Batted Ball Speed: capped at " + value.ToString("F0") + " km/h (std " + standard.ToString("F0") + ")";
        return "Batted Ball Speed: " + standard.ToString("F0") + " km/h top (std)";
    }

    private static string YawText(PlayerPerkBehaviour perks, int ability)
    {
        float baseValue = perks.GetStatBase(ability);
        float finalValue = perks.GetStatFinal(ability);
        if (perks.GetStatState(ability) != PlayerPerkBehaviour.StatApplied)
            return perks.GetValueOrStandard(ability).ToString("F0") + " deg";
        return baseValue.ToString("F0") + " -> " + finalValue.ToString("F0") + " deg";
    }

    private static string PitchingLines(PlayerPerkBehaviour perks)
    {
        string text = "\nPitch Speed Cap (4-Seam): " + perks.GetValueOrStandard(PerkCatalog.AbilityPitchSpeedCap).ToString("F0") + " km/h"
            + MoundNote(perks, PerkCatalog.AbilityPitchSpeedCap, "mound setting", "F0", " km/h");
        text += "\nThrow Speed Cap: " + perks.GetValueOrStandard(PerkCatalog.AbilityThrowSpeedCap).ToString("F0") + " km/h";
        int spin = perks.GetStage(PerkCatalog.AbilitySpin);
        text += "\n" + SpinLine(spin);
        int control = perks.GetStage(PerkCatalog.AbilityControl);
        float sigma = perks.GetControlSigmaAtZero();
        text += "\nControl: target " + PerkCatalog.ControlTargetText(control) + "; sigma at score 85 "
            + (sigma * 0.15f * 100f).ToString("F1") + " cm (at 0: " + (sigma * 100f).ToString("F0") + " cm)";
        int state = perks.GetStatState(PerkCatalog.AbilityControl);
        if (state == PlayerPerkBehaviour.StatNoEffect) text += "\n  last mound: penalty strength 0 - control error has no effect";
        else if (state == PlayerPerkBehaviour.StatApplied) text += "\n  last mound: applied (penalty strength above 0)";
        else text += "\n  not used on a mound yet";
        return text;
    }

    public static string SpinLine(int stage)
    {
        return "Spin: 4S " + PerkCatalog.SpinFourSeamRpm(stage).ToString("F0") + " rpm, breaking x" + PerkCatalog.SpinBreakingMultiplier(stage).ToString("F2")
            + ", Split " + PerkCatalog.SpinSplitRpm(stage).ToString("F0") + ", Change " + PerkCatalog.SpinChangeRpm(stage).ToString("F0")
            + " rpm, Knuckle SSW " + PerkCatalog.SpinKnuckleSsw(stage).ToString("F3");
    }

    // " (mound setting 140 km/h not used)" once a mound reported its own value.
    private static string MoundNote(PlayerPerkBehaviour perks, int ability, string what, string format, string unit)
    {
        if (perks.GetStatState(ability) == PlayerPerkBehaviour.StatNotReported) return "";
        return " (" + what + " " + perks.GetStatBase(ability).ToString(format) + unit + " not used)";
    }

    // A running value as Locomotion uses it; the percentage is final / base - 1, because Max
    // Speed also scales the acceleration and the Max Speed Debuff caps the initial speed.
    private static string AppliedLine(PlayerPerkBehaviour perks, string name, int ability, string unit)
    {
        float baseValue = perks.GetStatBase(ability);
        float finalValue = perks.GetStatFinal(ability);
        return PercentLine(name, baseValue, finalValue, RatioModifier(baseValue, finalValue), unit, "F2", perks.GetStatState(ability));
    }

    public static float RatioModifier(float baseValue, float finalValue)
    {
        if (!(baseValue > 0f) || !PerkStats.Finite(finalValue)) return 0f;
        return finalValue / baseValue - 1f;
    }
}
