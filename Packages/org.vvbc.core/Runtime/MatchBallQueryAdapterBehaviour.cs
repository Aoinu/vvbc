using Tsukimi;
using UnityEngine;

// Hands the confirmed possession of the match Ball to MatchControllerBehaviour. It sits in
// Runtime/ because it is the only place that sees both Ball/ and Match/ (T# sees the own folder
// and below); it only forwards BallBehaviour's confirmed-possession queries.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class MatchBallQueryAdapterBehaviour : MatchBallQueryBehaviour
{
    [Tooltip("The match Ball. Empty = no possession is ever confirmed (general Time is then never accepted).")]
    public BallBehaviour ball;

    public override bool HasConfirmedHold() { return ball != null && ball.HasConfirmedHold(); }
    public override int ConfirmedHoldMode() { return ball != null ? ball.ConfirmedHoldMode() : HoldFree; }
    public override int ConfirmedHolderId() { return ball != null ? ball.ConfirmedHolderId() : 0; }
}
