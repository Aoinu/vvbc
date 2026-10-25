import base64
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('vvbc_toolchain', Path(__file__).parents[1] / 'toolchain.py')
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class ExportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.raw = tempfile.TemporaryDirectory(prefix='VVBC-export-test-')
        self.addCleanup(self.temp.cleanup)
        self.addCleanup(self.raw.cleanup)
        self.root = Path(self.temp.name)
        self.output = 'Tools/VVBC.Toolchain/Fixtures/Generated/Toolchain/Example.cs'
        self.asset = 'Inputs/Example.asset'
        self.source = 'Inputs/Example.cs'
        self.write(self.asset, b'sourceCsScript: {fileID: 11500000, guid: abc123}\n')
        self.write(self.source, b'production source\n')
        self.write(self.source + '.meta', b'guid: abc123\n')
        self.write('Inputs/Example.Tests.cs', b'test source\n')
        self.write('Inputs/config.asmdef', b'{"name":"TSharp"}\n')
        self.write('Packages/org.vvbc.core/package.json', MODULE.encoded({'vpmDependencies': {'com.vrchat.worlds': '3.10.5'}}))
        context = {'unityVersion': '2022.3.22f1', 'worldsVersion': '3.10.5', 'tsukimiVersion': '0.10.1',
                   'defines': ['UNITY_EDITOR'], 'programBindings': [{'asset': 'Assets/VirtualVenueBaseballComponents/Scripts/TsukimiProgramAssets/Example.asset', 'source': 'Assets/VirtualVenueBaseballComponents/Scripts/Toolchain/Example.cs', 'sourceGuid': 'abc123'}]}
        self.write('context.json', MODULE.encoded(context))
        self.manifest = {'schemaVersion': 1, 'versions': {'unity': '2022.3.22f1', 'worlds': '3.10.5', 'tsukimi': '0.10.1'},
                         'unityContext': 'context.json', 'productionRoots': ['Inputs'], 'configurationInputs': ['Inputs/config.asmdef'],
                         'fixtureFiles': [], 'managedOutputs': {}, 'programs': [self.program()]}
        self.save()

    def program(self):
        return {'id': 'example', 'scope': 'fixture', 'module': 'Toolchain', 'programAsset': self.asset,
                'unityProgramAsset': 'Assets/VirtualVenueBaseballComponents/Scripts/TsukimiProgramAssets/Example.asset', 'unitySource': 'Assets/VirtualVenueBaseballComponents/Scripts/Toolchain/Example.cs', 'productionInputs': [self.source],
                'inputFingerprint': '', 'outputs': [{'path': self.output, 'exportName': 'Example.cs',
                                                    'kind': 'programEntry', 'typeName': 'Example'}]}

    def write(self, name, data):
        p = self.root / name
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(data)

    def save(self):
        self.write(MODULE.MANIFEST, MODULE.encoded(self.manifest))

    def tool(self):
        return MODULE.Toolchain(self.root)

    def run_file(self, tool, contents=b'raw U#\r\n', scope='fixture'):
        folder = Path(self.raw.name)
        export = folder / '0'
        export.mkdir(exist_ok=True)
        entries = []
        for g in tool.groups(scope):
            for o in g['outputs']:
                (export / o['exportName']).write_bytes(contents)
            entries.append({'id': g['id'], 'directory': str(export), 'outputs': g['outputs']})
        run = {'repository': str(self.root.resolve()), 'scope': scope, 'inputFingerprint': tool.fingerprint(), 'programs': entries}
        path = folder / 'run.json'
        path.write_bytes(MODULE.encoded(run))
        return path

    def baseline(self):
        tool = self.tool()
        tool.sync(self.run_file(tool), True)
        return self.tool()

    def test_raw_bytes_and_meta_are_preserved(self):
        self.write(self.output + '.meta', b'guid: stable\n')
        tool = self.baseline()
        self.assertEqual((self.root / self.output).read_bytes(), b'raw U#\r\n')
        self.assertEqual((self.root / (self.output + '.meta')).read_bytes(), b'guid: stable\n')
        tool.check()
        tool.sync(self.run_file(tool), True, check_only=True)

    def test_direct_edit_is_rejected_and_never_overwritten(self):
        self.baseline()
        self.write(self.output, b'manual edit')
        tool = self.tool()
        with self.assertRaisesRegex(ValueError, 'direct edit'):
            tool.check()
        with self.assertRaisesRegex(ValueError, 'Direct edit'):
            tool.sync(self.run_file(tool), True)
        self.assertEqual((self.root / self.output).read_bytes(), b'manual edit')

    def test_package_scope_sync_uses_declared_module_destination(self):
        output = 'Packages/org.vvbc.core/Runtime/Toolchain/Example.cs'
        self.manifest['programs'][0]['scope'] = 'package'
        self.manifest['programs'][0]['outputs'][0]['path'] = output
        self.save()
        tool = self.tool()
        tool.sync(self.run_file(tool, scope='package'), True)
        self.tool().check()
        self.assertEqual((self.root / output).read_bytes(), b'raw U#\r\n')

    def test_tests_excluded_but_production_asset_defines_and_config_included(self):
        before = self.baseline().fingerprint()
        self.write('Inputs/Example.Tests.cs', b'changed assertions')
        self.assertEqual(before, self.tool().fingerprint())
        for name in (self.source, self.asset, 'Inputs/config.asmdef', 'context.json'):
            original = (self.root / name).read_bytes()
            if name == 'context.json':
                context = json.loads(original)
                context['defines'].append('OTHER_TARGET')
                self.write(name, MODULE.encoded(context))
            else:
                self.write(name, original + b'\n')
            self.assertNotEqual(before, self.tool().fingerprint(), name)
            self.write(name, original)

    def test_stale_source_rejected(self):
        self.baseline()
        self.write(self.source, b'new production')
        with self.assertRaisesRegex(ValueError, 'stale export'):
            self.tool().check()

    def test_input_line_endings_are_portable_but_raw_output_is_exact(self):
        before = self.tool().fingerprint()
        for p in (self.root / 'Inputs').iterdir():
            p.write_bytes(p.read_bytes().replace(b'\n', b'\r\n'))
        self.assertEqual(before, self.tool().fingerprint())

    def test_global_shared_pool_affects_fingerprint(self):
        before = self.tool().fingerprint()
        self.write('Assets/Shared/TsukimiShared/Helper.cs', b'helper')
        self.assertNotEqual(before, self.tool().fingerprint())

    def test_unknown_output_and_stale_run_rejected_before_any_write(self):
        tool = self.tool()
        run = self.run_file(tool)
        (Path(self.raw.name) / '0/Unexpected.cs').write_bytes(b'extra')
        with self.assertRaisesRegex(ValueError, 'unknown raw outputs'):
            tool.sync(run, True)
        self.assertFalse((self.root / self.output).exists())
        (Path(self.raw.name) / '0/Unexpected.cs').unlink()
        self.write(self.source, b'source edited after Export')
        with self.assertRaisesRegex(ValueError, 'stale inputs'):
            tool.sync(run, True)

    def test_multiple_outputs_support_source_and_zero_outputs(self):
        support = 'Tools/VVBC.Toolchain/Fixtures/Generated/Toolchain/Base.cs'
        self.manifest['programs'][0]['outputs'].append({'path': support, 'exportName': 'Base.cs', 'kind': 'supportSource'})
        self.save()
        self.baseline().check()
        self.manifest = self.tool().manifest
        self.manifest['programs'][0]['outputs'] = []
        self.save()
        for p in (Path(self.raw.name) / '0').iterdir(): p.unlink()
        tool = self.tool()
        tool.sync(self.run_file(tool), True)
        self.assertFalse((self.root / support).exists())
        self.assertFalse((self.root / self.output).exists())
        self.tool().check()

    def test_transaction_rolls_back_manifest_output_and_meta_on_write_failure(self):
        tool = self.baseline()
        old_manifest = (self.root / MODULE.MANIFEST).read_bytes()
        self.write(self.output + '.meta', b'guid: stable')
        self.write(self.source, b'new production')
        tool = self.tool()
        run = self.run_file(tool, b'new generated')
        actual_replace = MODULE.os.replace
        failed = [False]
        def failing_replace(src, dst):
            if str(dst).endswith('tsharp-exports.json') and not failed[0]:
                failed[0] = True
                raise OSError('injected write failure')
            actual_replace(src, dst)
        with patch.object(MODULE.os, 'replace', side_effect=failing_replace):
            with self.assertRaisesRegex(OSError, 'injected'):
                tool.sync(run, True)
        self.assertEqual((self.root / self.output).read_bytes(), b'raw U#\r\n')
        self.assertEqual((self.root / (self.output + '.meta')).read_bytes(), b'guid: stable')
        self.assertEqual((self.root / MODULE.MANIFEST).read_bytes(), old_manifest)
        self.assertFalse(list(self.root.rglob('*.vvbcnew')))

    def test_path_escape_and_invalid_module_name_rejected(self):
        with self.assertRaises(ValueError): self.tool().local('../outside.cs')
        for name in ('', '.', '..', '../Outside', 'With/Slash', 'With\\Slash', '9StartsWithDigit',
                     'With Space', 'bad-name', 'bad_name', 'Toolchain\n', None, 42):
            with self.subTest(module=name):
                self.manifest['programs'][0]['module'] = name
                self.save()
                with self.assertRaisesRegex(ValueError, 'module'): self.tool().schema()

    def test_new_module_names_allowed_when_mapping_is_consistent(self):
        for name in ('Toolchain', 'CustomFeature42', 'Unknown', 'Z'):
            with self.subTest(module=name):
                group = self.program()
                group['module'] = name
                group['unitySource'] = group['unitySource'].replace('/Toolchain/', '/' + name + '/')
                group['outputs'][0]['path'] = group['outputs'][0]['path'].replace('/Toolchain/', '/' + name + '/')
                self.manifest['programs'] = [group]
                self.save()
                self.tool().schema()

    def test_schema_and_python_module_name_patterns_match(self):
        schema = json.loads((Path(__file__).parents[1] / 'tsharp-exports.schema.json').read_text())
        self.assertEqual(schema['$defs']['program']['properties']['module'],
                         {'type': 'string', 'pattern': MODULE.MODULE_NAME_PATTERN})

    def staged_fixture(self):
        group = self.program()
        self.write(self.asset + '.meta', b'guid: asset-guid\n')
        self.write('Inputs/Example.Tests.cs.meta', b'guid: test-guid\n')
        self.manifest['fixtureFiles'] = [
            {'canonical': self.source, 'staged': group['unitySource']},
            {'canonical': 'Inputs/Example.Tests.cs', 'staged': group['unitySource'].replace('.cs', '.Tests.cs')},
            {'canonical': self.asset, 'staged': group['unityProgramAsset']}]
        self.save()
        tool = self.tool()
        tool.fixture(True)
        folder = (self.root / group['unitySource']).parent
        self.write(folder.relative_to(self.root).as_posix() + '.meta', b'guid: module-folder\n')
        return tool, folder

    def test_fixture_cleanup_removes_empty_stage_directories_and_folder_metas(self):
        tool, folder = self.staged_fixture()
        tool.fixture(False)
        self.assertFalse(folder.exists())
        self.assertFalse(folder.with_name(folder.name + '.meta').exists())
        self.assertFalse((self.root / self.program()['unityProgramAsset']).parent.exists())
        self.assertTrue((self.root / self.source).exists())
        self.assertTrue((self.root / self.asset).exists())

    def test_fixture_cleanup_preserves_other_module_contents(self):
        tool, folder = self.staged_fixture()
        (folder / 'RealSource.cs').write_bytes(b'other production work')
        tool.fixture(False)
        self.assertEqual((folder / 'RealSource.cs').read_bytes(), b'other production work')
        self.assertTrue(folder.with_name(folder.name + '.meta').exists())
        self.assertFalse((folder / 'Example.cs').exists())

    def test_recovery_refuses_concurrent_edits(self):
        self.baseline()
        journal = {'originals': {self.output: base64.b64encode(b'old').decode()}, 'planned': {self.output: MODULE.sha(b'new')}}
        self.write(MODULE.STATE + '/transaction.json', MODULE.encoded(journal))
        self.write(self.output, b'concurrent work')
        with self.assertRaisesRegex(ValueError, 'Concurrent change'): self.tool().recover()
        self.assertEqual((self.root / self.output).read_bytes(), b'concurrent work')

    def test_crash_recovery_restores_original_output(self):
        self.baseline()
        journal = {'originals': {self.output: base64.b64encode(b'previous raw').decode()}, 'planned': {self.output: MODULE.sha(b'interrupted new')}}
        self.write(MODULE.STATE + '/transaction.json', MODULE.encoded(journal))
        self.write(MODULE.STATE + '/sync.lock', b'')
        self.write(self.output, b'interrupted new')
        self.tool().recover()
        self.assertEqual((self.root / self.output).read_bytes(), b'previous raw')
        self.assertFalse((self.root / (MODULE.STATE + '/sync.lock')).exists())

    def test_source_meta_mismatch_and_tsukimi_package_dependency_fail(self):
        self.baseline()
        self.write(self.source + '.meta', b'guid: other\n')
        with self.assertRaisesRegex(ValueError, 'GUID'): self.tool().check()
        self.write(self.source + '.meta', b'guid: abc123\n')
        self.write('Packages/org.vvbc.core/package.json', MODULE.encoded({'vpmDependencies': {'com.vrchat.worlds': '3.10.5', 'com.tsukimicode.udon-tools': '0.10.1'}}))
        with self.assertRaisesRegex(ValueError, 'Tsukimi dependency'): self.tool().check()


if __name__ == '__main__':
    unittest.main()
