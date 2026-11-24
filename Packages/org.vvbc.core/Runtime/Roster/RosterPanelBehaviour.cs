using TMPro;
using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

// Local UI of one team's roster (RosterPanel prefab): the field with ten position buttons and
// their TP buttons, and the 1-9 batting order with up/down buttons on each row. It also moves
// the local player to the team's dugout after a respawn.
//
// Nothing here is synced. Presses become RosterManagerBehaviour submissions; the display is
// rebuilt only from the manager's tables when it reports a change (or a request times out),
// and a pressed button shows Pending until the Owner's result arrives.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class RosterPanelBehaviour : TsukimiBehaviour
{
    public const int StateSyncing = 0;
    public const int StateEmpty = 1;
    public const int StateMine = 2;
    public const int StateOther = 3;
    public const int StateOtherOperable = 4;
    public const int StatePending = 5;

    private const int PendingNone = 0;
    private const int PendingRole = 1;
    private const int PendingOrder = 2;
    private const int PendingMove = 3;

    public RosterManagerBehaviour manager;
    [Tooltip("0 = Away (bats first), 1 = Home.")]
    public int team = RosterRules.TeamAway;

    [Header("Teleport")]
    [Tooltip("Where each position's TP button sends the local player, by role: P, C, 1B, 2B, 3B, SS, LF, CF, RF, DH.")]
    public Transform[] positionTeleportPoints = new Transform[RosterRules.RoleCount];
    [Tooltip("Dugout spawn of this team. Registered players are moved here after they respawn. Leave empty to keep the VRChat spawn.")]
    public Transform respawnPoint;

    [Header("Header")]
    public TextMeshProUGUI teamNameText;
    public TextMeshProUGUI teamIndicatorText;
    public Image teamIndicatorImage;
    public string awayName = "AWAY";
    public string homeName = "HOME";
    public string awayIndicator = "BATS FIRST";
    public string homeIndicator = "BATS SECOND";
    public Color awayColor = new Color(0.86f, 0.33f, 0.27f, 1f);
    public Color homeColor = new Color(0.2f, 0.45f, 0.85f, 1f);

    [Header("Positions (by role)")]
    public Button[] roleButtons = new Button[0];
    public Image[] roleButtonImages = new Image[0];
    public TextMeshProUGUI[] roleNameTexts = new TextMeshProUGUI[0];

    [Header("Batting order (rows 1-9)")]
    public Button[] orderButtons = new Button[0];
    public Image[] orderButtonImages = new Image[0];
    public TextMeshProUGUI[] orderNameTexts = new TextMeshProUGUI[0];
    public TextMeshProUGUI[] orderRoleTexts = new TextMeshProUGUI[0];
    public Button[] orderUpButtons = new Button[0];
    public Button[] orderDownButtons = new Button[0];

    [Header("Footer")]
    public TextMeshProUGUI statusText;

    [Header("Look")]
    public string entryText = "Entry";
    public string pendingText = "Pending…";
    public string syncingText = "Syncing…";
    [Tooltip("Longest name shown before it is cut with an ellipsis; TMP auto-size shrinks it further.")]
    public int nameLimit = 18;
    [Tooltip("Seconds to wait for the Owner before a pressed button is released again.")]
    public float pendingTimeoutSeconds = 4f;
    public Color emptyColor = new Color(0.97f, 0.97f, 0.93f, 1f);
    public Color mineColor = new Color(1f, 0.82f, 0.25f, 1f);
    public Color otherColor = new Color(0.78f, 0.82f, 0.8f, 1f);
    public Color operableColor = new Color(1f, 0.6f, 0.45f, 1f);
    public Color pendingColor = new Color(0.65f, 0.85f, 1f, 1f);
    public Color syncingColor = new Color(0.6f, 0.6f, 0.6f, 1f);
    public Color rowColor = new Color(0.97f, 0.97f, 0.93f, 1f);
    public Color rowAltColor = new Color(0.9f, 0.92f, 0.88f, 1f);

    private int _pendingKind;
    private int _pendingIndex = -1;
    private int _pendingRevision;
    private float _pendingDeadline;
    private bool _registered;

    void Start()
    {
        RegisterWithManager();
        ApplyHeader();
        Refresh();
    }

    void OnEnable()
    {
        if (_registered) Refresh();
    }

    private void RegisterWithManager()
    {
        if (_registered || manager == null) return;
        _registered = true;
        manager.AddChangeListener((UdonBehaviour)GetComponent(typeof(UdonBehaviour)));
    }

    // ---- Manager notification ----

    public void _OnRosterChanged()
    {
        if (_pendingKind != PendingNone && manager != null && manager.GetRevision() != _pendingRevision)
            ClearPending();
        Refresh();
    }

    public void _PendingTimeout()
    {
        if (_pendingKind == PendingNone) return;
        // A timer left by an earlier press fires early: wait for the rest of this one.
        float remaining = _pendingDeadline - Time.time;
        if (remaining > 0.05f)
        {
            SendCustomEventDelayedSeconds(nameof(_PendingTimeout), remaining);
            return;
        }
        // No change arrived: the Owner rejected the request or it was lost. Show the state again.
        ClearPending();
        Refresh();
    }

    // ---- Buttons ----

    public void _Role0() { PressRole(0); } public void _Role1() { PressRole(1); } public void _Role2() { PressRole(2); }
    public void _Role3() { PressRole(3); } public void _Role4() { PressRole(4); } public void _Role5() { PressRole(5); }
    public void _Role6() { PressRole(6); } public void _Role7() { PressRole(7); } public void _Role8() { PressRole(8); }
    public void _Role9() { PressRole(9); }

    public void _Tp0() { Teleport(0); } public void _Tp1() { Teleport(1); } public void _Tp2() { Teleport(2); }
    public void _Tp3() { Teleport(3); } public void _Tp4() { Teleport(4); } public void _Tp5() { Teleport(5); }
    public void _Tp6() { Teleport(6); } public void _Tp7() { Teleport(7); } public void _Tp8() { Teleport(8); }
    public void _Tp9() { Teleport(9); }

    public void _Order0() { PressOrder(0); } public void _Order1() { PressOrder(1); } public void _Order2() { PressOrder(2); }
    public void _Order3() { PressOrder(3); } public void _Order4() { PressOrder(4); } public void _Order5() { PressOrder(5); }
    public void _Order6() { PressOrder(6); } public void _Order7() { PressOrder(7); } public void _Order8() { PressOrder(8); }

    public void _Up0() { PressMove(0, -1); } public void _Up1() { PressMove(1, -1); } public void _Up2() { PressMove(2, -1); }
    public void _Up3() { PressMove(3, -1); } public void _Up4() { PressMove(4, -1); } public void _Up5() { PressMove(5, -1); }
    public void _Up6() { PressMove(6, -1); } public void _Up7() { PressMove(7, -1); } public void _Up8() { PressMove(8, -1); }

    public void _Down0() { PressMove(0, 1); } public void _Down1() { PressMove(1, 1); } public void _Down2() { PressMove(2, 1); }
    public void _Down3() { PressMove(3, 1); } public void _Down4() { PressMove(4, 1); } public void _Down5() { PressMove(5, 1); }
    public void _Down6() { PressMove(6, 1); } public void _Down7() { PressMove(7, 1); } public void _Down8() { PressMove(8, 1); }

    private void PressRole(int role)
    {
        if (_pendingKind != PendingNone || manager == null) return;
        int local = LocalId();
        if (RoleState(manager.GetPlayerAtRole(team, role), local, manager.IsLocalOperator(), manager.IsRosterReady(), false) == StateOther) return;
        int before = manager.GetRevision();
        if (!manager.SubmitRole(team, role)) return;
        BeginPending(PendingRole, role, before);
    }

    private void PressOrder(int row)
    {
        if (_pendingKind != PendingNone || manager == null) return;
        int order = row + RosterRules.FirstOrder;
        if (!OrderPressAllowed(manager.GetBattingKeyAtOrder(team, order), manager.GetBattingKeyOf(team, LocalId()), manager.IsRosterReady())) return;
        int before = manager.GetRevision();
        if (!manager.SubmitOrder(team, order)) return;
        BeginPending(PendingOrder, row, before);
    }

    private void PressMove(int row, int direction)
    {
        if (_pendingKind != PendingNone || manager == null) return;
        int order = row + RosterRules.FirstOrder;
        if (!MoveAllowed(row, direction, manager.GetBattingKeyAtOrder(team, order), manager.GetBattingKeyOf(team, LocalId()), manager.IsLocalOperator(), manager.IsRosterReady())) return;
        int before = manager.GetRevision();
        if (!manager.SubmitOrderMove(team, order, direction)) return;
        BeginPending(PendingMove, row, before);
    }

    // revisionBefore is read before the submission: the Owner's own request is applied on the
    // spot, so its change may already have arrived when this runs.
    private void BeginPending(int kind, int index, int revisionBefore)
    {
        _pendingKind = kind;
        _pendingIndex = index;
        _pendingRevision = revisionBefore;
        float timeout = Mathf.Max(0.5f, pendingTimeoutSeconds);
        _pendingDeadline = Time.time + timeout;
        SendCustomEventDelayedSeconds(nameof(_PendingTimeout), timeout);
        if (manager.GetRevision() != _pendingRevision) ClearPending();
        Refresh();
    }

    private void ClearPending()
    {
        _pendingKind = PendingNone;
        _pendingIndex = -1;
    }

    // Moves the local player to the position's point. Registration and team are unchanged.
    private void Teleport(int role)
    {
        if (role < 0 || role >= positionTeleportPoints.Length) return;
        Transform point = positionTeleportPoints[role];
        VRCPlayerApi local = Networking.LocalPlayer;
        if (point == null || !Utilities.IsValid(local)) return;
        local.TeleportTo(point.position, point.rotation);
    }

    // ---- Team respawn ----

    public override void OnPlayerRespawn(VRCPlayerApi player)
    {
        if (!Utilities.IsValid(player) || !player.isLocal || respawnPoint == null) return;
        // One frame later, so VRChat's own respawn placement has finished.
        SendCustomEventDelayedFrames(nameof(_RespawnToTeam), 1);
    }

    public void _RespawnToTeam()
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        if (manager == null || respawnPoint == null || !Utilities.IsValid(local)) return;
        if (manager.GetTeamOfPlayer(local.playerId) != team) return;
        local.TeleportTo(respawnPoint.position, respawnPoint.rotation);
    }

    // ---- Pure display rules (tested) ----

    public static int RoleState(int occupant, int localId, bool isOperator, bool ready, bool pending)
    {
        if (!ready) return StateSyncing;
        if (pending) return StatePending;
        if (occupant == RosterRules.NoPlayer) return StateEmpty;
        if (occupant == localId) return StateMine;
        return isOperator ? StateOtherOperable : StateOther;
    }

    // An order name can be pressed on an empty row (by a player who can bat) or on the local row.
    public static bool OrderPressAllowed(int rowKey, int localKey, bool ready)
    {
        if (!ready || localKey == RosterRules.NoPlayer) return false;
        return rowKey == RosterRules.NoPlayer || rowKey == localKey;
    }

    public static bool MoveAllowed(int row, int direction, int rowKey, int localKey, bool isOperator, bool ready)
    {
        if (!ready || !RosterRules.ValidRow(row + direction)) return false;
        if (isOperator) return true;
        return localKey != RosterRules.NoPlayer && rowKey == localKey;
    }

    // ---- Display ----

    private void ApplyHeader()
    {
        bool away = team == RosterRules.TeamAway;
        if (teamNameText != null) teamNameText.text = away ? awayName : homeName;
        if (teamIndicatorText != null) teamIndicatorText.text = away ? awayIndicator : homeIndicator;
        if (teamIndicatorImage != null) teamIndicatorImage.color = away ? awayColor : homeColor;
    }

    private void Refresh()
    {
        if (manager == null) return;
        bool ready = manager.IsRosterReady();
        int local = LocalId();
        bool isOperator = manager.IsLocalOperator();
        for (int role = 0; role < RosterRules.RoleCount; role++)
            ShowRole(role, ready, local, isOperator);
        int localKey = manager.GetBattingKeyOf(team, local);
        for (int row = 0; row < RosterRules.OrderCount; row++)
            ShowOrder(row, ready, localKey, isOperator);
        ShowStatus(ready, local, isOperator);
    }

    private void ShowRole(int role, bool ready, int local, bool isOperator)
    {
        int occupant = manager.GetPlayerAtRole(team, role);
        bool pending = _pendingKind == PendingRole && _pendingIndex == role;
        int state = RoleState(occupant, local, isOperator, ready, pending);
        string text;
        Color color;
        if (state == StateSyncing) { text = syncingText; color = syncingColor; }
        else if (state == StatePending) { text = pendingText; color = pendingColor; }
        else if (state == StateEmpty) { text = entryText; color = emptyColor; }
        else
        {
            text = RosterRules.ShortName(PlayerName(occupant), nameLimit);
            color = state == StateMine ? mineColor : state == StateOtherOperable ? operableColor : otherColor;
        }
        if (role < roleNameTexts.Length && roleNameTexts[role] != null) roleNameTexts[role].text = text;
        if (role < roleButtonImages.Length && roleButtonImages[role] != null) roleButtonImages[role].color = color;
        if (role < roleButtons.Length && roleButtons[role] != null)
            roleButtons[role].interactable = state == StateEmpty || state == StateMine || state == StateOtherOperable;
    }

    private void ShowOrder(int row, bool ready, int localKey, bool isOperator)
    {
        int order = row + RosterRules.FirstOrder;
        int key = manager.GetBattingKeyAtOrder(team, order);
        bool pending = (_pendingKind == PendingOrder || _pendingKind == PendingMove) && _pendingIndex == row;
        string name;
        string roleText = "";
        Color color = (row & 1) == 0 ? rowColor : rowAltColor;
        if (!ready) { name = syncingText; color = syncingColor; }
        else if (pending) { name = pendingText; color = pendingColor; }
        else if (key == RosterRules.NoPlayer) name = entryText;
        else
        {
            name = RosterRules.ShortName(PlayerName(manager.GetPlayerAtOrder(team, order)), nameLimit);
            int role = manager.GetRoleAtOrder(team, order);
            roleText = role >= 0 ? RosterRules.RoleName(role) : "";
            if (key == RosterRules.SharedPdhKey) roleText = role == RosterRules.RoleDH ? "DH" : "P";
            if (key == localKey) color = mineColor;
        }
        if (row < orderNameTexts.Length && orderNameTexts[row] != null) orderNameTexts[row].text = name;
        if (row < orderRoleTexts.Length && orderRoleTexts[row] != null) orderRoleTexts[row].text = roleText;
        if (row < orderButtonImages.Length && orderButtonImages[row] != null) orderButtonImages[row].color = color;
        bool idle = !pending && _pendingKind == PendingNone;
        if (row < orderButtons.Length && orderButtons[row] != null)
            orderButtons[row].interactable = idle && OrderPressAllowed(key, localKey, ready);
        if (row < orderUpButtons.Length && orderUpButtons[row] != null)
            orderUpButtons[row].interactable = idle && MoveAllowed(row, -1, key, localKey, isOperator, ready);
        if (row < orderDownButtons.Length && orderDownButtons[row] != null)
            orderDownButtons[row].interactable = idle && MoveAllowed(row, 1, key, localKey, isOperator, ready);
    }

    private void ShowStatus(bool ready, int local, bool isOperator)
    {
        if (statusText == null) return;
        string text;
        if (!ready) text = "Syncing roster…";
        else if (_pendingKind != PendingNone) text = "Waiting for the roster owner…";
        else
        {
            int myTeam = manager.GetTeamOfPlayer(local);
            if (myTeam == team)
            {
                int order = manager.GetOrderOfPlayer(local);
                text = "You: " + RosterRules.RoleName(manager.GetRoleOfPlayer(local))
                    + (order > 0 ? "  /  #" + order : "  /  no batting order");
            }
            else if (myTeam >= 0) text = "You are on the other team. Tap Entry here to transfer.";
            else text = "Tap Entry to join this team.";
        }
        if (isOperator) text = "[Operator] " + text;
        statusText.text = text;
    }

    private int LocalId()
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        return Utilities.IsValid(local) ? local.playerId : RosterRules.NoPlayer;
    }

    private string PlayerName(int playerId)
    {
        VRCPlayerApi player = VRCPlayerApi.GetPlayerById(playerId);
        return Utilities.IsValid(player) ? player.displayName : "#" + playerId;
    }
}
