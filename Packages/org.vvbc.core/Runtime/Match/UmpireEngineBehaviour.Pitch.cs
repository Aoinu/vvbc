using UnityEngine;

// Pitch and batted-ball adjudication of the umpire engine (docs/development/automatic-umpire.md):
// the manual pitch (the ball's HandPitch with its PitchLineage, thrown by the defense's P of the
// play snapshot), the batter's pitch result, the ball's accepted hit and catches, and this
// client's simulation of the batted ball (ground contacts, positions) for fair / foul.
public partial class UmpireEngineBehaviour
{
    // ---- The pitch being adjudicated (local; the handled stages are in the PlayState) ----
    private int _pcPitcher;
    private int _pcNumber;
    private double _pcRelease;

    // ---- What this client saw of the flights (every client, kept across an Owner change) ----
    // Per flight kind (the pitch, its batted ball), keyed by the PitchLineage: when this client
    // started flying it (server time) and its first ground contact here. Not having seen a
    // ground contact is evidence of no contact only when this client flew that flight from its
    // start: the ball's network catch-up stops at the first ground contact, so a flight received
    // late either is in step with the real one or shows its contact right after it started.
    public const int ObsPitch = 0;
    public const int ObsBatted = 1;
    private readonly int[] _obsPitcher = new int[2];
    private readonly int[] _obsNumber = new int[2];
    private readonly double[] _obsStart = new double[2];      // -1: the flight was not seen starting here
    private readonly bool[] _obsGround = new bool[2];
    private readonly double[] _obsGroundTime = new double[2];
    // A ground contact this soon after the flight started here may be where a late flight's
    // catch-up stopped: the real contact can then be anywhere since the release or the hit.
    private const double CatchUpGroundSeconds = 0.1;

    // GroundRelation answers.
    public const int GroundNotBefore = 1;   // confirmed: no ground contact before that time
    public const int GroundBefore = 2;      // confirmed: the first ground contact was before it
    public const int GroundTooClose = 3;    // both known, closer than the tolerance
    public const int GroundUnknown = 4;     // this client did not see enough of the flight

    // The batted ball of the pitch, as this client simulates it.
    private int _bbPitcher;
    private int _bbNumber;
    private int _bbDecision;
    private double _bbDecisionTime;
    private bool _bbGrounded;
    private double _bbGroundTime;
    private Vector3 _bbGroundPosition;
    private Vector3 _bbPrevious;
    private bool _bbHasPrevious;
    private double _bbSettleStart = -1.0;
    private const int OutOfPlayDecision = 3;
    // The batted ball of this pitch was caught on the fly: runners must retouch (umpire).
    private int _flyPitcher;
    private int _flyNumber;
    // Something waits for a timer (a review deadline, the third-out dead ball), apart from _open.
    private bool _wantPass;

    private void ResetLocal()
    {
        _evCount = 0;
        _evPlayId = 0;
        _seq = 0;
        _seqPlayId = 0;
        ResetPitchTracking();
    }

    private void ResetPitchTracking()
    {
        _pcPitcher = 0; _pcNumber = 0; _pcRelease = 0.0;
        ResetBatted(0, 0);
        _flyPitcher = 0; _flyNumber = 0;
    }

    private void ResetBatted(int pitcher, int number)
    {
        _bbPitcher = pitcher;
        _bbNumber = number;
        _bbDecision = FairFoulRules.Undecided;
        _bbDecisionTime = 0.0;
        _bbGrounded = false;
        _bbGroundTime = 0.0;
        _bbGroundPosition = Vector3.zero;
        _bbHasPrevious = false;
        _bbSettleStart = -1.0;
    }

    private void BeginPlay(int playId)
    {
        if (_evPlayId == playId) return;
        _evPlayId = playId;
        _evCount = 0;
        _baseCount = 0;
        _snapCount = 0;
        _snapWanted = true;
        // A new Owner's engine that starts in the middle of this play missed what was handed back.
        _logIncomplete = playId == _logResetPlayId;
        ResetPitchTracking();
    }

    // ---- The whole decision of one pass ----

    private void Decide()
    {
        _outCount = 0;
        _endsLive = false;
        _open = false;
        _wantPass = false;
        if (_seqPlayId != _playId)
        {
            _seqPlayId = _playId;
            _seq = 0;
        }
        _seqNext = _seq + 1;
        int settled = PlayEvents.Watermark(_p, _source);
        if (_seqNext <= settled) _seqNext = settled + 1;
        for (int i = 0; i < _evCount; i++)
        {
            _evNeeded[i] = false;
            _evDecidedBefore[i] = _evDecided[i];
        }
        DecidePitch();
        DecideRunners();
        DecideThirdOut();
        DecideAcks();
    }

    // ---- Ball history (newest first) ----

    private int HistField(int entry, int field) { return _hist[entry * MatchBallQueryBehaviour.HistoryFields + field]; }

    private bool SameLineage(int entry, int pitcher, int number)
    {
        return pitcher > 0 && HistField(entry, MatchBallQueryBehaviour.HistoryPitcher) == pitcher && HistField(entry, MatchBallQueryBehaviour.HistoryPitchNumber) == number;
    }

    // The newest manual pitch of pitcher in this play, -1 when none.
    private int LatestHandPitch(int pitcher)
    {
        if (pitcher <= 0) return -1;
        double earliest = _t[PlayRules.TimeStart];
        for (int i = 0; i < _histCount; i++)
        {
            if (HistField(i, MatchBallQueryBehaviour.HistoryKind) != MatchBallQueryBehaviour.KindHandPitch) continue;
            if (HistField(i, MatchBallQueryBehaviour.HistoryPitcher) != pitcher || HistField(i, MatchBallQueryBehaviour.HistoryPitchNumber) <= 0) continue;
            if (_histTimes[i] < earliest) return -1;
            return i;
        }
        return -1;
    }

    // The accepted hit of the pitch (kind Hit with its lineage), -1 when none.
    private int FindHit(int pitcher, int number, double release)
    {
        for (int i = _histCount - 1; i >= 0; i--)
            if (HistField(i, MatchBallQueryBehaviour.HistoryKind) == MatchBallQueryBehaviour.KindHit && SameLineage(i, pitcher, number) && _histTimes[i] >= release) return i;
        return -1;
    }

    // The earliest possession of the lineage after 'after' by a fielder of the defense (holder
    // = 0: any fielder; else only that player), -1 when none.
    private int FindCatch(int pitcher, int number, double after, int holder)
    {
        for (int i = _histCount - 1; i >= 0; i--)
        {
            if (HistField(i, MatchBallQueryBehaviour.HistoryIsPossession) != 1 || !SameLineage(i, pitcher, number) || _histTimes[i] < after) continue;
            int who = HistField(i, MatchBallQueryBehaviour.HistoryHolder);
            if (holder > 0 ? who == holder : PlayRules.IsDefensiveFielder(_p, _m, who)) return i;
        }
        return -1;
    }

    private int DefenseRole(int role) { return _p[PlayRules.SnapRoles + MatchRules.DefenseTeam(_m) * RosterRules.RoleCount + role]; }

    private int FindPitchResult(int pitcher, int number)
    {
        for (int i = 0; i < _evCount; i++)
            if (_evType[i] == ContactRules.TypePitchResult && _evTarget[i] == pitcher && _evArg[i] == number) return i;
        return -1;
    }

    private static bool PitchStageDone(int stage, bool strikeoutOpen)
    {
        if ((stage & PlayRules.PhysStageReview) != 0) return true;
        if ((stage & PlayRules.PhysStageCall) == 0) return false;
        return !strikeoutOpen || (stage & PlayRules.PhysStageStrikeout) != 0;
    }

    private void HandToUmpire(int review, int pitcher, int number, double time)
    {
        Review(review, pitcher, number, time);
        Mark(pitcher, number, PlayRules.PhysStageAll);
    }

    // ---- Pitch ----

    private void DecidePitch()
    {
        int pitcher = DefenseRole(RosterRules.RoleP);
        int at = LatestHandPitch(pitcher);
        if (at < 0) return;
        int number = HistField(at, MatchBallQueryBehaviour.HistoryPitchNumber);
        double release = _histTimes[at];
        // An older pitch than the one last handled is history.
        if (_p[PlayRules.PhysPitcher] == pitcher && number < _p[PlayRules.PhysPitchNumber]) return;
        if (_pcNumber > 0 && (_pcPitcher != pitcher || _pcNumber != number))
        {
            // A new pitch while the previous one is still open: the previous one is never ruled.
            int previous = PlayRules.PhysicalStage(_p, _pcPitcher, _pcNumber);
            bool stillOpen = !PitchStageDone(previous, false) && (_p[PlayRules.PhysPitcher] != pitcher || _p[PlayRules.PhysPitchNumber] != number);
            if (stillOpen && _pcRelease >= _t[PlayRules.TimeStart]) Review(ReviewPitchOverlap, _pcPitcher, _pcNumber, release);
            // The previous pitch's batted ball only: the new one's may already be tracked.
            if (_bbPitcher == _pcPitcher && _bbNumber == _pcNumber) ResetBatted(0, 0);
        }
        _pcPitcher = pitcher;
        _pcNumber = number;
        _pcRelease = release;
        int stage = PlayRules.PhysicalStage(_p, pitcher, number);
        int result = FindPitchResult(pitcher, number);
        if (result >= 0) _evNeeded[result] = true;
        if (PitchStageDone(stage, _p[PlayRules.PendingStrikeout] == 1))
        {
            if (result >= 0) _evDecided[result] = true;
            return;
        }
        int hit = FindHit(pitcher, number, release);
        if ((stage & PlayRules.PhysStageCall) == 0)
        {
            // A pitch ruling at or after the release that the engine did not make: the umpire
            // ruled this pitch by hand, and the engine stands aside for all of it.
            if (_t[PlayRules.TimeLastPitch] >= release && _p[PlayRules.PitchSeq] > 0)
            {
                HandToUmpire(ReviewManualPitchRuling, pitcher, number, _now);
                return;
            }
            if (hit >= 0)
            {
                DecideBattedBall(hit, pitcher, number);
                return;
            }
            if (result >= 0)
            {
                DecideCall(result, pitcher, number);
                return;
            }
            if (_now - release > pitchResultTimeoutSeconds)
            {
                HandToUmpire(ReviewNoPitchResult, pitcher, number, _now);
                return;
            }
            _open = true;
            return;
        }
        if (_p[PlayRules.PendingStrikeout] == 1) DecideStrikeout(result, pitcher, number, release);
    }

    // Ball, called strike or swinging strike from the batter's pitch result.
    private void DecideCall(int result, int pitcher, int number)
    {
        int flags = _evParts[result];
        double time = _evTime[result];
        if ((flags & ContactRules.PitchFlagContact) != 0)
        {
            // The batter's client took a contact, but the ball has no accepted hit (yet): a local
            // contact the network did not take is never a batted ball.
            if (_now - time < hitConfirmSeconds) { _open = true; return; }
            HandToUmpire(ReviewContactUnconfirmed, pitcher, number, time);
            _evDecided[result] = true;
            return;
        }
        if ((flags & ContactRules.PitchFlagSwingUnknown) != 0)
        {
            // The batter's bat could not be measured well enough: never a ball or a called
            // strike by default.
            HandToUmpire(ReviewSwingUnknown, pitcher, number, time);
            _evDecided[result] = true;
            return;
        }
        if ((flags & ContactRules.PitchFlagNoCrossing) != 0 && (flags & ContactRules.PitchFlagSwing) == 0)
        {
            HandToUmpire(ReviewNoCrossing, pitcher, number, time);
            _evDecided[result] = true;
            return;
        }
        if (!PlayRules.AcceptingPitch(_p, _m))
        {
            HandToUmpire(ReviewPitchNotAccepted, pitcher, number, time);
            _evDecided[result] = true;
            return;
        }
        int code = CallCode(flags);
        int applied = Rule(code, 0, 0, time);
        _evDecided[result] = true;
        if (applied == PlayRules.ResultApplied) Mark(pitcher, number, PlayRules.PhysStageCall);
        else HandToUmpire(ReviewRulingRefused, pitcher, number, time);
    }

    // The call of a pitch without contact (OBR 5.04 / Definitions): a swing is a strike; without
    // one, a ball the zone snapshot of the pitch contained is a called strike, unless it touched
    // the ground before the plate; everything else is a ball. 0 when the swing is unknown.
    public static int CallCode(int flags)
    {
        if ((flags & ContactRules.PitchFlagSwingUnknown) != 0) return 0;
        if ((flags & ContactRules.PitchFlagSwing) != 0) return PlayRules.JudgeSwingingStrike;
        if ((flags & ContactRules.PitchFlagZoneStrike) != 0 && (flags & ContactRules.PitchFlagBounced) == 0) return PlayRules.JudgeCalledStrike;
        return PlayRules.JudgeBall;
    }

    // Third strike: legally caught by the catcher (before the ball touched the ground) is a
    // strikeout; otherwise PlayRules decides (out with 1B occupied and fewer than two out, else the
    // batter runs). A catch and a ground contact too close to order go to the umpire.
    private void DecideStrikeout(int result, int pitcher, int number, double release)
    {
        double strike = _t[PlayRules.TimeLastPitch];
        int catcher = DefenseRole(RosterRules.RoleC);
        int c = catcher > 0 ? FindCatch(pitcher, number, release, catcher) : -1;
        bool bounced = result >= 0 && (_evParts[result] & ContactRules.PitchFlagBounced) != 0;
        bool grounded = ObservedGround(ObsPitch, pitcher, number);
        double groundTime = grounded ? _obsGroundTime[ObsPitch] : 0.0;
        int code = 0;
        double time = strike;
        if (bounced)
        {
            code = PlayRules.JudgeThirdStrikeUncaught;
            if (grounded) time = Later(strike, groundTime);
        }
        else if (c >= 0)
        {
            // A catch counts only when this client saw the pitch's flight not touching the
            // ground before it; not having seen the contact is not enough.
            int relation = GroundRelation(ObsPitch, pitcher, number, release, _histTimes[c]);
            if (relation == GroundNotBefore)
            {
                code = PlayRules.JudgeStrikeoutCaught;
                time = Later(strike, _histTimes[c]);
            }
            else if (relation == GroundBefore)
            {
                code = PlayRules.JudgeThirdStrikeUncaught;
                time = Later(strike, groundTime);
            }
            else
            {
                Review(relation == GroundTooClose ? ReviewCatchGroundOrder : ReviewGroundUnknown, pitcher, number, _histTimes[c]);
                Mark(pitcher, number, PlayRules.PhysStageStrikeout | PlayRules.PhysStageReview);
                return;
            }
        }
        else if (grounded)
        {
            code = PlayRules.JudgeThirdStrikeUncaught;
            time = Later(strike, groundTime);
        }
        if (code == 0)
        {
            if (_now - strike > thirdStrikeWaitSeconds)
            {
                Review(ReviewThirdStrikeUnknown, pitcher, number, _now);
                Mark(pitcher, number, PlayRules.PhysStageStrikeout | PlayRules.PhysStageReview);
                return;
            }
            _open = true;
            return;
        }
        if (_now < time + decisionDelaySeconds) { _open = true; return; }
        int applied = Rule(code, 0, 0, time);
        if (applied == PlayRules.ResultApplied) Mark(pitcher, number, PlayRules.PhysStageStrikeout);
        else
        {
            Review(ReviewRulingRefused, pitcher, number, time);
            Mark(pitcher, number, PlayRules.PhysStageStrikeout | PlayRules.PhysStageReview);
        }
    }

    private static double Later(double a, double b) { return a > b ? a : b; }

    // ---- Batted ball ----

    private void DecideBattedBall(int hit, int pitcher, int number)
    {
        double hitTime = _histTimes[hit];
        int batter = HistField(hit, MatchBallQueryBehaviour.HistoryBatter);
        if (batter != PlayRules.PaBatter(_p, _m))
        {
            HandToUmpire(ReviewHitMismatch, batter, number, hitTime);
            return;
        }
        if (_bbPitcher != pitcher || _bbNumber != number) ResetBatted(pitcher, number);
        int c = FindCatch(pitcher, number, hitTime, 0);
        if (c >= 0)
        {
            double caught = _histTimes[c];
            // A catch is a fly caught only when this client saw the batted flight from the hit
            // and it had not touched the ground before the catch (GroundRelation); a ground
            // contact that was simply not seen never makes an out.
            int relation = GroundRelation(ObsBatted, pitcher, number, hitTime, caught);
            if (relation == GroundNotBefore)
            {
                // Caught before it touched the ground: a fly (fair or foul) caught, or a foul tip.
                int code = IsFoulTip(c, hitTime) ? PlayRules.JudgeFoulTip : PlayRules.JudgeCatch;
                int applied = Rule(code, 0, 0, caught);
                if (applied == PlayRules.ResultApplied)
                {
                    Mark(pitcher, number, PlayRules.PhysStageCall | PlayRules.PhysStageBattedBall);
                    if (code == PlayRules.JudgeCatch) { _flyPitcher = pitcher; _flyNumber = number; }
                }
                else HandToUmpire(ReviewRulingRefused, pitcher, number, caught);
                return;
            }
            if (relation == GroundTooClose)
            {
                HandToUmpire(ReviewCatchGroundOrder, pitcher, number, caught);
                return;
            }
            if (relation == GroundUnknown)
            {
                HandToUmpire(ReviewGroundUnknown, pitcher, number, caught);
                return;
            }
            // Fielded after the ground: where the fielder first touched it decides, if nothing has.
            if (_bbDecision == FairFoulRules.Undecided && _frameValid)
                SetBattedDecision(FairFoulRules.Territory(_frame, _histPositions[c], field != null ? field.ballRadius : 0.037f), caught);
        }
        if (!_frameValid)
        {
            HandToUmpire(ReviewFairFoulUnknown, pitcher, number, hitTime);
            return;
        }
        if (_bbDecision == FairFoulRules.Fair)
        {
            int applied = Rule(PlayRules.JudgeFairBall, 0, 0, hitTime);
            if (applied == PlayRules.ResultApplied) Mark(pitcher, number, PlayRules.PhysStageCall | PlayRules.PhysStageBattedBall);
            else HandToUmpire(ReviewRulingRefused, pitcher, number, hitTime);
            return;
        }
        if (_bbDecision == FairFoulRules.Foul)
        {
            int applied = Rule(PlayRules.JudgeFoul, 0, 0, hitTime);
            if (applied == PlayRules.ResultApplied)
            {
                Mark(pitcher, number, PlayRules.PhysStageCall | PlayRules.PhysStageBattedBall);
                DeadBall(PlayRules.DeadFoul, Later(_bbDecisionTime, hitTime));
            }
            else HandToUmpire(ReviewRulingRefused, pitcher, number, hitTime);
            return;
        }
        if (_bbDecision == OutOfPlayDecision)
        {
            HandToUmpire(ReviewOutOfPlay, pitcher, number, _bbDecisionTime);
            return;
        }
        if (_now - hitTime > battedBallTimeoutSeconds)
        {
            HandToUmpire(ReviewFairFoulUnknown, pitcher, number, _now);
            return;
        }
        _open = true;
    }

    // Sharp and direct from the bat to the catcher's hands (OBR Definitions: foul tip).
    private bool IsFoulTip(int c, double hitTime)
    {
        int catcher = DefenseRole(RosterRules.RoleC);
        if (catcher <= 0 || HistField(c, MatchBallQueryBehaviour.HistoryHolder) != catcher) return false;
        if (_histTimes[c] - hitTime > foulTipMaxSeconds) return false;
        return !_frameValid || FairFoulRules.DistanceFromHome(_frame, _histPositions[c]) <= foulTipMaxDistance;
    }

    private void SetBattedDecision(int decision, double time)
    {
        if (_bbDecision != FairFoulRules.Undecided) return;
        _bbDecision = decision;
        _bbDecisionTime = time;
    }

    // ---- Flight observation (every client) ----

    // This client started flying the pitch or the batted ball of a lineage at time (the ball's
    // release / batted-flight event). A repeat for the same lineage (a resent hit) keeps the first.
    public void ObserveStart(int kind, int pitcher, int number, double time)
    {
        if (pitcher <= 0 || number <= 0) return;
        // Already seen (started, or a contact seen without its start: that stays unknown).
        if (_obsPitcher[kind] == pitcher && _obsNumber[kind] == number) return;
        _obsPitcher[kind] = pitcher;
        _obsNumber[kind] = number;
        _obsStart[kind] = time;
        _obsGround[kind] = false;
    }

    // The first ground contact of that flight here (later ones are ignored). A contact of a
    // lineage this client did not see starting is kept with an unknown start.
    public void ObserveGround(int kind, int pitcher, int number, double time)
    {
        if (pitcher <= 0 || number <= 0) return;
        if (_obsPitcher[kind] != pitcher || _obsNumber[kind] != number)
        {
            _obsPitcher[kind] = pitcher;
            _obsNumber[kind] = number;
            _obsStart[kind] = -1.0;
            _obsGround[kind] = false;
        }
        if (_obsGround[kind]) return;
        _obsGround[kind] = true;
        _obsGroundTime[kind] = time;
    }

    private bool ObservedGround(int kind, int pitcher, int number)
    {
        return pitcher > 0 && _obsPitcher[kind] == pitcher && _obsNumber[kind] == number && _obsGround[kind];
    }

    // The first ground contact of the flight (from = its release or hit time) relative to time
    // (a catch), with catchGroundToleranceSeconds. Confirmed answers need this client to have
    // flown the flight from its start, within the ball's catch-up window; a contact seen right
    // after a late start may have been anywhere since `from`.
    public int GroundRelation(int kind, int pitcher, int number, double from, double time)
    {
        if (pitcher <= 0 || _obsPitcher[kind] != pitcher || _obsNumber[kind] != number) return GroundUnknown;
        double start = _obsStart[kind];
        bool startKnown = start >= 0.0;
        double lag = startKnown ? start - from : 1e9;
        double tol = catchGroundToleranceSeconds;
        if (!_obsGround[kind])
        {
            if (!startKnown || lag > flightCatchUpSeconds) return GroundUnknown;
            return GroundNotBefore;
        }
        double latest = _obsGroundTime[kind];
        double earliest = latest;
        if (!startKnown || lag > flightCatchUpSeconds || (lag > CatchUpGroundSeconds && latest - start <= CatchUpGroundSeconds))
            earliest = from;
        if (time < earliest - tol) return GroundNotBefore;
        if (time > latest + tol) return GroundBefore;
        return earliest < latest ? GroundUnknown : GroundTooClose;
    }

    // A ground contact of the flight this client simulates (the pitch, or its batted ball).
    public void GroundContact(bool batted, int pitcher, int number, Vector3 position, double time)
    {
        if (pitcher <= 0 || number <= 0) return;
        ObserveGround(batted ? ObsBatted : ObsPitch, pitcher, number, time);
        if (!batted) return;
        if (_bbPitcher != pitcher || _bbNumber != number) ResetBatted(pitcher, number);
        if (_bbGrounded) return;
        _bbGrounded = true;
        _bbGroundTime = time;
        _bbGroundPosition = position;
        ClassifyLanding(position, time);
    }

    // The first landing: past first or third base it decides; the bags themselves are fair.
    private void ClassifyLanding(Vector3 position, double time)
    {
        if (!FrameReady()) return;
        float radius = field != null ? field.ballRadius : 0.037f;
        if (FairFoulRules.PastBase(_frame, position)) SetBattedDecision(FairFoulRules.Territory(_frame, position, radius), time);
        else if (FairFoulRules.TouchesCornerBase(_frame, position, _groundY, field != null ? field.baseTouchReach : 0.3f)) SetBattedDecision(FairFoulRules.Fair, time);
    }

    // Tracking samples may run outside a pass: the frame is built on demand.
    private bool FrameReady()
    {
        if (!_frameValid && field != null)
        {
            _frameValid = field.BuildFrame(_frame);
            _groundY = field.GroundHeight();
        }
        return _frameValid;
    }

    // One sample of the batted ball (this client's simulation), while fair / foul is open: out of
    // play, a bag touched, passing a base (interpolated between samples) and settling decide.
    public void TrackSample(bool batted, bool grounded, int pitcher, int number, Vector3 position, Vector3 velocity, double time)
    {
        if (!batted || pitcher <= 0 || number <= 0) return;
        if (_bbPitcher != pitcher || _bbNumber != number) ResetBatted(pitcher, number);
        if (_bbDecision != FairFoulRules.Undecided || !FrameReady()) return;
        float radius = field != null ? field.ballRadius : 0.037f;
        if (field != null && field.OutOfPlay(_frame, position))
        {
            SetBattedDecision(OutOfPlayDecision, time);
            return;
        }
        if (grounded && !_bbGrounded) GroundContact(true, pitcher, number, position, time);
        if (_bbGrounded && _bbDecision == FairFoulRules.Undecided)
        {
            if (FairFoulRules.TouchesCornerBase(_frame, position, _groundY, field != null ? field.baseTouchReach : 0.3f))
                SetBattedDecision(FairFoulRules.Fair, time);
            else if (_bbHasPrevious)
            {
                float fraction = FairFoulRules.PassFraction(_frame, _bbPrevious, position);
                if (fraction >= 0f) SetBattedDecision(FairFoulRules.Territory(_frame, Vector3.Lerp(_bbPrevious, position, fraction), radius), time);
            }
            if (_bbDecision == FairFoulRules.Undecided)
            {
                if (velocity.magnitude < settleSpeed)
                {
                    if (_bbSettleStart < 0.0) _bbSettleStart = time;
                    else if (time - _bbSettleStart >= settleSeconds) SetBattedDecision(FairFoulRules.Territory(_frame, position, radius), time);
                }
                else _bbSettleStart = -1.0;
            }
        }
        _bbPrevious = position;
        _bbHasPrevious = true;
    }

    private bool TrackingWanted()
    {
        return engineEnabled && _bbPitcher > 0 && _bbDecision == FairFoulRules.Undecided && ballQuery != null && ballQuery.BallBattedFlight();
    }

    // The batted ball of the current pitch is still open: runner decisions wait for it (a foul
    // sends everyone back; a catch changes what runners must do).
    private bool BattedBallOpen()
    {
        if (_pcNumber <= 0) return false;
        int stage = PlayRules.PhysicalStage(_p, _pcPitcher, _pcNumber);
        if ((stage & (PlayRules.PhysStageBattedBall | PlayRules.PhysStageReview)) != 0) return false;
        return FindHit(_pcPitcher, _pcNumber, _pcRelease) >= 0;
    }

    // The current pitch's batted ball was caught on the fly (engine, or the PlayState's last
    // plate appearance result while this pitch is the one handled).
    private bool AfterCaughtFly()
    {
        if (_pcNumber <= 0) return false;
        if (_flyPitcher == _pcPitcher && _flyNumber == _pcNumber) return true;
        return _p[PlayRules.PhysPitcher] == _pcPitcher && _p[PlayRules.PhysPitchNumber] == _pcNumber
            && (_p[PlayRules.PhysStage] & PlayRules.PhysStageBattedBall) != 0 && _p[PlayRules.DLastPaResult] == PlayRules.PaFlyOut
            && _p[PlayRules.BattedBall] == 0;
    }
}
