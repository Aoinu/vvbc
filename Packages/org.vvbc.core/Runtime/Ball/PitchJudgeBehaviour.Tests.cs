using Tsukimi;
using UnityEngine;
using VRC.SDKBase;

// The pure rules of PitchJudgeMath, and the judge's state machine and verdict sync with the
// prepared data and Ball the runner wires for this folder. A test can sync only its own
// Behaviour, so the prepared data is set without being sent, on the judging client itself (one
// player is both pitcher and batter there; the judge does not tell them apart), and the
// strike zone's occupancy is assumed (_assumeOccupantForTest). Other clients only receive the
// verdict. The Ball's side is in BallBehaviour.PitchSync.Tests.cs.
public partial class PitchJudgeBehaviour
{
    private const float FrameStep = 1f / 72f;

    // ---- PitchJudgeMath ----

    [TsukimiTest]
    public void TheDelayVerdictFollowsTheThresholdWith100msValid()
    {
        Assert.AreEqual(PitchJudgeMath.StatusValid, PitchJudgeMath.Classify(-300f, 100f), "data 300 ms before the motion");
        Assert.AreEqual(PitchJudgeMath.StatusValid, PitchJudgeMath.Classify(-50f, 100f), "data 50 ms before the motion");
        Assert.AreEqual(PitchJudgeMath.StatusValid, PitchJudgeMath.Classify(0f, 100f), "together");
        Assert.AreEqual(PitchJudgeMath.StatusValid, PitchJudgeMath.Classify(50f, 100f), "50 ms late");
        Assert.AreEqual(PitchJudgeMath.StatusValid, PitchJudgeMath.Classify(99f, 100f), "99 ms late");
        Assert.AreEqual(PitchJudgeMath.StatusValid, PitchJudgeMath.Classify(100f, 100f), "100 ms late is still valid");
        Assert.AreEqual(PitchJudgeMath.StatusInvalid, PitchJudgeMath.Classify(101f, 100f), "101 ms late");
        Assert.AreEqual(PitchJudgeMath.StatusInvalid, PitchJudgeMath.Classify(200f, 100f), "200 ms late");
        Assert.AreEqual(PitchJudgeMath.StatusInvalid, PitchJudgeMath.Classify(float.NaN, 100f), "an unmeasured delay is never valid");
    }

    [TsukimiTest]
    public void TheDelayIsMeasuredOnOneLocalClock()
    {
        Assert.IsTrue(Mathf.Abs(PitchJudgeMath.DelayMs(10.146f, 10f) - 146f) < 0.01f);
        Assert.IsTrue(PitchJudgeMath.DelayMs(9.7f, 10f) < 0f, "data first is a negative delay");
    }

    [TsukimiTest]
    public void TheThresholdStaysOnItsGrid()
    {
        Assert.AreEqual(100f, PitchJudgeMath.ClampThreshold(100f));
        Assert.AreEqual(500f, PitchJudgeMath.ClampThreshold(900f));
        Assert.AreEqual(0f, PitchJudgeMath.ClampThreshold(-20f));
        Assert.AreEqual(150f, PitchJudgeMath.ClampThreshold(147f));
        Assert.AreEqual(100f, PitchJudgeMath.ClampThreshold(float.NaN));
    }

    [TsukimiTest]
    public void TheInvalidMessageNamesTheDelayAndTheLimit()
    {
        Assert.AreEqual("INVALID PITCH: sync delay 146 ms (limit 100 ms). Pitch again.",
            PitchJudgeMath.MessageText(PitchJudgeMath.StatusInvalid, PitchJudgeMath.ReasonDelay, 146f, 100f));
        Assert.AreEqual("INVALID PITCH: release motion not detected. Pitch again.",
            PitchJudgeMath.MessageText(PitchJudgeMath.StatusInvalid, PitchJudgeMath.ReasonMotionNotDetected, float.NaN, 100f));
        Assert.AreEqual("PITCH CANCELLED: batter changed. Pitch again.",
            PitchJudgeMath.MessageText(PitchJudgeMath.StatusCancelled, PitchJudgeMath.ReasonBatterChanged, float.NaN, 100f));
        Assert.AreEqual("", PitchJudgeMath.MessageText(PitchJudgeMath.StatusValid, PitchJudgeMath.ReasonNone, 20f, 100f));
    }

    // Hand samples every frame along `velocity` through `ideal + offset` (closest at passTime);
    // the release time found, or -1.
    private static float PassTime(Vector3 ideal, Vector3 offset, Vector3 velocity, float passTime, Vector3 pitchDirection, int frames)
    {
        float start = passTime - 0.1f;
        bool falling = false;
        Vector3 previous = Vector3.zero;
        for (int i = 0; i < frames; i++)
        {
            float t = start + i * FrameStep;
            Vector3 hand = ideal + offset + velocity * (t - passTime);
            if (i > 0)
            {
                float u = PitchJudgeMath.ReleaseCandidate(previous, hand, ideal, ideal, FrameStep, pitchDirection, 0.7f, 2.5f, 75f, falling);
                falling = PitchJudgeMath.FallingThrough(previous - ideal, hand - ideal);
                if (u >= 0f) return t - FrameStep + u * FrameStep;
            }
            previous = hand;
        }
        return -1f;
    }

    [TsukimiTest]
    public void AThrowingHandIsFoundAtItsClosestPassOfTheIdealRelease()
    {
        Vector3 ideal = new Vector3(0.3f, 1.8f, 17.6f);
        Vector3 toward = new Vector3(0f, -0.05f, -1f).normalized;
        float found = PassTime(ideal, Vector3.zero, toward * 15f, 10.004f, toward, 20);
        Assert.IsTrue(Mathf.Abs(found - 10.004f) < 0.0005f, "the pass between two samples is interpolated: " + found);
    }

    [TsukimiTest]
    public void AvatarHandsOffTheControllerStillPassOnce()
    {
        Vector3 ideal = new Vector3(0.3f, 1.8f, 17.6f);
        Vector3 toward = new Vector3(0f, -0.05f, -1f).normalized;
        // A hand bone 25 cm beside and 15 cm below the controller's ideal (another avatar).
        float found = PassTime(ideal, new Vector3(0.25f, -0.15f, 0f), toward * 12f, 10.01f, toward, 20);
        Assert.IsTrue(Mathf.Abs(found - 10.01f) < 0.002f, "an offset hand is closest at the same time: " + found);
        Assert.AreEqual(-1f, PassTime(ideal, new Vector3(0.9f, 0f, 0f), toward * 12f, 10.01f, toward, 20), "too far: another motion");
    }

    [TsukimiTest]
    public void LeftAndRightHandedPassesAreBothFound()
    {
        Vector3 toward = new Vector3(0f, -0.05f, -1f).normalized;
        Assert.IsTrue(PassTime(new Vector3(0.3f, 1.8f, 17.6f), Vector3.zero, toward * 14f, 5f, toward, 20) > 0f, "right-handed");
        Assert.IsTrue(PassTime(new Vector3(-0.3f, 1.8f, 17.6f), Vector3.zero, toward * 14f, 5f, toward, 20) > 0f, "left-handed");
    }

    [TsukimiTest]
    public void PickoffsSlowHandsAndBackwardMotionAreNoRelease()
    {
        Vector3 ideal = new Vector3(0.3f, 1.8f, 17.6f);
        Vector3 toward = new Vector3(0f, -0.05f, -1f).normalized;
        Assert.AreEqual(-1f, PassTime(ideal, Vector3.zero, Vector3.right * 14f, 5f, toward, 20), "a throw across the mound");
        Assert.AreEqual(-1f, PassTime(ideal, Vector3.zero, toward * 1.5f, 5f, toward, 20), "a slow arm");
        Assert.AreEqual(-1f, PassTime(ideal, Vector3.zero, -toward * 14f, 5f, toward, 20), "away from the plate");
    }

    [TsukimiTest]
    public void APausedOrAbortedHandIsNoRelease()
    {
        Vector3 ideal = new Vector3(0.3f, 1.8f, 17.6f);
        Vector3 toward = new Vector3(0f, -0.05f, -1f).normalized;
        bool falling = false;
        Vector3 previous = ideal + toward * -0.4f;
        int found = 0;
        // Approach, stop 10 cm short of the ideal for two frames, then pull back.
        Vector3[] path = new Vector3[]
        {
            ideal + toward * -0.2f, ideal + toward * -0.1f, ideal + toward * -0.1f, ideal + toward * -0.1f,
            ideal + toward * -0.15f, ideal + toward * -0.3f
        };
        for (int i = 0; i < path.Length; i++)
        {
            if (PitchJudgeMath.ReleaseCandidate(previous, path[i], ideal, ideal, FrameStep, toward, 0.7f, 2.5f, 75f, falling) >= 0f) found++;
            falling = PitchJudgeMath.FallingThrough(previous - ideal, path[i] - ideal);
            previous = path[i];
        }
        Assert.AreEqual(0, found);
    }

    [TsukimiTest]
    public void APassOnASharedSampleIsCountedOnce()
    {
        Vector3 ideal = Vector3.zero;
        Vector3 toward = Vector3.forward;
        Vector3 a = toward * -0.2f, b = Vector3.zero, c = toward * 0.2f;
        float first = PitchJudgeMath.ReleaseCandidate(a, b, ideal, ideal, FrameStep, toward, 0.7f, 2.5f, 75f, false);
        bool falling = PitchJudgeMath.FallingThrough(a - ideal, b - ideal);
        float second = PitchJudgeMath.ReleaseCandidate(b, c, ideal, ideal, FrameStep, toward, 0.7f, 2.5f, 75f, falling);
        Assert.AreEqual(-1f, first, "still falling at the shared sample");
        Assert.IsTrue(falling);
        Assert.AreEqual(0f, second, "found once, at the shared sample");
    }

    // ---- State machine (this client is the batter) ----

    private static readonly Vector3 JudgeChest = new Vector3(0f, 1.4f, 18f);
    private static readonly Vector3 JudgePlane = new Vector3(0f, 0f, 0.43f);
    private static readonly Vector3 JudgeCalibrated = new Vector3(0.25f, 0.35f, 0.3f);
    private static readonly Vector3 JudgeTarget = new Vector3(0f, 0.75f, 0.43f);
    private const double JudgeServer = 1000.0;

    private PitchPrepBehaviour TestPrep() { return strikeZone.ball.manualPitcher.prepSync; }
    private Vector3 TestIdeal() { return IdealHand(PreparedPitch.Read(TestPrep()), JudgeChest, JudgePlane); }
    private Vector3 TestRelease() { return TestIdeal() + new Vector3(0f, 0.05f, -0.06f); }
    private Vector3 TestDirection() { return (JudgeTarget - TestRelease()).normalized; }

    // As the pitcher: data prepared for the batter, not sent (the release follows from the
    // calibrated ideal, so it is set twice).
    private void PrepareForTest(int batter)
    {
        PitchPrepBehaviour prep = TestPrep();
        prep.SetCourse(batter, false, GloveBehaviour.HandRight, Vector3.zero, Vector3.zero, JudgeCalibrated,
            Quaternion.identity, JudgeTarget, Vector3.back, 37.7f, Vector3.zero, BallAerodynamics.WakeNone, 0f, Vector3.zero, JudgeServer);
        prep.SetCourse(batter, false, GloveBehaviour.HandRight, TestRelease(), TestIdeal(), JudgeCalibrated,
            Quaternion.identity, JudgeTarget, TestDirection(), 37.7f, Vector3.zero, BallAerodynamics.WakeNone, 0f, Vector3.zero, JudgeServer);
    }

    // As the batter: occupying the plate, watching the pitcher holding the ball. Returns the generation.
    private int WatchForTest()
    {
        _ball = strikeZone.ball;
        _assumeOccupantForTest = true;
        RegisterPrep(TestPrep());
        RememberPreparation(TestPrep());
        Arm(PreparedPitch.Read(TestPrep()), 1);
        return TestPrep().prepGeneration;
    }

    // The remote hand passes the ideal release toward the plate at 14 m/s, closest at `pass`.
    private void HandPassForTest(float pass)
    {
        Vector3 ideal = TestIdeal();
        Vector3 direction = TestDirection();
        for (int i = 0; i < 9; i++)
        {
            float t = pass - 0.08f + i * FrameStep;
            SampleAt(t, JudgeChest, ideal + direction * 14f * (t - pass), JudgePlane, JudgeServer);
        }
    }

    // One pitch whose data arrives `delayMs` after the pass the batter sees (negative: before).
    // Returns the verdict, or -1 when a step did not go as a pitch goes.
    private int JudgeOnePitchForTest(int pitcher, int batter, int number, int generation, float pass, float delayMs)
    {
        RememberPreparation(TestPrep());
        Arm(PreparedPitch.Read(TestPrep()), number);
        float arrival = pass + delayMs * 0.001f;
        if (delayMs <= 0f)
        {
            if (!GateHandPitchAt(pitcher, number, generation, batter, arrival, JudgeServer)) return -1;
            if (phase != PhaseAwaitMotion) return -1;
            HandPassForTest(pass);
        }
        else
        {
            HandPassForTest(pass);
            if (phase != PhaseCandidate) return -1;
            TickAt(arrival, JudgeServer);
            if (!GateHandPitchAt(pitcher, number, generation, batter, arrival, JudgeServer)) return -1;
        }
        return HasVerdictFor(pitcher, number) ? _knownStatus : -1;
    }

    [TsukimiTest(maxSteps: 20000000)]
    public void ANewPreparationDoesNotInvalidateAReleaseObservedWithThePreviousGeneration()
    {
        VRCPlayerApi player = Mimic.Join();
        int id = player.playerId;
        PrepareForTest(id);
        int generation = WatchForTest();
        HandPassForTest(10f);
        PrepareForTest(id);
        OnPrepChangedAt(TestPrep(), JudgeServer);

        Assert.IsTrue(GateHandPitchAt(id, 1, generation, id, 10.05f, JudgeServer));
        Assert.AreEqual(PitchJudgeMath.StatusValid, KnownStatus());
    }

    [TsukimiTest(maxSteps: 20000000)]
    public void AReleaseSeenBetweenTheNewPreparationAndTheOldHandPitchCanBeMatched()
    {
        VRCPlayerApi player = Mimic.Join();
        int id = player.playerId;
        PrepareForTest(id);
        int releasedGeneration = WatchForTest();
        PrepareForTest(id);
        PitchPrepBehaviour prep = TestPrep();
        OnPrepChangedAt(prep, JudgeServer);
        Arm(PreparedPitch.Read(prep), 1);
        HandPassForTest(10f);
        Assert.AreEqual(prep.prepGeneration, _candidateGeneration);

        Assert.IsTrue(GateHandPitchAt(id, 1, releasedGeneration, id, 10.05f, JudgeServer));
        Assert.AreEqual(PitchJudgeMath.StatusValid, KnownStatus());
    }

    [TsukimiTest]
    public void CancellationConvergesWhetherTheValidVerdictArrivesBeforeOrAfterIt()
    {
        Mimic.Join();
        ApplyOutcomeAt(8, 1, PitchJudgeMath.StatusValid, 0, 10f, true, 100f, JudgeServer, JudgeServer);
        ApplyOutcomeAt(8, 1, PitchJudgeMath.StatusCancelled, 4, 0f, false, 0f, JudgeServer + 3.0, JudgeServer + 3.0);
        Assert.AreEqual(PitchJudgeMath.StatusCancelled, KnownStatus());
        ApplyOutcomeAt(8, 1, PitchJudgeMath.StatusValid, 0, 10f, true, 100f, JudgeServer + 4.0, JudgeServer + 4.0);
        Assert.AreEqual(PitchJudgeMath.StatusCancelled, KnownStatus());
    }

    [TsukimiTest(maxSteps: 20000000)]
    public void HandPitchBeforePreparationCanStillUseItsOwnReceivedGeneration()
    {
        VRCPlayerApi player = Mimic.Join();
        int id = player.playerId;
        PrepareForTest(id);
        int generation = WatchForTest();
        int nextGeneration = generation + 2;
        Assert.IsTrue(GateHandPitchAt(id, 1, nextGeneration, id, 9.9f, JudgeServer));
        Assert.IsFalse(monitoring);
        PrepareForTest(id);
        OnPrepChangedAt(TestPrep(), JudgeServer);
        Assert.IsTrue(monitoring);
        HandPassForTest(10f);
        Assert.AreEqual(PitchJudgeMath.StatusValid, KnownStatus());
    }

    [TsukimiTest(maxSteps: 20000000)]
    public void APassForAnOldHandSessionIsNotUsedForTheNextPitchOnTheSameCourse()
    {
        VRCPlayerApi player = Mimic.Join();
        int id = player.playerId;
        PrepareForTest(id);
        int generation = WatchForTest();
        HandPassForTest(10f);
        Assert.AreEqual(PhaseCandidate, phase);
        Assert.IsTrue(GateHandPitchAt(id, 2, generation, id, 10.04f, JudgeServer));
        Assert.AreEqual(PhaseAwaitMotion, phase);
        Assert.AreEqual(PitchJudgeMath.StatusNone, KnownStatus());
        HandPassForTest(10.15f);
        Assert.AreEqual(2, KnownNumber());
        Assert.AreEqual(PitchJudgeMath.StatusValid, KnownStatus());
        Assert.IsTrue(KnownDelayMs() < -100f);
    }

    [TsukimiTest(maxSteps: 20000000)]
    public void TheBatterJudgesEachPitchByTheDelayAfterTheReleaseItSaw()
    {
        VRCPlayerApi me = Mimic.Join();
        int pitcher = me.playerId;
        int batter = me.playerId;
        PrepareForTest(batter);
        int generation = WatchForTest();
        Assert.IsTrue(monitoring, "a prepared pitch is watched");
        float[] delays = new float[] { -300f, -50f, 0f, 50f, 99f, 101f, 200f };
        int[] expected = new int[]
        {
            PitchJudgeMath.StatusValid, PitchJudgeMath.StatusValid, PitchJudgeMath.StatusValid, PitchJudgeMath.StatusValid,
            PitchJudgeMath.StatusValid, PitchJudgeMath.StatusInvalid, PitchJudgeMath.StatusInvalid
        };
        for (int i = 0; i < delays.Length; i++)
        {
            int status = JudgeOnePitchForTest(pitcher, batter, i + 1, generation, 10f + i * 2f, delays[i]);
            Assert.AreEqual(expected[i], status, "delay " + delays[i] + " ms");
            Assert.IsTrue(Mathf.Abs(_knownDelayMs - delays[i]) < 1f, "measured " + _knownDelayMs + " for " + delays[i]);
            if (status == PitchJudgeMath.StatusInvalid) Assert.AreEqual(PitchJudgeMath.ReasonDelay, _knownReason);
            Assert.AreEqual(PhaseIdle, phase);
        }
    }

    [TsukimiTest(maxSteps: 20000000)]
    public void APassWithoutPitchDataTimesOutWithoutAVerdict()
    {
        VRCPlayerApi me = Mimic.Join();
        int pitcher = me.playerId;
        int batter = me.playerId;
        PrepareForTest(batter);
        WatchForTest();
        HandPassForTest(10f);
        Assert.AreEqual(PhaseCandidate, phase);
        Assert.IsFalse(monitoring, "one pass per pitch");
        TickAt(10.09f, JudgeServer);
        Assert.AreEqual(PhaseCandidate, phase, "within the threshold");
        TickAt(10.2f, JudgeServer);
        Assert.AreEqual(PhaseTimedOut, phase, "past the threshold");
        Assert.AreEqual(PitchJudgeMath.StatusNone, _knownStatus, "a pass alone is never judged");
        Arm(PreparedPitch.Read(TestPrep()), 1);
        Assert.IsTrue(GateHandPitchAt(pitcher, 1, TestPrep().prepGeneration, batter, 13f, JudgeServer));
        Assert.AreEqual(PhaseAwaitMotion, phase, "beyond candidateMatchSeconds the data waits for its own motion");
    }

    [TsukimiTest(maxSteps: 20000000)]
    public void AThrowAfterAPassIsNoInvalidPitch()
    {
        VRCPlayerApi me = Mimic.Join();
        int pitcher = me.playerId;
        int batter = me.playerId;
        PrepareForTest(batter);
        WatchForTest();
        HandPassForTest(10f);
        Assert.AreEqual(PhaseCandidate, phase);
        // The ball's accepted state is no longer the pitcher's hand (a pickoff or throw).
        OnBallStateAppliedAt(JudgeServer);
        Assert.AreEqual(PhaseIdle, phase, "the pass is dropped");
        Assert.AreEqual(PitchJudgeMath.StatusNone, _knownStatus, "no verdict");
        Assert.IsFalse(monitoring);
    }

    [TsukimiTest(maxSteps: 20000000)]
    public void PitchDataWithoutItsPreparedGenerationIsInvalid()
    {
        VRCPlayerApi me = Mimic.Join();
        int pitcher = me.playerId;
        int batter = me.playerId;
        PrepareForTest(batter);
        int generation = WatchForTest();
        Assert.IsTrue(GateHandPitchAt(pitcher, 1, generation + 1, batter, 10f, JudgeServer));
        TickAt(10.6f, JudgeServer);
        Assert.AreEqual(PitchJudgeMath.StatusInvalid, _knownStatus);
        Assert.AreEqual(PitchJudgeMath.ReasonPrepMissing, _knownReason);
        Assert.IsTrue(GateHandPitchAt(pitcher + 50, 1, 3, batter, 11f, JudgeServer), "no data at all");
        TickAt(11.6f, JudgeServer);
        Assert.AreEqual(PitchJudgeMath.ReasonPrepMissing, _knownReason);
        Assert.IsTrue(GateHandPitchAt(pitcher, 3, generation, batter, 12f, JudgeServer + 700.0),
            "expired data");
        TickAt(12.6f, JudgeServer + 700.0);
        Assert.AreEqual(3, _knownNumber);
        Assert.AreEqual(PitchJudgeMath.ReasonPrepMissing, _knownReason);
    }

    [TsukimiTest(maxSteps: 20000000)]
    public void DataThatNeverMeetsAReleaseMotionIsInvalid()
    {
        VRCPlayerApi me = Mimic.Join();
        int pitcher = me.playerId;
        int batter = me.playerId;
        PrepareForTest(batter);
        int generation = WatchForTest();
        Assert.IsTrue(GateHandPitchAt(pitcher, 1, generation, batter, 10f, JudgeServer));
        TickAt(10.4f, JudgeServer);
        Assert.AreEqual(PitchJudgeMath.StatusNone, _knownStatus, "still waiting");
        TickAt(10.6f, JudgeServer);
        Assert.AreEqual(PitchJudgeMath.StatusInvalid, _knownStatus);
        Assert.AreEqual(PitchJudgeMath.ReasonMotionNotDetected, _knownReason);
        Assert.IsFalse(_knownDelayMeasured, "no delay is claimed");
    }

    [TsukimiTest(maxSteps: 20000000)]
    public void TheSamePitchArrivingTwiceIsJudgedOnce()
    {
        VRCPlayerApi me = Mimic.Join();
        int pitcher = me.playerId;
        int batter = me.playerId;
        PrepareForTest(batter);
        int generation = WatchForTest();
        Assert.AreEqual(PitchJudgeMath.StatusInvalid, JudgeOnePitchForTest(pitcher, batter, 1, generation, 10f, 200f));
        int sequence = verdictSequence;
        Assert.IsTrue(GateHandPitchAt(pitcher, 1, generation, batter, 10.5f, JudgeServer),
            "an invalid pitch arriving again is still held back");
        Assert.AreEqual(sequence, verdictSequence, "no second verdict");
        Assert.AreEqual(PitchJudgeMath.StatusValid, JudgeOnePitchForTest(pitcher, batter, 2, generation, 12f, 30f));
        Assert.IsTrue(GateHandPitchAt(pitcher, 2, generation, batter, 12.5f, JudgeServer),
            "a valid pitch arriving again does not fall through to launch");
        Assert.AreEqual(sequence + 1, verdictSequence);
    }

    [TsukimiTest(maxSteps: 20000000)]
    public void OnlyThePreparedBatterJudgesAndOnlyOnTheSharedPlate()
    {
        VRCPlayerApi me = Mimic.Join();
        int pitcher = me.playerId;
        int batter = me.playerId;
        PrepareForTest(batter);
        int generation = WatchForTest();
        Assert.IsFalse(GateHandPitchAt(pitcher, 1, generation, -1, 10f, JudgeServer), "no batter: the old path");
        Assert.IsFalse(GateHandPitchAt(pitcher, 1, generation, batter + 1, 10f, JudgeServer),
            "prepared for someone else");
        _assumeOccupantForTest = false;
        Assert.IsFalse(GateHandPitchAt(pitcher, 1, generation, batter, 10f, JudgeServer),
            "not the plate's occupant");
        _assumeOccupantForTest = true;
        strikeZone.localOnly = true;
        Assert.IsFalse(GateHandPitchAt(pitcher, 1, generation, batter, 10f, JudgeServer),
            "a LocalRig plate never judges");
        Assert.AreEqual(0, verdictSequence);
    }

    [TsukimiTest(maxSteps: 20000000)]
    public void TheThresholdIsTakenOncePerPitch()
    {
        VRCPlayerApi me = Mimic.Join();
        int pitcher = me.playerId;
        int batter = me.playerId;
        PrepareForTest(batter);
        delaySetting = null;
        fallbackThresholdMs = 100f;
        int generation = WatchForTest();
        HandPassForTest(10f);
        Assert.AreEqual(PhaseCandidate, phase);
        // Raised to 200 ms after the pass: this pitch keeps the 100 ms it started with.
        fallbackThresholdMs = 200f;
        TickAt(10.15f, JudgeServer);
        Assert.IsTrue(GateHandPitchAt(pitcher, 1, generation, batter, 10.15f, JudgeServer));
        Assert.AreEqual(PitchJudgeMath.StatusInvalid, _knownStatus);
        Assert.AreEqual(100f, _knownThresholdMs);
        Assert.AreEqual(PitchJudgeMath.StatusValid, JudgeOnePitchForTest(pitcher, batter, 2, generation, 12f, 150f), "the next pitch uses 200 ms");
    }

    // ---- Verdict sync ----

    // Each client voids the pitch on its own Ball from the verdict it follows (ApplyOutcomeAt);
    // the Ball's side of that is tested in BallBehaviour.PitchSync.Tests.cs.
    [TsukimiTest(maxSteps: 20000000)]
    public void AnInvalidVerdictReachesEveryoneAndALateJoiner()
    {
        VRCPlayerApi me = Mimic.Join();
        int pitcher = me.playerId;
        int batter = me.playerId;
        VRCPlayerApi fielder = Mimic.Join();
        Mimic.Become(me);
        PrepareForTest(batter);
        int generation = WatchForTest();
        Assert.AreEqual(PitchJudgeMath.StatusInvalid, JudgeOnePitchForTest(pitcher, batter, 1, generation, 10f, 146f));
        Assert.AreEqual(batter, verdictBatterId);
        Mimic.Deliver();
        Mimic.Become(fielder);
        Assert.AreEqual(PitchJudgeMath.StatusInvalid, _knownStatus, "the fielder follows the batter");
        Assert.AreEqual(1, _knownNumber);
        Assert.IsTrue(MessageText().StartsWith("INVALID PITCH: sync delay 146 ms (limit 100 ms)"), MessageText());
        VRCPlayerApi late = Mimic.Join();
        Mimic.Become(late);
        Assert.AreEqual(PitchJudgeMath.StatusInvalid, _knownStatus, "a late joiner follows the synced verdict");
        Assert.AreEqual("", MessageText(), "the late joiner did not see the pitch: not announced");
    }

    [TsukimiTest(maxSteps: 20000000)]
    public void AVerdictFromSomeoneOtherThanThePlatesOwnerIsIgnored()
    {
        VRCPlayerApi me = Mimic.Join();
        int pitcher = me.playerId;
        int batter = me.playerId;
        PrepareForTest(batter);
        int generation = WatchForTest();
        JudgeOnePitchForTest(pitcher, batter, 1, generation, 10f, 146f);
        // A record naming a batter who does not own the plate.
        VRCPlayerApi other = Mimic.Join();
        verdictBatterId = other.playerId;
        verdictPitchNumber = 2;
        RequestSerialization();
        Mimic.Deliver();
        Mimic.Become(other);
        Assert.IsFalse(HasVerdictFor(pitcher, 2));
    }

    [TsukimiTest]
    public void ALateValidOrAnOlderPitchNeverOverridesAVerdict()
    {
        Mimic.Join();
        _ball = strikeZone.ball;
        ApplyOutcomeAt(7, 2, PitchJudgeMath.StatusInvalid, PitchJudgeMath.ReasonDelay, 150f, true, 100f, 100.0, 100.5);
        ApplyOutcomeAt(7, 2, PitchJudgeMath.StatusValid, PitchJudgeMath.ReasonNone, 20f, true, 100f, 101.0, 101.5);
        Assert.AreEqual(PitchJudgeMath.StatusInvalid, _knownStatus, "invalid is final");
        ApplyOutcomeAt(7, 2, PitchJudgeMath.StatusCancelled, PitchJudgeMath.ReasonBatterChanged, 0f, false, 0f, 101.0, 101.5);
        Assert.AreEqual(PitchJudgeMath.StatusCancelled, _knownStatus, "a synchronized cancellation takes precedence");
        ApplyOutcomeAt(7, 1, PitchJudgeMath.StatusValid, PitchJudgeMath.ReasonNone, 20f, true, 100f, 99.0, 101.5);
        Assert.AreEqual(2, _knownNumber, "an older pitch's verdict does not replace the newer one");
        ApplyOutcomeAt(7, 3, PitchJudgeMath.StatusValid, PitchJudgeMath.ReasonNone, 20f, true, 100f, 102.0, 102.5);
        Assert.AreEqual(3, _knownNumber, "the next pitch's verdict is followed");
        Assert.AreEqual(PitchJudgeMath.StatusValid, _knownStatus);
        Assert.AreEqual("", MessageText(), "a valid pitch clears the message");
        Assert.IsTrue(_ball.PitchVoided(7, 2), "the invalid pitch stays void");
    }

    [TsukimiTest]
    public void AnOldVerdictIsFollowedButNotShown()
    {
        Mimic.Join();
        _ball = strikeZone.ball;
        ApplyOutcomeAt(7, 2, PitchJudgeMath.StatusInvalid, PitchJudgeMath.ReasonDelay, 150f, true, 100f, 100.0, 160.0);
        Assert.AreEqual(PitchJudgeMath.StatusInvalid, _knownStatus);
        Assert.IsTrue(_ball.PitchVoided(7, 2));
        Assert.AreEqual("", MessageText(), "a minute old: not shown");
    }
}
