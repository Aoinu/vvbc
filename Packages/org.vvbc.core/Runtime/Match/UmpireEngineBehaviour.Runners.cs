using UnityEngine;

// Runner adjudication of the umpire engine (docs/development/automatic-umpire.md#runners): force
// plays, tag plays, advances and runs, from the accepted base touches and tags (server times of
// the players' own clients) and the ball's accepted possessions. Touching a base never awards it
// by itself: a ruling does, through PlayRules, and only in server-time order per runner.
public partial class UmpireEngineBehaviour
{
    // Candidate of one runner (NextRunnerDecision).
    private const int CandNone = 0;
    private const int CandRuling = 1;
    private const int CandNoRuling = 2;   // decided: nothing to rule (a tag on a protected runner)
    private const int CandReview = 3;
    private int _candKind;
    private int _candCode;
    private int _candArg;
    private int _candReview;
    private int _candEvent;
    private double _candTime;

    // ---- Local event log ----

    private void Ingest(int source, int seq, int type, int target, int baseIndex, int parts, int arg, double time)
    {
        for (int i = 0; i < _evCount; i++)
        {
            if (_evSource[i] != source || _evSeq[i] != seq) continue;
            _evInInbox[i] = true;
            return;
        }
        if (_evCount >= MaxEvents && !DropOldestAcked()) { _logOverflow++; return; }
        int at = _evCount++;
        _evSource[at] = source;
        _evSeq[at] = seq;
        _evType[at] = type;
        _evTarget[at] = target;
        _evBase[at] = baseIndex;
        _evParts[at] = parts;
        _evArg[at] = arg;
        _evTime[at] = time;
        _evAcked[at] = false;
        _evDecided[at] = false;
        _evNeeded[at] = false;
        _evInInbox[at] = true;
    }

    private bool DropOldestAcked()
    {
        int oldest = -1;
        for (int i = 0; i < _evCount; i++)
            if (_evAcked[i] && (oldest < 0 || _evTime[i] < _evTime[oldest])) oldest = i;
        if (oldest < 0) return false;
        int last = _evCount - 1;
        for (int i = oldest; i < last; i++)
        {
            _evSource[i] = _evSource[i + 1]; _evSeq[i] = _evSeq[i + 1]; _evType[i] = _evType[i + 1];
            _evTarget[i] = _evTarget[i + 1]; _evBase[i] = _evBase[i + 1]; _evParts[i] = _evParts[i + 1];
            _evArg[i] = _evArg[i + 1]; _evTime[i] = _evTime[i + 1]; _evAcked[i] = _evAcked[i + 1];
            _evDecided[i] = _evDecided[i + 1]; _evNeeded[i] = _evNeeded[i + 1]; _evInInbox[i] = _evInInbox[i + 1];
        }
        _evCount = last;
        return true;
    }

    // ---- Contact timelines ----

    private int CurrentMask(int player)
    {
        for (int i = 0; i < _maskCount; i++) if (_maskPlayer[i] == player) return _maskValue[i] < 0 ? 0 : _maskValue[i];
        return 0;
    }

    // Whether player touched baseIndex (0 home .. 3) at time. Before the player's first event at
    // that base in this play the state is what that event implies (an exit: touching), or, with no
    // event at all, the player's current contact bits.
    private bool TouchingAt(int player, int baseIndex, double time)
    {
        int first = -1, latest = -1;
        for (int i = 0; i < _evCount; i++)
        {
            if (_evSource[i] != player || _evBase[i] != baseIndex || !ContactRules.IsBaseType(_evType[i])) continue;
            if (first < 0 || _evTime[i] < _evTime[first]) first = i;
            if (_evTime[i] <= time && (latest < 0 || _evTime[i] >= _evTime[latest])) latest = i;
        }
        if (latest >= 0) return _evType[latest] == ContactRules.TypeBaseTouchEnter;
        if (first >= 0) return _evType[first] == ContactRules.TypeBaseTouchExit;
        return (CurrentMask(player) & (1 << baseIndex)) != 0;
    }

    private int TouchMaskAt(int player, double time)
    {
        int mask = 0;
        for (int b = 0; b < ContactRules.BaseCount; b++) if (TouchingAt(player, b, time)) mask |= 1 << b;
        return mask;
    }

    // The earliest touch of baseIndex by player strictly after 'after', -1 when none.
    private int FirstEnter(int player, int baseIndex, double after)
    {
        int best = -1;
        for (int i = 0; i < _evCount; i++)
        {
            if (_evSource[i] != player || _evType[i] != ContactRules.TypeBaseTouchEnter || _evBase[i] != baseIndex || _evTime[i] <= after) continue;
            if (best < 0 || _evTime[i] < _evTime[best]) best = i;
        }
        return best;
    }

    // The same, skipping touches already decided (one that led to a review or to nothing).
    private int FirstOpenEnter(int player, int baseIndex, double after)
    {
        int best = -1;
        for (int i = 0; i < _evCount; i++)
        {
            if (_evDecided[i] || _evSource[i] != player || _evType[i] != ContactRules.TypeBaseTouchEnter || _evBase[i] != baseIndex || _evTime[i] <= after) continue;
            if (best < 0 || _evTime[i] < _evTime[best]) best = i;
        }
        return best;
    }

    // The nearest change of any base contact of player to time, in seconds (large when none).
    private double NearestContactChange(int player, double time)
    {
        double best = 1e9;
        for (int i = 0; i < _evCount; i++)
        {
            if (_evSource[i] != player || !ContactRules.IsBaseType(_evType[i])) continue;
            double d = _evTime[i] > time ? _evTime[i] - time : time - _evTime[i];
            if (d < best) best = d;
        }
        return best;
    }

    // The latest exit of baseIndex by player at or before time, -1 when none.
    private int LastExit(int player, int baseIndex, double time)
    {
        int best = -1;
        for (int i = 0; i < _evCount; i++)
        {
            if (_evSource[i] != player || _evType[i] != ContactRules.TypeBaseTouchExit || _evBase[i] != baseIndex || _evTime[i] > time) continue;
            if (best < 0 || _evTime[i] > _evTime[best]) best = i;
        }
        return best;
    }

    // ---- Holds ----

    // The earliest time at or after 'from' when a fielder of the defense holds the ball (an
    // accepted possession, until the next state that is not the same possession) while touching
    // baseIndex: the force is made then. -1 when there is none.
    private double ForceHoldTime(int baseIndex, double from)
    {
        double best = -1.0;
        int defense = MatchRules.DefenseTeam(_m);
        for (int r = RosterRules.RoleP; r <= RosterRules.RoleRF; r++)
        {
            int fielder = _p[PlayRules.SnapRoles + defense * RosterRules.RoleCount + r];
            if (fielder <= 0) continue;
            for (int h = _histCount - 1; h >= 0; h--)
            {
                if (HistField(h, MatchBallQueryBehaviour.HistoryIsPossession) != 1 || HistField(h, MatchBallQueryBehaviour.HistoryHolder) != fielder) continue;
                // Only the start of a possession (a republish of the same hold continues it).
                if (h + 1 < _histCount && SamePossession(h + 1, fielder, HistField(h, MatchBallQueryBehaviour.HistoryHold))) continue;
                double start = _histTimes[h];
                double end = 1e12;
                for (int n = h - 1; n >= 0; n--)
                {
                    if (SamePossession(n, fielder, HistField(h, MatchBallQueryBehaviour.HistoryHold))) continue;
                    end = _histTimes[n];
                    break;
                }
                double t = TouchOverlap(fielder, baseIndex, start > from ? start : from, end);
                if (t >= 0.0 && (best < 0.0 || t < best)) best = t;
            }
        }
        return best;
    }

    private bool SamePossession(int entry, int holder, int hold)
    {
        return HistField(entry, MatchBallQueryBehaviour.HistoryIsPossession) == 1 && HistField(entry, MatchBallQueryBehaviour.HistoryHolder) == holder
            && HistField(entry, MatchBallQueryBehaviour.HistoryHold) == hold;
    }

    // The earliest time in [start, end) at which fielder touches baseIndex, -1 when never.
    private double TouchOverlap(int fielder, int baseIndex, double start, double end)
    {
        if (end <= start) return -1.0;
        if (TouchingAt(fielder, baseIndex, start)) return start;
        int e = FirstEnter(fielder, baseIndex, start);
        if (e >= 0 && _evTime[e] < end) return _evTime[e];
        return -1.0;
    }

    // ---- Forces ----

    // Forced at time (RunnerRules.IsForced, with outs made after that time not yet counted): the
    // force a following runner's out removes only exists from that out on.
    private bool ForcedAt(int slot, double time)
    {
        if (_p[PlayRules.BattedBall] == 0 || !RunnerRules.IsActive(_p, slot)) return false;
        int start = _p[RunnerRules.At(slot, RunnerRules.StartBase)];
        if (_p[RunnerRules.At(slot, RunnerRules.Base)] != start) return false;
        for (int behind = start - 1; behind >= 0; behind--)
        {
            bool held = false;
            for (int s = 0; s < RunnerRules.SlotCount && !held; s++)
            {
                if (s == slot || _p[RunnerRules.At(s, RunnerRules.StartBase)] != behind) continue;
                int status = _p[RunnerRules.At(s, RunnerRules.Status)];
                if (status == RunnerRules.StatusActive || status == RunnerRules.StatusScored) held = true;
                else if (status == RunnerRules.StatusOut && _t[PlayRules.TimeRunner + s] > time) held = true;
            }
            if (!held) return false;
        }
        return true;
    }

    private static int TouchBase(int target) { return target == RunnerRules.Scored ? 0 : target; }

    private bool HasPendingFor(int runnerId)
    {
        for (int i = 0; i < _p[PlayRules.PendingCount]; i++)
            if (_p[PlayRules.Pending + i * PlayRules.PendingStride + 1] == runnerId) return true;
        return false;
    }

    // ---- Runner decisions ----

    private void DecideRunners()
    {
        if (BattedBallOpen())
        {
            // Nothing a runner did is ruled before fair / foul: keep every event.
            for (int i = 0; i < _evCount; i++) if (_evType[i] != ContactRules.TypePitchResult) _evNeeded[i] = true;
            _open = true;
            return;
        }
        for (int guard = 0; guard < 12 && !_endsLive; guard++)
        {
            int bestSlot = -1;
            int bestKind = CandNone, bestCode = 0, bestArg = 0, bestReview = 0, bestEvent = -1;
            double bestTime = 0.0;
            for (int s = 0; s < RunnerRules.SlotCount; s++)
            {
                if (!RunnerRules.IsActive(_p, s)) continue;
                NextRunnerDecision(s);
                if (_candKind == CandNone) continue;
                if (bestSlot >= 0 && _candTime >= bestTime) continue;
                bestSlot = s; bestKind = _candKind; bestCode = _candCode; bestArg = _candArg;
                bestReview = _candReview; bestEvent = _candEvent; bestTime = _candTime;
            }
            if (bestSlot < 0) return;
            int runnerId = _p[RunnerRules.At(bestSlot, RunnerRules.Id)];
            int player = _p[RunnerRules.At(bestSlot, RunnerRules.Player)];
            if (bestEvent >= 0) _evDecided[bestEvent] = true;
            if (bestKind == CandRuling) Rule(bestCode, runnerId, bestArg, bestTime);
            else if (bestKind == CandReview)
            {
                // The umpire takes this runner over for the rest of the play.
                Review(bestReview, player, bestArg, bestTime);
                Defer(player);
            }
        }
    }

    // The earliest decidable thing of the runner in slot, by server time: an undecided tag, the
    // force play at the force base, or the touch of the next base. Fills _cand*; CandNone when
    // nothing is decidable yet (waiting marks the events it needs and keeps the engine open).
    private void NextRunnerDecision(int slot)
    {
        _candKind = CandNone;
        _candEvent = -1;
        _candReview = 0;
        int player = _p[RunnerRules.At(slot, RunnerRules.Player)];
        int runnerId = _p[RunnerRules.At(slot, RunnerRules.Id)];
        int baseIndex = _p[RunnerRules.At(slot, RunnerRules.Base)];
        double since = _t[PlayRules.TimeRunner + slot];
        if (HasPendingFor(runnerId) || IsDeferred(player)) return;

        // The earliest undecided tag of this runner.
        int tag = -1;
        double tagTime = 0.0;
        for (int i = 0; i < _evCount; i++)
        {
            if (_evType[i] != ContactRules.TypeTagContactEnter || _evTarget[i] != player || _evDecided[i]) continue;
            double corrected = CorrectedTagTime(i);
            if (tag < 0 || corrected < tagTime) { tag = i; tagTime = corrected; }
        }

        // The force play, or the next base.
        bool forced = ForcedAt(slot, _now);
        int target = forced ? RunnerRules.ForceTarget(_p, slot) : baseIndex + 1;
        if (!forced && baseIndex >= 3) target = RunnerRules.Scored;
        if (target > RunnerRules.Scored) target = RunnerRules.Scored;
        int arrival = FirstOpenEnter(player, TouchBase(target), since);
        double forceTime = forced ? ForceHoldTime(TouchBase(target), _t[PlayRules.TimeLastPitch]) : -1.0;

        double next = 1e12;
        int what = 0; // 1 tag, 2 force out, 3 arrival
        if (tag >= 0) { next = tagTime; what = 1; }
        if (forced && forceTime >= 0.0 && forceTime < next && (arrival < 0 || forceTime < _evTime[arrival])) { next = forceTime; what = 2; }
        if (arrival >= 0 && _evTime[arrival] < next) { next = _evTime[arrival]; what = 3; }
        if (what == 0)
        {
            MarkMissedBase(slot, player, baseIndex, since);
            return;
        }
        if (tag >= 0) _evNeeded[tag] = true;
        if (arrival >= 0) _evNeeded[arrival] = true;
        MarkPlayerEventsNeeded(player);
        if (_now < next + decisionDelaySeconds)
        {
            _open = true;
            return;
        }
        _candTime = next;
        if (what == 1) { DecideTag(slot, player, tag, tagTime, forced); return; }
        if (what == 2)
        {
            double margin = arrival >= 0 ? _evTime[arrival] - forceTime : 1e9;
            if (margin < contestedMarginSeconds) SetReview(ReviewForceContested, target, -1);
            else SetRuling(PlayRules.JudgeForceOut, target, -1);
            return;
        }
        // Arrival at the target base.
        if (AfterCaughtFly()) { SetReview(ReviewAfterCaughtFly, target, arrival); return; }
        if (forced)
        {
            double margin = forceTime >= 0.0 ? forceTime - _evTime[arrival] : 1e9;
            if (margin >= 0.0 && margin < contestedMarginSeconds && margin > 0.0) SetReview(ReviewForceContested, target, arrival);
            else SetRuling(PlayRules.JudgeForceSafe, target, arrival);
            return;
        }
        int check = RunnerRules.CanAdvance(_p, slot, target);
        if (check == RunnerRules.AdvancePassing) { SetReview(ReviewPassing, target, arrival); return; }
        if (check == RunnerRules.AdvanceInvalid) { SetNoRuling(arrival); return; }
        SetRuling(target == RunnerRules.Scored ? PlayRules.JudgeRunScored : PlayRules.JudgeRunnerAdvance, target, arrival);
    }

    private void SetRuling(int code, int arg, int eventIndex) { _candKind = CandRuling; _candCode = code; _candArg = arg; _candEvent = eventIndex; }
    private void SetReview(int review, int arg, int eventIndex) { _candKind = CandReview; _candReview = review; _candArg = arg; _candEvent = eventIndex; }
    private void SetNoRuling(int eventIndex) { _candKind = CandNoRuling; _candEvent = eventIndex; }

    private void MarkPlayerEventsNeeded(int player)
    {
        for (int i = 0; i < _evCount; i++) if (_evSource[i] == player || _evTarget[i] == player) _evNeeded[i] = true;
    }

    // A touch of a base beyond the next one without the next one: a missed base (appeal) is the
    // umpire's, never an award.
    private void MarkMissedBase(int slot, int player, int baseIndex, double since)
    {
        for (int b = baseIndex + 2; b <= RunnerRules.Scored; b++)
        {
            int e = FirstEnter(player, TouchBase(b), since);
            if (e < 0 || _evDecided[e] || _now < _evTime[e] + decisionDelaySeconds) continue;
            _evDecided[e] = true;
            Review(ReviewMissedBase, player, b, _evTime[e]);
            return;
        }
    }

    // A tag's server time, moved back by how late the fielder's client showed the runner (when
    // enabled and measured). Only the time is corrected; the contact itself is never moved.
    private double CorrectedTagTime(int index)
    {
        double time = _evTime[index];
        if (!applyTagTimeCorrection || _evArg[index] <= 0) return time;
        float delay = Mathf.Min(_evArg[index] / 1000f, Mathf.Max(0f, maxTagCorrectionSeconds));
        return time - delay;
    }

    // A tag at time: out when the runner touched no base they are entitled to (OBR 5.09(b)(4)),
    // nothing when they were on their base or had reached the next one, the umpire when the touch
    // and the tag are too close to order, when a batter-runner may be returning from an overrun of
    // first base, or when the runner stood on a base they do not hold.
    private void DecideTag(int slot, int player, int tag, double time, bool forced)
    {
        _lastTagRawTime = _evTime[tag];
        _lastTagCorrectedTime = time;
        _lastTagCorrection = (float)(_evTime[tag] - time);
        _candEvent = tag;
        if (AfterCaughtFly()) { SetReview(ReviewAfterCaughtFly, 0, tag); return; }
        int baseIndex = _p[RunnerRules.At(slot, RunnerRules.Base)];
        int start = _p[RunnerRules.At(slot, RunnerRules.StartBase)];
        int mask = TouchMaskAt(player, time);
        bool forcedThen = forced && ForcedAt(slot, time);
        int own = baseIndex == RunnerRules.Scored ? 0 : (1 << baseIndex);
        // A batter-runner at home has no base yet; a forced runner has lost the start base.
        if (baseIndex == RunnerRules.HomePlate || (forcedThen && baseIndex == start)) own = 0;
        int next = baseIndex < 3 ? 1 << (baseIndex + 1) : 1;
        if ((mask & (own | next)) != 0)
        {
            SetNoRuling(tag);
            return;
        }
        if (NearestContactChange(player, time) < contestedMarginSeconds) { SetReview(ReviewTagContested, 0, tag); return; }
        if (mask != 0 && !(forcedThen && (mask & (1 << start)) != 0)) { SetReview(ReviewRunnerConflict, 0, tag); return; }
        if (start == RunnerRules.HomePlate && baseIndex == 1)
        {
            int exit = LastExit(player, 1, time);
            if (exit >= 0 && time - _evTime[exit] <= overrunWindowSeconds && FirstEnter(player, 2, _evTime[exit]) < 0)
            {
                SetReview(ReviewOverrun, 1, tag);
                return;
            }
        }
        SetRuling(PlayRules.JudgeTagOut, 0, tag);
    }

    // ---- Third out ----

    // After the third out the half-inning waits for a dead ball; once nothing is open for a while
    // the engine reports it, so the commit and the change of sides need no Time.
    private void DecideThirdOut()
    {
        if (_endsLive || _p[PlayRules.DHalfEnd] != 1 || thirdOutDeadBallSeconds <= 0f) return;
        if (_open) return;
        if (_now - _t[PlayRules.TimeThirdOut] < thirdOutDeadBallSeconds)
        {
            _wantPass = true;
            return;
        }
        DeadBall(PlayRules.DeadThreeOuts, _now);
    }

    // ---- Handing events back to the receiver ----

    // An event leaves the inbox once nothing open needs it: decided pitch results and tags at
    // once, events of players who are neither runners nor fielders at once, base contacts after
    // eventRetentionSeconds. When the inbox nears its capacity the oldest events nothing open
    // needs go first (counted), so the senders are never blocked by the engine.
    private void DecideAcks()
    {
        int inbox = receiver != null ? receiver.GetInboxCount() : 0;
        int capacity = receiver != null ? receiver.GetInboxCapacity() : MatchEventReceiverBehaviour.MaxInbox;
        bool pressure = inbox >= capacity - 4;
        for (int i = 0; i < _evCount; i++)
        {
            if (_evAcked[i]) continue;
            if (!_evInInbox[i]) { _evAcked[i] = true; continue; }
            if (_evDecided[i] && !_evNeeded[i]) { Ack(i); continue; }
            if (_evNeeded[i]) continue;
            if (!Relevant(i)) { Ack(i); continue; }
            if (_now - _evTime[i] > eventRetentionSeconds) { Ack(i); continue; }
            if (pressure) { _ackEarly++; Ack(i); pressure = false; }
        }
    }

    private bool Relevant(int index)
    {
        int source = _evSource[index];
        if (_evType[index] == ContactRules.TypePitchResult) return !_evDecided[index];
        if (RunnerRules.FindActiveByPlayer(_p, source) >= 0) return true;
        if (_evType[index] == ContactRules.TypeTagContactEnter) return RunnerRules.FindActiveByPlayer(_p, _evTarget[index]) >= 0;
        return PlayRules.IsDefensiveFielder(_p, _m, source) && _p[PlayRules.BattedBall] == 1;
    }
}
