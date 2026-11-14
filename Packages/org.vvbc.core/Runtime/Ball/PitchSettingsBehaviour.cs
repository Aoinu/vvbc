using TMPro;
using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

// Local UI of the batting pitcher (PitchSettingsPage). Tabs, scroll, drafts and countdowns are
// local and never synced; only confirmed changes are sent to BattingPitcherBehaviour.
public partial class PitchSettingsBehaviour : TsukimiBehaviour
{
    public BattingPitcherBehaviour pitcher;
    [Tooltip("Optional lane (HomePlateSet). When set, the lane decides which pitcher this page edits.")]
    public HomePlateSetBehaviour lane;

    [Header("Lane mode")]
    public GameObject modeBar;
    public TextMeshProUGUI modeText;
    public TextMeshProUGUI modeButtonText;
    public GameObject endPracticeButton;
    public GameObject joinOverlay;
    public GameObject confirmDialog;
    public TextMeshProUGUI footerText;
    [Tooltip("Footer shown in Local Mode; the Shared footer is the text authored on footerText.")]
    public string localFooter = "Local practice: your pitcher, ball, playlist and settings stay on this client.";
    private string _sharedFooter;

    [Header("Header")]
    public TextMeshProUGUI ownerText;
    public TextMeshProUGUI runStateText;
    public Image runStateImage;
    public TextMeshProUGUI startStopText;
    public Image startStopImage;

    [Header("Next pitch")]
    public TextMeshProUGUI phaseText;
    public TextMeshProUGUI phaseSecondsText;
    public TextMeshProUGUI phaseDetailText;
    public RectTransform progressFill;
    public Image progressFillImage;

    [Header("Tabs and mode")]
    public GameObject controlsPage;
    public GameObject editorPage;
    public Image controlsTabImage;
    public Image editorTabImage;
    public Image randomImage;
    public Image sequenceImage;

    [Header("Playlist")]
    public TextMeshProUGUI playlistCountText;
    public ScrollRect playlistScroll;
    public GameObject emptyHint;
    public GameObject[] rows = new GameObject[0];
    public Image[] rowImages = new Image[0];
    public TextMeshProUGUI[] rowNumberTexts = new TextMeshProUGUI[0];
    public TextMeshProUGUI[] rowTypeTexts = new TextMeshProUGUI[0];
    public TextMeshProUGUI[] rowCourseTexts = new TextMeshProUGUI[0];
    public TextMeshProUGUI[] rowStatusTexts = new TextMeshProUGUI[0];
    public Image[] rowStatusImages = new Image[0];
    public Button[] rowUpButtons = new Button[0];
    public Button[] rowDownButtons = new Button[0];

    [Header("Controls")]
    public Slider speedSlider;
    public Slider spinSlider;
    public Slider resetSlider;
    public TextMeshProUGUI speedText;
    public TextMeshProUGUI spinText;
    public TextMeshProUGUI resetText;
    public Image onEventImage;
    public Image onTimerImage;
    [Tooltip("Quiet time after the last edit before the settings are committed, in seconds.")]
    public float commitDelaySeconds = 0.5f;

    [Header("Pitch editor")]
    public TextMeshProUGUI editorTitleText;
    public Image[] zoneImages = new Image[0];
    public TextMeshProUGUI zoneCountText;
    public TMP_Dropdown pitchTypeDropdown;
    public TextMeshProUGUI editorMessageText;
    [Tooltip("Pitch type (preset index) of each dropdown option, top to bottom.")]
    public int[] pitchTypeOrder = { 0, 2, 1, 6, 4, 5, 3 };
    [Tooltip("Display name of each pitch type, by preset index.")]
    public string[] pitchTypeNames = { "Four-Seam", "Curveball", "Slider", "Sweeper", "Splitter", "Screwball", "Sinker" };

    [Header("Look")]
    [Tooltip("Seconds between countdown refreshes while the page is shown.")]
    public float displayInterval = 0.066f;
    public int ownerNameLimit = 20;
    public Color accentColor = new Color(0.157f, 0.808f, 0.894f, 1f);
    public Color idleColor = new Color(0.137f, 0.18f, 0.247f, 1f);
    public Color runningColor = new Color(0.18f, 0.74f, 0.42f, 1f);
    public Color stopColor = new Color(0.86f, 0.3f, 0.32f, 1f);
    public Color nextColor = new Color(0.157f, 0.808f, 0.894f, 0.35f);
    public Color doneColor = new Color(0.36f, 0.42f, 0.5f, 1f);
    public Color rowColor = new Color(0.1f, 0.13f, 0.18f, 1f);
    public Color editingRowColor = new Color(0.16f, 0.28f, 0.36f, 1f);

    private bool applying;
    private bool commitScheduled;
    private float lastEditTime;
    private bool draftTimer;
    private bool editing;
    private int editItemId;
    private int draftType;
    private int draftMask = 16;
    private int saveToken;
    private float nextDisplay;
    private float nextOwnerRefresh;
    private int shownPhase = -1;
    private int shownTenths = -1;
    private float phaseSince;
    // True while the shown phase counts down or animates; static phases wait for a notification.
    private bool animating;

    private void Start()
    {
        if (footerText != null) _sharedFooter = footerText.text;
        if (lane != null) pitcher = lane.ActivePitcher();
        HideConfirm();
        RefreshMode();
        applying = true;
        if (speedSlider != null) { speedSlider.minValue = BattingPitcherBehaviour.MinSpeedKmh; speedSlider.maxValue = BattingPitcherBehaviour.MaxSpeedKmh; }
        if (spinSlider != null) { spinSlider.minValue = BattingPitcherBehaviour.MinSpinRpm; spinSlider.maxValue = BattingPitcherBehaviour.MaxSpinRpm; }
        if (resetSlider != null) { resetSlider.minValue = BattingPitcherBehaviour.MinResetSeconds; resetSlider.maxValue = BattingPitcherBehaviour.MaxResetSeconds; }
        applying = false;
        if (pitcher != null) pitcher.RegisterSettingsListener(GetComponent<UdonBehaviour>());
        ShowEditor(false);
        _OnPitcherStateChanged();
    }

    // Points the page at another pitcher (Shared, a LocalRig's, or none). The old pitcher stops
    // notifying this page, and drafts meant for it are dropped rather than sent to the new one.
    public void SetPitcher(BattingPitcherBehaviour next)
    {
        if (next == pitcher) return;
        UdonBehaviour self = GetComponent<UdonBehaviour>();
        if (pitcher != null) pitcher.UnregisterSettingsListener(self);
        pitcher = next;
        commitScheduled = false;
        saveToken = 0;
        editing = false;
        ShowEditorMessage("");
        ShowEditor(false);
        if (pitcher != null)
        {
            pitcher.RegisterSettingsListener(self);
            _OnPitcherStateChanged();
        }
    }

    // The lane mode or this client's LocalRig changed.
    public void _OnLaneChanged()
    {
        if (lane == null) return;
        SetPitcher(lane.ActivePitcher());
        if (!lane.IsLocalMode()) HideConfirm();
        RefreshMode();
        RefreshHeader();
    }

    private void RefreshMode()
    {
        bool switching = lane != null && lane.allowModeSwitching;
        bool local = lane != null && lane.IsLocalMode();
        bool rig = lane != null && lane.HasLocalRig();
        if (modeBar != null) modeBar.SetActive(switching);
        if (modeText != null) modeText.text = local ? "LOCAL MODE" : "SHARED MODE";
        if (modeButtonText != null) modeButtonText.text = local ? "SWITCH TO SHARED" : "SWITCH TO LOCAL";
        if (endPracticeButton != null) endPracticeButton.SetActive(local && rig);
        if (joinOverlay != null) joinOverlay.SetActive(local && !rig);
        if (footerText != null && _sharedFooter != null) footerText.text = local ? localFooter : _sharedFooter;
    }

    private void HideConfirm()
    {
        if (confirmDialog != null) confirmDialog.SetActive(false);
    }

    // Shared -> Local at once; Local -> Shared asks first.
    public void _ToggleMode()
    {
        if (lane == null || !lane.allowModeSwitching) return;
        if (!lane.IsLocalMode())
        {
            lane.RequestLocalMode();
            return;
        }
        if (confirmDialog != null) confirmDialog.SetActive(true);
        else lane.RequestSharedMode();
    }

    public void _ConfirmShared()
    {
        HideConfirm();
        if (lane != null) lane.RequestSharedMode();
    }

    public void _CancelShared() { HideConfirm(); }
    public void _JoinPractice() { if (lane != null) lane.JoinPractice(); }
    public void _EndPractice() { if (lane != null) lane.EndPractice(); }

    // Synced state, Ownership or the cycle changed. A pending local draft is not overwritten.
    public void _OnPitcherStateChanged()
    {
        if (pitcher == null) return;
        if (!commitScheduled)
        {
            applying = true;
            if (speedSlider != null) speedSlider.value = pitcher.speedKmh;
            if (spinSlider != null) spinSlider.value = pitcher.spinRpm;
            if (resetSlider != null) resetSlider.value = pitcher.resetSeconds;
            applying = false;
            draftTimer = pitcher.IsResetOnTimer();
        }
        CheckSave();
        RefreshHeader();
        RefreshControls();
        RefreshPlaylist();
        RefreshEditor();
        shownPhase = -1;
        RefreshProgress();
    }

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        RefreshHeader();
    }

    // Countdown and gauge only; runs while the page is active and the phase moves by itself.
    // Stopped, Ball Held and Syncing change only through _OnPitcherStateChanged.
    private void Update()
    {
        if (pitcher == null || !animating || Time.time < nextDisplay) return;
        nextDisplay = Time.time + Mathf.Max(0.02f, displayInterval);
        RefreshProgress();
        if (Time.time >= nextOwnerRefresh)
        {
            nextOwnerRefresh = Time.time + 2f;
            RefreshHeader();
        }
    }

    // ---- Header ----

    public void _ToggleRunning()
    {
        if (pitcher != null) pitcher.RequestRunning(!pitcher.IsRunning());
    }

    private void RefreshHeader()
    {
        if (pitcher == null) return;
        if (ownerText != null) ownerText.text = OwnerLabel();
        bool running = pitcher.IsRunning();
        if (runStateText != null) runStateText.text = running ? "RUNNING" : "STOPPED";
        if (runStateImage != null) runStateImage.color = running ? runningColor : doneColor;
        if (startStopText != null) startStopText.text = running ? "STOP" : "START";
        if (startStopImage != null) startStopImage.color = running ? stopColor : runningColor;
    }

    private string OwnerLabel()
    {
        if (pitcher.localOnly) return "LOCAL";
        VRCPlayerApi owner = Networking.GetOwner(pitcher.gameObject);
        if (!Utilities.IsValid(owner)) return "Connecting...";
        return Shorten(owner.displayName, ownerNameLimit);
    }

    private string Shorten(string value, int limit)
    {
        if (value == null) return "";
        if (limit < 2 || value.Length <= limit) return value;
        return value.Substring(0, limit - 1) + "…";
    }

    // ---- Next pitch progress ----

    private void RefreshProgress()
    {
        if (pitcher == null) return;
        int phase = pitcher.CyclePhase();
        if (phase != shownPhase)
        {
            phaseSince = Time.time;
            shownPhase = phase;
            shownTenths = -1;
            if (phaseText != null) phaseText.text = PhaseTitle(phase);
            if (phaseDetailText != null) phaseDetailText.text = PhaseDetail(phase);
            if (progressFillImage != null) progressFillImage.color = phase == BattingPitcherBehaviour.PhaseBallHeld ? stopColor : accentColor;
        }
        float remaining = pitcher.PhaseRemaining();
        float span = pitcher.PhaseSpan();
        bool counting = HasCountdown(phase);
        bool looping = phase == BattingPitcherBehaviour.PhasePreparing;
        animating = counting || looping;
        float elapsed = Time.time - phaseSince;
        int tenths = counting ? Mathf.CeilToInt(remaining * 10f) : looping ? Mathf.FloorToInt(elapsed * 10f) : -2;
        if (tenths != shownTenths)
        {
            shownTenths = tenths;
            if (phaseSecondsText != null)
                phaseSecondsText.text = counting ? (tenths / 10f).ToString("F1") + " s" : looping ? (tenths / 10f).ToString("F1") + " s" : "—";
        }
        if (progressFill == null) return;
        float from = 0f;
        float to = 0f;
        if (counting && span > 0f) to = Mathf.Clamp01(1f - remaining / span);
        else if (looping)
        {
            // An indeterminate segment sweeps across, so a long preparation never looks frozen.
            float t = Mathf.Repeat(elapsed * 0.6f, 1.3f);
            from = Mathf.Clamp01(t - 0.3f);
            to = Mathf.Clamp01(t);
        }
        progressFill.anchorMin = new Vector2(from, 0f);
        progressFill.anchorMax = new Vector2(to, 1f);
    }

    private bool HasCountdown(int phase)
    {
        return phase == BattingPitcherBehaviour.PhaseScheduled || phase == BattingPitcherBehaviour.PhaseAwaitingContact ||
            phase == BattingPitcherBehaviour.PhaseResetting;
    }

    private string PhaseTitle(int phase)
    {
        if (phase == BattingPitcherBehaviour.PhaseAwaitingContact) return "AWAITING CONTACT";
        if (phase == BattingPitcherBehaviour.PhaseResetting) return "RESET IN PROGRESS";
        if (phase == BattingPitcherBehaviour.PhaseBallHeld) return "BALL HELD";
        if (phase == BattingPitcherBehaviour.PhasePreparing) return "PREPARING";
        if (phase == BattingPitcherBehaviour.PhaseScheduled) return "PITCH SCHEDULED";
        if (phase == BattingPitcherBehaviour.PhaseSyncing) return "SYNCING";
        return "STOPPED";
    }

    private string PhaseDetail(int phase)
    {
        if (phase == BattingPitcherBehaviour.PhaseAwaitingContact) return "Waiting for the first catch or bounce. Resets on its own when the fallback runs out.";
        if (phase == BattingPitcherBehaviour.PhaseResetting) return "Earliest time the next pitch can be prepared. Preparation adds a moment.";
        if (phase == BattingPitcherBehaviour.PhaseBallHeld) return "The next pitch waits until the ball is released.";
        if (phase == BattingPitcherBehaviour.PhasePreparing) return "Staging the ball and solving the aim.";
        if (phase == BattingPitcherBehaviour.PhaseScheduled) return "The pitching motion is about to start.";
        if (phase == BattingPitcherBehaviour.PhaseSyncing) return "Restoring the shared pitcher state.";
        return "Press START to begin pitching.";
    }

    // ---- Tabs and mode ----

    public void _ShowControls()
    {
        editing = false;
        ShowEditor(false);
    }

    public void _ShowEditor()
    {
        if (!editing) BeginEdit(0, PitchPlaylist.DefaultType, PitchPlaylist.CenterMask);
    }

    public void _SetRandom() { if (pitcher != null) pitcher.RequestSequence(false); }
    public void _SetSequence() { if (pitcher != null) pitcher.RequestSequence(true); }

    private void ShowEditor(bool show)
    {
        if (controlsPage != null) controlsPage.SetActive(!show);
        if (editorPage != null) editorPage.SetActive(show);
        if (controlsTabImage != null) controlsTabImage.color = show ? idleColor : accentColor;
        if (editorTabImage != null) editorTabImage.color = show ? accentColor : idleColor;
    }

    // ---- Controls ----

    public void _SpeedChanged() { Edited(); }
    public void _SpinChanged() { Edited(); }
    public void _ResetChanged() { Edited(); }
    public void _SetOnEvent() { draftTimer = false; Edited(); }
    public void _SetOnTimer() { draftTimer = true; Edited(); }

    // Slider changes come from drag, click and keyboard alike; the commit waits for the edits to settle.
    private void Edited()
    {
        if (applying) return;
        RefreshControls();
        lastEditTime = Time.time;
        if (commitScheduled) return;
        commitScheduled = true;
        SendCustomEventDelayedSeconds(nameof(_Commit), Mathf.Max(0.05f, commitDelaySeconds));
    }

    // Seconds still to wait so that the commit comes commitDelaySeconds after the last edit.
    private float CommitRemaining(float now)
    {
        return Mathf.Max(0.05f, commitDelaySeconds) - (now - lastEditTime);
    }

    public void _Commit()
    {
        if (!commitScheduled) return;
        float remaining = CommitRemaining(Time.time);
        if (remaining > 0.01f)
        {
            SendCustomEventDelayedSeconds(nameof(_Commit), remaining);
            return;
        }
        commitScheduled = false;
        if (pitcher == null) return;
        pitcher.SubmitSettings(speedSlider != null ? speedSlider.value : pitcher.speedKmh,
            spinSlider != null ? spinSlider.value : pitcher.spinRpm,
            resetSlider != null ? resetSlider.value : pitcher.resetSeconds, draftTimer);
    }

    private void RefreshControls()
    {
        if (pitcher == null) return;
        if (speedText != null && speedSlider != null) speedText.text = Mathf.RoundToInt(speedSlider.value) + " km/h";
        if (spinText != null && spinSlider != null) spinText.text = Mathf.RoundToInt(spinSlider.value) + " rpm";
        if (resetText != null && resetSlider != null) resetText.text = resetSlider.value.ToString("F1") + " s";
        if (onEventImage != null) onEventImage.color = draftTimer ? idleColor : accentColor;
        if (onTimerImage != null) onTimerImage.color = draftTimer ? accentColor : idleColor;
        bool sequence = pitcher.IsSequence();
        if (randomImage != null) randomImage.color = sequence ? idleColor : accentColor;
        if (sequenceImage != null) sequenceImage.color = sequence ? accentColor : idleColor;
    }

    // ---- Playlist ----

    public void _AddPitch() { BeginEdit(0, PitchPlaylist.DefaultType, PitchPlaylist.CenterMask); }

    // Scrolls to the next pitch on request only; a shuffle never moves the list by itself.
    public void _LocateNext()
    {
        if (pitcher == null || playlistScroll == null) return;
        int next = pitcher.NextIndex();
        int count = pitcher.PlaylistCount();
        if (next < 0 || count < 2) return;
        playlistScroll.verticalNormalizedPosition = 1f - (float)next / (count - 1);
    }

    private void SelectRow(int index)
    {
        if (pitcher == null || index >= pitcher.PlaylistCount()) return;
        int item = pitcher.PlaylistItem(index);
        BeginEdit(PitchPlaylist.Id(item), PitchPlaylist.Type(item), PitchPlaylist.Mask(item));
    }

    private void MoveRow(int index, int direction)
    {
        if (pitcher == null || index >= pitcher.PlaylistCount()) return;
        pitcher.RequestMoveItem(PitchPlaylist.Id(pitcher.PlaylistItem(index)), direction);
    }

    private void DeleteRow(int index)
    {
        if (pitcher == null || index >= pitcher.PlaylistCount()) return;
        pitcher.RequestDeleteItem(PitchPlaylist.Id(pitcher.PlaylistItem(index)));
    }

    public void _Row0() { SelectRow(0); } public void _Row1() { SelectRow(1); } public void _Row2() { SelectRow(2); }
    public void _Row3() { SelectRow(3); } public void _Row4() { SelectRow(4); } public void _Row5() { SelectRow(5); }
    public void _Row6() { SelectRow(6); } public void _Row7() { SelectRow(7); } public void _Row8() { SelectRow(8); }
    public void _Row9() { SelectRow(9); }
    public void _Up0() { MoveRow(0, -1); } public void _Up1() { MoveRow(1, -1); } public void _Up2() { MoveRow(2, -1); }
    public void _Up3() { MoveRow(3, -1); } public void _Up4() { MoveRow(4, -1); } public void _Up5() { MoveRow(5, -1); }
    public void _Up6() { MoveRow(6, -1); } public void _Up7() { MoveRow(7, -1); } public void _Up8() { MoveRow(8, -1); }
    public void _Up9() { MoveRow(9, -1); }
    public void _Down0() { MoveRow(0, 1); } public void _Down1() { MoveRow(1, 1); } public void _Down2() { MoveRow(2, 1); }
    public void _Down3() { MoveRow(3, 1); } public void _Down4() { MoveRow(4, 1); } public void _Down5() { MoveRow(5, 1); }
    public void _Down6() { MoveRow(6, 1); } public void _Down7() { MoveRow(7, 1); } public void _Down8() { MoveRow(8, 1); }
    public void _Down9() { MoveRow(9, 1); }
    public void _Delete0() { DeleteRow(0); } public void _Delete1() { DeleteRow(1); } public void _Delete2() { DeleteRow(2); }
    public void _Delete3() { DeleteRow(3); } public void _Delete4() { DeleteRow(4); } public void _Delete5() { DeleteRow(5); }
    public void _Delete6() { DeleteRow(6); } public void _Delete7() { DeleteRow(7); } public void _Delete8() { DeleteRow(8); }
    public void _Delete9() { DeleteRow(9); }

    private void RefreshPlaylist()
    {
        if (pitcher == null) return;
        int count = pitcher.PlaylistCount();
        int next = pitcher.NextIndex();
        if (playlistCountText != null) playlistCountText.text = count + " / " + PitchPlaylist.MaxItems;
        if (emptyHint != null) emptyHint.SetActive(count == 0);
        for (int i = 0; i < rows.Length; i++)
        {
            bool shown = i < count;
            if (rows[i] != null && rows[i].activeSelf != shown) rows[i].SetActive(shown);
            if (!shown) continue;
            int item = pitcher.PlaylistItem(i);
            bool done = pitcher.PlaylistDone(i);
            bool isNext = i == next;
            if (i < rowNumberTexts.Length && rowNumberTexts[i] != null) rowNumberTexts[i].text = (i + 1).ToString();
            if (i < rowTypeTexts.Length && rowTypeTexts[i] != null) rowTypeTexts[i].text = TypeName(PitchPlaylist.Type(item));
            if (i < rowCourseTexts.Length && rowCourseTexts[i] != null) rowCourseTexts[i].text = CourseLabel(PitchPlaylist.Mask(item));
            if (i < rowStatusTexts.Length && rowStatusTexts[i] != null) rowStatusTexts[i].text = isNext ? "NEXT" : done ? "DONE" : "";
            if (i < rowStatusImages.Length && rowStatusImages[i] != null)
            {
                rowStatusImages[i].enabled = isNext || done;
                rowStatusImages[i].color = isNext ? accentColor : doneColor;
            }
            if (i < rowImages.Length && rowImages[i] != null)
                rowImages[i].color = editing && PitchPlaylist.Id(item) == editItemId ? editingRowColor : isNext ? nextColor : rowColor;
            if (i < rowUpButtons.Length && rowUpButtons[i] != null) rowUpButtons[i].interactable = i > 0;
            if (i < rowDownButtons.Length && rowDownButtons[i] != null) rowDownButtons[i].interactable = i < count - 1;
        }
    }

    public string TypeName(int type)
    {
        return type >= 0 && type < pitchTypeNames.Length ? pitchTypeNames[type] : "Four-Seam";
    }

    // "Zone 5" for one zone, "Zones 1 3 7" for several, "All zones" for all nine.
    public string CourseLabel(int mask)
    {
        int count = PitchPlaylist.CountCourses(mask);
        if (count == 0) return "Zone 5 (default)";
        if (count == 9) return "All zones";
        string label = count == 1 ? "Zone" : "Zones";
        for (int i = 0; i < 9; i++) if ((mask & (1 << i)) != 0) label += " " + (i + 1);
        return label;
    }

    // ---- Pitch editor ----

    private void BeginEdit(int itemId, int type, int mask)
    {
        editing = true;
        editItemId = itemId;
        draftType = type;
        draftMask = mask & PitchPlaylist.CourseBits;
        saveToken = 0;
        if (editorMessageText != null) editorMessageText.text = "";
        ShowEditor(true);
        RefreshEditor();
        RefreshPlaylist();
    }

    private void ToggleZone(int index)
    {
        if (!editing) return;
        draftMask ^= 1 << index;
        RefreshEditor();
    }

    public void _Zone0() { ToggleZone(0); } public void _Zone1() { ToggleZone(1); } public void _Zone2() { ToggleZone(2); }
    public void _Zone3() { ToggleZone(3); } public void _Zone4() { ToggleZone(4); } public void _Zone5() { ToggleZone(5); }
    public void _Zone6() { ToggleZone(6); } public void _Zone7() { ToggleZone(7); } public void _Zone8() { ToggleZone(8); }

    public void _ClearZones()
    {
        draftMask = 0;
        RefreshEditor();
    }

    public void _PitchTypeChanged()
    {
        if (applying || pitchTypeDropdown == null) return;
        int option = pitchTypeDropdown.value;
        if (option >= 0 && option < pitchTypeOrder.Length) draftType = pitchTypeOrder[option];
    }

    public void _CancelEdit()
    {
        editing = false;
        saveToken = 0;
        ShowEditorMessage("");
        ShowEditor(false);
        RefreshPlaylist();
    }

    // The draft stays open until the Owner confirms the save; a full list keeps it for another try.
    public void _SaveEdit()
    {
        if (!editing || pitcher == null || saveToken != 0) return;
        bool exists = editItemId != 0 && pitcher.IndexOfItem(editItemId) >= 0;
        if (!exists && pitcher.PlaylistCount() >= PitchPlaylist.MaxItems)
        {
            ShowEditorMessage("Playlist is full (10 / 10). Delete a pitch, then save again.");
            return;
        }
        ShowEditorMessage("Saving...");
        saveToken = pitcher.RequestSaveItem(editItemId, draftType, draftMask);
        CheckSave();
    }

    private void CheckSave()
    {
        if (saveToken == 0 || pitcher == null) return;
        int result = pitcher.SaveResult(saveToken);
        if (result == BattingPitcherBehaviour.SavePending) return;
        saveToken = 0;
        if (result == BattingPitcherBehaviour.SaveDone)
        {
            ShowEditorMessage("");
            editing = false;
            ShowEditor(false);
            return;
        }
        ShowEditorMessage(result == BattingPitcherBehaviour.SaveFull
            ? "Playlist is full (10 / 10). Delete a pitch, then save again."
            : "Another player changed the playlist first. Save again.");
    }

    private void ShowEditorMessage(string message)
    {
        if (editorMessageText != null) editorMessageText.text = message;
    }

    private void RefreshEditor()
    {
        if (!editing) return;
        if (editorTitleText != null)
        {
            int index = editItemId == 0 || pitcher == null ? -1 : pitcher.IndexOfItem(editItemId);
            editorTitleText.text = editItemId == 0 ? "NEW PITCH" : index >= 0 ? "EDIT PITCH #" + (index + 1) : "EDIT PITCH (REMOVED: SAVE ADDS IT)";
        }
        for (int i = 0; i < zoneImages.Length && i < 9; i++)
            if (zoneImages[i] != null) zoneImages[i].color = (draftMask & (1 << i)) != 0 ? accentColor : idleColor;
        int zones = PitchPlaylist.CountCourses(draftMask);
        if (zoneCountText != null) zoneCountText.text = zones == 0 ? "0 selected (pitches to Zone 5)" : zones + " selected";
        if (pitchTypeDropdown != null)
        {
            int option = 0;
            for (int i = 0; i < pitchTypeOrder.Length; i++) if (pitchTypeOrder[i] == draftType) option = i;
            applying = true;
            if (pitchTypeDropdown.value != option) pitchTypeDropdown.value = option;
            applying = false;
        }
    }
}
