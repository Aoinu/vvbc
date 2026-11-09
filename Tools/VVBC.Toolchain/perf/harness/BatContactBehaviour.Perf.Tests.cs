using Tsukimi;
using UnityEngine;

// Performance driver for the production OnBatContactPose. Scenario methods are generated from
// perf/budgets.json by run.py (BatContactBehaviour.Perf.Scenarios.Tests.cs).
public partial class BatContactBehaviour
{
    private const float PerfNow = 1.0125f;
    private const float PerfBallStep = 0.02f;
    private const float PerfBallHistory = 0.1333f;
    private const float PerfAxisOffset = 0.4f;

    // mode 0 = seed + steady-state init call, 1 = + OnBatContactPose for the measured frame(s), 2 = + second frame and pose (warm),
    // 3 = as 2 without the second pose (cost reference for mode 2).
    // The bat is tilted about Z by tilt degrees so that the horizontal timing check can pass.
    // The ball plane distance is -speed * (t - (now - cross)): it crosses the bat plane `cross` seconds before now.
    private void PerfRun(float y0, float vy, float speed, float cross, float tilt, bool zone,
        float dt, int frames, int mode)
    {
        strikeZone = null;
        contactZone = transform;
        perfZoneCenter = zone ? new Vector3(0f, 1f, 0f) : new Vector3(500f, 0f, 0f);
        flight.flying = true;
        flight.contactConsumed = false;
        flight.flightSequence = 1;
        _activationTime = 0f;
        float start = PerfNow - frames * dt;
        float mid = start + 0.5f * frames * dt;
        PerfBall(PerfNow - PerfBallHistory, PerfNow, speed, cross);
        PerfBat(start - dt, y0, vy, mid, tilt);
        PerfBat(start, y0, vy, mid, tilt);
        OnBatContactPose(); // steady state: the previous frame was already scanned (all modes)
        for (int k = 1; k <= frames; k++) PerfBat(start + k * dt, y0, vy, mid, tilt);
        if (mode >= 1) OnBatContactPose();
        if (mode >= 2)
        {
            for (int k = 1; k <= frames; k++) PerfBat(PerfNow + k * dt, y0, vy, mid, tilt);
            PerfBall(PerfNow, PerfNow + frames * dt, speed, cross);
            if (mode == 2) OnBatContactPose();
        }
    }

    private void PerfBat(float time, float y0, float vy, float mid, float tilt)
    {
        float half = tilt * 0.5f * 0.0174532925f;
        Quaternion rotation = new Quaternion(0f, 0f, Mathf.Sin(half), Mathf.Cos(half));
        float s = Mathf.Sin(tilt * 0.0174532925f), c = Mathf.Cos(tilt * 0.0174532925f);
        Vector3 axis = new Vector3(-s, c, 0f);
        Vector3 plane = new Vector3(-c, -s, 0f);
        float lateral = y0 + vy * (time - mid);
        motion.PerfRecord(time, new Vector3(0f, 1f, 0f) - axis * PerfAxisOffset - plane * lateral, rotation);
    }

    private void PerfBall(float from, float to, float speed, float cross)
    {
        int k = Mathf.FloorToInt(from / PerfBallStep + 0.0001f) + 1;
        while (k * PerfBallStep <= to + 0.000001f)
        {
            float t = k * PerfBallStep;
            flight.PerfRecord(t, new Vector3(0f, 1f, speed * (t - (PerfNow - cross))),
                new Vector3(0f, 0f, speed), new Vector3(0f, 0f, 100f));
            k++;
        }
    }
}
