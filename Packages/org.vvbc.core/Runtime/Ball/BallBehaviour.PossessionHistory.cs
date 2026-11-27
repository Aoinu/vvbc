// A short local record of the accepted states this client has seen (docs/development/player-event-collector.md,
// tag hold verification). Read-only for the match: the MatchManager Owner checks a tag against the
// possession that was accepted when the tag was made, also after the ball has moved on. Nothing of
// it is synced or sent; it records every accepted change (any kind, so the end of a possession is
// known) and keeps the last PossessionHistoryCapacity of them. A state this client never received
// (an intermediate snapshot overwritten before it arrived, a client that joined later) is simply
// not in it: a reader must treat a missing entry as unknown, never as the current state.
public partial class BallBehaviour
{
    public const int PossessionHistoryCapacity = 8;
    public const int PossessionHistoryStride = 5;
    public const int HistorySequence = 0;
    public const int HistoryPublisher = 1;
    public const int HistoryIsPossession = 2;   // 1 when the state is a possession
    public const int HistoryHold = 3;           // HoldGlove / HoldHand (possession only)
    public const int HistoryHolder = 4;

    private readonly int[] _possessionHistory = new int[PossessionHistoryCapacity * PossessionHistoryStride];
    private readonly double[] _possessionHistoryTimes = new double[PossessionHistoryCapacity];
    private int _possessionHistoryNext;
    private int _possessionHistoryCount;

    // The publisher of the confirmed hold (sequences are compared only within one publisher).
    public int ConfirmedHoldPublisher() { return HasConfirmedHold() ? _acceptedPublisherId : 0; }

    public int GetPossessionHistoryCount() { return _possessionHistoryCount; }

    // Entry 0 is the newest accepted state.
    public int GetPossessionHistoryField(int entry, int field)
    {
        if (entry < 0 || entry >= _possessionHistoryCount || field < 0 || field >= PossessionHistoryStride) return 0;
        return _possessionHistory[PossessionHistoryIndex(entry) * PossessionHistoryStride + field];
    }

    public double GetPossessionHistoryTime(int entry)
    {
        if (entry < 0 || entry >= _possessionHistoryCount) return 0.0;
        return _possessionHistoryTimes[PossessionHistoryIndex(entry)];
    }

    private int PossessionHistoryIndex(int entry)
    {
        return (_possessionHistoryNext - 1 - entry + PossessionHistoryCapacity * 2) % PossessionHistoryCapacity;
    }

    // After every change of the accepted state. The same state again (a repeated receive) is
    // recorded once.
    private void RecordAcceptedHistory()
    {
        if (localOnly || _acceptedSequence <= 0) return;
        if (_possessionHistoryCount > 0)
        {
            int last = PossessionHistoryIndex(0) * PossessionHistoryStride;
            if (_possessionHistory[last + HistorySequence] == _acceptedSequence && _possessionHistory[last + HistoryPublisher] == _acceptedPublisherId) return;
        }
        bool possession = _acceptedKind == PossessionState;
        int at = _possessionHistoryNext * PossessionHistoryStride;
        _possessionHistory[at + HistorySequence] = _acceptedSequence;
        _possessionHistory[at + HistoryPublisher] = _acceptedPublisherId;
        _possessionHistory[at + HistoryIsPossession] = possession ? 1 : 0;
        _possessionHistory[at + HistoryHold] = possession ? _acceptedHold : HoldFree;
        _possessionHistory[at + HistoryHolder] = possession ? _acceptedHolderId : 0;
        _possessionHistoryTimes[_possessionHistoryNext] = _acceptedServerTime;
        _possessionHistoryNext = (_possessionHistoryNext + 1) % PossessionHistoryCapacity;
        if (_possessionHistoryCount < PossessionHistoryCapacity) _possessionHistoryCount++;
    }
}
