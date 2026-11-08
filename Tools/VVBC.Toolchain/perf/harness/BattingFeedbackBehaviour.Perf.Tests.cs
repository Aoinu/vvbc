using Tsukimi;
using UnityEngine;

// Performance driver for the production Scan. Scenario test methods are generated from
// perf/budgets.json by run.py and appended as BattingFeedbackBehaviour.Perf.Scenarios.Tests.cs.
public partial class BattingFeedbackBehaviour
{
    private const float PerfNow = 1.0125f;
    private const float PerfBallStep = 0.02f;
    private const float PerfBallHistory = 0.1333f;
    private const float PerfAxisOffset = 0.4f;

    // Records production Motion/Ball histories, then optionally runs the production Scan.
    // mode 0 = record only, 1 = record + Scan frame 1, 2 = also record frame 2 + Scan again (warm cache),
    // 3 = as 2 without the second Scan (the cost reference for mode 2).
    // lateral = y0 + vy * (t - mid) is the ball position across the bat plane Y axis;
    // the plane normal distance is n0 + vn * (t - now).
    private void PerfRun(float y0, float vy, float n0, float vn, bool zone, float dt, int frames, int mode)
    {
        BatMotionBehaviour motion = contact.motion;
        contact.contactZone = contact.transform;
        contact.perfZoneCenter = zone ? new Vector3(0f, 1f, 0f) : new Vector3(500f, 0f, 0f);
        float start = PerfNow - frames * dt;
        float mid = start + 0.5f * frames * dt;
        for (int k = 0; k <= frames; k++) PerfBat(motion, start + k * dt, y0, vy, mid);
        PerfBall(PerfNow - PerfBallHistory, PerfNow, n0, vn);
        if (mode >= 1) Scan(motion);
        if (mode >= 2)
        {
            for (int k = 1; k <= frames; k++) PerfBat(motion, PerfNow + k * dt, y0, vy, mid);
            PerfBall(PerfNow, PerfNow + frames * dt, n0, vn);
            if (mode == 2) Scan(motion);
        }
    }

    private void PerfBat(BatMotionBehaviour motion, float time, float y0, float vy, float mid)
    {
        float lateral = y0 + vy * (time - mid);
        motion.PerfRecord(time, new Vector3(-lateral, 1f - PerfAxisOffset, 0f), Quaternion.identity);
    }

    // Records every fixed step in (from, to].
    private void PerfBall(float from, float to, float n0, float vn)
    {
        int k = Mathf.FloorToInt(from / PerfBallStep + 0.0001f) + 1;
        while (k * PerfBallStep <= to + 0.000001f)
        {
            float t = k * PerfBallStep;
            flight.PerfRecord(t, new Vector3(0f, 1f, -(n0 + vn * (t - PerfNow))),
                new Vector3(0f, 0f, -vn), new Vector3(0f, 0f, 100f));
            k++;
        }
    }
}
