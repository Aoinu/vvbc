using TMPro;
using Tsukimi;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

public enum BattingOrderTeam
{
    Away = RosterRules.TeamAway,
    Home = RosterRules.TeamHome
}

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class BattingOrderBehaviour : TsukimiBehaviour
{
    public MatchControllerBehaviour match;
    public RosterManagerBehaviour roster;
    [HideInInspector] public BattingOrderTeam team;
    [Tooltip("Optional display name. Blank uses AWAY or HOME for the selected team.")]
    public string teamName = "";

    [Header("Appearance")]
    public Color backgroundColor = new Color(10f / 255f, 19f / 255f, 27f / 255f);
    public Color rowColor = new Color(16f / 255f, 30f / 255f, 41f / 255f);
    public Color alternateRowColor = new Color(23f / 255f, 41f / 255f, 54f / 255f);
    public Color textColor = new Color(244f / 255f, 248f / 255f, 250f / 255f);
    public Color positionColor = new Color(175f / 255f, 193f / 255f, 205f / 255f);
    public Color batterColor = new Color(1f, 211f / 255f, 110f / 255f);
    public Color lineColor = new Color(54f / 255f, 79f / 255f, 94f / 255f);
    [Min(1f)] public float teamFontSize = 88f;
    [Min(1f)] public float numberFontSize = 36f;
    [Min(1f)] public float playerFontSize = 39f;
    [Min(1f)] public float positionFontSize = 34f;

    [Header("Elements")]
    public Image background;
    public Image headerBackground;
    public Image headerLine;
    public TextMeshProUGUI teamLabel;
    public Image[] rowBackgrounds = new Image[RosterRules.OrderCount];
    public Image[] indicators = new Image[RosterRules.OrderCount];
    public TextMeshProUGUI[] orderNumbers = new TextMeshProUGUI[RosterRules.OrderCount];
    public TextMeshProUGUI[] playerNames = new TextMeshProUGUI[RosterRules.OrderCount];
    public TextMeshProUGUI[] positions = new TextMeshProUGUI[RosterRules.OrderCount];

    private int _activeRow = -2;

    void Start()
    {
        ApplyStyle();
        UdonBehaviour self = (UdonBehaviour)(Component)this;
        if (match != null) match.AddChangeListener(self);
        if (roster != null) roster.AddChangeListener(self);
        _OnRosterChanged();
    }

    public void _OnRosterChanged()
    {
        bool ready = roster != null && roster.IsRosterReady();
        for (int row = 0; row < RosterRules.OrderCount; row++)
        {
            int order = row + RosterRules.FirstOrder;
            int playerId = ready ? roster.GetPlayerAtOrder((int)team, order) : RosterRules.NoPlayer;
            int role = playerId > 0 ? roster.GetRoleAtOrder((int)team, order) : RosterRules.NotRegistered;
            ShowRow(row, playerId > 0 ? PlayerName(playerId) : "", role);
        }
        _OnMatchChanged();
    }

    public override void OnPlayerJoined(VRCPlayerApi player)
    {
        // The roster snapshot and the local player directory can arrive in either order.
        if (roster != null && roster.IsRosterReady() && roster.GetTeamOfPlayer(player.playerId) == (int)team)
            _OnRosterChanged();
    }

    public void _OnMatchChanged()
    {
        int row = -1;
        if (match != null && roster != null && match.IsMatchReady() && roster.IsRosterReady())
        {
            int batter = ActiveBatter(match.GetPhase(), match.GetOffenseTeam(), (int)team, match.GetBatterId());
            if (batter > 0)
            {
                for (int order = RosterRules.FirstOrder; order <= RosterRules.LastOrder; order++)
                {
                    if (roster.GetPlayerAtOrder((int)team, order) != batter) continue;
                    row = order - RosterRules.FirstOrder;
                    break;
                }
            }
        }
        ShowActiveRow(row);
    }

    public static int ActiveBatter(int phase, int offenseTeam, int displayTeam, int batterId)
    {
        return phase == MatchRules.PhaseInProgress && offenseTeam == displayTeam && batterId > 0 ? batterId : 0;
    }

    public static string DisplayTeamName(BattingOrderTeam displayTeam, string name)
    {
        if (!string.IsNullOrEmpty(name)) return name;
        return displayTeam == BattingOrderTeam.Home ? "HOME" : "AWAY";
    }

    public void ApplyStyle()
    {
        if (background != null) background.color = backgroundColor;
        if (headerBackground != null) headerBackground.color = rowColor;
        if (headerLine != null) headerLine.color = lineColor;
        if (teamLabel != null)
        {
            teamLabel.color = textColor;
            teamLabel.fontSizeMax = teamFontSize;
            SetText(teamLabel, DisplayTeamName(team, teamName));
        }
        for (int row = 0; row < RosterRules.OrderCount; row++)
        {
            if (rowBackgrounds[row] != null) rowBackgrounds[row].color = row % 2 == 0 ? rowColor : alternateRowColor;
            if (indicators[row] != null) indicators[row].color = batterColor;
            if (orderNumbers[row] != null) orderNumbers[row].fontSize = numberFontSize;
            if (playerNames[row] != null) playerNames[row].fontSizeMax = playerFontSize;
            if (positions[row] != null) positions[row].fontSize = positionFontSize;
        }
    }

    public void ShowRow(int row, string playerName, int role)
    {
        SetText(playerNames[row], playerName);
        SetText(positions[row], RosterRules.RoleName(role));
    }

    public void ShowActiveRow(int row)
    {
        if (row == _activeRow) return;
        if (_activeRow == -2)
        {
            for (int i = 0; i < RosterRules.OrderCount; i++) SetRowHighlight(i, i == row);
        }
        else
        {
            if (_activeRow >= 0) SetRowHighlight(_activeRow, false);
            if (row >= 0) SetRowHighlight(row, true);
        }
        _activeRow = row;
    }

    private void SetRowHighlight(int row, bool active)
    {
        SetColor(orderNumbers[row], active ? batterColor : textColor);
        SetColor(playerNames[row], active ? batterColor : textColor);
        SetColor(positions[row], active ? batterColor : positionColor);
        Image indicator = indicators[row];
        if (indicator != null && indicator.enabled != active) indicator.enabled = active;
    }

    private void SetColor(TextMeshProUGUI label, Color color)
    {
        if (label != null && label.color != color) label.color = color;
    }

    private void SetText(TextMeshProUGUI label, string value)
    {
        if (label != null && label.text != value) label.text = value;
    }

    private string PlayerName(int playerId)
    {
        VRCPlayerApi player = VRCPlayerApi.GetPlayerById(playerId);
        return Utilities.IsValid(player) ? player.displayName : "#" + playerId;
    }
}
