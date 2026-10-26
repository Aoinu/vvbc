"""Static T# package validation and isolated consumer preparation (no compiler copying)."""
import argparse
import json
from pathlib import Path
import re
import shutil
import sys

ROOT = Path(__file__).resolve().parents[2]
CORE = 'Packages/org.vvbc.core'
RUNTIME = CORE + '/Runtime'
FIXTURE = 'Tools/VVBC.Toolchain/Fixtures'
TSUKIMI = 'com.tsukimicode.udon-tools'
REFERENCES = {'TsukimiCode.UdonTools.Runtime', 'UdonSharp.Runtime', 'VRC.Udon', 'Unity.TextMeshPro'}


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')


class Toolchain:
    def __init__(self, root=ROOT):
        self.root = Path(root).resolve()

    @staticmethod
    def source_reference(path):
        return next(line.strip() for line in path.read_text(encoding='utf-8-sig').splitlines()
                    if line.strip().startswith('sourceCsScript:'))

    def check(self):
        metadata = read_json(self.root / CORE / 'package.json')
        if metadata['name'] != 'org.vvbc.core' or metadata['vpmDependencies'] != {
                'com.vrchat.worlds': '3.10.5', TSUKIMI: '0.10.1'}:
            raise ValueError('Core must declare exactly the verified Worlds/Tsukimi dependencies')
        if metadata.get('dependencies'):
            raise ValueError('Core dependencies are resolved through its reviewed VPM declarations')
        asmdef = read_json(self.root / RUNTIME / 'VVBC.Runtime.asmdef')
        if asmdef['name'] != 'VVBC.Runtime' or set(asmdef['references']) != REFERENCES:
            raise ValueError('Runtime assembly must use the installed Tsukimi template references')
        if list((self.root / RUNTIME).rglob('*.UdonSharpAssembly.asset')):
            raise ValueError('T# runtime must not be registered as UdonSharp input')
        if (self.root / RUNTIME / '__ToolchainFixture').exists():
            raise ValueError('Temporary fixture deployment must not enter the production snapshot')
        scripts = self.root / 'Assets/VirtualVenueBaseballComponents/Scripts'
        if scripts.exists() and list(scripts.rglob('*.cs')):
            raise ValueError('Runtime sources belong in core, not development Assets/Scripts')
        for path in (self.root / CORE).rglob('*'):
            if not path.is_file() or path.suffix not in ('.cs', '.asmdef', '.asmref', '.json', '.asset'):
                continue
            value = path.read_text(encoding='utf-8-sig')
            if re.search(r'UnityMCP|Unity\.MCP|VirtualVenueBaseballComponents|VVBC\.Toolchain|Assets/|jp\.shiranui-isuzu', value, re.I):
                raise ValueError(f'Development-only reference in package: {path}')
            if path.suffix == '.cs' and re.search(r':\s*(?:global::)?(?:UdonSharp\.)?UdonSharpBehaviour', value):
                raise ValueError(f'UdonSharp implementation in T# package: {path}')
        source = self.root / FIXTURE / 'Source/Toolchain/ToolchainFixtureBehaviour.cs'
        asset = self.root / FIXTURE / 'TsukimiProgramAssets/ToolchainFixtureBehaviour.asset'
        guid = re.search(r'^guid: ([a-f0-9]{32})$', Path(str(source) + '.meta').read_text(), re.M).group(1)
        if f'guid: {guid}' not in self.source_reference(asset):
            raise ValueError('Fixture Program Asset/source GUID mismatch')
        return metadata

    def fixture_files(self):
        for source in sorted((self.root / FIXTURE / 'Source/Toolchain').glob('*')):
            yield source, Path(RUNTIME) / '__ToolchainFixture' / source.name
        for source in sorted((self.root / FIXTURE / 'TsukimiProgramAssets').glob('*')):
            yield source, Path(RUNTIME) / 'TsukimiProgramAssets' / source.name

    def fixture(self, stage):
        # Validate every destination first; preserve edits. Unity owns compiled caches.
        files = list(self.fixture_files())
        for source, relative in files:
            destination = self.root / relative
            if destination.exists() and destination.read_bytes() != source.read_bytes():
                if destination.suffix == '.asset' and self.source_reference(destination) == self.source_reference(source):
                    continue
                raise ValueError(f'Fixture destination edited; reconcile first: {relative}')
        for source, relative in files:
            destination = self.root / relative
            if stage:
                destination.parent.mkdir(parents=True, exist_ok=True)
                if not destination.exists():
                    shutil.copyfile(source, destination)
            else:
                destination.unlink(missing_ok=True)
        if not stage:
            for relative in {relative.parent for _, relative in files}:
                folder = self.root / relative
                if folder.exists() and not any(folder.iterdir()):
                    folder.rmdir()
                    Path(str(folder) + '.meta').unlink(missing_ok=True)

    def consumer_prepare(self, project, scope, tsukimi_package):
        self.check()
        project = Path(project).resolve()
        compiler = Path(tsukimi_package).resolve()
        if project.is_relative_to(self.root):
            raise ValueError('Consumer must be outside the development repository')
        if not (project / 'ProjectSettings/ProjectVersion.txt').is_file():
            raise ValueError('Consumer must be an initialized Unity project')
        installed = read_json(compiler / 'package.json')
        if installed['name'] != TSUKIMI or installed['version'] != '0.10.1':
            raise ValueError('Installed Tsukimi metadata does not match core dependency')
        package = project / CORE
        validation = project / 'Assets/__VVBCValidation'
        if package.exists() or validation.exists():
            raise ValueError('Refusing to overwrite a previous consumer validation')
        if (self.root / RUNTIME / '__ToolchainFixture').exists():
            raise ValueError('Clean local fixture deployment before snapshotting production core')
        shutil.copytree(self.root / CORE, package)
        if scope == 'fixture':
            for source, relative in self.fixture_files():
                target = project / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(source, target)
        manifest_path = project / 'Packages/manifest.json'
        manifest = read_json(manifest_path)
        manifest.setdefault('dependencies', {})[TSUKIMI] = 'file:' + compiler.as_posix()
        write_json(manifest_path, manifest)
        editor = validation / 'Editor'
        editor.mkdir(parents=True)
        for name in ('VVBC.ToolchainValidation.cs', 'VVBC.Toolchain.Editor.asmdef'):
            shutil.copyfile(self.root / 'Assets/VirtualVenueBaseballComponents/Editor' / name, editor / name)
        write_json(validation / 'request.json', {'requireClean': True, 'scope': scope})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=('check', 'fixture-stage', 'fixture-clean', 'consumer-prepare'))
    parser.add_argument('--scope', choices=('package', 'fixture'), default='package')
    parser.add_argument('--project', type=Path)
    parser.add_argument('--tsukimi-package', type=Path)
    args = parser.parse_args()
    try:
        tool = Toolchain()
        if args.command == 'check':
            tool.check()
        elif args.command.startswith('fixture-'):
            tool.fixture(args.command == 'fixture-stage')
        else:
            if not args.project or not args.tsukimi_package:
                parser.error('--project and --tsukimi-package are required')
            tool.consumer_prepare(args.project, args.scope, args.tsukimi_package)
        print('PASS ' + args.command)
    except (ValueError, OSError, KeyError, StopIteration, AttributeError) as error:
        print(f'FAIL: {error}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
