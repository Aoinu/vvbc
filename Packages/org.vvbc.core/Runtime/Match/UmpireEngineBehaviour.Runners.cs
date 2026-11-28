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
        if (_evCount >= MaxEvents && !DropOldestAcked())
        {
            _logOverflow++;
            _logIncomplete = true;
            return;
        }
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
        // A base contact leaving the log becomes its player's baseline: the contact state at
        // its time, from which the later events continue the timeline (TouchAt).
        if (ContactRules.IsBaseType(_evType[oldest])) KeepBaseline(_evSource[oldest], _evTime[oldest]);
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
    //
    // A player's base contact at a past time comes from the player's own events of the play
    // (Enter / Exit with their server times) and the contact bits the engine saw at the play's
    // first pass (the snapshot), never from the current bits alone: those only continue the
    // timeline after its last event when they agree with it. A player with no event at a base
    // kept the snapshot's contact there, as long as the current bits still show it. Anything
    // the log cannot show (the log lost events: a new Owner, an overflow; the events, the
    // snapshot or the current bits disagree: events still on the way; no snapshot; a time
    // before what the play's stream covers) is TouchUnknown, never read as "not touching".

    public const int TouchNo = 0;
    public const int TouchYes = 1;
    public const int TouchUnknown = 2;

    // The live play's log lost events (a new Owner, the log overflowing): each player's timeline
    // is then complete only where the log shows every sequence the receiver settled for them.
    private bool _logIncomplete;
    private int _logResetPlayId;
    // Baselines: the contact state of a player when their oldest base event left the log.
    private readonly int[] _basePlayer = new int[MaxMasks];
    private readonly double[] _baseTime = new double[MaxMasks];
    private readonly int[] _baseYes = new int[MaxMasks];
    private readonly int[] _baseUnknown = new int[MaxMasks];
    private int _baseCount;
    // Per player of the mask list: whether their timeline in the log is complete (LoadMasks).
    private readonly bool[] _maskComplete = new bool[MaxMasks];
    // The contact bits of the play's players at the engine's first pass of the play (-1: no
    // collector), at _snapTime.
    private readonly int[] _snapPlayer = new int[MaxMasks];
    private readonly int[] _snapBits = new int[MaxMasks];
    private int _snapCount;
    private double _snapTime;
    private bool _snapWanted;

    private int SnapIndex(int player)
    {
        for (int i = 0; i < _snapCount; i++) if (_snapPlayer[i] == player) return i;
        return -1;
    }

    // Takes the snapshot from the contact bits just loaded (the first pass of a play).
    private void TakeSnapshot(double time)
    {
        _snapWanted = false;
        _snapTime = time;
        _snapCount = 0;
        for (int i = 0; i < _maskCount && _snapCount < MaxMasks; i++)
        {
            _snapPlayer[_snapCount] = _maskPlayer[i];
            _snapBits[_snapCount] = _maskValue[i];
            _snapCount++;
        }
    }

    private int MaskIndex(int player)
    {
        for (int i = 0; i < _maskCount; i++) if (_maskPlayer[i] == player) return i;
        return -1;
    }

    private int BaselineIndex(int player)
    {
        for (int i = 0; i < _baseCount; i++) if (_basePlayer[i] == player) return i;
        return -1;
    }

    private void KeepBaseline(int player, double time)
    {
        int at = BaselineIndex(player);
        if (at < 0)
        {
            if (_baseCount >= MaxMasks) { _logIncomplete = true; return; }
            at = _baseCount++;
            _basePlayer[at] = player;
        }
        else if (_baseTime[at] > time) return;
        int yes = 0, unknown = 0;
        for (int b = 0; b < ContactRules.BaseCount; b++)
        {
            int state = TouchAt(player, b, time);
            if (state == TouchYes) yes |= 1 << b;
            else if (state == TouchUnknown) unknown |= 1 << b;
        }
        _baseTime[at] = time;
        _baseYes[at] = yes;
        _baseUnknown[at] = unknown;
    }

    // Every sequence 1..watermark of player's events is in the log (rejected ones count as
    // missing: the answer is then "not known").
    private bool SequencesInLog(int player, int watermark)
    {
        for (int seq = 1; seq <= watermark; seq++)
        {
            bool found = false;
            for (int i = 0; i < _evCount && !found; i++) found = _evSource[i] == player && _evSeq[i] == seq;
            if (!found) return false;
        }
        return true;
    }

    // Player's contact with baseIndex (0 home .. 3) at time: TouchYes, TouchNo or TouchUnknown.
    private int TouchAt(int player, int baseIndex, double time)
    {
        int mi = MaskIndex(player);
        if (mi < 0 || !_maskComplete[mi]) return TouchUnknown;
        if (time < _t[PlayRules.TimeStart] - PlayRules.TimeSkew) return TouchUnknown;
        int bit = 1 << baseIndex;
        double from = -1e12;
        int startState = -1;
        int bi = BaselineIndex(player);
        if (bi >= 0)
        {
            if (time < _baseTime[bi]) return TouchUnknown;
            from = _baseTime[bi];
            startState = (_baseUnknown[bi] & bit) != 0 ? TouchUnknown : (_baseYes[bi] & bit) != 0 ? TouchYes : TouchNo;
        }
        // The snapshot is a known point of the timeline when no baseline replaced it.
        int snapState = -1;
        if (bi < 0)
        {
            int si = SnapIndex(player);
            if (si >= 0 && _snapBits[si] >= 0) snapState = (_snapBits[si] & bit) != 0 ? TouchYes : TouchNo;
        }
        int first = -1, latest = -1, last = -1, atSnap = -1;
        for (int i = 0; i < _evCount; i++)
        {
            if (_evSource[i] != player || _evBase[i] != baseIndex || !ContactRules.IsBaseType(_evType[i]) || _evTime[i] <= from) continue;
            if (first < 0 || _evTime[i] < _evTime[first]) first = i;
            if (last < 0 || _evTime[i] >= _evTime[last]) last = i;
            if (_evTime[i] <= time && (latest < 0 || _evTime[i] >= _evTime[latest])) latest = i;
            if (_evTime[i] <= _snapTime && (atSnap < 0 || _evTime[i] >= _evTime[atSnap])) atSnap = i;
        }
        if (snapState >= 0 && first >= 0)
        {
            // The events must say at the snapshot's time what the snapshot saw; otherwise one is
            // missing and nothing of this base's timeline can be told.
            int eventsAtSnap = atSnap >= 0 ? (_evType[atSnap] == ContactRules.TypeBaseTouchEnter ? TouchYes : TouchNo)
                : (_evType[first] == ContactRules.TypeBaseTouchExit ? TouchYes : TouchNo);
            if (eventsAtSnap != snapState) return TouchUnknown;
        }
        int state;
        if (latest >= 0) state = _evType[latest] == ContactRules.TypeBaseTouchEnter ? TouchYes : TouchNo;
        else if (first >= 0) state = startState == TouchYes || startState == TouchNo ? startState
            : _evType[first] == ContactRules.TypeBaseTouchExit ? TouchYes : TouchNo;
        else state = bi >= 0 ? startState : snapState;
        if (last >= 0 && time <= _evTime[last]) return state;
        // No event at all, up to the snapshot: what it saw.
        if (last < 0 && bi < 0 && snapState >= 0 && time <= _snapTime) return state;
        // After the last known point: the current bits must agree with it.
        int mask = _maskValue[mi];
        if (mask < 0 || state < 0 || state == TouchUnknown) return TouchUnknown;
        int now = (mask & bit) != 0 ? TouchYes : TouchNo;
        return state == now ? state : TouchUnknown;
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

    // When a fielder of the defense holds the ball (an accepted possession, until the next state
    // that is not the same possession) while touching baseIndex, at or after 'from': the force is
    // made then. Known exactly, _forceLo == _forceHi; with a hold whose start fell out of the
    // history or a fielder's contact not known, the force was made somewhere in
    // [_forceLo, _forceHi] (_forceHi Unbounded: maybe never). Returns _forceLo, -1 when no hold
    // can make the force.
    private const double Unbounded = 1e12;
    private double _forceLo;
    private double _forceHi;
    private double _overlapHi;

    private double ForceHoldTime(int baseIndex, double from)
    {
        _forceLo = -1.0;
        _forceHi = Unbounded;
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
                // The oldest entry of a full history may continue an older, dropped one.
                bool startKnown = h + 1 < _histCount || _histCount < MaxHistory;
                double end = Unbounded;
                for (int n = h - 1; n >= 0; n--)
                {
                    if (SamePossession(n, fielder, HistField(h, MatchBallQueryBehaviour.HistoryHold))) continue;
                    end = _histTimes[n];
                    break;
                }
                double lo = TouchOverlap(fielder, baseIndex, start > from ? start : from, end);
                if (lo < 0.0) continue;
                double hi = _overlapHi;
                if (!startKnown && lo <= start) lo = from;
                if (_forceLo < 0.0 || lo < _forceLo) _forceLo = lo;
                if (hi < _forceHi) _forceHi = hi;
            }
        }
        return _forceLo;
    }

    private bool SamePossession(int entry, int holder, int hold)
    {
        return HistField(entry, MatchBallQueryBehaviour.HistoryIsPossession) == 1 && HistField(entry, MatchBallQueryBehaviour.HistoryHolder) == holder
            && HistField(entry, MatchBallQueryBehaviour.HistoryHold) == hold;
    }

    // The earliest time in [start, end) at which fielder touches baseIndex, -1 when never. With
    // the contact at start not known, the touch is somewhere from start to the first known
    // touch (_overlapHi; Unbounded without one).
    private double TouchOverlap(int fielder, int baseIndex, double start, double end)
    {
        _overlapHi = Unbounded;
        if (end <= start) return -1.0;
        int state = TouchAt(fielder, baseIndex, start);
        int e = FirstEnter(fielder, baseIndex, start);
        double enter = e >= 0 && _evTime[e] < end ? _evTime[e] : -1.0;
        if (state == TouchYes) { _overlapHi = start; return start; }
        if (state == TouchUnknown)
        {
            if (enter >= 0.0) _overlapHi = enter;
            return start;
        }
        if (enter < 0.0)
        {
            // No touch in the log; still possible if the timeline is not known up to the end.
            double until = end < _now ? end : _now;
            return TouchAt(fielder, baseIndex, until) == TouchUnknown ? start : -1.0;
        }
        _overlapHi = enter;
        return enter;
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
            if (bestKind == CandRuling)
            {
                // A ruling the rules refuse (it contradicts one already applied, e.g. a force
                // whose events came after a later out) is reviewed by Rule; the umpire then takes
                // the runner over, so the same candidate is not sent again.
                if (Rule(bestCode, runnerId, bestArg, bestTime) == PlayRules.RejectedContradiction) Defer(player);
            }
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

        // The force play, or the next base. Whether the runner is forced is a question about the
        // time of each candidate (a following runner's out removes the force only from then on,
        // ForcedAt), never about now: a force made before that out stays a force.
        bool forceable = ForcedAt(slot, _t[PlayRules.TimeLastPitch]);
        int target = forceable ? RunnerRules.ForceTarget(_p, slot) : baseIndex + 1;
        if (!forceable && baseIndex >= 3) target = RunnerRules.Scored;
        if (target > RunnerRules.Scored) target = RunnerRules.Scored;
        int arrival = FirstOpenEnter(player, TouchBase(target), since);
        double arrivalTime = arrival >= 0 ? _evTime[arrival] : -1.0;
        double forceLo = -1.0, forceHi = Unbounded;
        if (forceable && ForceHoldTime(TouchBase(target), _t[PlayRules.TimeLastPitch]) >= 0.0)
        {
            forceLo = _forceLo;
            forceHi = _forceHi;
            // A force already removed when the hold met the base is none.
            if (!ForcedAt(slot, forceLo)) { forceLo = -1.0; forceHi = Unbounded; }
        }

        double next = 1e12;
        int what = 0; // 1 tag, 2 force out, 3 arrival
        if (tag >= 0) { next = tagTime; what = 1; }
        if (forceLo >= 0.0 && forceLo < next && (arrival < 0 || forceLo < arrivalTime)) { next = forceLo; what = 2; }
        if (arrival >= 0 && arrivalTime < next) { next = arrivalTime; what = 3; }
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
        if (what == 1) { DecideTag(slot, player, tag, tagTime, forceable); return; }
        if (what == 2) { DecideForceOut(slot, player, target, forceLo, forceHi, arrivalTime); return; }
        // Arrival at the target base.
        if (AfterCaughtFly()) { SetReview(ReviewAfterCaughtFly, target, arrival); return; }
        if (forceable && ForcedAt(slot, arrivalTime))
        {
            double margin = forceLo >= 0.0 ? forceLo - arrivalTime : 1e9;
            if (margin >= 0.0 && margin < contestedMarginSeconds && margin > 0.0) SetReview(ReviewForceContested, target, arrival);
            else SetRuling(PlayRules.JudgeForceSafe, target, arrival);
            return;
        }
        int check = RunnerRules.CanAdvance(_p, slot, target);
        if (check == RunnerRules.AdvancePassing) { SetReview(ReviewPassing, target, arrival); return; }
        if (check == RunnerRules.AdvanceInvalid) { SetNoRuling(arrival); return; }
        SetRuling(target == RunnerRules.Scored ? PlayRules.JudgeRunScored : PlayRules.JudgeRunnerAdvance, target, arrival);
    }

    // The force made (by a hold on the base) before the runner reached it: out at the time the
    // force is certainly made. Unknown contacts (the fielder's, or whether the runner had reached
    // the base) and a runner within contestedMarginSeconds go to the umpire.
    private void DecideForceOut(int slot, int player, int target, double lo, double hi, double arrivalTime)
    {
        if (hi >= Unbounded) { SetReview(ReviewTouchUnknown, target, -1); return; }
        if (arrivalTime >= 0.0 && arrivalTime - hi < contestedMarginSeconds)
        {
            SetReview(hi > lo ? ReviewTouchUnknown : ReviewForceContested, target, -1);
            return;
        }
        int runner = TouchAt(player, TouchBase(target), hi);
        if (runner == TouchUnknown) { SetReview(ReviewTouchUnknown, target, -1); return; }
        if (runner == TouchYes) { SetReview(ReviewForceContested, target, -1); return; }
        if (hi > lo && _now < hi + decisionDelaySeconds) { _open = true; return; }
        _candTime = hi;
        SetRuling(PlayRules.JudgeForceOut, target, -1);
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
        // The runner's contacts at the (corrected) tag time, from the timeline only.
        int mask = 0, unknown = 0;
        for (int b = 0; b < ContactRules.BaseCount; b++)
        {
            int state = TouchAt(player, b, time);
            if (state == TouchYes) mask |= 1 << b;
            else if (state == TouchUnknown) unknown |= 1 << b;
        }
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
        // On or off a base not known (never read as off): the umpire.
        if (unknown != 0) { SetReview(ReviewTouchUnknown, 0, tag); return; }
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
