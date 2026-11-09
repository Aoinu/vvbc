"""Compare the existing 58b5c0a diagnostic replica with the current Scan.

Requires D:/Workspaces/VVBC/diag/{tools,work} from the local diagnostic setup.
Cost cases intentionally assert false to expose runner counters; regression cases
must pass. Unity AnimationCurve and several scene APIs are represented by stubs.
"""
import os, sys, re, json, csv
from pathlib import Path
sys.path.insert(0, r'D:\Workspaces\VVBC\diag\tools')
import scan_ab
from tsmcp import Client
base=Path(r'D:\Workspaces\VVBC\diag\work\Packages\org.vvbc.core\DiagBench')
bench=(base/'ScanBench.cs').read_text()
sim=(base/'ScanSim.cs').read_text()
geometry=(base/'BatGeometry.cs').read_text()
source=Path('Packages/org.vvbc.core/Runtime/BattingFeedbackBehaviour.Scan.cs').read_text()
method=source[source.index('    private float SegmentDistanceSquaredScalars('):source.index('    private void ConsiderCandidate(')]
method=method.replace('private float CandidateDistance(BatMotionBehaviour motion)', 'private float OptimizedDistance()').replace('float px = _candidateX, py = _candidateYValue;', 'distCalls++; float px = _candidateX, py = _candidateYValue;')
for a,b in [('motion.', ''),('_candidateX','candX'),('_candidateYValue','candY'),('_candidateWidth','candWidth'),('_candidateBallRadius','candBallRadius'),('_candidateBall','candBall'),('_candidateCore','candCore'),('_candidateNormal','candNormal')]: method=method.replace(a,b)
fields='''    private float candWidth; private int _simRevision; private bool _simMotionKnown;
    private readonly float[] _outlineX = new float[17];
    private readonly float[] _outlineWidth = new float[17];
    private bool _outlineReady, _outlineEndsReady, _outlineFinite;
    private float _outlineMinX, _outlineMaxX, _outlineMinWidth, _outlineMaxWidth;
'''
motion=Path('Packages/org.vvbc.core/Runtime/Bat/BatMotionBehaviour.Batting.cs').read_text()
fast=motion[motion.index('    private bool _feedbackGeometryValid;'):motion.index('    public bool IsInsideContactRange(')]
fast=fast.replace('contactMargin', 'margin').replace('AnimationCurve _feedbackRadiusCurve','float[] _feedbackRadiusCurve').replace('body != null ? body.transform.lossyScale : Vector3.one','bodyT != null ? LossyScale() : Vector3.one').replace('_feedbackRadiusCurve.Evaluate(normalized)','Mathf.Lerp(_feedbackRadiusCurve[0], _feedbackRadiusCurve[1], normalized)')
# The diagnostic curve is a two-element linear stub, so equality and cloning
# model its two values rather than Unity's native AnimationCurve implementation.
fast=fast.replace("!radiusCurve.Equals(_feedbackCurveSnapshot) ||\n            radiusCurve.preWrapMode != _feedbackCurveSnapshot.preWrapMode ||\n            radiusCurve.postWrapMode != _feedbackCurveSnapshot.postWrapMode", "radiusCurve[0] != _feedbackCurveSnapshot[0] || radiusCurve[1] != _feedbackCurveSnapshot[1]")
fast=fast.replace("CloneCurve(radiusCurve)", "new float[] { radiusCurve[0], radiusCurve[1] }")
fast=fast.replace('if (!_feedbackGeometryValid || !BatGeometry.Finite(planeDistance)) return 0f;', 'widthCalls++; evalCount++; if (!_feedbackGeometryValid || !BatGeometry.Finite(planeDistance)) return 0f;')
bench=bench[:bench.rfind('}')]+fields+method+fast+'}\n'
sim=sim.replace('private float _lt, _ld, _mt, _md, _best;', 'private float _lt, _ld, _mt, _md, _best, _bestTime;').replace('_has = true; _best = d;', '_has = true; _best = d; _bestTime = time;')
# Mode 4 defers candidate width until an in-zone/refinement or boundary sample.
sim=sim.replace('float w = ContactWidthAtLocalY(localY, candBallRadius);','float w = mode == 4 ? FeedbackSampleWidth(candX) : ContactWidthAtLocalY(localY, candBallRadius); candWidth = w;')
sim=sim.replace('float d = Dist();', 'if (mode == 4) candWidth = FeedbackSampleWidth(candX); float d = Dist();', 1)
sim=sim.replace('float da = Dist();', 'if (mode == 4) candWidth = FeedbackSampleWidth(candX); float da = Dist();')
sim=sim.replace('float db = Dist();', 'if (mode == 4) candWidth = FeedbackSampleWidth(candX); float db = Dist();')
sim=sim.replace('            Sample(boundary);', '            Sample(boundary); if (mode == 4) candWidth = FeedbackSampleWidth(candX);')
sim=sim.replace('return mode == 1 ? NewDistance() : OldDistance();','return mode == 1 ? NewDistance() : mode == 4 ? OptimizedDistance() : OldDistance();')
sim=sim.replace('        cScan++;\n', '        cScan++; if (mode == 4) { PrepareFeedbackGeometry(candBallRadius); int rev = FeedbackGeometryRevision(); if (!_simMotionKnown || _simRevision != rev) { _outlineReady = false; _outlineEndsReady = false; _simMotionKnown = true; _simRevision = rev; } }\n')
lines=['using Tsukimi;', 'using UnityEngine;', 'public partial class ScanBench', '{']
lines.append('    private void InitSim(int m, float y0_, float vy_, float n0_, float vn_, float cross, float z0, float z1, float[] mt, float[] bt) { bodyT = transform; radiusCurve = new float[] { 0.016f, 0.033f }; mode = m; y0 = y0_; vy = vy_; n0 = n0_; vn = vn_; crossT = cross; zoneStart = z0; zoneEnd = z1; mTimes = mt; bTimes = bt; }')
names=[]
for cn,(y0,vy,n0,vn,zone) in scan_ab.CASES.items():
 for fn,dt in scan_ab.FRAMES.items():
  start,m,b=scan_ab.samples(dt); cross=scan_ab.T-dt/2; z0,z1=(0.,100.) if zone else (500.,600.)
  for mode in (1,4):
   nm=f'{cn}__{fn}__{mode}'
   init=f'InitSim({mode}, {y0}f, {vy}f, {n0}f, {vn}f, {cross:.5f}f, {z0}f, {z1}f, {scan_ab.arr(m)}, {scan_ab.arr(b)});'
   lines.append(f'    [TsukimiTest(maxSteps: 100000000)] public void {nm}__setup() {{ {init} Assert.IsTrue(false); }}')
   lines.append(f'    [TsukimiTest(maxSteps: 100000000)] public void {nm}__scan() {{ {init} RunScan({start:.5f}f, {scan_ab.T}f); Assert.AreEqual("", Counters()); }}')
   names.append(nm)
# Repeat the identical workload with an already-built, unchanged contour.
lines.append("    private void ResetWarmCounters() { cScan = 0; cSeg = 0; cPoints = 0; cRefine = 0; cSample = 0; cConsider = 0; distCalls = 0; widthCalls = 0; lossyCalls = 0; evalCount = 0; _pts = 0; _has = false; _best = 0f; _bestTime = 0f; }")
for cn,(y0,vy,n0,vn,zone) in scan_ab.CASES.items():
 for fn,dt in scan_ab.FRAMES.items():
  start,m,b=scan_ab.samples(dt); cross=scan_ab.T-dt/2; z0,z1=(0.,100.) if zone else (500.,600.)
  nm=f'{cn}__{fn}__4warm'
  init=f'InitSim(4, {y0}f, {vy}f, {n0}f, {vn}f, {cross:.5f}f, {z0}f, {z1}f, {scan_ab.arr(m)}, {scan_ab.arr(b)}); RunScan({start:.5f}f, {scan_ab.T}f); ResetWarmCounters();'
  lines.append(f'    [TsukimiTest(maxSteps: 100000000)] public void {nm}__setup() {{ {init} Assert.IsTrue(false); }}')
  lines.append(f'    [TsukimiTest(maxSteps: 100000000)] public void {nm}__scan() {{ {init} RunScan({start:.5f}f, {scan_ab.T}f); Assert.AreEqual("", Counters()); }}')
  names.append(nm)
lines.append("""    [TsukimiTest(maxSteps: 10000000)]
    public void CacheReuseRegression()
    {
        InitSim(4, .25f, 0f, .3f, -1f, 1.005f, 0f, 100f, new float[] { 1f, 1.0125f }, new float[0]);
        RunScan(1f, 1.0125f); Assert.IsTrue(_outlineReady); int revision = FeedbackGeometryRevision();
        ResetWarmCounters(); RunScan(1f, 1.0125f); Assert.AreEqual(revision, FeedbackGeometryRevision()); Assert.AreEqual(10, widthCalls);
        radiusCurve[1] = .05f; ResetWarmCounters(); RunScan(1f, 1.0125f); Assert.IsTrue(revision != FeedbackGeometryRevision()); Assert.AreEqual(28, widthCalls);
        revision = FeedbackGeometryRevision(); margin = .01f; ResetWarmCounters(); RunScan(1f, 1.0125f); Assert.IsTrue(revision != FeedbackGeometryRevision());
        revision = FeedbackGeometryRevision(); sweet = .5f; minQ = .4f; ResetWarmCounters(); RunScan(1f, 1.0125f); Assert.AreEqual(revision, FeedbackGeometryRevision()); Assert.AreEqual(10, widthCalls);
    }
""")
# Compare each measurement trajectory as well as measuring its cost.
for cn,(y0,vy,n0,vn,zone) in scan_ab.CASES.items():
 for fn,dt in scan_ab.FRAMES.items():
  start,m,b=scan_ab.samples(dt); cross=scan_ab.T-dt/2; z0,z1=(0.,100.) if zone else (500.,600.)
  init=f'InitSim(1, {y0}f, {vy}f, {n0}f, {vn}f, {cross:.5f}f, {z0}f, {z1}f, {scan_ab.arr(m)}, {scan_ab.arr(b)});'
  lines.append(f'''    [TsukimiTest(maxSteps: 100000000)] public void TrajectoryRegression__{cn}__{fn}() {{
   {init} RunScan({start:.5f}f, {scan_ab.T}f);
   float best = _best, time = _bestTime; int refs = cRefine, samples = cSample, points = cPoints; bool has = _has;
   _has = false; _pts = 0; _best = 0f; _bestTime = 0f; cRefine = 0; cSample = 0; cPoints = 0; cScan = 0; cSeg = 0; mode = 4;
   RunScan({start:.5f}f, {scan_ab.T}f);
   Assert.IsTrue(has == _has && refs == cRefine && samples == cSample && points == cPoints);
   Assert.IsTrue(!has || (Mathf.Abs(best - _best) <= .0001f && Mathf.Abs(time - _bestTime) <= .000001f));
  }}''')
lines.append("""    [TsukimiTest(seeds: 12, maxSteps: 10000000)]
    public void GeometryRegression(int seed)
    {
        bodyT = transform; radiusCurve = new float[] { 0.001f + (seed % 4) * .013f, .002f + (seed % 3) * .019f };
        fakeScale = new Vector3(.4f + (seed % 3) * .5f, .7f + (seed % 4) * .37f, 1.3f);
        margin = (seed % 2) * .011f; candBallRadius = .01f + (seed % 3) * .013f;
        candNormal = Vector3.forward; candBall = new Vector3(0f, 0f, .05f); candCore = Vector3.zero;
        _outlineReady = false; _outlineEndsReady = false; PrepareFeedbackGeometry(candBallRadius);
        for (int i = 0; i < 40; i++)
        {
            candX = (-.1f + (i % 10) * .14f) * fakeScale.y;
            candY = (i / 10 - 2) * .09f;
            float local = Mathf.Clamp(ContactPlaneDistanceToLocalY(candX), ContactStartLocalY(), ContactEndLocalY());
            candWidth = ContactWidthAtLocalY(local, candBallRadius);
            Assert.IsTrue(Mathf.Abs(candWidth - FeedbackSampleWidth(candX)) <= .000001f);
            float expected = NewDistance(); float actual = OptimizedDistance();
            Assert.IsTrue(Mathf.Abs(actual - expected) <= .0001f);
            Assert.IsTrue((actual <= .050001f) == (expected <= .050001f));
        }
    }
""")
lines.append("""    [TsukimiTest(seeds: 5, maxSteps: 100000000)]
    public void ScanRegression(int seed)
    {
        InitSim(1, seed == 0 ? .25f : 0f, seed == 3 ? 20f : seed == 4 ? 40f : 0f,
            seed < 2 ? .3f : .02f, seed == 0 ? -1f : 0f, 1.005f,
            seed == 0 ? 500f : 0f, seed == 0 ? 600f : 100f,
            new float[] { 1f, 1.0125f }, new float[] { 1.005f });
        RunScan(1f, 1.0125f);
        float best = _best, time = _bestTime; int refs = cRefine, samples = cSample, points = cPoints;
        bool has = _has;
        _has = false; _pts = 0; _best = 0f; _bestTime = 0f;
        cRefine = 0; cSample = 0; cPoints = 0; cScan = 0; cSeg = 0;
        mode = 4;
        RunScan(1f, 1.0125f);
        Assert.IsTrue(has == _has && refs == cRefine && samples == cSample && points == cPoints);
        Assert.IsTrue(!has || (Mathf.Abs(best - _best) <= .0001f && Mathf.Abs(time - _bestTime) <= .000001f));
    }
""")
lines.append('}')
sources=[{'name':'ScanBench.cs','text':bench},{'name':'ScanSim.cs','text':sim},{'name':'BatGeometry.cs','text':geometry},{'name':'ScanBench.Tests.cs','text':'\n'.join(lines)}]
c=Client(r'D:\Workspaces\VVBC\diag\work')
r=c.call('test',{'sources':sources}); c.close()
out=Path('Tools/VVBC.Toolchain/.state/scan-opt'); out.mkdir(parents=True, exist_ok=True); (out/'result.json').write_text(json.dumps(r,indent=1))
by={x['name']:x for x in r.get('cases',[])}; rows=[]
for n in names:
 a=by.get(n+'__scan',{}); setup=by.get(n+'__setup',{}); parts=n.split('__'); counters=dict(re.findall(r'(\w+)=(\d+)',a.get('actual','')))
 if 'steps' not in a or 'steps' not in setup: print('ERROR',n,a,setup); continue
 rows.append(dict(case=parts[0],frame=parts[1],mode=parts[2],steps=a['steps']-setup['steps'],externs=a['externCalls']-setup['externCalls'],**counters))
with (out/'result.csv').open('w',newline='') as f:
 w=csv.DictWriter(f,fieldnames=['case','frame','mode','steps','externs','seg','pts','sample','dist','refine','width','lossy']);w.writeheader();w.writerows(rows)
print('runner:', r.get('passed'),r.get('failed'),r.get('notRunnable'),'rows:',len(rows)); print('regression:', [(x['name'], x['outcome']) for x in r.get('cases',[]) if x['name'].startswith('GeometryRegression') or x['name'].startswith('ScanRegression')])
print('\n'.join(str(row) for row in rows if row['frame']=='90fps'))


regressions = [x for x in r.get('cases', []) if x['name'].startswith(('GeometryRegression', 'ScanRegression', 'TrajectoryRegression', 'CacheReuseRegression'))]
if len(rows) != len(names) or len(regressions) != 48 or any(x['outcome'] != 'passed' for x in regressions):
    raise SystemExit('Scan diagnostic or regression failed; see result.json')
