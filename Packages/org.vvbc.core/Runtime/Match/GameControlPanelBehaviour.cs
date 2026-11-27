using TMPro;
using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

// Match operation and display (GameControlPanel prefab, docs/development/match-state.md).
//
// Local only, nothing synced. It shows the effective state (live) or the committed state
// (dead), the play and revisions, the sync state and the umpire, and turns presses into
// MatchControllerBehaviour / ProductionMatchOperatorAuthorityBehaviour submissions. Buttons a
// player may not use are disabled, and the Owner checks every request again anyway.
// Anyone may ask for Time. Manual corrections are edited in a local buffer and applied in one
// request. The ruling buttons for development appear only for the umpire when the controller
// allows development rulings.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class GameControlPanelBehaviour : TsukimiBehaviour
{
    // Fields of the correction buffer.
    public const int FieldPhase = 0;
    public const int FieldInning = 1;
    public const int FieldHalf = 2;
    public const int FieldOuts = 3;
    public const int FieldBalls = 4;
    public const int FieldStrikes = 5;
    public const int FieldScoreInning = 6;   // which inning the two run fields edit
    public const int FieldAwayRuns = 7;
    public const int FieldHomeRuns = 8;
    public const int FieldAwayHits = 9;
    public const int FieldHomeHits = 10;
    public const int FieldAwayErrors = 11;
    public const int FieldHomeErrors = 12;
    public const int FieldAwayCursor = 13;
    public const int FieldHomeCursor = 14;
    public const int FieldBatter = 15;
    public const int Field1B = 16;
    public const int Field2B = 17;
    public const int Field3B = 18;
    public const int FieldAttention = 19;
    public const int FieldResult = 20;
    public const int FieldCount = 21;

    public MatchControllerBehaviour controller;
    public ProductionMatchOperatorAuthorityBehaviour authority;

    [Header("Display")]
    public TextMeshProUGUI statusText;
    public TextMeshProUGUI scoreText;
    public TextMeshProUGUI situationText;
    public TextMeshProUGUI playersText;
    public TextMeshProUGUI umpireText;
    public TextMeshProUGUI answerText;
    public TextMeshProUGUI editText;
    public TextMeshProUGUI rulingText;

    [Header("Match")]
    public Button initializeButton;
    public Button startMatchButton;
    public Button endMatchButton;
    public Button startLiveButton;
    public Button timeButton;
    public Button forceTimeButton;
    public Button transferTargetButton;
    public Button transferButton;
    [Tooltip("Correction buttons (load, field, -/+, apply): enabled for the umpire while dead.")]
    public Button[] editButtons = new Button[0];
    [Tooltip("Shown only for the umpire when the controller allows development rulings.")]
    public GameObject developmentRulings;
    public int nameLimit = 14;

    private readonly int[] _view = new int[MatchRules.Length];
    private readonly int[] _edit = new int[MatchRules.Length];
    private bool _editLoaded;
    private int _editField = FieldInning;
    private int _editInning = 1;
    private int _transferTarget;
    private int _rulingBase;        // base of the runner the runner rulings name (0 = batter-runner)
    private int _rulingTarget = 1;  // base the force / advance / award rulings name
    private bool _registered;
    private const int MaxPlayers = 128;
    private readonly VRCPlayerApi[] _players = new VRCPlayerApi[MaxPlayers];

    void Start()
    {
        Register();
        Refresh();
    }

    void OnEnable()
    {
        if (_registered) Refresh();
    }

    private void Register()
    {
        if (_registered) return;
        _registered = true;
        UdonBehaviour self = (UdonBehaviour)GetComponent(typeof(UdonBehaviour));
        if (controller != null) controller.AddChangeListener(self);
        if (authority != null) authority.AddChangeListener(self);
    }

    public void _OnMatchChanged() { Refresh(); }
    public void _OnAuthorityChanged() { Refresh(); }

    // ---- Buttons ----

    public void _Initialize()
    {
        // After a recovery that found only invalid states, Initialize is the umpire's reset.
        if (controller != null && controller.IsRecoveryFailed() && controller.IsLocalUmpire()) controller.SubmitRecoveryReset();
        else if (Umpire()) controller.SubmitInitializeMatch();
    }
    public void _StartMatch() { if (Umpire()) controller.SubmitStartMatch(); }
    public void _EndMatch() { if (Umpire()) controller.SubmitEndMatch(ResultByScore(controller.GetRuns(0), controller.GetRuns(1))); }
    public void _StartLive() { if (Umpire()) controller.SubmitStartLive(); }
    public void _Time() { if (controller != null && controller.IsLive()) controller.SubmitTime(); }
    public void _ForceTime() { if (Umpire() && controller.IsLive()) controller.SubmitForceTime(); }

    public void _NextTransferTarget()
    {
        _transferTarget = NextPlayer(_transferTarget, true);
        Refresh();
    }

    public void _Transfer()
    {
        if (authority == null || _transferTarget <= 0) return;
        authority.SubmitTransfer(_transferTarget);
    }

    public void _EditLoad()
    {
        if (controller == null || !controller.CopyMatchState(_edit)) return;
        _editLoaded = true;
        _editInning = _edit[MatchRules.Inning];
        Refresh();
    }

    public void _EditPrevField() { _editField = (_editField + FieldCount - 1) % FieldCount; Refresh(); }
    public void _EditNextField() { _editField = (_editField + 1) % FieldCount; Refresh(); }
    public void _EditMinus() { Step(-1); }
    public void _EditPlus() { Step(1); }

    public void _EditApply()
    {
        if (!_editLoaded || !Umpire()) return;
        controller.SubmitCorrection(_edit, _editField);
        _editLoaded = false;
        Refresh();
    }

    private void Step(int delta)
    {
        if (!_editLoaded) return;
        if (_editField == FieldScoreInning)
            _editInning = Mathf.Clamp(_editInning + delta, 1, MatchRules.MaxInnings);
        else if (IsPlayerField(_editField))
            SetPlayerField(_edit, _editField, delta > 0 ? NextPlayer(PlayerField(_edit, _editField), false) : 0);
        else
            StepField(_edit, _editField, delta, _editInning);
        Refresh();
    }

    // Development rulings (the umpire, with allowDevelopmentRulings).
    public void _RulingBall() { Ruling(PlayRules.JudgeBall, false); }
    public void _RulingStrike() { Ruling(PlayRules.JudgeCalledStrike, false); }
    public void _RulingFoul() { Ruling(PlayRules.JudgeFoul, false); }
    public void _RulingHitByPitch() { Ruling(PlayRules.JudgeHitByPitch, false); }
    public void _RulingWalk() { Ruling(PlayRules.JudgeWalk, false); }
    public void _RulingStrikeoutCaught() { Ruling(PlayRules.JudgeStrikeoutCaught, false); }
    public void _RulingThirdStrikeUncaught() { Ruling(PlayRules.JudgeThirdStrikeUncaught, false); }
    public void _RulingFair() { Ruling(PlayRules.JudgeFairBall, false); }
    public void _RulingCatch() { Ruling(PlayRules.JudgeCatch, _rulingBase == 0); }
    public void _RulingForceOut() { Ruling(PlayRules.JudgeForceOut, true); }
    public void _RulingForceSafe() { Ruling(PlayRules.JudgeForceSafe, true); }
    public void _RulingTagOut() { Ruling(PlayRules.JudgeTagOut, true); }
    public void _RulingAdvance() { Ruling(PlayRules.JudgeRunnerAdvance, true); }
    public void _RulingRunScored() { Ruling(PlayRules.JudgeRunScored, true); }
    public void _RulingAward() { Ruling(PlayRules.RulingAwardBase, true); }
    public void _RulingOut() { Ruling(PlayRules.RulingOut, true); }
    public void _RulingHit() { if (Umpire()) controller.SubmitRuling(PlayRules.RulingHit, 0, controller.GetOffenseTeam()); }
    public void _RulingError() { if (Umpire()) controller.SubmitRuling(PlayRules.RulingError, 0, controller.GetDefenseTeam()); }
    public void _RulingClearPending() { Ruling(PlayRules.RulingClearPending, false); }
    public void _NextRulingRunner() { _rulingBase = (_rulingBase + 1) % 4; Refresh(); }
    public void _NextRulingTarget() { _rulingTarget = _rulingTarget % 4 + 1; Refresh(); }

    // withRunner: the ruling names the runner on _rulingBase (Catch with a batter-runner).
    private void Ruling(int code, bool withRunner)
    {
        if (!Umpire() || !controller.IsLive()) return;
        int runner = withRunner ? controller.GetRunnerIdOnBase(_rulingBase) : 0;
        if (withRunner && runner == 0 && code != PlayRules.RulingOut) return;
        controller.SubmitRuling(code, runner, _rulingTarget);
    }

    private bool Umpire()
    {
        return controller != null && controller.IsMatchReady() && controller.IsLocalUmpire();
    }

    // ---- Pure rules (tested) ----

    public static int ResultByScore(int away, int home)
    {
        if (away > home) return MatchRules.ResultAwayWin;
        if (home > away) return MatchRules.ResultHomeWin;
        return MatchRules.ResultTie;
    }

    public static bool IsPlayerField(int field)
    {
        return field == FieldBatter || field == Field1B || field == Field2B || field == Field3B;
    }

    public static int PlayerField(int[] m, int field)
    {
        if (field == FieldBatter) return m[MatchRules.PaBatter];
        if (field >= Field1B && field <= Field3B) return m[MatchRules.Runners + field - Field1B];
        return 0;
    }

    public static void SetPlayerField(int[] m, int field, int playerId)
    {
        if (field == FieldBatter)
        {
            m[MatchRules.PaBatter] = playerId;
            if (playerId > 0 && m[MatchRules.PaId] == 0) m[MatchRules.PaId] = 1;
        }
        else if (field >= Field1B && field <= Field3B) m[MatchRules.Runners + field - Field1B] = playerId;
    }

    // The value a numeric field shows (cursors as orders 1..9).
    public static int FieldValue(int[] m, int field, int inning)
    {
        switch (field)
        {
            case FieldPhase: return m[MatchRules.Phase];
            case FieldInning: return m[MatchRules.Inning];
            case FieldHalf: return m[MatchRules.Half];
            case FieldOuts: return m[MatchRules.Outs];
            case FieldBalls: return m[MatchRules.Balls];
            case FieldStrikes: return m[MatchRules.Strikes];
            case FieldScoreInning: return inning;
            case FieldAwayRuns: return MatchRules.RunsIn(m, 0, inning);
            case FieldHomeRuns: return MatchRules.RunsIn(m, 1, inning);
            case FieldAwayHits: return m[MatchRules.Hits];
            case FieldHomeHits: return m[MatchRules.Hits + 1];
            case FieldAwayErrors: return m[MatchRules.Errors];
            case FieldHomeErrors: return m[MatchRules.Errors + 1];
            case FieldAwayCursor: return m[MatchRules.Cursor] + 1;
            case FieldHomeCursor: return m[MatchRules.Cursor + 1] + 1;
            case FieldAttention: return m[MatchRules.Attention];
            case FieldResult: return m[MatchRules.Result];
        }
        return PlayerField(m, field);
    }

    // Moves a numeric field within its range. Nothing is checked against the other fields
    // here: the Owner rejects an inconsistent proposal and names the broken rule.
    public static void StepField(int[] m, int field, int delta, int inning)
    {
        switch (field)
        {
            case FieldPhase: m[MatchRules.Phase] = Mathf.Clamp(m[MatchRules.Phase] + delta, 0, 2); return;
            case FieldInning: m[MatchRules.Inning] = Mathf.Clamp(m[MatchRules.Inning] + delta, 1, MatchRules.MaxInnings); return;
            case FieldHalf: m[MatchRules.Half] = 1 - m[MatchRules.Half]; return;
            case FieldOuts: m[MatchRules.Outs] = Mathf.Clamp(m[MatchRules.Outs] + delta, 0, 2); return;
            case FieldBalls: m[MatchRules.Balls] = Mathf.Clamp(m[MatchRules.Balls] + delta, 0, 3); return;
            case FieldStrikes: m[MatchRules.Strikes] = Mathf.Clamp(m[MatchRules.Strikes] + delta, 0, 2); return;
            case FieldAwayRuns: StepRuns(m, 0, inning, delta); return;
            case FieldHomeRuns: StepRuns(m, 1, inning, delta); return;
            case FieldAwayHits: m[MatchRules.Hits] = Mathf.Max(0, m[MatchRules.Hits] + delta); return;
            case FieldHomeHits: m[MatchRules.Hits + 1] = Mathf.Max(0, m[MatchRules.Hits + 1] + delta); return;
            case FieldAwayErrors: m[MatchRules.Errors] = Mathf.Max(0, m[MatchRules.Errors] + delta); return;
            case FieldHomeErrors: m[MatchRules.Errors + 1] = Mathf.Max(0, m[MatchRules.Errors + 1] + delta); return;
            case FieldAwayCursor: m[MatchRules.Cursor] = (m[MatchRules.Cursor] + delta + RosterRules.OrderCount) % RosterRules.OrderCount; return;
            case FieldHomeCursor: m[MatchRules.Cursor + 1] = (m[MatchRules.Cursor + 1] + delta + RosterRules.OrderCount) % RosterRules.OrderCount; return;
            case FieldAttention: if (delta < 0) m[MatchRules.Attention] = 0; return;
            case FieldResult: m[MatchRules.Result] = Mathf.Clamp(m[MatchRules.Result] + delta, 0, 3); return;
        }
    }

    private static void StepRuns(int[] m, int team, int inning, int delta)
    {
        if (!MatchRules.ValidInning(inning)) return;
        int at = MatchRules.InningRunsIndex(team, inning);
        m[at] = Mathf.Max(0, m[at] + delta);
    }

    public static string FieldName(int field)
    {
        switch (field)
        {
            case FieldPhase: return "Phase";
            case FieldInning: return "Inning";
            case FieldHalf: return "Top/Bottom";
            case FieldOuts: return "Outs";
            case FieldBalls: return "Balls";
            case FieldStrikes: return "Strikes";
            case FieldScoreInning: return "Runs of inning";
            case FieldAwayRuns: return "Away runs";
            case FieldHomeRuns: return "Home runs";
            case FieldAwayHits: return "Away H";
            case FieldHomeHits: return "Home H";
            case FieldAwayErrors: return "Away E";
            case FieldHomeErrors: return "Home E";
            case FieldAwayCursor: return "Away order";
            case FieldHomeCursor: return "Home order";
            case FieldBatter: return "Batter";
            case Field1B: return "1B runner";
            case Field2B: return "2B runner";
            case Field3B: return "3B runner";
            case FieldAttention: return "Attention (- clears)";
            case FieldResult: return "Result";
        }
        return "";
    }

    // "  1 2 3 ... | R H E" lines for innings 1..count.
    public static string ScoreLines(int[] m, int count)
    {
        string head = "     ";
        string away = "AWAY ";
        string home = "HOME ";
        for (int i = 1; i <= count; i++)
        {
            head += Pad(i.ToString());
            away += Pad(MatchRules.InningReached(m, 0, i) ? MatchRules.RunsIn(m, 0, i).ToString() : "");
            home += Pad(MatchRules.InningReached(m, 1, i) ? MatchRules.RunsIn(m, 1, i).ToString() : "");
        }
        head += "|  R  H  E";
        away += "|" + Pad(MatchRules.TeamRuns(m, 0).ToString()) + Pad(m[MatchRules.Hits].ToString()) + Pad(m[MatchRules.Errors].ToString());
        home += "|" + Pad(MatchRules.TeamRuns(m, 1).ToString()) + Pad(m[MatchRules.Hits + 1].ToString()) + Pad(m[MatchRules.Errors + 1].ToString());
        return head + "\n" + away + "\n" + home;
    }

    private static string Pad(string text)
    {
        while (text.Length < 3) text = " " + text;
        return text;
    }

    public static string SituationLine(int[] m)
    {
        string half = m[MatchRules.Half] == MatchRules.HalfTop ? "Top" : "Bottom";
        return half + " " + m[MatchRules.Inning] + "   B " + m[MatchRules.Balls] + "  S " + m[MatchRules.Strikes] + "  O " + m[MatchRules.Outs];
    }

    public static string PhaseName(int phase, int result)
    {
        if (phase == MatchRules.PhaseNotStarted) return "Not started";
        if (phase == MatchRules.PhaseInProgress) return "In progress";
        if (result == MatchRules.ResultAwayWin) return "Final: Away wins";
        if (result == MatchRules.ResultHomeWin) return "Final: Home wins";
        return "Final: tie";
    }

    // ---- Display ----

    private void Refresh()
    {
        if (controller == null) return;
        bool ready = controller.IsMatchReady();
        bool live = controller.IsLive();
        bool umpire = ready && controller.IsLocalUmpire();
        if (!ready || !controller.CopyEffectiveState(_view))
        {
            bool failed = controller.IsRecoveryFailed();
            SetText(statusText, failed ? "Recovery found no valid match state.\nUmpire: Initialize starts a new match (flagged)."
                : controller.IsRecovering() ? "Recovering the match state…" : "Syncing the match…");
            SetText(scoreText, "");
            SetText(situationText, "");
            SetText(playersText, "");
            SetButtons(false, false, false);
            SetInteractable(initializeButton, failed && controller.IsLocalUmpire());
            return;
        }
        string status = (live ? "LIVE" : "DEAD") + "   " + PhaseName(_view[MatchRules.Phase], _view[MatchRules.Result])
            + "\nplay " + controller.GetPlayId() + " (rev " + controller.GetPlayRevision() + ")   match rev " + controller.GetMatchRevision()
            + "   last committed " + controller.GetLastCommittedPlayId();
        if (controller.IsPreparingLive()) status += "\nLocking the roster…";
        if (live && !controller.RosterLockHolds()) status += "\nRoster lock lost: rulings paused";
        if (controller.GetLastStartRejection() != 0) status += "\nLive start refused (" + controller.GetLastStartRejection() + ")";
        int attention = controller.GetAttention();
        if (attention != 0) status += "\nNeeds the umpire (attention " + attention + ")";
        if (controller.IsHalfEndPending()) status += "\nThird out: Time to change sides";
        if (controller.IsStrikeoutPending()) status += "\nThird strike: caught or not?";
        if (controller.GetSerializationFailureStreak() > 0) status += "\n(match send retrying)";
        SetText(statusText, status);
        // Monospaced so the inning columns line up whatever the font.
        SetText(scoreText, "<mspace=0.6em>" + ScoreLines(_view, Mathf.Min(controller.GetInningsToShow(), MatchRules.MaxInnings)));
        SetText(situationText, SituationLine(_view));
        string players = "Batter: " + Name(_view[MatchRules.PaBatter]) + "   (PA " + _view[MatchRules.PaId] + ")";
        for (int b = 1; b <= 3; b++) players += "\n" + b + "B: " + Name(_view[MatchRules.Runners + b - 1]);
        if (live) players += "\nPitch " + controller.GetPitchSequence() + "   pending rulings " + controller.GetPendingRulingCount();
        SetText(playersText, players);
        SetText(umpireText, "Umpire: " + Name(authority != null ? authority.GetUmpireId() : 0)
            + (umpire ? "  (you)" : "") + "   hand over to: " + Name(_transferTarget));
        if (controller.GetLastCommand() != 0)
            SetText(answerText, "Last request " + controller.GetLastCommand() + ": " + controller.GetLastCommandResult());
        if (_editLoaded)
            SetText(editText, "Edit  " + FieldName(_editField) + " = "
                + (IsPlayerField(_editField) ? Name(PlayerField(_edit, _editField)) : FieldValue(_edit, _editField, _editInning).ToString())
                + "   (inning " + _editInning + ")");
        else SetText(editText, umpire && !live ? "Load the state to correct it" : "");
        SetText(rulingText, "Runner on " + (_rulingBase == 0 ? "home" : _rulingBase + "B") + ": " + Name(PlayerOnRulingBase())
            + "   target " + (_rulingTarget == 4 ? "home" : _rulingTarget + "B"));
        SetButtons(true, live, umpire);
    }

    private int PlayerOnRulingBase()
    {
        int runner = controller.GetRunnerIdOnBase(_rulingBase);
        for (int s = 0; s < RunnerRules.SlotCount; s++)
            if (runner != 0 && controller.GetRunnerId(s) == runner) return controller.GetRunnerPlayer(s);
        return 0;
    }

    private void SetButtons(bool ready, bool live, bool umpire)
    {
        bool dead = ready && !live && !controller.IsPreparingLive();
        int phase = _view[MatchRules.Phase];
        SetInteractable(initializeButton, umpire && dead);
        SetInteractable(startMatchButton, umpire && dead && phase == MatchRules.PhaseNotStarted);
        SetInteractable(endMatchButton, umpire && dead && phase == MatchRules.PhaseInProgress);
        SetInteractable(startLiveButton, umpire && dead && phase == MatchRules.PhaseInProgress);
        SetInteractable(timeButton, ready && live);
        SetInteractable(forceTimeButton, umpire && live);
        SetInteractable(transferTargetButton, umpire);
        SetInteractable(transferButton, umpire && _transferTarget > 0);
        for (int i = 0; i < editButtons.Length; i++) SetInteractable(editButtons[i], umpire && dead);
        if (developmentRulings != null) developmentRulings.SetActive(umpire && controller.allowDevelopmentRulings);
    }

    private static void SetInteractable(Button button, bool on)
    {
        if (button != null) button.interactable = on;
    }

    private static void SetText(TextMeshProUGUI text, string value)
    {
        if (text != null) text.text = value;
    }

    // The present player after `after` in id order, wrapping (skipLocal leaves the local player out).
    private int NextPlayer(int after, bool skipLocal)
    {
        int count = VRCPlayerApi.GetPlayerCount();
        VRCPlayerApi.GetPlayers(_players);
        int best = 0;
        int first = 0;
        int local = LocalId();
        for (int i = 0; i < _players.Length && i < count; i++)
        {
            VRCPlayerApi player = _players[i];
            if (!Utilities.IsValid(player)) continue;
            int id = player.playerId;
            if (skipLocal && id == local) continue;
            if (first == 0 || id < first) first = id;
            if (id > after && (best == 0 || id < best)) best = id;
        }
        return best != 0 ? best : first;
    }

    private string Name(int playerId)
    {
        if (playerId <= 0) return "-";
        VRCPlayerApi player = VRCPlayerApi.GetPlayerById(playerId);
        return Utilities.IsValid(player) ? RosterRules.ShortName(player.displayName, nameLimit) : "#" + playerId;
    }

    private int LocalId()
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        return Utilities.IsValid(local) ? local.playerId : 0;
    }
}
