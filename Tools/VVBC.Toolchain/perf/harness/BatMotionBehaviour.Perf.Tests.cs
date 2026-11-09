using Tsukimi;
using UnityEngine;
using VRC.Udon;

// Performance driver for the production Bat Motion state transitions and PostLateUpdate.
// Scenario methods are generated from perf/budgets.json (BatMotionBehaviour.Perf.Scenarios.Tests.cs).
// Pickup events are replaced by the production Configure/StopMotion calls the Bat controller makes
// (no real VRC_Pickup, VRCPlayerApi or hand tracking is involved).
public partial class BatMotionBehaviour
{
    private const float PerfStart = 1f;

    private void PerfConfigure(int state, int lower, int upper, int revision, bool recaptureLock)
    {
        bool two = upper != BatGripConstants.HandNone;
        Configure(LocalHeld, state, lower, upper, BatGripConstants.ProxyA, BatGripConstants.ProxyB,
            two ? BatGripConstants.ProxyB : BatGripConstants.ProxyNone, BatGripConstants.ProxyA,
            revision, null, true, true, recaptureLock, null);
    }

    private void PerfHand(float time, float speed)
    {
        float t = time - PerfStart;
        float angle = speed * t / 0.8f;
        float half = angle * 0.5f;
        _perfHandPos = new Vector3(speed * t, 1.2f, 0f);
        _perfHandRot = new Quaternion(0f, 0f, Mathf.Sin(half), Mathf.Cos(half));
        _perfUpperPos = _perfHandPos + _perfHandRot * new Vector3(0f, 0.2f, 0f);
        _perfTime = time;
    }

    private void PerfFrame(int index, float dt, float speed)
    {
        PerfHand(PerfStart + index * dt, speed);
        _perfDelta = dt;
        PostLateUpdate();
    }

    // kind: frame | pickup | second_pickup | two_hand | lock | drop.  mode 0 = setup only, 1 = + measured work.
    private void PerfMotion(int kind, int grip, float speed, float dt, int mode)
    {
        PerfBindTransforms();
        UdonBehaviour listener = GetComponent<UdonBehaviour>();
        RegisterContactListener(listener, true);
        int upper = grip == BatGripConstants.GripTwoHandHeld ? BatGripConstants.HandLeft : BatGripConstants.HandNone;
        bool recapture = grip == BatGripConstants.GripLocked;
        int frames = 0;
        PerfHand(PerfStart, speed);
        if (kind == 0) // frame: steady-state PostLateUpdate while held
        {
            PerfConfigure(grip, BatGripConstants.HandRight, upper, 1, recapture);
            for (frames = 0; frames < 3; frames++) PerfFrame(frames, dt, speed);
            if (mode >= 1) PerfFrame(frames, dt, speed);
            return;
        }
        if (kind == 1) // pickup: first pickup
        {
            if (mode >= 1) PerfConfigure(grip, BatGripConstants.HandRight, upper, 1, recapture);
            return;
        }
        if (kind == 2) // second pickup: after a completed first hold
        {
            PerfConfigure(BatGripConstants.GripLowerHeld, BatGripConstants.HandRight, BatGripConstants.HandNone, 1, false);
            for (frames = 0; frames < 3; frames++) PerfFrame(frames, dt, speed);
            StopMotion();
            if (mode >= 1) PerfConfigure(grip, BatGripConstants.HandRight, upper, 2, recapture);
            return;
        }
        // kinds 3..5 start from a one-hand hold with recorded history
        PerfConfigure(BatGripConstants.GripLowerHeld, BatGripConstants.HandRight, BatGripConstants.HandNone, 1, false);
        for (frames = 0; frames < 3; frames++) PerfFrame(frames, dt, speed);
        if (mode == 0) return;
        if (kind == 3) PerfConfigure(BatGripConstants.GripTwoHandHeld, BatGripConstants.HandRight, BatGripConstants.HandLeft, 2, false);
        else if (kind == 4) PerfConfigure(BatGripConstants.GripLocked, BatGripConstants.HandRight, BatGripConstants.HandNone, 2, true);
        else { ReleaseVelocity(); StopMotion(); }
    }
}
