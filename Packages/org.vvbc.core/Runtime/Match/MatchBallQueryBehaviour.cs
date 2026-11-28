using Tsukimi;
using UnityEngine;
using VRC.Udon;

// Confirmed possession of the match ball, as MatchControllerBehaviour needs it for Time.
// T# sees only the own folder and below, and Match/ does not see Ball/, so the controller asks
// this adapter; its implementation (MatchBallQueryAdapterBehaviour in Runtime/) sees both.
// Only possession that the ball's sync has confirmed counts: a local catch or pickup still
// waiting for ownership never does.
//
// The tag sensor queries serve TagContactDetectorBehaviour on the holder's own client: the
// region a tag is made with for the current confirmed hold (the glove's TagSensor for a glove
// hold, the ball for a hand hold). They never change the ball, the glove or their sync.
public abstract class MatchBallQueryBehaviour : TsukimiBehaviour
{
    public const int HoldFree = 0;
    public const int HoldGlove = 1;
    public const int HoldHand = 2;

    public abstract bool HasConfirmedHold();
    // HoldGlove / HoldHand, HoldFree when nothing is confirmed.
    public abstract int ConfirmedHoldMode();
    public abstract int ConfirmedHolderId();
    // Sequence of the accepted possession that confirms it (0 when none); grows with each change.
    public abstract int ConfirmedHoldSequence();
    // Publisher of that possession (sequences are compared only within one publisher's stream).
    public abstract int ConfirmedHoldPublisher();

    // The accepted states this client has seen, newest first (entry 0), a fixed short history kept
    // locally by the ball and never synced. A state that never reached this client is not in it.
    public const int HistorySequence = 0;
    public const int HistoryPublisher = 1;
    public const int HistoryIsPossession = 2;
    public const int HistoryHold = 3;
    public const int HistoryHolder = 4;
    // Read by the umpire engine (docs/development/automatic-umpire.md): what the state was and
    // which pitch it belongs to (BallBehaviour.HistoryKind* numbers).
    public const int HistoryKind = 5;
    public const int HistoryPitcher = 6;
    public const int HistoryPitchNumber = 7;
    public const int HistoryBatter = 8;
    public const int HistoryFields = 9;
    public const int KindStage = 1;
    public const int KindPitch = 2;
    public const int KindHit = 3;
    public const int KindThrow = 4;
    public const int KindPossession = 5;
    public const int KindHandPitch = 6;
    public abstract int HoldHistoryCount();
    public abstract int HoldHistoryCapacity();
    public abstract int HoldHistoryField(int entry, int field);
    public abstract double HoldHistoryTime(int entry);
    // The ball position the state was published with (a catch: where it was caught).
    public abstract Vector3 HoldHistoryPosition(int entry);

    // The ball as this client simulates it now (the umpire engine on the MatchManager Owner tracks
    // a batted ball with it). Local only: nothing is synced for it.
    public abstract bool BallAvailable();
    public abstract Vector3 BallPosition();
    public abstract Vector3 BallVelocity();
    public abstract bool BallBattedFlight();
    public abstract bool BallFlightGrounded();
    public abstract int BallLineagePitcher();
    public abstract int BallLineageNumber();
    // listener receives the ball's presentation events (OnBallPitchReleased, OnBallGroundContact,
    // OnBallBattedFlightStarted, OnBallPresentationChanged) on this client.
    public abstract void RegisterBallListener(UdonBehaviour listener);

    // The local player holds the ball (confirmed) and the region for that hold is usable now.
    public abstract bool LocalTagSensorReady();
    public abstract Vector3 LocalTagSensorCenter();
    public abstract float LocalTagSensorRadius();
}
