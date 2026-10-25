#!/usr/bin/env python3
"""VVBC raw T# export bookkeeping. Python 3.11+, standard library only."""
import argparse
import base64
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys
import tempfile
import uuid

MODULE_NAME_PATTERN = r'^[A-Za-z][A-Za-z0-9]*$'
BASE = Path(__file__).resolve().parents[2]
MANIFEST = 'Tools/VVBC.Toolchain/tsharp-exports.json'
STATE = 'Tools/VVBC.Toolchain/.state'


def sha(data):
    return hashlib.sha256(data).hexdigest()


def input_sha(data):
    # Git checkouts may use CRLF or LF. This canonicalization applies ONLY to text
    # input fingerprints; exported U# is always compared/hashed as raw bytes.
    return sha(data.replace(b'\r\n', b'\n'))


def encoded(value):
    return (json.dumps(value, sort_keys=True, ensure_ascii=False, indent=2) + '\n').encode()


class Toolchain:
    def __init__(self, root=BASE):
        self.root = Path(root).resolve()
        self.path = self.local(MANIFEST)
        self.manifest = json.loads(self.path.read_text(encoding='utf-8-sig'))

    def local(self, name):
        if not isinstance(name, str) or '\\' in name or not name or Path(name).is_absolute():
            raise ValueError(f'Expected repository-relative slash path: {name}')
        path = (self.root / name).resolve()
        if not path.is_relative_to(self.root) or '..' in Path(name).parts:
            raise ValueError(f'Path escapes repository: {name}')
        return path

    def groups(self, scope):
        return [g for g in self.manifest['programs'] if scope == 'all' or g['scope'] == scope]

    def schema(self):
        m = self.manifest
        if m['schemaVersion'] != 1:
            raise ValueError('Unsupported manifest schema')
        ids, destinations, assets = set(), set(), set()
        for g in m['programs']:
            if not re.fullmatch(r'[a-z0-9-]+', g['id']) or g['id'] in ids or not isinstance(g['module'], str) or not re.fullmatch(MODULE_NAME_PATTERN, g['module']) or g['scope'] not in ('package', 'fixture'):
                raise ValueError(f'Invalid/duplicate program or module: {g["id"]}')
            ids.add(g['id'])
            if g['unityProgramAsset'] in assets:
                raise ValueError('A Program Asset must have one manifest group')
            assets.add(g['unityProgramAsset'])
            for name in [g['programAsset'], g['unityProgramAsset'], g['unitySource'], *g['productionInputs']]:
                self.local(name)
            if not g['unitySource'].startswith('Assets/VirtualVenueBaseballComponents/Scripts/' + g['module'] + '/') or not g['unityProgramAsset'].startswith('Assets/VirtualVenueBaseballComponents/Scripts/TsukimiProgramAssets/'):
                raise ValueError('Unity source/Program Asset does not follow the canonical module layout')
            if not g['productionInputs'] or any(p.endswith('.Tests.cs') for p in g['productionInputs']):
                raise ValueError('Production inputs must exclude .Tests.cs')
            names = set()
            for o in g['outputs']:
                self.local(o['path'])
                export_name = o['exportName']
                if Path(export_name).name != export_name or not export_name.endswith('.cs'):
                    raise ValueError(f'Invalid exportName: {export_name}')
                if export_name in names:
                    raise ValueError(f'Duplicate raw output filename: {export_name}')
                names.add(export_name)
                prefix = ('Packages/org.vvbc.core/Runtime/' if g['scope'] == 'package'
                          else 'Tools/VVBC.Toolchain/Fixtures/Generated/') + g['module'] + '/'
                if not o['path'].startswith(prefix) or o['path'] in destinations:
                    raise ValueError(f'Unknown/duplicate output mapping: {o["path"]}')
                destinations.add(o['path'])
                if o['kind'] not in ('programEntry', 'supportSource'):
                    raise ValueError('Output kind must be programEntry or supportSource')
                if o['kind'] == 'programEntry' and not o.get('typeName'):
                    raise ValueError('Program entry requires typeName')
        return destinations

    def fingerprint(self):
        """Conservative whole-source closure; tests excluded, raw asset/config bytes retained."""
        m = self.manifest
        paths = set(m['configurationInputs'])
        # Tsukimi 0.10.1 collects global TsukimiShared pools as well as siblings.
        roots = list(m['productionRoots'])
        assets = self.local('Assets')
        if assets.exists():
            roots.extend(p.relative_to(self.root).as_posix() for p in assets.rglob('TsukimiShared') if p.is_dir())
        for root in roots:
            folder = self.local(root)
            for p in folder.rglob('*'):
                rel = p.relative_to(self.root).as_posix()
                if p.is_file() and (p.suffix in ('.asmdef', '.asmref') or
                                    (p.suffix == '.cs' and not p.name.endswith('.Tests.cs'))):
                    # Staged fixture copies are represented by the canonical fixture inputs.
                    if rel not in {x['staged'] for x in m['fixtureFiles']}:
                        paths.add(rel)
        for g in m['programs']:
            paths.update(g['productionInputs'])
            paths.add(g['programAsset'])
        values = {}
        for name in sorted(paths):
            p = self.local(name)
            values[name] = input_sha(p.read_bytes())
            if p.with_name(p.name + '.meta').exists():
                values[name + '.meta'] = input_sha(p.with_name(p.name + '.meta').read_bytes())
        context = json.loads(self.local(m['unityContext']).read_text(encoding='utf-8-sig'))
        if context['unityVersion'] != m['versions']['unity'] or context['worldsVersion'] != m['versions']['worlds'] or context['tsukimiVersion'] != m['versions']['tsukimi']:
            raise ValueError('Captured Unity context does not match pinned versions')
        if context.get('tsharpAutomaticallyReferenced'):
            raise ValueError('T# assembly is exposed through automatic references')
        bindings = {b['asset']: b for b in context.get('programBindings', [])}
        known = {g['unityProgramAsset'] for g in m['programs']}
        if set(bindings) != known:
            raise ValueError('Unity Program Asset set differs from manifest; capture context while all fixtures are staged')
        for g in m['programs']:
            if bindings[g['unityProgramAsset']]['source'] != g['unitySource']:
                raise ValueError('Program Asset/source mapping differs from captured Unity context')
            guid = bindings[g['unityProgramAsset']]['sourceGuid']
            input_guids = []
            for p in g['productionInputs']:
                meta = self.local(p + '.meta')
                if meta.exists():
                    found = re.search(r'^guid:\s*(\w+)', meta.read_text(), re.MULTILINE)
                    if not found:
                        raise ValueError(f'Invalid source meta GUID: {meta}')
                    input_guids.append(found.group(1))
            if guid not in input_guids or f'guid: {guid}' not in self.source_reference(self.local(g['programAsset'])):
                raise ValueError('Program Asset/source GUID does not match canonical .meta files')
        for mapping in m['fixtureFiles']:
            staged = self.local(mapping['staged'])
            if staged.exists() and staged.suffix == '.cs' and not staged.name.endswith('.Tests.cs'):
                if staged.read_bytes() != self.local(mapping['canonical']).read_bytes():
                    raise ValueError(f'Staged fixture source edited: {staged}')
        configuration = {k: v for k, v in m.items() if k not in ('managedOutputs', 'programs')}
        configuration['programs'] = [{k: v for k, v in g.items() if k != 'inputFingerprint'} for g in m['programs']]
        return sha(encoded({'files': values, 'context': context, 'mapping': configuration}))

    def check(self, scope='all'):
        if self.local(STATE + '/transaction.json').exists():
            raise ValueError('Interrupted sync requires recover before validation')
        destinations = self.schema()
        current = self.fingerprint()
        failures = []
        for g in self.groups(scope):
            changed_input = g.get('inputFingerprint') != current
            changed_output = [o['path'] for o in g['outputs'] if not self.local(o['path']).is_file()
                              or sha(self.local(o['path']).read_bytes()) != self.manifest['managedOutputs'].get(o['path'])]
            if changed_input:
                failures.append(f'{g["id"]}: ' + ('inputs and outputs changed; fresh Export comparison required' if changed_output else 'stale export: production inputs/configuration changed'))
            elif changed_output:
                failures.append(f'{g["id"]}: generated direct edit/missing output: {changed_output}')
        package = self.local('Packages/org.vvbc.core')
        metadata = json.loads((package / 'package.json').read_text(encoding='utf-8-sig'))
        if metadata.get('vpmDependencies', {}).get('com.vrchat.worlds') != self.manifest['versions']['worlds']:
            failures.append('Worlds dependency must equal the pinned version')
        for name in ('dependencies', 'vpmDependencies'):
            if any('tsukimi' in key.lower() for key in metadata.get(name, {})):
                failures.append('Tsukimi dependency in org.vvbc.core')
        for p in package.rglob('*.asmdef'):
            if 'tsukimi' in p.read_text(encoding='utf-8-sig').lower():
                failures.append(f'Tsukimi reference in {p}')
        for folder in (package / 'Runtime', self.local('Tools/VVBC.Toolchain/Fixtures/Generated')):
            for p in folder.rglob('*.cs'):
                name = p.relative_to(self.root).as_posix()
                if name not in destinations:
                    failures.append(f'Unmanaged generated/runtime source: {name}')
                if 'tsukimi' in p.read_text(encoding='utf-8-sig').lower():
                    failures.append(f'Tsukimi runtime symbol in {name}')
        expected = {o['path'] for g in self.manifest['programs'] for o in g['outputs']}
        if expected != set(self.manifest['managedOutputs']):
            failures.append('Stale/missing managed output mapping; synchronize full scopes')
        if failures:
            raise ValueError('\n'.join(failures))
        print(f'PASS manifest/hash/dependency validation ({len(expected)} outputs)')

    def prepare(self, scope):
        self.schema()
        run = Path(tempfile.mkdtemp(prefix='VVBC-TSharpExport-'))
        descriptor = {'runId': uuid.uuid4().hex, 'repository': str(self.root), 'scope': scope,
                      'inputFingerprint': self.fingerprint(), 'programs': []}
        for index, g in enumerate(self.groups(scope)):
            folder = run / str(index)
            folder.mkdir()
            descriptor['programs'].append({'id': g['id'], 'directory': str(folder),
                                            'outputs': g['outputs']})
            print(f'{g["id"]}: select {g["unitySource"]}; manual Export into {folder}')
            for output in g['outputs']:
                print(f'  expected raw file: {folder / output["exportName"]}')
        (run / 'run.json').write_bytes(encoded(descriptor))
        print(f'RUN={run / "run.json"}')

    @contextmanager
    def locked(self):
        state = self.local(STATE)
        state.mkdir(parents=True, exist_ok=True)
        lock = state / 'sync.lock'
        fd = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        os.close(fd)
        try:
            yield
        finally:
            lock.unlink(missing_ok=True)

    def transaction(self, updates):
        with self.locked():
            journal = self.local(STATE + '/transaction.json')
            if journal.exists():
                raise ValueError('An interrupted transaction needs recover before writing')
            originals = {}
            for name in updates:
                p = self.local(name)
                for candidate in (p, p.with_name(p.name + '.meta')):
                    key = candidate.relative_to(self.root).as_posix()
                    originals[key] = base64.b64encode(candidate.read_bytes()).decode() if candidate.exists() else None
            journal.write_bytes(encoded({'originals': originals, 'planned': {
                p: sha(data) if data is not None else None for p, data in updates.items()}}))
            try:
                for name, data in updates.items():
                    self.write(name, data)
            except BaseException:
                for name, value in originals.items():
                    self.write(name, base64.b64decode(value) if value is not None else None)
                journal.unlink()
                raise
            else:
                journal.unlink()

    def write(self, name, data):
        p = self.local(name)
        if data is None:
            p.unlink(missing_ok=True)
        else:
            p.parent.mkdir(parents=True, exist_ok=True)
            temporary = p.with_name(p.name + '.vvbcnew')
            try:
                temporary.write_bytes(data)
                os.replace(temporary, p)
            finally:
                temporary.unlink(missing_ok=True)

    def recover(self, confirm_no_writers=False):
        journal = self.local(STATE + '/transaction.json')
        if not journal.exists():
            if not confirm_no_writers:
                raise ValueError('No journal. Stop writers, then use recover --confirm-no-writers for an orphan lock')
            self.local(STATE + '/sync.lock').unlink(missing_ok=True)
            print('No file changes to recover; removed orphan lock')
            return
        info = json.loads(journal.read_text())
        for name, planned in info['planned'].items():
            p = self.local(name)
            actual = sha(p.read_bytes()) if p.exists() else None
            original = info['originals'][name]
            old = sha(base64.b64decode(original)) if original is not None else None
            if actual not in (old, planned):
                raise ValueError(f'Concurrent change; recovery preserved journal and {name}')
        for name, original in info['originals'].items():
            if name not in info['planned'] and original is not None:
                p = self.local(name)
                if not p.exists() or sha(p.read_bytes()) != sha(base64.b64decode(original)):
                    raise ValueError(f'Concurrent metadata change; recovery preserved journal and {name}')
        for name, value in info['originals'].items():
            self.write(name, base64.b64decode(value) if value is not None else None)
        journal.unlink()
        self.local(STATE + '/sync.lock').unlink(missing_ok=True)
        print('Recovered interrupted sync')

    def sync(self, run_path, confirmed, check_only=False):
        if not confirmed:
            raise ValueError('Manual Export completion must be explicitly confirmed')
        self.schema()
        run_path = Path(run_path).resolve()
        if run_path.is_relative_to(self.root):
            raise ValueError('Raw Export run must be outside the development project')
        run = json.loads(run_path.read_text(encoding='utf-8-sig'))
        if run['repository'] != str(self.root) or run['inputFingerprint'] != self.fingerprint():
            raise ValueError('Export run belongs to another repository or stale inputs/context')
        groups = self.groups(run['scope'])
        if [g['id'] for g in groups] != [g['id'] for g in run['programs']]:
            raise ValueError('Incomplete/unknown Export group set')
        updates, expected = {}, set()
        for g, entry in zip(groups, run['programs']):
            if entry['outputs'] != g['outputs']:
                raise ValueError('Export mapping changed since prepare')
            folder = Path(entry['directory']).resolve()
            if not folder.is_relative_to(run_path.parent):
                raise ValueError('Export directory escapes run')
            if {p.name for p in folder.iterdir()} != {o['exportName'] for o in g['outputs']}:
                raise ValueError(f'Missing or unknown raw outputs for {g["id"]}')
            for o in g['outputs']:
                raw = folder / o['exportName']
                if raw.is_symlink() or not raw.is_file() or raw.stat().st_size == 0:
                    raise ValueError(f'Invalid raw output: {raw}')
                data = raw.read_bytes()  # No header, normalization or newline conversion.
                destination = self.local(o['path'])
                old_hash = self.manifest['managedOutputs'].get(o['path'])
                if destination.exists() and sha(destination.read_bytes()) not in (old_hash, sha(data)):
                    raise ValueError(f'Direct edit or unmanaged destination: {o["path"]}')
                if check_only and (not destination.exists() or destination.read_bytes() != data):
                    raise ValueError(f'Fresh Export differs: {o["path"]}')
                updates[o['path']] = data
                expected.add(o['path'])
            g['inputFingerprint'] = run['inputFingerprint']
        prefixes = ('Packages/org.vvbc.core/Runtime/',) if run['scope'] == 'package' else (
            'Tools/VVBC.Toolchain/Fixtures/Generated/',) if run['scope'] == 'fixture' else (
                'Packages/org.vvbc.core/Runtime/', 'Tools/VVBC.Toolchain/Fixtures/Generated/')
        for old, digest in list(self.manifest['managedOutputs'].items()):
            if old.startswith(prefixes) and old not in expected:
                p = self.local(old)
                if not p.exists() or sha(p.read_bytes()) != digest:
                    raise ValueError(f'Edited stale output; refusing deletion: {old}')
                if check_only:
                    raise ValueError(f'Stale generated output: {old}')
                updates[old] = None
                updates[old + '.meta'] = None
                del self.manifest['managedOutputs'][old]
        for name in expected:
            self.manifest['managedOutputs'][name] = sha(updates[name])
        if not check_only:
            updates[MANIFEST] = encoded(self.manifest)
            self.transaction(updates)
        print(f'PASS fresh raw Export comparison ({len(expected)} outputs)' + ('; synchronized' if not check_only else ''))

    def fixture(self, stage):
        files = self.manifest['fixtureFiles']
        if stage:
            updates = {}
            for mapping in files:
                for suffix in ('', '.meta'):
                    src, dest = self.local(mapping['canonical'] + suffix), self.local(mapping['staged'] + suffix)
                    if dest.exists() and dest.read_bytes() != src.read_bytes():
                        if not suffix and dest.suffix == '.asset' and self.source_reference(dest) == self.source_reference(src):
                            continue  # Keep Unity's derived cache; do not rewrite the Program Asset.
                        raise ValueError(f'Staging would overwrite work: {dest}')
                    updates[mapping['staged'] + suffix] = src.read_bytes()
            self.transaction(updates)
            print('Fixture copies staged; canonical sources remain under Tools')
        else:
            # Validate every file before deleting any. Compiled Program Asset cache may change.
            updates = {}
            for mapping in files:
                for suffix in ('', '.meta'):
                    src, dest = self.local(mapping['canonical'] + suffix), self.local(mapping['staged'] + suffix)
                    if not dest.exists():
                        continue
                    if dest.read_bytes() != src.read_bytes():
                        if not (not suffix and dest.suffix == '.asset' and
                                self.source_reference(dest) == self.source_reference(src)):
                            raise ValueError(f'Staged file edited; preserve and reconcile first: {dest}')
                    updates[mapping['staged'] + suffix] = None
            self.transaction(updates)
            # Staging creates temporary Unity directories, not architecture reservations.
            # rmdir only succeeds for an empty directory; preserve production contents.
            for folder in {self.local(m['staged']).parent for m in files}:
                if folder.exists() and not any(folder.iterdir()):
                    folder.rmdir()
                    try:
                        self.transaction({folder.relative_to(self.root).as_posix() + '.meta': None})
                    except BaseException:
                        folder.mkdir(exist_ok=True)
                        raise
            print('Fixture copies removed; canonical sources and GUIDs retained')

    @staticmethod
    def source_reference(path):
        return next(line.strip() for line in path.read_text(encoding='utf-8-sig').splitlines()
                    if line.strip().startswith('sourceCsScript:'))

    def consumer_prepare(self, project, scope):
        self.check(scope)
        project = Path(project).resolve()
        if project.is_relative_to(self.root) or project == self.root:
            raise ValueError('Consumer project must be outside the development repository')
        assets = project / 'Assets/__VVBCValidation'
        if assets.exists():
            raise ValueError('Consumer project already has a validation run')
        package = project / 'Packages/org.vvbc.core'
        if package.exists():
            raise ValueError('Refusing to replace an existing consumer core package')
        shutil.copytree(self.local('Packages/org.vvbc.core'), package)
        editor = assets / 'Editor'
        editor.mkdir(parents=True)
        for name in ('VVBC.ToolchainValidation.cs', 'VVBC.Toolchain.Editor.asmdef'):
            shutil.copy2(self.local('Assets/VirtualVenueBaseballComponents/Editor/' + name), editor / name)
        entries = []
        for g in self.groups(scope):
            for index, output in enumerate(g['outputs']):
                path = output['path']
                if g['scope'] == 'fixture':
                    relative = f'Assets/__VVBCFixture/{g["id"]}/{output["exportName"]}'
                    dest = project / relative
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(self.local(path), dest)
                    path = relative
                if output['kind'] == 'programEntry':
                    entries.append({'path': path, 'typeName': output['typeName']})
        request = {'requireClean': True, 'unityVersion': self.manifest['versions']['unity'],
                   'worldsVersion': self.manifest['versions']['worlds'], 'scope': scope, 'entries': entries}
        (assets / 'request.json').write_bytes(encoded(request))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=('check', 'prepare', 'sync', 'compare', 'recover', 'fixture-stage', 'fixture-clean', 'consumer-prepare'))
    parser.add_argument('--scope', choices=('fixture', 'package', 'all'), default='all')
    parser.add_argument('--run', type=Path)
    parser.add_argument('--project', type=Path)
    parser.add_argument('--confirm-manual-export', action='store_true')
    parser.add_argument('--confirm-no-writers', action='store_true')
    args = parser.parse_args()
    try:
        tool = Toolchain()
        if args.command == 'check': tool.check(args.scope)
        elif args.command == 'prepare': tool.prepare(args.scope)
        elif args.command in ('sync', 'compare'):
            if not args.run: parser.error('--run is required')
            tool.sync(args.run, args.confirm_manual_export, args.command == 'compare')
        elif args.command == 'recover': tool.recover(args.confirm_no_writers)
        elif args.command.startswith('fixture-'): tool.fixture(args.command == 'fixture-stage')
        elif args.command == 'consumer-prepare':
            if not args.project: parser.error('--project is required')
            tool.consumer_prepare(args.project, args.scope)
    except (ValueError, OSError, KeyError, StopIteration) as error:
        print(f'FAIL: {error}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
