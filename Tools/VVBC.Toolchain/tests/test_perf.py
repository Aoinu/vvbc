"""Tests of the Performance Budget gate itself: it must fail loudly for every unsafe condition."""
import contextlib
import copy
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest

PERF = Path(__file__).resolve().parents[1] / 'perf'
sys.path.insert(0, str(PERF))

import budgets  # noqa: E402
import drivers  # noqa: E402
import harness  # noqa: E402
import mcp_client  # noqa: E402
import run  # noqa: E402

ROOT = PERF.parents[2]


def scenario(**overrides):
    value = {
        'id': 'feedback_sample', 'component': 'BattingFeedbackBehaviour', 'function': 'Scan',
        'description': 'sample', 'driver': 'feedback-scan',
        'params': {'y0': 0.0, 'vy': 20.0, 'n0': 0.02, 'vn': 0.0, 'zone': True, 'fps': 90, 'frames': 1},
        'expect': {'entered': True}, 'measure': {'base': 'm0', 'run': 'm1'},
        'baseline': {'steps': 1000, 'externs': 100}, 'limits': {'steps': 1300, 'externs': 130},
        'baselineCommit': 'abc', 'rationale': 'baseline +30% for tests'}
    value.update(overrides)
    return value


def budget(*scenarios):
    return {'schema': 1, 'environment': {'tsukimiVersion': '0.10.1', 'measurementMethod': 'test'},
            'scenarios': list(scenarios) or [scenario()]}


def case(name, steps, externs, outcome='passed', detail=None):
    value = {'name': name, 'outcome': outcome, 'steps': steps, 'externCalls': externs}
    if detail:
        value['detail'] = detail
    return value


class BudgetEvaluationTests(unittest.TestCase):
    def verdict(self, steps, externs, **overrides):
        return budgets.evaluate(scenario(**overrides), {'steps': steps, 'externs': externs})['verdict']

    def test_below_limit_passes(self):
        self.assertEqual(budgets.PASS, self.verdict(1000, 100))

    def test_exactly_at_limit_passes(self):
        self.assertEqual(budgets.PASS, self.verdict(1300, 130))

    def test_steps_over_limit_fails(self):
        self.assertEqual(budgets.FAIL_STEPS, self.verdict(1301, 100))

    def test_externs_over_limit_fails(self):
        self.assertEqual(budgets.FAIL_EXTERNS, self.verdict(1000, 131))

    def test_both_over_limit_reports_both(self):
        result = budgets.evaluate(scenario(), {'steps': 2600, 'externs': 260})
        self.assertEqual(budgets.FAIL_BOTH, result['verdict'])
        self.assertAlmostEqual(1.0, result['overage']['steps'])

    def test_absolute_ceiling_caps_the_limit(self):
        s = scenario(limits={'steps': 1300, 'externs': 130}, absoluteLimits={'steps': 1300, 'externs': 130})
        self.assertEqual(budgets.PASS, budgets.evaluate(s, {'steps': 1300, 'externs': 130})['verdict'])
        self.assertTrue(budgets.is_failure(budgets.evaluate(s, {'steps': 1301, 'externs': 130})['verdict']))

    def test_missing_measurement_is_an_error(self):
        result = budgets.evaluate(scenario(), None)
        self.assertEqual(budgets.ERROR_NO_MEASUREMENT, result['verdict'])
        self.assertTrue(budgets.is_failure(result['verdict']))

    def test_scenario_that_was_not_run_is_an_error(self):
        results = budgets.evaluate_all(budget(), {})
        self.assertEqual([budgets.ERROR_NOT_RUN], [r['verdict'] for r in results])
        self.assertTrue(budgets.is_failure(results[0]['verdict']))

    def test_unsupported_scenarios_are_reported_not_covered_never_pass(self):
        s = {'id': 'motion_pickup_real', 'component': 'BatMotionBehaviour', 'function': 'OnPickup',
             'description': 'x', 'driver': 'motion-pose', 'status': 'unsupported', 'reason': 'needs VRCPickup'}
        results = budgets.evaluate_all(budget(scenario(), s), {'feedback_sample': {'steps': 1, 'externs': 1}})
        self.assertEqual(budgets.NOT_COVERED, results[1]['verdict'])
        self.assertNotEqual(budgets.PASS, results[1]['verdict'])


class BudgetValidationTests(unittest.TestCase):
    def assertInvalid(self, data):
        with self.assertRaises(budgets.BudgetError):
            budgets.validate(data, drivers=set(drivers.DRIVERS))

    def test_valid_budget_is_accepted(self):
        budgets.validate(budget(), drivers=set(drivers.DRIVERS))

    def test_repository_budget_file_is_valid(self):
        budgets.load(PERF / 'budgets.json')

    def test_rejects_wrong_schema(self):
        self.assertInvalid(dict(budget(), schema=2))

    def test_rejects_missing_environment(self):
        data = budget()
        del data['environment']
        self.assertInvalid(data)

    def test_rejects_empty_scenarios(self):
        self.assertInvalid(dict(budget(), scenarios=[]))

    def test_rejects_duplicate_ids(self):
        self.assertInvalid(budget(scenario(), scenario()))

    def test_rejects_limit_below_baseline(self):
        self.assertInvalid(budget(scenario(limits={'steps': 999, 'externs': 130})))

    def test_rejects_zero_and_negative_and_non_integer_values(self):
        for bad in (0, -5, 1.5, '10', None, True):
            self.assertInvalid(budget(scenario(limits={'steps': bad, 'externs': 130})))

    def test_rejects_limit_above_absolute_ceiling(self):
        self.assertInvalid(budget(scenario(absoluteLimits={'steps': 1200, 'externs': 130})))

    def test_rejects_missing_rationale_and_commit(self):
        for key in ('rationale', 'baselineCommit', 'baseline', 'limits', 'measure', 'params'):
            s = scenario()
            del s[key]
            self.assertInvalid(budget(s))

    def test_rejects_unknown_driver_and_status(self):
        self.assertInvalid(budget(scenario(driver='nope')))
        self.assertInvalid(budget(scenario(status='skipped')))

    def test_unsupported_requires_reason(self):
        self.assertInvalid(budget(scenario(), {'id': 'x_y', 'component': 'A', 'function': 'b', 'description': 'd',
                                               'driver': 'motion-pose', 'status': 'unsupported'}))

    def test_load_rejects_unreadable_and_malformed_files(self):
        with tempfile.TemporaryDirectory() as directory:
            missing = Path(directory) / 'missing.json'
            with self.assertRaises(budgets.BudgetError):
                budgets.load(missing)
            broken = Path(directory) / 'broken.json'
            broken.write_text('{not json', encoding='utf-8')
            with self.assertRaises(budgets.BudgetError):
                budgets.load(broken)


class MeasurementTests(unittest.TestCase):
    def modes(self, base, run_case):
        return {'m0': base, 'm1': run_case}

    def test_cost_is_run_minus_base(self):
        measured, error = run.measure(scenario(), self.modes(case('a', 100, 10), case('b', 350, 40)))
        self.assertIsNone(error)
        self.assertEqual({'steps': 250, 'externs': 30}, measured)

    def test_missing_case_is_an_error(self):
        measured, error = run.measure(scenario(), {'m0': case('a', 1, 1)})
        self.assertIsNone(measured)
        self.assertIn('not executed', error)

    def test_not_runnable_case_is_an_error_with_detail(self):
        bad = case('b', 5, 5, outcome='notRunnable', detail='This call is not modelled here: X')
        measured, error = run.measure(scenario(), self.modes(case('a', 1, 1), bad))
        self.assertIsNone(measured)
        self.assertIn('not modelled', error)

    def test_failed_expectation_is_an_error(self):
        measured, error = run.measure(scenario(), self.modes(case('a', 1, 1), case('b', 9, 9, outcome='assertFailed')))
        self.assertIsNone(measured)
        self.assertIn('assertFailed', error)

    def test_missing_counters_are_an_error(self):
        broken = {'name': 'b', 'outcome': 'passed'}
        measured, error = run.measure(scenario(), self.modes(case('a', 1, 1), broken))
        self.assertIsNone(measured)
        self.assertIn('no steps', error)

    def test_non_positive_difference_means_no_production_work_ran(self):
        measured, error = run.measure(scenario(), self.modes(case('a', 100, 10), case('b', 100, 10)))
        self.assertIsNone(measured)
        self.assertIn('did not execute', error)


class FakeClient:
    """Stands in for the Tsukimi MCP server."""
    replies = None
    raise_error = False

    def __init__(self, root):
        self.root = root

    def call(self, tool, arguments):
        if FakeClient.raise_error:
            raise mcp_client.McpError('server crashed')
        return FakeClient.replies(arguments)

    def close(self):
        pass


def fake_reply(steps_run=1500, externs_run=150, run_outcome='passed', drop_run=False, extra=None):
    def reply(arguments):
        text = '\n'.join(s['text'] for s in arguments['sources'])
        assert 'Perf_feedback_sample_m1' in text
        cases = [case('Perf_feedback_sample_m0', 500, 50)]
        if not drop_run:
            cases.append(case('Perf_feedback_sample_m1', steps_run, externs_run, outcome=run_outcome))
        return {'origin': 'mcp', 'cases': cases + (extra or [])}
    return reply


class EndToEndGateTests(unittest.TestCase):
    """run.main with a fake compiler: exit codes and output for each failure class."""

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.budget_path = Path(self.directory.name) / 'budgets.json'
        self.write_budget(budget())
        FakeClient.raise_error = False
        self.original_factory = run.CLIENT_FACTORY
        self.original_environment = run.check_environment
        run.CLIENT_FACTORY = FakeClient
        run.check_environment = lambda budget_data, root: '0.10.1'
        self.addCleanup(self.restore)

    def restore(self):
        run.CLIENT_FACTORY = self.original_factory
        run.check_environment = self.original_environment

    def write_budget(self, data):
        self.budget_path.write_text(json.dumps(data), encoding='utf-8')

    def main(self, *extra):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = run.main(['--budgets', str(self.budget_path), '--report-dir', self.directory.name] + list(extra))
        return code, out.getvalue(), err.getvalue()

    def test_within_budget_exits_zero(self):
        FakeClient.replies = fake_reply(steps_run=1500, externs_run=150)  # cost 1000 / 100
        code, out, _ = self.main()
        self.assertEqual(run.EXIT_OK, code)
        self.assertIn('PASS', out)

    def test_exactly_at_limit_exits_zero(self):
        FakeClient.replies = fake_reply(steps_run=1800, externs_run=180)  # cost 1300 / 130
        self.assertEqual(run.EXIT_OK, self.main()[0])

    def test_steps_over_limit_exits_nonzero_and_reports_details(self):
        FakeClient.replies = fake_reply(steps_run=1801, externs_run=150)
        code, out, _ = self.main()
        self.assertEqual(run.EXIT_REGRESSION, code)
        for text in ('feedback_sample', 'BattingFeedbackBehaviour.Scan', 'steps   measured 1301', 'limit 1300',
                     'externs measured 100', 'limit 130', 'over limit', 'Run-PerformanceTests.ps1 -Scenario feedback_sample'):
            self.assertIn(text, out)

    def test_externs_over_limit_exits_nonzero(self):
        FakeClient.replies = fake_reply(steps_run=1500, externs_run=181)
        self.assertEqual(run.EXIT_REGRESSION, self.main()[0])

    def test_scenario_not_executed_exits_error(self):
        FakeClient.replies = fake_reply(drop_run=True)
        code, out, _ = self.main()
        self.assertEqual(run.EXIT_ERROR, code)
        self.assertNotIn('PASS  feedback_sample', out)

    def test_not_runnable_scenario_exits_error(self):
        FakeClient.replies = fake_reply(run_outcome='notRunnable')
        self.assertEqual(run.EXIT_ERROR, self.main()[0])

    def test_tool_error_exits_error(self):
        FakeClient.replies = fake_reply()
        FakeClient.raise_error = True
        code, _, err = self.main()
        self.assertEqual(run.EXIT_ERROR, code)
        self.assertIn('server crashed', err)

    def test_malformed_reply_exits_error(self):
        FakeClient.replies = lambda arguments: {'origin': 'mcp'}
        self.assertEqual(run.EXIT_ERROR, self.main()[0])

    def test_unexpected_compile_failure_exits_error(self):
        stray = {'name': 'BattingFeedbackBehaviour.cs', 'outcome': 'notRunnable', 'detail': 'did not compile: CS1061'}
        FakeClient.replies = fake_reply(extra=[stray])
        code, _, err = self.main()
        self.assertEqual(run.EXIT_ERROR, code)
        self.assertIn('did not compile', err)

    def test_invalid_budget_exits_error(self):
        self.write_budget(dict(budget(), schema=99))
        FakeClient.replies = fake_reply()
        self.assertEqual(run.EXIT_ERROR, self.main()[0])

    def test_unknown_scenario_filter_exits_error(self):
        FakeClient.replies = fake_reply()
        self.assertEqual(run.EXIT_ERROR, self.main('--scenario', 'does_not_exist')[0])

    def test_environment_mismatch_is_distinct_from_regression(self):
        def mismatch(budget_data, root):
            raise run.EnvironmentMismatch('recorded with 0.10.1, installed 0.11.0')
        run.check_environment = mismatch
        FakeClient.replies = fake_reply()
        code, _, err = self.main()
        self.assertEqual(run.EXIT_ENVIRONMENT, code)
        self.assertIn('not a performance regression', err)

    def test_partial_run_is_marked_partial(self):
        FakeClient.replies = fake_reply()
        _, out, _ = self.main('--scenario', 'feedback_sample')
        self.assertIn('PARTIAL RUN', out)


class EnvironmentTests(unittest.TestCase):
    def test_version_mismatch_raises_environment_error(self):
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory) / 'Packages' / 'com.tsukimicode.udon-tools'
            package.mkdir(parents=True)
            (package / 'package.json').write_text('{"version": "9.9.9"}', encoding='utf-8')
            with self.assertRaises(run.EnvironmentMismatch) as caught:
                run.check_environment(budget(), directory)
            self.assertIn('environment error', str(caught.exception))

    def test_missing_compiler_package_raises_environment_error(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(run.EnvironmentMismatch):
                run.check_environment(budget(), directory)

    def test_matching_version_is_accepted(self):
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory) / 'Packages' / 'com.tsukimicode.udon-tools'
            package.mkdir(parents=True)
            (package / 'package.json').write_text('{"version": "0.10.1"}', encoding='utf-8')
            self.assertEqual('0.10.1', run.check_environment(budget(), directory))


class HarnessStubRuleTests(unittest.TestCase):
    def test_repository_rules_match_current_production_sources(self):
        run_code = run.main(['--check'])
        self.assertEqual(run.EXIT_OK, run_code)

    def test_changed_call_site_fails_instead_of_silently_measuring_stale_code(self):
        texts = {p.name: p.read_text(encoding='utf-8-sig') for p in harness.production_sources(ROOT)}
        scan = 'BattingFeedbackBehaviour.Scan.cs'
        changed = dict(texts)
        changed[scan] = changed[scan].replace('flight.ballCollider != null', 'flight.ballCollider  !=  null')
        with self.assertRaises(harness.HarnessError) as caught:
            harness.apply_rules(changed)
        self.assertIn('Feedback ball collider presence', str(caught.exception))

    def test_rule_matching_nothing_fails(self):
        rules = [harness.rule('never', 'text-that-does-not-exist', 'x', None)]
        with self.assertRaises(harness.HarnessError):
            harness.apply_rules({'A.cs': 'class A {}'}, rules)

    def test_rule_matching_more_often_than_reviewed_fails(self):
        rules = [harness.rule('twice', 'foo', 'bar', 1)]
        with self.assertRaises(harness.HarnessError):
            harness.apply_rules({'A.cs': 'foo foo'}, rules)

    def test_harness_compiles_current_production_not_a_copy(self):
        sources, _ = harness.build(ROOT, [])
        production = (ROOT / harness.RUNTIME / 'BattingFeedbackBehaviour.Scan.cs').read_text(encoding='utf-8-sig')
        text = next(s['text'] for s in sources if s['name'] == 'BattingFeedbackBehaviour.Scan.cs')
        # Untouched production lines are present verbatim; only reviewed stub sites differ.
        self.assertIn('private float CandidateDistance(BatMotionBehaviour motion)', text)
        self.assertIn('RefineClosest(motion, _scanLeftTime, time);', text)
        self.assertEqual(production.count('private'), text.count('private'))


class GeneratedTestTests(unittest.TestCase):
    def test_every_scenario_generates_its_base_and_run_tests(self):
        data = budgets.load(PERF / 'budgets.json')
        text = '\n'.join(t['text'] for t in drivers.generate(data['scenarios']))
        for s in data['scenarios']:
            if s.get('status', 'active') == 'active':
                for mode in (s['measure']['base'], s['measure']['run']):
                    self.assertIn('public void %s()' % drivers.test_name(s['id'], mode), text)

    def test_unknown_expectation_is_rejected(self):
        with self.assertRaises(drivers.DriverError):
            drivers.generate([scenario(expect={'bogus': True})])

    def test_bad_mode_is_rejected(self):
        with self.assertRaises(drivers.DriverError):
            drivers.generate([scenario(measure={'base': 'x', 'run': 'm1'})])

    def test_budget_ids_cover_required_scenarios(self):
        ids = {s['id'] for s in budgets.load(PERF / 'budgets.json')['scenarios']}
        for case_name in ('c1', 'c2', 'c3', 'c4', 'c5'):
            for frame in ('90fps', '60fps', '30fps', '15fps', 'backlog200ms'):
                self.assertIn('feedback_%s_%s' % (case_name, frame), ids)
        for required in ('contact_hit_90fps', 'contact_out_of_zone_90fps', 'motion_first_pickup', 'motion_second_pickup',
                         'motion_grip_lock', 'motion_drop', 'motion_frame_two_hand_swing_90fps'):
            self.assertIn(required, ids)


if __name__ == '__main__':
    unittest.main()
