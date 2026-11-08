"""Report rendering for the Performance Budget run."""
import json
from pathlib import Path

RERUN = 'pwsh -File Tools/VVBC.Toolchain/Run-PerformanceTests.ps1 -Scenario {id}'


def build(results, budget, runner, info, reply, only, seconds):
    return {
        'schema': 1,
        'runner': {'tsukimiVersion': runner, 'measurementMethod': budget['environment']['measurementMethod'],
                   'origin': reply.get('origin')},
        'production': {'files': info['productionFiles'], 'sha256': info['productionSha256'],
                       'stubRuleMatches': info['ruleMatches']},
        'partialRun': bool(only),
        'seconds': round(seconds, 1),
        'results': results,
        'summary': summarize(results),
    }


def summarize(results):
    counts = {}
    for result in results:
        counts[result['verdict']] = counts.get(result['verdict'], 0) + 1
    return counts


def write(document, directory):
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    (directory / 'report.json').write_text(json.dumps(document, indent=2) + '\n', encoding='utf-8')
    (directory / 'report.md').write_text(render_markdown(document), encoding='utf-8')


def _pct(value):
    return '%+.1f%%' % (value * 100.0)


def render_markdown(document):
    lines = ['# VVBC Performance Budget report', '',
             'Runner: Tsukimi %s (%s). Production sources: %d files, sha256 `%s`.' % (
                 document['runner']['tsukimiVersion'], document['runner']['measurementMethod'],
                 document['production']['files'], document['production']['sha256'][:16]),
             '', 'Steps and externs are Udon instruction/extern counts, not CPU time.', '',
             '| Scenario | Verdict | steps (limit) | externs (limit) | vs baseline |', '|---|---|---:|---:|---:|']
    for r in document['results']:
        if r.get('measured'):
            m, l = r['measured'], r['limits']
            lines.append('| %s | %s | %d (%d) | %d (%d) | %s / %s |' % (
                r['id'], r['verdict'], m['steps'], l['steps'], m['externs'], l['externs'],
                _pct(r['vsBaseline']['steps']), _pct(r['vsBaseline']['externs'])))
        else:
            lines.append('| %s | %s | - | - | %s |' % (r['id'], r['verdict'], r.get('error') or r.get('reason', '')))
    lines += ['', 'Summary: ' + ', '.join('%s=%d' % kv for kv in sorted(document['summary'].items())), '']
    return '\n'.join(lines)


def render_text(document, rerun_hint=RERUN):
    out = []
    for r in document['results']:
        v = r['verdict']
        if v == 'PASS':
            m, l = r['measured'], r['limits']
            out.append('PASS  %-48s steps %8d/%-8d externs %7d/%-7d' % (r['id'], m['steps'], l['steps'], m['externs'], l['externs']))
        elif v == 'NOT_COVERED':
            out.append('SKIP  %-48s NOT COVERED: %s' % (r['id'], r['reason']))
        elif v.startswith('FAIL'):
            m, l = r['measured'], r['limits']
            out.append('FAIL  %s  [%s.%s]' % (r['id'], r['component'], r['function']))
            out.append('        steps   measured %d  limit %d  (%s over limit)' % (m['steps'], l['steps'], _pct(r['overage']['steps'])))
            out.append('        externs measured %d  limit %d  (%s over limit)' % (m['externs'], l['externs'], _pct(r['overage']['externs'])))
            out.append('        rerun: ' + rerun_hint.format(id=r['id']))
        else:
            out.append('ERROR %s  [%s.%s] %s: %s' % (r['id'], r['component'], r['function'], v, r.get('error', 'no measurement')))
            out.append('        rerun: ' + rerun_hint.format(id=r['id']))
    summary = ', '.join('%s=%d' % kv for kv in sorted(document['summary'].items()))
    out.append('')
    out.append('Performance budget summary: %s (%.1fs)%s' % (summary, document['seconds'],
                                                              ' [PARTIAL RUN]' if document['partialRun'] else ''))
    if document['summary'].get('NOT_COVERED'):
        out.append('NOTE: NOT_COVERED scenarios are not measured and are not counted as passing.')
    return '\n'.join(out)


def render_proposal(results):
    lines = ['', 'Measured values (budgets.json is never edited automatically; review before adopting):']
    for r in results:
        if r.get('measured'):
            lines.append('  %-48s "baseline": {"steps": %d, "externs": %d}' % (
                r['id'], r['measured']['steps'], r['measured']['externs']))
    return '\n'.join(lines)
