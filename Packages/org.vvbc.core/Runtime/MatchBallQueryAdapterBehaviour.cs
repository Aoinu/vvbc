using Tsukimi;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

// Hands the confirmed possession of the match Ball to MatchControllerBehaviour and the tag sensor
// to TagContactDetectorBehaviour. It sits in Runtime/ because it is the only place that sees both
// Ball/ and Match/ (T# sees the own folder and below); it only forwards BallBehaviour's
// confirmed-possession queries and the local GloveBehaviour's TagSensor.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public partial class MatchBallQueryAdapterBehaviour : MatchBallQueryBehaviour
{
    [Tooltip("The match Ball. Empty = no possession is ever confirmed (general Time is then never accepted).")]
    public BallBehaviour ball;
    [Tooltip("Radius of the tag region around the ball while it is held in the hand, in metres. Default 0.06.")]
    public float handTagRadius = 0.06f;

    private GloveBehaviour _localGlove;
    private float _nextGloveSearch;

    public override bool HasConfirmedHold() { return ball != null && ball.HasConfirmedHold(); }
    public override int ConfirmedHoldMode() { return ball != null ? ball.ConfirmedHoldMode() : HoldFree; }
    public override int ConfirmedHolderId() { return ball != null ? ball.ConfirmedHolderId() : 0; }
    public override int ConfirmedHoldSequence() { return ball != null ? ball.ConfirmedHoldSequence() : 0; }
    public override int ConfirmedHoldPublisher() { return ball != null ? ball.ConfirmedHoldPublisher() : 0; }
    public override int HoldHistoryCount() { return ball != null ? ball.GetPossessionHistoryCount() : 0; }
    public override int HoldHistoryCapacity() { return BallBehaviour.PossessionHistoryCapacity; }
    public override int HoldHistoryField(int entry, int field) { return ball != null ? ball.GetPossessionHistoryField(entry, field) : 0; }
    public override double HoldHistoryTime(int entry) { return ball != null ? ball.GetPossessionHistoryTime(entry) : 0.0; }
    public override Vector3 HoldHistoryPosition(int entry) { return ball != null ? ball.GetPossessionHistoryPosition(entry) : Vector3.zero; }

    public override bool BallAvailable() { return ball != null; }
    public override Vector3 BallPosition() { return ball != null ? ball.transform.position : Vector3.zero; }
    public override Vector3 BallVelocity() { return ball != null && ball.body != null ? ball.body.velocity : Vector3.zero; }
    public override bool BallBattedFlight() { return ball != null && ball.BattedFlight(); }
    public override bool BallFlightGrounded() { return ball != null && ball.FlightGrounded(); }
    public override int BallLineagePitcher() { return ball != null ? ball.CurrentLineagePitcher() : 0; }
    public override int BallLineageNumber() { return ball != null ? ball.CurrentLineageNumber() : 0; }
    public override void RegisterBallListener(UdonBehaviour listener) { if (ball != null) ball.RegisterPresentationListener(listener); }

    public override bool LocalTagSensorReady()
    {
        if (ball == null || !ball.HasConfirmedHold()) return false;
        VRCPlayerApi local = Networking.LocalPlayer;
        if (!Utilities.IsValid(local) || ball.ConfirmedHolderId() != local.playerId) return false;
        int mode = ball.ConfirmedHoldMode();
        if (mode == HoldHand) return true;
        if (mode != HoldGlove) return false;
        GloveBehaviour glove = LocalGlove();
        return glove != null && glove.TagSensorReady();
    }

    public override Vector3 LocalTagSensorCenter()
    {
        if (ball != null && ball.ConfirmedHoldMode() == HoldGlove)
        {
            GloveBehaviour glove = LocalGlove();
            if (glove != null) return glove.TagSensorCenter();
        }
        return ball != null ? ball.transform.position : Vector3.zero;
    }

    public override float LocalTagSensorRadius()
    {
        if (ball != null && ball.ConfirmedHoldMode() == HoldGlove)
        {
            GloveBehaviour glove = LocalGlove();
            if (glove != null) return glove.TagSensorRadius();
        }
        return handTagRadius;
    }

    // Exactly one GloveBehaviour among the local player's PlayerObjects (searched at most every 2 s).
    private GloveBehaviour LocalGlove()
    {
        if (_localGlove != null) return _localGlove;
        if (Time.time < _nextGloveSearch) return null;
        _nextGloveSearch = Time.time + 2f;
        VRCPlayerApi local = Networking.LocalPlayer;
        if (!Utilities.IsValid(local)) return null;
        GameObject[] objects = Networking.GetPlayerObjects(local);
        if (objects == null) return null;
        GloveBehaviour found = null;
        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] == null) continue;
            GloveBehaviour candidate = objects[i].GetComponentInChildren<GloveBehaviour>(true);
            if (candidate == null) continue;
            if (found != null) return null;
            found = candidate;
        }
        _localGlove = found;
        return found;
    }
}
