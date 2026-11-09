"""Generates the [TsukimiTest] methods for each budget scenario from its parameters.

A scenario is measured as the difference between two test modes ("run" minus "base") so that world
start-up and history seeding cancel out and only the production function under test remains.
"""


class DriverError(ValueError):
    pass


def f(value):
    return ('%.7g' % float(value)) + 'f'


def mode_number(mode):
    if len(mode) != 2 or mode[0] != 'm' or not mode[1].isdigit():
        raise DriverError('Test mode must look like m0, m1, m2: %r' % (mode,))
    return int(mode[1])


def test_name(scenario_id, mode):
    return 'Perf_%s_%s' % (scenario_id, mode)


def _asserts(expect):
    fields = {'entered': '_entered', 'exited': '_exited', 'hasClosest': '_hasClosest', 'hasResult': '_hasResult'}
    lines = []
    for key, value in sorted((expect or {}).items()):
        if key not in fields:
            raise DriverError('Unknown expectation %r' % key)
        lines.append('        Assert.AreEqual(%s, %s);' % ('true' if value else 'false', fields[key]))
    return lines


def feedback_scan(scenario):
    p = scenario['params']
    dt = 1.0 / p['fps']
    lines = []
    for mode in sorted({scenario['measure']['base'], scenario['measure']['run']}):
        body = '        PerfRun(%s, %s, %s, %s, %s, %s, %d, %d);' % (
            f(p['y0']), f(p['vy']), f(p['n0']), f(p['vn']), 'true' if p['zone'] else 'false', f(dt),
            p['frames'], mode_number(mode))
        asserts = _asserts(scenario.get('expect')) if mode == scenario['measure']['run'] else []
        lines += ['    [TsukimiTest(maxSteps: 100000000)]',
                  '    public void %s()' % test_name(scenario['id'], mode), '    {', body] + asserts + ['    }', '']
    return lines


def _contact_asserts(expect):
    fields = {'contactValid': 'contactValid', 'enteredZone': '_enteredZone', 'opportunityEnded': '_opportunityEnded'}
    lines = []
    for key, value in sorted((expect or {}).items()):
        if key not in fields:
            raise DriverError('Unknown expectation %r' % key)
        lines.append('        Assert.AreEqual(%s, %s);' % ('true' if value else 'false', fields[key]))
    return lines


def contact_pose(scenario):
    p = scenario['params']
    dt = 1.0 / p['fps']
    lines = []
    for mode in sorted({scenario['measure']['base'], scenario['measure']['run']}):
        body = '        Mimic.Join();\n        PerfRun(%s, %s, %s, %s, %s, %s, %s, %d, %d);' % (
            f(p['y0']), f(p['vy']), f(p['speed']), f(p['crossSeconds']), f(p['tilt']),
            'true' if p['zone'] else 'false', f(dt), p['frames'], mode_number(mode))
        asserts = _contact_asserts(scenario.get('expect')) if mode == scenario['measure']['run'] else []
        lines += ['    [TsukimiTest(maxSteps: 100000000)]',
                  '    public void %s()' % test_name(scenario['id'], mode), '    {', body] + asserts + ['    }', '']
    return lines


MOTION_KINDS = {'frame': 0, 'pickup': 1, 'second_pickup': 2, 'two_hand': 3, 'lock': 4, 'drop': 5}
MOTION_EXPECT = {'held': '_mode == LocalHeld', 'inactive': '_mode == Inactive', 'recorded': 'contactLatestTime > 0f',
                 'lockCaptured': '_hasLockOffset', 'gripState': 'gripState'}


def motion_pose(scenario):
    p = scenario['params']
    if p['kind'] not in MOTION_KINDS:
        raise DriverError('Unknown motion kind %r' % p['kind'])
    dt = 1.0 / p['fps']
    lines = []
    for mode in sorted({scenario['measure']['base'], scenario['measure']['run']}):
        asserts = []
        if mode == scenario['measure']['run']:
            for key, value in sorted((scenario.get('expect') or {}).items()):
                if key not in MOTION_EXPECT:
                    raise DriverError('Unknown expectation %r' % key)
                if key == 'gripState':
                    asserts.append('        Assert.AreEqual(%d, gripState);' % value)
                else:
                    asserts.append('        Assert.%s(%s);' % ('IsTrue' if value else 'IsFalse', MOTION_EXPECT[key]))
        lines += ['    [TsukimiTest(maxSteps: 100000000)]', '    public void %s()' % test_name(scenario['id'], mode), '    {',
                  '        PerfMotion(%d, %d, %s, %s, %d);' % (MOTION_KINDS[p['kind']], p['grip'], f(p['speed']), f(dt),
                                                               mode_number(mode))] + asserts + ['    }', '']
    return lines


# driver name -> (class that owns the tests, generator)
DRIVERS = {
    'feedback-scan': ('BattingFeedbackBehaviour', feedback_scan),
    'contact-pose': ('BatContactBehaviour', contact_pose),
    'motion-pose': ('BatMotionBehaviour', motion_pose),
}


def generate(scenarios):
    """Returns [{'name','text'}] test sources, one per component class."""
    by_class = {}
    for scenario in scenarios:
        if scenario.get('status', 'active') != 'active':
            continue
        owner, generator = DRIVERS[scenario['driver']]
        by_class.setdefault(owner, []).extend(generator(scenario))
    sources = []
    for owner, lines in sorted(by_class.items()):
        text = '\n'.join(['using Tsukimi;', 'using UnityEngine;', '',
                          '// Generated from perf/budgets.json by run.py. Do not edit.',
                          'public partial class %s' % owner, '{'] + lines + ['}', ''])
        sources.append({'name': '%s.Perf.Scenarios.Tests.cs' % owner, 'text': text})
    return sources
