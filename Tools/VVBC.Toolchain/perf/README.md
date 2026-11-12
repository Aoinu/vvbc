# VVBC Performance Budget tests

Detect large Udon cost regressions in the Bat Motion, Bat Contact and Batting Feedback code
(for example a `CandidateDistance()` that suddenly costs tens of times more) before they are merged.
The production T# code is executed by the Tsukimi test runner on fixed scenarios; **Udon steps and
extern calls** are compared with the reviewed budgets in [`budgets.json`](budgets.json).
A scenario over budget fails the run with a non-zero exit code.

## What is measured, and what it is not

| Metric | Meaning |
|---|---|
| `steps` | Udon VM instructions executed by the test runner (default Udon path) |
| `externs` | Udon extern calls (every Unity API call and every float/vector operator is one) |

These are **deterministic counts, not CPU time**. They track the *amount of Udon work* well, which is
what regresses when an algorithm changes, but they do not give milliseconds: an extern is not a
fixed cost (`Physics` or `AnimationCurve.Evaluate` cost more than a float add), and VRChat's Udon VM,
GC, and device speed are not modelled. CPU time and FPS are deliberately not used for pass/fail
(environment dependent). Treat a failure as "the code does materially more Udon work", then confirm the
impact with ClientSim / real clients.

**Not covered by this test** (it can say nothing about them): Unity rendering and shader cost,
PhysX / Rigidbody / collision cost, network latency and bandwidth, Audio / haptics playback, Udon
event dispatch overhead outside the measured entry points, real `VRC_Pickup` / `VRCPlayerApi` /
hand-tracking behaviour, GC and memory, and device-specific performance (Quest).

## Run

```powershell
pwsh -File Tools/VVBC.Toolchain/Run-PerformanceTests.ps1                 # all scenarios (~40 s)
pwsh -File Tools/VVBC.Toolchain/Run-PerformanceTests.ps1 -Scenario feedback_c4_90fps,contact_hit_90fps
pwsh -File Tools/VVBC.Toolchain/Run-PerformanceTests.ps1 -Propose        # also print measured values
pwsh -File Tools/VVBC.Toolchain/Run-PerformanceTests.ps1 -Check          # static checks only, measures nothing
python Tools/VVBC.Toolchain/perf/run.py                                  # same, without PowerShell
```

Requirements: Python 3.11+, .NET 8+ (`dotnet`) and the installed `com.tsukimicode.udon-tools` 0.10.1
package. **No Unity Editor is needed**; the runner uses the package's MCP server (`Server~/TsukimiCode.Mcp.dll`).
`-CompilerRoot`/`VVBC_COMPILER_ROOT` points to another project that provides the package (e.g. measuring a second checkout such as a worktree: `--root <worktree> --compiler-root <main project>`).

Exit codes: `0` all active scenarios within budget; `1` budget exceeded; `2` error (scenario not run or
not measurable, tool failure, invalid budget file, stale stub rule); `3` environment mismatch (different
Tsukimi version than the budgets were recorded with; this is **not** a regression). `NOT_COVERED`
scenarios are listed in the output and never counted as passing. The report is written to
`Tools/VVBC.Toolchain/.state/perf/report.json` and `report.md` (ignored by git).

## When a scenario fails

The output names the scenario, component and function, the measured and allowed steps/externs, how far
over the limit each is, and the command to rerun it. Then:

1. Rerun that scenario (the numbers are deterministic; a rerun that passes means the working tree changed).
2. `git diff` the production function named in the failure (`Component.function`). Scenario ids tell
   the situation: `feedback_c2_*` ball inside the zone but outside the bat region, `feedback_c4_*` a swing
   crossing the ball, `*_backlog200ms` a 0.2 s pause processed in one call, `*_warm` the second frame with
   caches filled.
3. Fix the cost, or - if the extra work is intended - update the budget in a reviewed change (below).
4. `ERROR` is never a performance verdict: read the printed reason (`notRunnable`, assertion about the
   scenario state, stale stub rule, missing Tsukimi package).

## `budgets.json`

```jsonc
{
  "schema": 1,
  "environment": { "tsukimiVersion": "0.10.1", "measurementMethod": "...", "recordedAgainstProductionCommit": "<sha>" },
  "scenarios": [{
    "id": "feedback_c4_90fps",                  // C#-identifier-like, unique
    "component": "BattingFeedbackBehaviour", "function": "Scan",
    "description": "...",
    "driver": "feedback-scan",                  // feedback-scan | contact-pose | motion-pose
    "params": { "...": "driver specific fixed inputs" },
    "expect": { "entered": true },              // state the scenario must reach, else ERROR
    "measure": { "base": "m0", "run": "m1" },   // cost = run test - base test (setup cancels)
    "baseline": { "steps": 0, "externs": 0 },   // measured at baselineCommit
    "limits":   { "steps": 0, "externs": 0 },   // fail above this (baseline +30%)
    "absoluteLimits": { "steps": 0, "externs": 0 }, // hard ceiling; limits may never exceed it
    "baselineCommit": "<production commit>", "rationale": "why these numbers"
  }]
}
```

`status: "unsupported"` plus a `reason` records a scenario that cannot be measured; it shows as
`NOT_COVERED`. A failing measurement of an active scenario is an error, never a pass.

### Scenarios

| Driver (component.function) | Scenarios |
|---|---|
| `feedback-scan` (`BattingFeedbackBehaviour.Scan`) | C1 outside the zone; C2 in zone, outside bat region; C3 in zone, inside region; C4 normal swing with closest-point search; C5 fast swing - each at 90/60/30/15 fps for one frame, with a 0.2 s and a 0.5 s backlog, and C1-C5 `_warm` (second consecutive 90 fps frame, cached outline) |
| `contact-pose` (`BatContactBehaviour.OnBatContactPose` -> `ScanInterval`, `Evaluate`, `AcceptCandidate`) | out of zone, zone entry without crossing, plane crossing rejected by bat width / by timing yaw, accepted hit (static and swinging bat, 90/30 fps), 0.2 s backlog variants |
| `motion-pose` (`BatMotionBehaviour.PostLateUpdate`, `Configure`, `StopMotion`, `RecordContactPose`) | first pickup, second pickup, one hand -> two hands, -> Lock Grip, drop; steady-state frames for one hand (idle, normal, fast swing), two hands, Lock Grip, and 30 fps |

Pickup events cannot be raised by the runner. The Motion scenarios call the state-transition functions
the Bat controller calls on a pickup (`Configure`, `StopMotion`, `ReleaseVelocity`); a real
`VRC_Pickup` round trip is **not** measured. Idle, normal and fast Motion frames cost the same because
`PostLateUpdate` has no speed-dependent branch (this is a measured fact, not a copy error).

## How the production code is executed (no stale copies)

`harness.py` reads the **current** `Packages/org.vvbc.core/Runtime/**/*.cs` (non-test) on every run, applies a
fixed list of reviewed call-site rewrites in memory, and passes the result to the compiler as inline sources.
Nothing is copied into the repository, so a production change is always what gets measured.

The runner cannot construct `GameObject`/`Rigidbody`/`SphereCollider`/`AnimationCurve` and does not model a few
APIs, so these production call sites are rewritten (`RULES` in `harness.py`, stand-ins in `harness/PerfUnity.cs`
and the `harness/*.Perf.cs` seams):

| Stubbed API | Replaced by | Effect on the measurement |
|---|---|---|
| `Rigidbody body` pose (`body.transform.position/rotation/TransformPoint/lossyScale`) | harness fields; scale = 1 | a few externs fewer per call |
| `SphereCollider ballCollider` radius / scale | `perfBallRadius`, `perfBallScale` fields | ~1 extern less per Scan / Accept |
| contact zone `Transform.InverseTransformPoint` | vector subtraction (identity zone) | plain math instead of one extern |
| `AnimationCurve.Evaluate/Equals/wrap modes/Clone` (radius and power curves) | piecewise-linear math (`PerfUnity.RadiusCurve`, `PowerCurve`) | **cost of the curve itself is not measured**; call counts are |
| `Quaternion.Slerp/Dot/AngleAxis/ToAngleAxis`, `Time.time/fixedTime/fixedDeltaTime`, `Physics.gravity` | plain-math stand-ins / constants | stand-ins use more steps, fewer externs than the real call |
| pickup proxy / grip `Transform`s | harness fields | |
| `BallBehaviour.ApplyContactResult` (Ball hand-off, network queue, physics apply) | `PerfUnity.HandOff` returns true | **excluded** from the Contact "hit" cost |
| `motion.IgnoreBallCollision` (once per pitch), `GetComponentInParent<BatBehaviour>` | removed / null | excluded |
| `Rigidbody.velocity/angularVelocity` on drop, hand-tracking reads in `RecordContactPose` (`_owner` is null) | removed / skipped | excluded |
| `WorldSettingsBehaviour.OnPlayerJoined` (`VRCPlayerApi.Set*Speed`, `SetJumpImpulse`, `SetVoiceDistance*`), raised by `Mimic.Join()` | returns at once | excluded; it runs once per join, never inside a measured call |

So absolute numbers are for the harness, not for VRChat; **comparisons across commits are meaningful**
because the same stubs apply to both. A regression inside a stand-in-replaced call (e.g. making a curve
evaluate more expensive) is not seen; a regression in how often it is called is.

Every rule has an exact reviewed match count (a total, or per file for rules that also appear in files no scenario
executes; those declare why extra matches there are tolerated). Extra matches in a measured file or a vanished
stubbed call both fail the run. If a production edit touches a stubbed
call site, the run fails with `Stub rule "..." matched N time(s), expected M` (also with
`-Check`). Review the rule and `harness/PerfUnity.cs`, then update `RULES` in the same change.
Production changes outside those call sites need no harness change.

## Validity of the baseline (checked at `2c6511a`)

- Setup is subtracted: the base test (`m0`) records the identical Motion/Ball history, only the run test adds the
  production call, so cost = that call alone. `*_warm` = (scan after a second frame) - (the same without the
  scan), so cold-cache initialization (outline build, geometry preparation) and steady-state cost are separate.
  For `feedback_c4_90fps`: cold 1.41 M steps, warm 0.90 M, so initialization is about 0.5 M of the cold frame.
- Stub influence is small: replacing every stand-in (Slerp, curves, TransformPoint, ...) by a constant changed
  `feedback_c4_90fps` by -2.5 % steps (1.414 M -> 1.379 M) and `contact_hit_90fps` by -2.8 %. The cost is the
  production code itself.
- Same harness, same scenarios, `58b5c0a` (before the Scan optimization) versus `2c6511a`: `feedback_c1_90fps`
  223 k -> 93 k steps, `c2` 1.82 M -> 0.60 M, `c3` 451 k -> 221 k, `c4` 5.51 M -> 1.41 M, `c4_warm` 3.54 M -> 0.90 M,
  `c5` 4.12 M -> 1.10 M (-58 % to -75 %). Contact and Motion scenarios are identical at both commits (those
  files did not change). Older commits need extra comparison-only stub rules; they are not supported by
  `RULES`, which target the current sources.
- Open lead (not a regression): after the cache is built, `CandidateDistance` for a point outside the bat region
  costs about 34 k steps / 7.9 k externs per call, roughly three times the cost of its 16 segment tests measured in
  isolation (about 10 k / 2 k); the first call costs about 130 k steps. The cause (T# code generation for the
  loop, or genuine work) is not identified.

## Adding or changing a scenario

1. Add the scenario to `budgets.json` with provisional `baseline`/`limits` (any positive numbers).
2. `Run-PerformanceTests.ps1 -Scenario <id> -Propose` prints the measured cost. The `expect` assertions
   prove the scenario reached the intended state (zone entered, contact accepted, ...).
3. Set `baseline` to the measurement, `limits` to baseline +30%, `absoluteLimits` to a ceiling (we use
   baseline +50%), fill `baselineCommit` and `rationale`. A new driver kind is a function in `drivers.py`
   plus a seam/driver file in `harness/`.
4. Run the whole suite and `python -m unittest discover -s Tools/VVBC.Toolchain/tests`.

## Updating budgets (reviewed, never automatic)

No tool edits `budgets.json`; `--propose` only prints. A budget change must state, in the pull request:
the reason, old and new baseline, the increase in percent, the changed feature, and the performance
impact. Prefer lowering the baseline after an optimization (recommended). Never raise a limit just to make
a failing run pass without that explanation. `limits` may not exceed `absoluteLimits`; changing the
ceiling needs an explicit justification. Re-record **all** baselines in one reviewed change when the Tsukimi
version changes (exit code 3 until then).

## Local only

This gate runs locally; no CI workflow runs it (the proprietary Tsukimi compiler cannot be installed on
hosted runners). Run it before merging runtime changes to the Bat/Ball/Feedback Behaviours. Its Python unit
tests (`tests/test_perf.py`) run with the other toolchain tests, and `-Check` validates budgets, generated
scenario tests and stub rules without the compiler (it measures nothing).
