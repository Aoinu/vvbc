"""Performance Budget loading, validation and evaluation (pure functions, unit tested)."""
import json
from pathlib import Path

SCHEMA = 1
METRICS = ('steps', 'externs')
STATUS_ACTIVE = 'active'
STATUS_UNSUPPORTED = 'unsupported'

# Verdicts
PASS = 'PASS'
FAIL_STEPS = 'FAIL_STEPS'
FAIL_EXTERNS = 'FAIL_EXTERNS'
FAIL_BOTH = 'FAIL_STEPS_AND_EXTERNS'
ERROR_NOT_RUN = 'ERROR_NOT_RUN'
ERROR_NO_MEASUREMENT = 'ERROR_NO_MEASUREMENT'
NOT_COVERED = 'NOT_COVERED'

REQUIRED_SCENARIO_KEYS = ('id', 'component', 'function', 'description', 'driver', 'params', 'measure',
                          'baseline', 'limits', 'baselineCommit', 'rationale')


class BudgetError(ValueError):
    pass


def load(path):
    try:
        data = json.loads(Path(path).read_text(encoding='utf-8-sig'))
    except (OSError, ValueError) as error:
        raise BudgetError('Cannot read budget file %s: %s' % (path, error))
    validate(data)
    return data


def _positive_int(value):
    return isinstance(value, int) and not isinstance(value, bool) and value > 0


def validate(data, drivers=None):
    """Raises BudgetError when the budget file is malformed. drivers: optional set of known driver names."""
    if not isinstance(data, dict) or data.get('schema') != SCHEMA:
        raise BudgetError('Budget file must be an object with "schema": %d' % SCHEMA)
    environment = data.get('environment')
    if not isinstance(environment, dict) or not environment.get('tsukimiVersion') or not environment.get('measurementMethod'):
        raise BudgetError('Budget file needs environment.tsukimiVersion and environment.measurementMethod')
    scenarios = data.get('scenarios')
    if not isinstance(scenarios, list) or not scenarios:
        raise BudgetError('Budget file needs a non-empty "scenarios" list')
    seen = set()
    for scenario in scenarios:
        if not isinstance(scenario, dict):
            raise BudgetError('Every scenario must be an object')
        sid = scenario.get('id')
        if not isinstance(sid, str) or not sid.replace('_', '').isalnum() or sid[0].isdigit():
            raise BudgetError('Scenario id must be a C#-identifier-like string: %r' % (sid,))
        if sid in seen:
            raise BudgetError('Duplicate scenario id: ' + sid)
        seen.add(sid)
        status = scenario.get('status', STATUS_ACTIVE)
        if status not in (STATUS_ACTIVE, STATUS_UNSUPPORTED):
            raise BudgetError('%s: unknown status %r' % (sid, status))
        for key in ('component', 'function', 'description', 'driver'):
            if not isinstance(scenario.get(key), str) or not scenario[key]:
                raise BudgetError('%s: missing "%s"' % (sid, key))
        if status == STATUS_UNSUPPORTED:
            if not scenario.get('reason'):
                raise BudgetError('%s: unsupported scenarios must state a reason' % sid)
            continue
        for key in REQUIRED_SCENARIO_KEYS:
            if key not in scenario:
                raise BudgetError('%s: missing "%s"' % (sid, key))
        if drivers is not None and scenario['driver'] not in drivers:
            raise BudgetError('%s: unknown driver %r' % (sid, scenario['driver']))
        measure = scenario['measure']
        if not isinstance(measure, dict) or not measure.get('run') or not measure.get('base'):
            raise BudgetError('%s: measure needs "run" and "base" test modes' % sid)
        for section in ('baseline', 'limits'):
            values = scenario[section]
            if not isinstance(values, dict) or set(values) != set(METRICS) or \
                    not all(_positive_int(values[m]) for m in METRICS):
                raise BudgetError('%s: %s needs positive integer steps and externs' % (sid, section))
        for metric in METRICS:
            if scenario['limits'][metric] < scenario['baseline'][metric]:
                raise BudgetError('%s: limit below baseline for %s' % (sid, metric))
        absolute = scenario.get('absoluteLimits')
        if absolute is not None:
            if not isinstance(absolute, dict) or set(absolute) != set(METRICS) or \
                    not all(_positive_int(absolute[m]) for m in METRICS):
                raise BudgetError('%s: absoluteLimits needs positive integer steps and externs' % sid)
            for metric in METRICS:
                if scenario['limits'][metric] > absolute[metric]:
                    raise BudgetError('%s: limits.%s exceeds the absolute ceiling' % (sid, metric))
        if not isinstance(scenario['rationale'], str) or len(scenario['rationale']) < 10:
            raise BudgetError('%s: rationale must explain the threshold' % sid)


def effective_limits(scenario):
    limits = dict(scenario['limits'])
    for metric, value in (scenario.get('absoluteLimits') or {}).items():
        limits[metric] = min(limits[metric], value)
    return limits


def evaluate(scenario, measured):
    """measured: {'steps': int, 'externs': int} or None. Returns a result dict with a verdict."""
    result = {'id': scenario['id'], 'component': scenario['component'], 'function': scenario['function'],
              'description': scenario['description'], 'measured': measured}
    if scenario.get('status', STATUS_ACTIVE) == STATUS_UNSUPPORTED:
        result.update(verdict=NOT_COVERED, reason=scenario['reason'])
        return result
    limits = effective_limits(scenario)
    result.update(baseline=scenario['baseline'], limits=limits)
    if measured is None:
        result['verdict'] = ERROR_NO_MEASUREMENT
        return result
    over = {m: measured[m] > limits[m] for m in METRICS}
    result['overage'] = {m: (measured[m] - limits[m]) / limits[m] for m in METRICS}
    result['vsBaseline'] = {m: (measured[m] - scenario['baseline'][m]) / scenario['baseline'][m] for m in METRICS}
    if over['steps'] and over['externs']:
        result['verdict'] = FAIL_BOTH
    elif over['steps']:
        result['verdict'] = FAIL_STEPS
    elif over['externs']:
        result['verdict'] = FAIL_EXTERNS
    else:
        result['verdict'] = PASS
    return result


def is_failure(verdict):
    return verdict.startswith('FAIL') or verdict.startswith('ERROR')


def evaluate_all(budget, measurements):
    """measurements: {scenario id: {'steps','externs'} | None}. Missing active scenarios are errors."""
    results = []
    for scenario in budget['scenarios']:
        sid = scenario['id']
        if scenario.get('status', STATUS_ACTIVE) == STATUS_UNSUPPORTED:
            results.append(evaluate(scenario, None))
        elif sid not in measurements:
            result = evaluate(scenario, None)
            result['verdict'] = ERROR_NOT_RUN
            results.append(result)
        else:
            results.append(evaluate(scenario, measurements[sid]))
    return results
