"""Runs the VVBC Performance Budget tests (steps / externs) through the Tsukimi MCP test runner.

Exit codes: 0 all active scenarios within budget, 1 budget exceeded, 2 error (scenario not run, no
measurement, bad configuration, stale harness rule, tool failure), 3 environment mismatch.
"""
import argparse
import json
from pathlib import Path
import sys
import time

PERF_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(PERF_DIR))

import budgets  # noqa: E402
import drivers  # noqa: E402
import harness  # noqa: E402
import mcp_client  # noqa: E402
import report  # noqa: E402

ROOT = PERF_DIR.parents[2]
BUDGET_FILE = PERF_DIR / 'budgets.json'
TSUKIMI_PACKAGE = 'Packages/com.tsukimicode.udon-tools/package.json'
CLIENT_FACTORY = mcp_client.Client  # replaced by unit tests
EXIT_OK, EXIT_REGRESSION, EXIT_ERROR, EXIT_ENVIRONMENT = 0, 1, 2, 3


class EnvironmentMismatch(RuntimeError):
    pass


def installed_tsukimi_version(root):
    path = Path(root) / TSUKIMI_PACKAGE
    try:
        return json.loads(path.read_text(encoding='utf-8-sig'))['version']
    except (OSError, ValueError, KeyError):
        raise EnvironmentMismatch('Tsukimi compiler package not found or unreadable: %s' % path)


def check_environment(budget, root):
    expected = budget['environment']['tsukimiVersion']
    actual = installed_tsukimi_version(root)
    if actual != expected:
        raise EnvironmentMismatch(
            'Measurements are only comparable for the same runner: budgets were recorded with Tsukimi %s, '
            'installed is %s. This is an environment error, not a performance regression; re-record the '
            'baselines in a reviewed change.' % (expected, actual))
    return actual


def parse_cases(cases, scenarios):
    """Maps MCP test cases to {scenario id: {mode: case}}."""
    index = {}
    for scenario in scenarios:
        if scenario.get('status', 'active') != 'active':
            continue
        for mode in (scenario['measure']['base'], scenario['measure']['run']):
            index[drivers.test_name(scenario['id'], mode)] = (scenario['id'], mode)
    found = {}
    for case in cases:
        key = index.get(case.get('name'))
        if key:
            found.setdefault(key[0], {})[key[1]] = case
    return found


def measure(scenario, cases_by_mode):
    """Returns (measured or None, error text or None)."""
    run, base = scenario['measure']['run'], scenario['measure']['base']
    for mode in (base, run):
        case = cases_by_mode.get(mode)
        if case is None:
            return None, 'test %s was not executed' % drivers.test_name(scenario['id'], mode)
        if case.get('outcome') != 'passed':
            detail = case.get('detail') or case.get('actual') or case.get('expected') or ''
            return None, 'test %s -> %s: %s' % (drivers.test_name(scenario['id'], mode), case.get('outcome'), detail)
        if not isinstance(case.get('steps'), int) or not isinstance(case.get('externCalls'), int):
            return None, 'test %s returned no steps/externCalls' % drivers.test_name(scenario['id'], mode)
    measured = {'steps': cases_by_mode[run]['steps'] - cases_by_mode[base]['steps'],
                'externs': cases_by_mode[run]['externCalls'] - cases_by_mode[base]['externCalls']}
    if measured['steps'] <= 0 or measured['externs'] <= 0:
        return None, 'non-positive cost difference %s: the scenario did not execute production work' % measured
    return measured, None


def run_scenarios(root, budget, only=None, client_factory=None, compiler_root=None):
    scenarios = [s for s in budget['scenarios'] if only is None or s['id'] in only]
    if only is not None and len(scenarios) != len(set(only)):
        known = {s['id'] for s in budget['scenarios']}
        raise budgets.BudgetError('Unknown scenario id(s): %s' % ', '.join(sorted(set(only) - known)))
    tests = drivers.generate(scenarios)
    sources, info = harness.build(root, tests)
    client = (client_factory or CLIENT_FACTORY)(compiler_root or root)
    try:
        reply = client.call('test', {'sources': sources, 'summary': False})
    finally:
        client.close()
    cases = reply.get('cases')
    if not isinstance(cases, list):
        raise mcp_client.McpError('Test tool returned no cases: %s' % json.dumps(reply)[:300])
    by_scenario = parse_cases(cases, scenarios)
    measurements, errors = {}, {}
    for scenario in scenarios:
        if scenario.get('status', 'active') != 'active':
            continue
        measured, error = measure(scenario, by_scenario.get(scenario['id'], {}))
        measurements[scenario['id']] = measured
        if error:
            errors[scenario['id']] = error
    known = set()
    for modes in by_scenario.values():
        known.update(id(c) for c in modes.values())
    stray = [c for c in cases if id(c) not in known and c.get('outcome') != 'passed']
    if stray:
        raise mcp_client.McpError('Test run produced unexpected failures (harness did not compile or run?): ' +
                                  '; '.join('%s -> %s: %s' % (c.get('name'), c.get('outcome'), str(c.get('detail', ''))[:600])
                                            for c in stray[:3]))
    return scenarios, measurements, errors, info, reply


def static_check(budget, root):
    """Validates everything that does not need the Tsukimi compiler. Never reports performance."""
    try:
        tests = drivers.generate(budget['scenarios'])
        sources, info = harness.build(root, tests)
    except (harness.HarnessError, drivers.DriverError, budgets.BudgetError, OSError) as error:
        print('PERF STATIC CHECK FAILED: %s' % error, file=sys.stderr)
        return EXIT_ERROR
    active = [s for s in budget['scenarios'] if s.get('status', 'active') == 'active']
    names = {c.split('public void ')[1].split('(')[0] for t in tests for c in t['text'].splitlines() if 'public void ' in c}
    expected = {drivers.test_name(s['id'], m) for s in active for m in (s['measure']['base'], s['measure']['run'])}
    if names != expected:
        print('PERF STATIC CHECK FAILED: generated tests do not match the scenarios', file=sys.stderr)
        return EXIT_ERROR
    print('Performance budget static check OK: %d active scenario(s), %d generated tests, %d production files, '
          '%d stub rules matched. No performance was measured.' % (len(active), len(names), info['productionFiles'],
                                                                   len(info['ruleMatches'])))
    return EXIT_OK


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--root', default=str(ROOT))
    parser.add_argument('--compiler-root', default=None,
                        help='project that provides the Tsukimi compiler package (default: --root); '
                             'used to measure production sources from another checkout, e.g. a worktree')
    parser.add_argument('--budgets', default=str(BUDGET_FILE))
    parser.add_argument('--scenario', action='append', help='run only this scenario id (repeatable)')
    parser.add_argument('--report-dir', default=str(ROOT / 'Tools' / 'VVBC.Toolchain' / '.state' / 'perf'))
    parser.add_argument('--check', action='store_true',
                        help='static validation only (no compiler needed): budgets, generated tests and stub rules '
                             'against the current production sources. This is NOT a performance test.')
    parser.add_argument('--propose', action='store_true',
                        help='print measured values next to the budgets; never edits budgets.json')
    args = parser.parse_args(argv)
    started = time.time()
    try:
        budget = budgets.load(args.budgets)
        budgets.validate(budget, drivers=set(drivers.DRIVERS))
    except budgets.BudgetError as error:
        print('PERF ERROR (budget configuration): %s' % error, file=sys.stderr)
        return EXIT_ERROR
    if args.check:
        return static_check(budget, args.root)
    try:
        runner = check_environment(budget, args.compiler_root or args.root)
    except EnvironmentMismatch as error:
        print('PERF ENVIRONMENT ERROR (not a performance regression): %s' % error, file=sys.stderr)
        return EXIT_ENVIRONMENT
    try:
        scenarios, measurements, errors, info, reply = run_scenarios(args.root, budget, args.scenario, compiler_root=args.compiler_root)
    except (budgets.BudgetError, harness.HarnessError, drivers.DriverError, mcp_client.McpError, OSError) as error:
        print('PERF ERROR: %s' % error, file=sys.stderr)
        return EXIT_ERROR
    subset = dict(budget, scenarios=scenarios)
    results = budgets.evaluate_all(subset, measurements)
    for result in results:
        if result['id'] in errors:
            result['error'] = errors[result['id']]
    document = report.build(results, budget, runner, info, reply, args.scenario, time.time() - started)
    report.write(document, Path(args.report_dir))
    print(report.render_text(document, rerun_hint=report.RERUN))
    if args.propose:
        print(report.render_proposal(results))
    verdicts = [r['verdict'] for r in results]
    if any(v.startswith('ERROR') for v in verdicts):
        return EXIT_ERROR
    if any(v.startswith('FAIL') for v in verdicts):
        return EXIT_REGRESSION
    return EXIT_OK


if __name__ == '__main__':
    sys.exit(main())
