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
// rebuilt only from the manager's tables when it reports a change. The manager keeps the local
// player's one outstanding request, so a pressed button shows Pending (and every button of both
// panels waits) until the Owner's decision shows in the roster, a rejection arrives or the
// request times out. Changes by other players do not end it.
//
// Batting order: a player gets a row automatically with their role. Tapping an empty row moves
// their own row there; their own row cannot be released on its own.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class RosterPanelBehaviour : TsukimiBehaviour
{
    public const int StateSyncing = 0;
    public const int StateEmpty = 1;
    public const int StateMine = 2;
    public const int StateOther = 3;
    public const int StateOtherOperable = 4;
    public const int StatePending = 5;

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
    public Color emptyColor = new Color(0.97f, 0.97f, 0.93f, 1f);
    public Color mineColor = new Color(1f, 0.82f, 0.25f, 1f);
    public Color otherColor = new Color(0.78f, 0.82f, 0.8f, 1f);
    public Color operableColor = new Color(1f, 0.6f, 0.45f, 1f);
    public Color pendingColor = new Color(0.65f, 0.85f, 1f, 1f);
    public Color syncingColor = new Color(0.6f, 0.6f, 0.6f, 1f);
    public Color rowColor = new Color(0.97f, 0.97f, 0.93f, 1f);
    public Color rowAltColor = new Color(0.9f, 0.92f, 0.88f, 1f);

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

    // The manager refuses a submission while another request of this client is outstanding,
    // and reports the change (Pending) to every panel itself.
    private void PressRole(int role)
    {
        if (manager == null || manager.HasPendingRequest()) return;
        int state = RoleState(manager.GetPlayerAtRole(team, role), LocalId(), manager.IsLocalOperator(), manager.IsRosterReady(), false);
        if (state != StateEmpty && state != StateMine && state != StateOtherOperable) return;
        manager.SubmitRole(team, role);
    }

    private void PressOrder(int row)
    {
        if (manager == null || manager.HasPendingRequest()) return;
        int localKey = manager.GetBattingKeyOf(team, LocalId());
        if (!OrderPressAllowed(manager.GetBattingKeyAtOrder(team, row + RosterRules.FirstOrder), localKey, FindRow(localKey), manager.IsRosterReady())) return;
        manager.SubmitOrder(team, row + RosterRules.FirstOrder);
    }

    private void PressMove(int row, int direction)
    {
        if (manager == null || manager.HasPendingRequest() || !RosterRules.ValidRow(row + direction)) return;
        int order = row + RosterRules.FirstOrder;
        int rowKey = manager.GetBattingKeyAtOrder(team, order);
        int targetKey = manager.GetBattingKeyAtOrder(team, order + direction);
        if (!MoveAllowed(row, direction, rowKey, targetKey, manager.GetBattingKeyOf(team, LocalId()), manager.IsLocalOperator(), manager.IsRosterReady())) return;
        manager.SubmitOrderMove(team, order, direction);
    }

    private int FindRow(int key)
    {
        if (key == RosterRules.NoPlayer) return -1;
        for (int row = 0; row < RosterRules.OrderCount; row++)
            if (manager.GetBattingKeyAtOrder(team, row + RosterRules.FirstOrder) == key) return row;
        return -1;
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

    // An empty row can be pressed by a player who bats on this team and has a row (every such
    // player has one): their row moves there. Their own row and other rows cannot be pressed.
    public static bool OrderPressAllowed(int rowKey, int localKey, int localRow, bool ready)
    {
        if (!ready || localKey == RosterRules.NoPlayer || localRow < 0) return false;
        return rowKey == RosterRules.NoPlayer;
    }

    // Swapping with the neighbour: the local player's own row, or any row for an operator.
    // Two empty rows have nothing to swap.
    public static bool MoveAllowed(int row, int direction, int rowKey, int targetKey, int localKey, bool isOperator, bool ready)
    {
        if (!ready || !RosterRules.ValidRow(row + direction) || rowKey == targetKey) return false;
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
        bool busy = manager.HasPendingRequest();
        int pendingKind = busy && manager.GetPendingRequestTeam() == team ? manager.GetPendingRequestKind() : RosterRules.RequestNone;
        int pendingIndex = manager.GetPendingRequestIndex();
        for (int role = 0; role < RosterRules.RoleCount; role++)
            ShowRole(role, ready, local, isOperator, busy, pendingKind == RosterRules.RequestRoleKind && pendingIndex == role);
        int localKey = manager.GetBattingKeyOf(team, local);
        int localRow = FindRow(localKey);
        bool orderPending = pendingKind == RosterRules.RequestRowKind || pendingKind == RosterRules.RequestMoveKind;
        for (int row = 0; row < RosterRules.OrderCount; row++)
            ShowOrder(row, ready, localKey, localRow, isOperator, busy, orderPending && pendingIndex == row);
        ShowStatus(ready, local, isOperator, busy);
    }

    private void ShowRole(int role, bool ready, int local, bool isOperator, bool busy, bool pending)
    {
        int occupant = manager.GetPlayerAtRole(team, role);
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
            roleButtons[role].interactable = !busy && (state == StateEmpty || state == StateMine || state == StateOtherOperable);
    }

    private void ShowOrder(int row, bool ready, int localKey, int localRow, bool isOperator, bool busy, bool pending)
    {
        int order = row + RosterRules.FirstOrder;
        int key = manager.GetBattingKeyAtOrder(team, order);
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
        int above = row > 0 ? manager.GetBattingKeyAtOrder(team, order - 1) : RosterRules.NoPlayer;
        int below = row < RosterRules.OrderCount - 1 ? manager.GetBattingKeyAtOrder(team, order + 1) : RosterRules.NoPlayer;
        if (row < orderButtons.Length && orderButtons[row] != null)
            orderButtons[row].interactable = !busy && OrderPressAllowed(key, localKey, localRow, ready);
        if (row < orderUpButtons.Length && orderUpButtons[row] != null)
            orderUpButtons[row].interactable = !busy && MoveAllowed(row, -1, key, above, localKey, isOperator, ready);
        if (row < orderDownButtons.Length && orderDownButtons[row] != null)
            orderDownButtons[row].interactable = !busy && MoveAllowed(row, 1, key, below, localKey, isOperator, ready);
    }

    private void ShowStatus(bool ready, int local, bool isOperator, bool busy)
    {
        if (statusText == null) return;
        string text;
        if (!ready) text = "Syncing roster…";
        else if (busy) text = "Waiting for the roster owner…";
        else
        {
            int myTeam = manager.GetTeamOfPlayer(local);
            if (myTeam == team)
            {
                int order = manager.GetOrderOfPlayer(local);
                text = "You: " + RosterRules.RoleName(manager.GetRoleOfPlayer(local))
                    + (order > 0 ? "  /  #" + order + "  -  tap an empty row to move" : "  /  the DH bats for you");
            }
            else if (myTeam >= 0) text = "You are on the other team. Tap Entry here to transfer.";
            else text = "Tap Entry on a position to join. Your batting order is assigned.";
        }
        if (manager.GetSerializationFailureStreak() > 0) text += "  (roster send retrying)";
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
