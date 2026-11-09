"""Builds the performance harness from the live production T# sources.

The T# test runner does not model every Unity API the production code touches (AnimationCurve,
GameObject/Rigidbody/SphereCollider construction, Transform.TransformPoint, Quaternion.Slerp, ...).
The harness therefore compiles the *current* production sources after a fixed set of reviewed
call-site rewrites (RULES). Every rule must match exactly the expected number of times, so a
production change that touches a stubbed call site fails the run until the rule is reviewed.
Nothing is copied into the repository: the result is passed to the MCP server as inline sources.
"""
import hashlib
from pathlib import Path
import re

PERF_DIR = Path(__file__).resolve().parent
RUNTIME = 'Packages/org.vvbc.core/Runtime'
HARNESS_DIR = PERF_DIR / 'harness'
TEST_FILE_SUFFIX = '.Tests.cs'


class HarnessError(RuntimeError):
    pass


def rule(name, pattern, replacement, count, files=None, regex=False, others=None):
    """count: exact total (int) or exact per-file matches ({file: n}).
    others: for a dict count, why matches in *unlisted* files are tolerated (those files are never executed
    by a scenario, so a new match there cannot change a measurement). Without it unlisted matches fail."""
    return {'name': name, 'pattern': pattern, 'replacement': replacement, 'count': count,
            'files': files, 'regex': regex, 'others': others}


# Call-site rewrites for APIs the runner does not model. Their stand-ins live in
# harness/PerfUnity.cs and are documented in perf/README.md ("Stubbed Unity APIs").
M = 'BatMotionBehaviour.cs'
UNEXECUTED = ('other files are not executed by any scenario (Ball network/audio/flight launch, pitcher, '
              'impact audio), so extra matches there cannot change a measurement')
HOT = {'BallBehaviour.History.cs', 'BatMotionBehaviour.Contact.cs', 'BatMotionBehaviour.cs',
       'BattingFeedbackBehaviour.Scan.cs', 'BatContactBehaviour.cs'}

RULES = [
    # Bat Motion pose solving: proxies, grips and the Body Rigidbody become harness fields. These
    # specific rewrites must precede the global Time rewrites below.
    rule('Motion record time', 'RecordContactPose(Time.time)', 'RecordContactPose(_perfTime)', 1, files=['BatMotionBehaviour.cs']),
    rule('Motion drop-velocity delta', 'UpdateDropVelocity(Time.deltaTime)', 'UpdateDropVelocity(_perfDelta)', 1,
         files=['BatMotionBehaviour.cs']),
    rule('Motion lower proxy position', 'lowerProxyTransform.position', '_perfHandPos', 2, files=['BatMotionBehaviour.cs']),
    rule('Motion lower proxy rotation', 'lowerProxyTransform.rotation', '_perfHandRot', 2, files=['BatMotionBehaviour.cs']),
    rule('Motion upper proxy position', 'upperProxyTransform.position', '_perfUpperPos', 1, files=['BatMotionBehaviour.cs']),
    rule('Motion grip local position', 'lowerGrip.localPosition', '_perfGripPos', 4, files=['BatMotionBehaviour.cs']),
    rule('Motion grip local rotation', 'lowerGrip.localRotation', '_perfGripRot', 3, files=['BatMotionBehaviour.cs']),
    rule('Motion body position (Motion)', 'body.transform.position', '_perfPos', {M: 11}, files=[M]),
    rule('Motion body rotation (Motion)', 'body.transform.rotation', '_perfRot', {M: 12}, files=[M]),
    rule('Motion body velocity', 'body.velocity = _dropVelocity;', '', 1, files=['BatMotionBehaviour.cs']),
    rule('Motion body angular velocity', 'body.angularVelocity = _dropAngularVelocity;', '', 1, files=['BatMotionBehaviour.cs']),
    rule('Motion body == null', r'\bbody == null', 'false', {M: 4}, files=[M], regex=True),
    rule('Motion body != null', r'\bbody != null', 'true', {M: 1}, files=[M], regex=True),
    rule('Motion proxy follow', 'target.SetPositionAndRotation(anchor.position, anchor.rotation);',
         'target.position = anchor.position;', 1, files=['BatMotionBehaviour.cs']),
    rule('Quaternion.Slerp', 'Quaternion.Slerp(', 'PerfUnity.Slerp(',
         {'BallBehaviour.History.cs': 1, 'BatMotionBehaviour.Contact.cs': 4, M: 1}, others=UNEXECUTED),
    rule('Quaternion.Dot', 'Quaternion.Dot(', 'PerfUnity.QuatDot(',
         {'BallBehaviour.History.cs': 3, 'BatMotionBehaviour.Contact.cs': 3, M: 1}, others=UNEXECUTED),
    rule('Quaternion.AngleAxis', 'Quaternion.AngleAxis(', 'PerfUnity.AngleAxis(',
         {'BallBehaviour.History.cs': 1, 'BatMotionBehaviour.Contact.cs': 1}, others=UNEXECUTED),
    rule('Time.time', r'\bTime\.time\b', 'PerfUnity.Now()',
         {M: 6, 'BatContactBehaviour.cs': 2, 'BattingFeedbackBehaviour.Scan.cs': 1}, regex=True,
         others='Update/launch/audio/trail code outside the measured entry points'),
    rule('Time.fixedTime', r'\bTime\.fixedTime\b', 'PerfUnity.Now()', {}, regex=True,
         others='Ball launch/flight code is not measured; no measured file may use fixedTime'),
    rule('Time.fixedDeltaTime', r'\bTime\.fixedDeltaTime\b', 'PerfUnity.FixedStep()',
         {'BallBehaviour.History.cs': 1}, regex=True, others=UNEXECUTED),
    rule('Physics.gravity', r'\bPhysics\.gravity\b', 'PerfUnity.Gravity()',
         {'BallBehaviour.History.cs': 2}, regex=True, others=UNEXECUTED),
    rule('Quaternion.ToAngleAxis', 'delta.ToAngleAxis(out angle, out axis);',
         'angle = PerfUnity.AngleOf(delta); axis = PerfUnity.AxisOf(delta);', 1, files=['BatKinematics.cs']),
    # Bat Motion: the held Rigidbody transform becomes harness fields.
    rule('Motion body null guard', '        if (body == null) return;\n        // The driven Transform has just moved',
         '        // The driven Transform has just moved', 1, files=['BatMotionBehaviour.Contact.cs']),
    rule('Motion body position', 'body.transform.position', '_perfPos', 1, files=['BatMotionBehaviour.Contact.cs']),
    rule('Motion body rotation', 'body.transform.rotation', '_perfRot', 1, files=['BatMotionBehaviour.Contact.cs']),
    rule('Motion body TransformPoint', 'body.transform.TransformPoint(new Vector3(0f, SweetSpotLocalY(), 0f))',
         'PerfUnity.TransformPoint(_perfPos, _perfRot, new Vector3(0f, SweetSpotLocalY(), 0f))', 1,
         files=['BatMotionBehaviour.Contact.cs']),
    rule('Motion lossyScale (Batting)', 'body.transform.lossyScale', 'Vector3.one', 4,
         files=['BatMotionBehaviour.Batting.cs']),
    rule('Motion body null checks (Batting)', 'body != null', 'true', 4, files=['BatMotionBehaviour.Batting.cs']),
    rule('Motion radius curve Evaluate', '_feedbackRadiusCurve.Evaluate(normalized)',
         'PerfUnity.RadiusCurve(normalized)', 1, files=['BatMotionBehaviour.Batting.cs']),
    rule('Motion curve change detection',
         'bool curveChanged = _feedbackCurveSnapshot == null ||\n            !radiusCurve.Equals(_feedbackCurveSnapshot) ||\n'
         '            radiusCurve.preWrapMode != _feedbackCurveSnapshot.preWrapMode ||\n'
         '            radiusCurve.postWrapMode != _feedbackCurveSnapshot.postWrapMode;',
         'bool curveChanged = !_feedbackShapeKnown;', 1, files=['BatMotionBehaviour.Batting.cs']),
    rule('Motion curve snapshot', 'if (curveChanged) _feedbackCurveSnapshot = CloneCurve(radiusCurve);', '', 1,
         files=['BatMotionBehaviour.Batting.cs']),
    rule('Motion raw power curve',
         'if (powerCurve == null) powerCurve = CreateDefaultPowerCurve();\n        if (powerCurve.length == 0) return 0f;\n'
         '        powerCurve.preWrapMode = WrapMode.ClampForever;\n        powerCurve.postWrapMode = WrapMode.ClampForever;\n'
         '        float power = powerCurve.Evaluate(speed);',
         'float power = PerfUnity.PowerCurve(speed);', 1, files=['BatMotionBehaviour.Batting.cs']),
    # Bat geometry helpers: curve objects are not constructible, the sampled radius is.
    rule('Geometry curve validity', 'curve != null && curve.length > 0 &&', '', 1, files=['BatGeometry.cs']),
    rule('Geometry EvaluateRadius guard',
         'if (curve == null || curve.length == 0 || !Finite(normalized)) return float.NaN;',
         'if (!Finite(normalized)) return float.NaN;', 1, files=['BatGeometry.cs']),
    rule('Geometry EvaluateRadius sample', 'curve.Evaluate(Mathf.Clamp01(normalized))',
         'PerfUnity.RadiusCurve(Mathf.Clamp01(normalized))', 1, files=['BatGeometry.cs']),
    rule('Contact plate forward', 'strikeZone.PlateTransform().forward', 'PerfUnity.PlateForward(strikeZone.PlateTransform())', 1,
         files=['BatContactBehaviour.cs']),
    # Bat Contact: ball collider, contact zone Transform and the Ball hand-off.
    rule('Contact collision ignore (once per pitch)', 'motion.IgnoreBallCollision(flight.ballCollider);', '', 1,
         files=['BatContactBehaviour.cs']),
    rule('Contact ball collider presence', 'flight.ballCollider == null', 'flight.perfBallRadius <= 0f', 2,
         files=['BatContactBehaviour.cs']),
    rule('Contact ball radius', 'flight.ballCollider.radius, flight.ballCollider.transform.lossyScale',
         'flight.perfBallRadius, flight.perfBallScale', 1, files=['BatContactBehaviour.cs']),
    rule('Contact zone transform', 'contactZone.InverseTransformPoint(position)',
         'PerfUnity.InverseTransformPoint(perfZoneCenter, position)', 1, files=['BatContactBehaviour.cs']),
    rule('Contact Ball hand-off (ApplyContactResult)',
         'flight.ApplyContactResult(_sequence, time, _ballPosition, _ballVelocity, _ballSpin,\n            result.linearVelocity, result.angularVelocity, batterId, strength)',
         'PerfUnity.HandOff(batterId, strength)', 1, files=['BatContactBehaviour.cs']),
    rule('Contact bat effects lookup', 'BatBehaviour bat = motion.GetComponentInParent<BatBehaviour>();', 'BatBehaviour bat = null;', 1,
         files=['BatContactBehaviour.cs']),
    # Broad phase: the Transform wrapper is replaced by the box-local core with the identity-rotation harness zone.
    rule('Contact broad phase zone', 'flight.ContactPathOutsideBox(start, boundary, maxExtrapolation, contactZone, zoneHalfSize)',
         'flight.ContactPathOutsideLocalBox(start, boundary, maxExtrapolation, perfZoneCenter, Quaternion.identity, Vector3.one, zoneHalfSize, PerfUnity.Gravity())',
         1, files=['BatContactBehaviour.cs']),
    rule('Feedback broad phase zone',
         'flight.ContactPathOutsideBox(start, boundary, contact.maxExtrapolation, contact.contactZone, contact.zoneHalfSize)',
         'flight.ContactPathOutsideLocalBox(start, boundary, contact.maxExtrapolation, contact.perfZoneCenter, Quaternion.identity, Vector3.one, contact.zoneHalfSize, PerfUnity.Gravity())',
         {'BattingFeedbackBehaviour.Scan.cs': 1, 'BattingFeedbackBehaviour.Trail.cs': 1}, others='no other file calls the broad phase'),
    # Ball collider / contact zone Transform (feedback).
    rule('Feedback ball collider presence', 'flight.ballCollider != null', 'flight.perfBallRadius > 0f', 1,
         files=['BattingFeedbackBehaviour.Scan.cs']),
    rule('Feedback ball radius', 'flight.ballCollider.radius, flight.ballCollider.transform.lossyScale',
         'flight.perfBallRadius, flight.perfBallScale', 1, files=['BattingFeedbackBehaviour.Scan.cs']),
    rule('Feedback zone transform', 'contact.contactZone.InverseTransformPoint(position)',
         'PerfUnity.InverseTransformPoint(contact.perfZoneCenter, position)', 1,
         files=['BattingFeedbackBehaviour.Scan.cs']),
]


def production_sources(root):
    runtime = Path(root) / RUNTIME
    return [p for p in sorted(runtime.rglob('*.cs'))
            if not p.name.endswith(TEST_FILE_SUFFIX) and 'Samples' not in p.relative_to(runtime).parts
            and not p.name.startswith('__')]


def apply_rules(texts, rules=RULES):
    """texts: {filename: text}. Returns (rewritten texts, per-rule match counts).
    Counts are exact: an int is the reviewed total, a dict the reviewed matches per file."""
    counts = {}
    out = dict(texts)
    for r in rules:
        per_file = {}
        for name in out:
            if r['files'] is not None and name not in r['files']:
                continue
            if r['regex']:
                out[name], n = re.subn(r['pattern'], r['replacement'], out[name])
            else:
                n = out[name].count(r['pattern'])
                out[name] = out[name].replace(r['pattern'], r['replacement'])
            if n:
                per_file[name] = n
        counts[r['name']] = sum(per_file.values())
        expected = r['count']
        problem = None
        if isinstance(expected, dict):
            for name, n in sorted(expected.items()):
                if per_file.get(name, 0) != n:
                    problem = '%s: matched %d time(s), expected %d' % (name, per_file.get(name, 0), n)
                    break
            if problem is None and not r['others']:
                extra = sorted(set(per_file) - set(expected))
                if extra:
                    problem = 'unexpected new match in %s' % ', '.join(extra)
        elif sum(per_file.values()) != expected:
            problem = 'matched %d time(s), expected %s' % (sum(per_file.values()), expected)
        if problem:
            raise HarnessError(
                'Stub rule "%s" %s: the production call site changed; review perf/harness.py and '
                'perf/README.md before updating the rule.' % (r['name'], problem))
    return out, counts


def build(root, test_sources):
    """Returns (sources for the MCP test call, info). test_sources: list of {name,text}."""
    root = Path(root)
    prod = {}
    for p in production_sources(root):
        if p.name in prod:
            raise HarnessError('Duplicate production file name: ' + p.name)
        prod[p.name] = p.read_text(encoding='utf-8-sig')
    rewritten, counts = apply_rules(prod)
    sources = [{'name': n, 'text': t} for n, t in rewritten.items()]
    for p in sorted(HARNESS_DIR.glob('*.cs')):
        sources.append({'name': p.name, 'text': p.read_text(encoding='utf-8')})
    sources.extend(test_sources)
    digest = hashlib.sha256()
    for name in sorted(prod):
        digest.update(name.encode() + b'\0' + prod[name].encode() + b'\0')
    return sources, {'productionFiles': len(prod), 'ruleMatches': counts, 'productionSha256': digest.hexdigest()}
