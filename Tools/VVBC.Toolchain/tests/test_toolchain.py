import importlib.util
from pathlib import Path
import shutil
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location('vvbc_toolchain', Path(__file__).parents[1] / 'toolchain.py')
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class DirectPackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / 'repo'
        self.root.mkdir()
        for relative in (MODULE.CORE, MODULE.FIXTURE):
            shutil.copytree(MODULE.ROOT / relative, self.root / relative)
        self.tool = MODULE.Toolchain(self.root)
        self.compiler = Path(self.temp.name) / 'compiler'
        MODULE.write_json(self.compiler / 'package.json', {'name': MODULE.TSUKIMI, 'version': '0.10.1'})
        self.project = Path(self.temp.name) / 'consumer'
        self.write(self.project / 'ProjectSettings/ProjectVersion.txt', 'm_EditorVersion: 2022.3.22f1')
        MODULE.write_json(self.project / 'Packages/manifest.json', {'dependencies': {}})
        editor = 'Assets/VirtualVenueBaseballComponents/Editor'
        shutil.copytree(MODULE.ROOT / editor, self.root / editor)

    def write(self, path, text):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding='utf-8')

    def test_declared_dependencies(self):
        self.assertEqual(self.tool.check()['vpmDependencies'], {
            MODULE.TSUKIMI: '0.10.1', 'com.vrchat.worlds': '3.10.5'})

    def test_missing_or_wrong_compiler_dependency(self):
        path = self.root / MODULE.CORE / 'package.json'
        for version in (None, '0.10.0'):
            data = MODULE.read_json(path)
            data['vpmDependencies'][MODULE.TSUKIMI] = version
            MODULE.write_json(path, data)
            with self.assertRaises(ValueError): self.tool.check()

    def test_extra_development_dependency(self):
        path = self.root / MODULE.CORE / 'package.json'
        data = MODULE.read_json(path)
        data['vpmDependencies']['jp.shiranui-isuzu.unity-mcp'] = '4.4.2'
        MODULE.write_json(path, data)
        with self.assertRaises(ValueError): self.tool.check()

    def test_upm_development_dependency_is_also_rejected(self):
        path = self.root / MODULE.CORE / 'package.json'
        data = MODULE.read_json(path)
        data['dependencies'] = {'development-only': 'file:../../Assets'}
        MODULE.write_json(path, data)
        with self.assertRaises(ValueError): self.tool.check()

    def test_forbidden_source_and_assembly_references(self):
        for name, source in [('Hidden.cs', 'using UnityMCP;'), ('Hidden.asmdef', '{"references":["VVBC.Toolchain.Editor"]}')]:
            path = self.root / MODULE.RUNTIME / name
            self.write(path, source)
            with self.assertRaises(ValueError): self.tool.check()
            path.unlink()

    def test_duplicate_development_source(self):
        self.write(self.root / 'Assets/VirtualVenueBaseballComponents/Scripts/Example.cs', 'class Example {}')
        with self.assertRaises(ValueError): self.tool.check()

    def test_generated_udonsharp_rejected(self):
        self.write(self.root / MODULE.RUNTIME / 'Example.cs', 'class Example : global::UdonSharp.UdonSharpBehaviour {}')
        with self.assertRaises(ValueError): self.tool.check()

    def test_fixture_binding_rejected(self):
        asset = self.root / MODULE.FIXTURE / 'TsukimiProgramAssets/ToolchainFixtureBehaviour.asset'
        self.write(asset, 'sourceCsScript: {guid: wrong}')
        with self.assertRaises(ValueError): self.tool.check()

    def test_fixture_roundtrip_preserves_guids_and_production_files(self):
        self.tool.fixture(True)
        for source, relative in self.tool.fixture_files():
            self.assertEqual(source.read_bytes(), (self.root / relative).read_bytes())
        self.write(self.root / MODULE.RUNTIME / 'TsukimiProgramAssets/Production.asset', 'production')
        with self.assertRaises(ValueError): self.tool.check()  # fixture cannot ship
        self.tool.fixture(False)
        self.assertFalse((self.root / MODULE.RUNTIME / '__ToolchainFixture').exists())
        self.assertTrue((self.root / MODULE.RUNTIME / 'TsukimiProgramAssets/Production.asset').exists())

    def test_edited_fixture_blocks_cleanup_before_any_deletion(self):
        self.tool.fixture(True)
        files = list(self.tool.fixture_files())
        self.write(self.root / files[-1][1], 'edited')
        with self.assertRaises(ValueError): self.tool.fixture(False)
        for _, relative in files: self.assertTrue((self.root / relative).exists())

    def test_compiled_cache_allowed_but_source_rebinding_rejected(self):
        self.tool.fixture(True)
        asset = self.root / MODULE.RUNTIME / 'TsukimiProgramAssets/ToolchainFixtureBehaviour.asset'
        self.write(asset, asset.read_text() + '\ncache: changed\n')
        self.tool.fixture(False)
        self.tool.fixture(True)
        self.write(asset, 'sourceCsScript: {guid: wrong}')
        with self.assertRaises(ValueError): self.tool.fixture(False)

    def test_consumer_package_has_no_fixture_or_development_assets(self):
        self.tool.consumer_prepare(self.project, 'package', self.compiler)
        self.assertFalse((self.project / MODULE.RUNTIME / '__ToolchainFixture').exists())
        self.assertFalse((self.project / 'Assets/VirtualVenueBaseballComponents').exists())
        manifest = MODULE.read_json(self.project / 'Packages/manifest.json')
        self.assertEqual(manifest['dependencies'][MODULE.TSUKIMI], 'file:' + self.compiler.as_posix())
        self.assertFalse((self.project / 'Packages' / MODULE.TSUKIMI).exists())
        with self.assertRaises(ValueError): self.tool.consumer_prepare(self.project, 'package', self.compiler)

    def test_fixture_consumer_compiles_package_sources(self):
        self.tool.consumer_prepare(self.project, 'fixture', self.compiler)
        for source, relative in self.tool.fixture_files():
            self.assertEqual(source.read_bytes(), (self.project / relative).read_bytes())
        request = MODULE.read_json(self.project / 'Assets/__VVBCValidation/request.json')
        self.assertTrue(request['requireClean'])
        self.assertEqual(request['scope'], 'fixture')

    def test_wrong_installed_compiler_is_rejected_before_copy(self):
        MODULE.write_json(self.compiler / 'package.json', {'name': MODULE.TSUKIMI, 'version': '0.9.0'})
        with self.assertRaises(ValueError): self.tool.consumer_prepare(self.project, 'package', self.compiler)
        self.assertFalse((self.project / MODULE.CORE).exists())

    def test_consumer_cannot_be_inside_repository(self):
        with self.assertRaises(ValueError): self.tool.consumer_prepare(self.root / 'consumer', 'package', self.compiler)

    def test_consumer_cannot_capture_temporary_fixture(self):
        self.tool.fixture(True)
        with self.assertRaises(ValueError): self.tool.consumer_prepare(self.project, 'fixture', self.compiler)


if __name__ == '__main__':
    unittest.main()
