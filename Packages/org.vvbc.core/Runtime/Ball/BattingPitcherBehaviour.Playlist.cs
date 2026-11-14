using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// Shared playlist, Start/Stop and settings. Every player may edit: a request is queued
// locally, Ownership is taken, and the Owner applies the queue to the synced arrays and sends
// them whole. Concurrent edits converge to the last accepted state.
public partial class BattingPitcherBehaviour
{
    public const float MinSpeedKmh = 60f;
    public const float MaxSpeedKmh = 160f;
    public const float MinSpinRpm = 0f;
    public const float MaxSpinRpm = 3000f;
    public const float MinResetSeconds = 3f;
    public const float MaxResetSeconds = 15f;

    // Cycle phases reported to UIs. Every client derives them from the synced state.
    public const int PhaseStopped = 0;
    public const int PhaseAwaitingContact = 1;
    public const int PhaseResetting = 2;
    public const int PhaseBallHeld = 3;
    public const int PhasePreparing = 4;
    public const int PhaseScheduled = 5;
    public const int PhaseSyncing = 6;

    // Save results for UIs.
    public const int SavePending = 0;
    public const int SaveDone = 1;
    public const int SaveFull = -1;
    public const int SaveInterrupted = -2;

    [UdonSynced] private int[] _playlist = new int[10];
    [UdonSynced] private int _playlistState;
    [UdonSynced] private int _nextItemId = 1;

    // Requests waiting for Ownership. Start/Stop, mode and settings keep only their latest value;
    // item edits are applied in order and address items by id, never by index.
    private bool _settingsPending;
    private float _pendingSpeed, _pendingSpin, _pendingReset;
    private bool _pendingTimer;
    private bool _runningPending, _pendingRunning;
    private bool _modePending, _pendingSequence;
    private const int OpCapacity = 8;
    private const int OpSave = 1;
    private const int OpMove = 2;
    private const int OpDelete = 3;
    private readonly int[] _opKind = new int[OpCapacity];
    private readonly int[] _opId = new int[OpCapacity];
    private readonly int[] _opValue = new int[OpCapacity];
    private readonly int[] _opToken = new int[OpCapacity];
    private int _opCount;
    private int _saveToken;
    private int _resultToken;
    private int _saveResult;
    private int _savedItemId;

    // ---- Queries (local; no network) ----

    public bool IsRunning() { return PitchPlaylist.Has(_playlistState, PitchPlaylist.RunningFlag); }
    public bool IsSequence() { return PitchPlaylist.Has(_playlistState, PitchPlaylist.SequenceFlag); }
    public bool IsResetOnTimer() { return PitchPlaylist.Has(_playlistState, PitchPlaylist.TimerFlag); }
    public int PlaylistCount() { return PitchPlaylist.Count(_playlistState); }
    public int PlaylistItem(int index) { return index >= 0 && index < PlaylistCount() ? _playlist[index] : 0; }
    public bool PlaylistDone(int index) { return PitchPlaylist.Done(_playlistState, index); }
    public int NextIndex() { return PitchPlaylist.FirstPending(_playlistState); }
    public int IndexOfItem(int itemId) { return PitchPlaylist.IndexOf(_playlist, _playlistState, itemId); }
    public bool StateKnown() { return _stateKnown; }

    // The phase this client sees now. The countdowns below never claim a pitch start time that
    // has not been committed: only PhaseScheduled counts down to an actual motion start.
    public int CyclePhase() { return CyclePhaseAt(ServerNow()); }
    public float PhaseRemaining() { return PhaseRemainingAt(ServerNow()); }
    public float PhaseSpan() { return PhaseSpanAt(ServerNow()); }

    private int CyclePhaseAt(double now)
    {
        if (!_stateKnown) return PhaseSyncing;
        if (_suspended) return PhaseStopped;
        if (_appliedId != 0 && _motionStartedId != _appliedId && ServerDelta(_appliedStart, now) > 0.0) return PhaseScheduled;
        if (!IsRunning()) return PhaseStopped;
        if (ball != null && ball.IsHeld()) return PhaseBallHeld;
        if (_appliedId != 0 && ServerDelta(CycleReadyAt(), now) > 0.0)
            return !_appliedOnTimer && !_appliedHasEvent ? PhaseAwaitingContact : PhaseResetting;
        return PhasePreparing;
    }

    // Seconds until the phase's own deadline: the motion start when scheduled, the fallback
    // when awaiting contact, the earliest preparation when resetting. Zero otherwise.
    private float PhaseRemainingAt(double now)
    {
        if (_appliedId == 0) return 0f;
        int phase = CyclePhaseAt(now);
        double target;
        if (phase == PhaseScheduled) target = _appliedStart;
        else if (phase == PhaseAwaitingContact) target = FallbackAt();
        else if (phase == PhaseResetting) target = CycleReadyAt();
        else return 0f;
        return Mathf.Max(0f, (float)ServerDelta(target, now));
    }

    // Length of the span PhaseRemaining counts down, for the progress gauge.
    private float PhaseSpanAt(double now)
    {
        if (_appliedId == 0) return 0f;
        int phase = CyclePhaseAt(now);
        if (phase == PhaseScheduled) return Mathf.Max(0.01f, leadSeconds);
        if (phase == PhaseAwaitingContact) return Mathf.Max(0.01f, (float)ServerDelta(FallbackAt(), _appliedStart));
        if (phase == PhaseResetting)
        {
            double from = _appliedOnTimer ? _appliedStart : Later(_appliedStart, _appliedEventTime);
            return Mathf.Max(0.01f, (float)ServerDelta(CycleReadyAt(), from));
        }
        return 0f;
    }

    // ---- Requests (any player) ----

    public void RequestRunning(bool running)
    {
        _runningPending = true;
        _pendingRunning = running;
        RequestEdit();
    }

    public void RequestSequence(bool sequence)
    {
        _modePending = true;
        _pendingSequence = sequence;
        RequestEdit();
    }

    public void SubmitSettings(float speed, float spin, float reset, bool onTimer)
    {
        _pendingSpeed = speed;
        _pendingSpin = spin;
        _pendingReset = reset;
        _pendingTimer = onTimer;
        _settingsPending = true;
        RequestEdit();
    }

    // Saves an item: the item with this id is updated, otherwise (new, or deleted meanwhile)
    // a new id is appended. Returns a token for SaveResult.
    public int RequestSaveItem(int itemId, int type, int mask)
    {
        _saveToken++;
        QueueOp(OpSave, itemId, PitchPlaylist.Pack(0, type, mask), _saveToken);
        return _saveToken;
    }

    public void RequestMoveItem(int itemId, int direction) { QueueOp(OpMove, itemId, direction, 0); }
    public void RequestDeleteItem(int itemId) { QueueOp(OpDelete, itemId, 0, 0); }

    // SavePending until the Owner applied the save with this token.
    public int SaveResult(int token)
    {
        if (token <= 0 || token > _resultToken) return SavePending;
        return token == _resultToken ? _saveResult : SaveDone;
    }

    public int SavedItemId() { return _savedItemId; }

    private void QueueOp(int kind, int id, int value, int token)
    {
        if (_opCount >= OpCapacity)
        {
            // Full queue: the oldest edit is dropped; a dropped save reports as interrupted.
            if (_opKind[0] == OpSave) FinishSave(_opToken[0], SaveInterrupted, 0);
            for (int i = 1; i < OpCapacity; i++)
            {
                _opKind[i - 1] = _opKind[i];
                _opId[i - 1] = _opId[i];
                _opValue[i - 1] = _opValue[i];
                _opToken[i - 1] = _opToken[i];
            }
            _opCount--;
        }
        _opKind[_opCount] = kind;
        _opId[_opCount] = id;
        _opValue[_opCount] = value;
        _opToken[_opCount] = token;
        _opCount++;
        RequestEdit();
    }

    private void RequestEdit()
    {
        if (IsPitchController())
        {
            ApplyPendingEdits();
            return;
        }
        if (Networking.LocalPlayer != null) Networking.SetOwner(Networking.LocalPlayer, gameObject);
    }

    // Ownership went to someone else first: this client's edits are superseded.
    private void DropPendingEdits()
    {
        _settingsPending = false;
        _runningPending = false;
        _modePending = false;
        for (int i = 0; i < _opCount; i++)
            if (_opKind[i] == OpSave) FinishSave(_opToken[i], SaveInterrupted, 0);
        _opCount = 0;
    }

    private void FinishSave(int token, int result, int itemId)
    {
        if (token < _resultToken) return;
        _resultToken = token;
        _saveResult = result;
        _savedItemId = itemId;
    }

    // Owner with a synced state: applies everything queued and sends once.
    private void ApplyPendingEdits()
    {
        if (!IsPitchController() || !_stateKnown) return;
        if (!_settingsPending && !_runningPending && !_modePending && _opCount == 0) return;
        bool wasRunning = IsRunning();
        if (_settingsPending)
        {
            _settingsPending = false;
            speedKmh = Mathf.Clamp(_pendingSpeed, MinSpeedKmh, MaxSpeedKmh);
            spinRpm = Mathf.Clamp(_pendingSpin, MinSpinRpm, MaxSpinRpm);
            resetSeconds = Mathf.Clamp(_pendingReset, MinResetSeconds, MaxResetSeconds);
            _playlistState = PitchPlaylist.WithFlag(_playlistState, PitchPlaylist.TimerFlag, _pendingTimer);
        }
        for (int i = 0; i < _opCount; i++) ApplyOp(_opKind[i], _opId[i], _opValue[i], _opToken[i]);
        _opCount = 0;
        if (_modePending)
        {
            _modePending = false;
            ApplyMode(_pendingSequence);
        }
        if (_runningPending)
        {
            _runningPending = false;
            _playlistState = PitchPlaylist.WithFlag(_playlistState, PitchPlaylist.RunningFlag, _pendingRunning);
            // A fresh Random lap that has not pitched anything starts in a random order.
            if (_pendingRunning && !wasRunning && !IsSequence() && PitchPlaylist.History(_playlistState) == 0)
                _playlistState = PitchPlaylist.Shuffle(_playlist, _playlistState, NextShuffleSeed());
        }
        Sync();
        if (IsRunning()) TryAdvanceCycle();
        else StopPreparation();
        NotifyListeners();
    }

    private void ApplyOp(int kind, int id, int value, int token)
    {
        int index = PitchPlaylist.IndexOf(_playlist, _playlistState, id);
        if (kind == OpSave)
        {
            int type = PitchPlaylist.Type(value);
            int mask = PitchPlaylist.Mask(value);
            if (index >= 0)
            {
                // Editing keeps the id, the position and the done bit.
                _playlist[index] = PitchPlaylist.Pack(id, type, mask);
                FinishSave(token, SaveDone, id);
                return;
            }
            int newId = PitchPlaylist.FreeId(_playlist, _playlistState, _nextItemId);
            int state = PitchPlaylist.Append(_playlist, _playlistState, PitchPlaylist.Pack(newId, type, mask));
            if (state < 0)
            {
                FinishSave(token, SaveFull, 0);
                return;
            }
            _playlistState = state;
            _nextItemId = newId >= PitchPlaylist.IdBits ? 1 : newId + 1;
            FinishSave(token, SaveDone, newId);
            return;
        }
        if (index < 0) return;
        if (kind == OpMove) _playlistState = PitchPlaylist.Swap(_playlist, _playlistState, index, index + (value < 0 ? -1 : 1));
        else if (kind == OpDelete) _playlistState = PitchPlaylist.RemoveAt(_playlist, _playlistState, index);
    }

    // A mode change starts a new lap from the next unconfirmed pitch. Sequence keeps the
    // current order; Random reshuffles.
    private void ApplyMode(bool sequence)
    {
        if (sequence == IsSequence()) return;
        _playlistState = PitchPlaylist.WithFlag(_playlistState, PitchPlaylist.SequenceFlag, sequence);
        _playlistState = sequence ? PitchPlaylist.WithHistory(_playlistState, 0)
            : PitchPlaylist.Shuffle(_playlist, _playlistState, NextShuffleSeed());
    }

    // Stop cancels only work that has not been sent; a reservation in flight completes, and a
    // committed motion and flight always continue.
    private void StopPreparation()
    {
        if (_reserving || !_preparing) return;
        _preparing = false;
        _solving = false;
    }
}
