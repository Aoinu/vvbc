using Tsukimi;

// Confirmed possession of the match ball, as MatchControllerBehaviour needs it for Time.
// T# sees only the own folder and below, and Match/ does not see Ball/, so the controller asks
// this adapter; its implementation (MatchBallQueryAdapterBehaviour in Runtime/) sees both.
// Only possession that the ball's sync has confirmed counts: a local catch or pickup still
// waiting for ownership never does.
public abstract class MatchBallQueryBehaviour : TsukimiBehaviour
{
    public const int HoldFree = 0;
    public const int HoldGlove = 1;
    public const int HoldHand = 2;

    public abstract bool HasConfirmedHold();
    // HoldGlove / HoldHand, HoldFree when nothing is confirmed.
    public abstract int ConfirmedHoldMode();
    public abstract int ConfirmedHolderId();
}
