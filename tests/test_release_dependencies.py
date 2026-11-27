"""Positive/negative tests for archive dependency closure and offline installation."""
import importlib.util
import json
import os
import posixpath
from pathlib import Path
import shutil
import re
import subprocess
import tempfile
import unittest
from zipfile import ZipFile

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('client_release', ROOT / 'tools/package_client_release.py')
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)
POWERSHELL = shutil.which('powershell') or str(Path(os.environ.get('SystemRoot', 'C:/Windows')) / 'System32/WindowsPowerShell/v1.0/powershell.exe')

class ReleaseDependencies(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.output = Path(os.environ.get('CM_RELEASE_DIR') or release.DEFAULT_OUTPUT)
        cls.language = os.environ.get('CM_PACKAGE_LANGUAGE', 'zh-CN')
        cls.paths = release.CONTENT.layout(cls.language)
        edition = '中文版' if cls.language == 'zh-CN' else 'International'
        cls.mono = next(cls.output.glob('*Mono-SPT4x-' + edition + '.zip'))
        cls.il2cpp = next(cls.output.glob('*IL2CPP-SPT5.0be-' + edition + '.zip'))
        cls.temp = tempfile.TemporaryDirectory(prefix='cm-dependency-tests-')
        cls.scratch = Path(cls.temp.name)
    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()
    def mutate(self, original, change):
        target = self.scratch / (self._testMethodName + '.zip')
        with ZipFile(original) as src:
            contents = {name: src.read(name) for name in src.namelist()}
        change(contents)
        with ZipFile(target, 'w') as dst:
            for name, data in contents.items():
                dst.writestr(name, data)
        return target
    def test_both_packages_pass(self):
        self.assertEqual(release.inspect_client_package(self.mono)['runtime'], release.RUNTIMES[0])
        self.assertEqual(release.inspect_client_package(self.il2cpp)['runtime'], release.RUNTIMES[1])
    def test_localized_layout_and_all_links(self):
        for path in (self.mono, self.il2cpp):
            with ZipFile(path) as archive:
                names = set(archive.namelist())
                expected = {'BepInEx', '说明与工具', '安装说明.txt'} if self.language == 'zh-CN' else {'BepInEx', 'Extras', 'README.txt'}
                manifest = json.loads(archive.read(self.paths['manifest']))
                version = manifest['packageLayoutVersion']
                web_root, _ = release.CONTENT.web_payload(version)
                if 'webWorkbench' in manifest:
                    expected.add(web_root.rstrip('/'))
                self.assertEqual({name.split('/')[0] for name in names}, expected)
                if self.language == 'en': self.assertTrue(all(name.isascii() for name in names))
                if version >= release.CONTENT.COMPACT_LAYOUT_VERSION:
                    readme = archive.read(self.paths['readme']).decode('utf-8-sig')
                    self.assertLessEqual(len(readme.splitlines()), 20)
                    self.assertIn(self.paths['agent'], readme)
                    self.assertIn(self.paths['script'], readme)
                    self.assertEqual({name for name in names if name.endswith('.md')},
                                     {self.paths['agent'], self.paths['license']})
                    expected_html = {web_root + 'wwwroot/index.html'} if 'webWorkbench' in manifest else set()
                    self.assertEqual({name for name in names if name.endswith('.html')}, expected_html)
                    self.assertIn('BepInEx/plugins/zh-cn/glossary/eft.zh-CN.example.tsv', names)
                    agent = archive.read(self.paths['agent']).decode('utf-8-sig')
                    sample = json.loads(re.search(r'```jsonc\s+({.*?})\s*```', agent, re.S).group(1))
                    self.assertEqual(set(sample), {'PluginInfo', 'ConfigEntries', 'Enum'})
                    self.assertIn('eft.zh-CN.tsv', agent)
                    self.assertIn('Chat Completions', agent)
                    continue
                paths = release.CONTENT.layout(self.language, version)
                docs = [paths[key] for key in ('guide', 'glossary', 'selection')]
                if version in (3, 4):
                    docs.append(release.CONTENT.WEB_GUIDE_PATH[self.language])
                for doc in docs:
                    text = archive.read(doc).decode('utf-8-sig')
                    for link in re.findall(r'\]\(([^)]+)\)', text):
                        if '://' not in link:
                            self.assertIn(posixpath.normpath(posixpath.join(posixpath.dirname(doc), link)), names, (doc, link))
                editor = archive.read(paths['editor']).decode('utf-8')
                script = re.search(r'<script src="([^"]+)"', editor).group(1)
                self.assertIn(posixpath.join(posixpath.dirname(paths['editor']), script), names)
                self.assertIn('lang="' + self.language.split('-')[0] if self.language == 'en' else 'lang="zh-CN"', editor)
    def test_historical_bundled_archives_remain_readable(self):
        for path in (self.mono, self.il2cpp):
            with ZipFile(path) as archive:
                manifest = json.loads(archive.read(self.paths['manifest']))
                version = manifest['packageLayoutVersion']
                if version == release.CONTENT.LAYOUT_VERSION:
                    self.assertTrue(manifest['webWorkbenchSeparate'])
                    self.assertFalse(any(name.endswith(('.exe', '.html')) for name in archive.namelist()))
                    continue
                if version not in (4, 5, 6, 7) or 'webWorkbench' not in manifest:
                    self.skipTest('Historical archive layout')
                self.assertEqual(set(manifest['webWorkbench']), release.workbench_paths(self.language, version))
                web_root, web_files = release.CONTENT.web_payload(version)
                self.assertEqual([name for name in archive.namelist() if name.endswith('.exe')], [web_root + web_files[0]])
                self.assertNotIn('说明与工具/网页汉化工作台.html', archive.namelist())
                self.assertNotIn('Extras/Web-Workbench.html', archive.namelist())
                html = archive.read(web_root + 'wwwroot/index.html').decode('utf-8-sig')
                self.assertIn('AI 翻译术语表', html)
                css = archive.read(web_root + 'wwwroot/workbench.css').decode('utf-8-sig')
                self.assertIn('background:#121820', css)
                self.assertNotIn('background: #171717', css)
        if version != release.CONTENT.LAYOUT_VERSION:
            missing = self.mutate(self.mono, lambda data: data.pop(web_root + 'wwwroot/workbench.js'))
            with self.assertRaises(ValueError): release.inspect_client_package(missing)
    def test_current_release_uses_one_shared_chinese_web_build(self):
        mono, il2cpp = map(release.inspect_client_package, (self.mono, self.il2cpp))
        if mono['packageLayoutVersion'] != release.CONTENT.LAYOUT_VERSION:
            self.skipTest('Historical archive layout')
        self.assertEqual(mono['packageLanguage'], 'zh-CN')
        self.assertEqual(il2cpp['packageLanguage'], 'zh-CN')
        self.assertEqual(mono.get('webWorkbench'), il2cpp.get('webWorkbench'))
        self.assertEqual(mono.get('webWorkbenchSeparate'), il2cpp.get('webWorkbenchSeparate'))
        if mono.get('webWorkbenchSeparate'):
            with ZipFile(next(self.output.glob('网页汉化工作台-*-Windows.zip'))) as web:
                root, names = release.CONTENT.web_payload()
                self.assertTrue({root + name for name in names}.issubset(web.namelist()))
                self.assertEqual([file for file in web.namelist() if file.endswith('.exe')],
                                 ['网页汉化工作台.exe'])
                self.assertIn('连接 SPT 服务端', web.read(root + 'wwwroot/index.html').decode('utf-8'))
                self.assertIn('AI 翻译术语表', web.read(root + 'wwwroot/index.html').decode('utf-8'))
                self.assertNotIn(release.PLUGIN, web.namelist())
        self.assertFalse(list(self.output.glob('*-International.zip')))
    def test_manifest_layout_mismatch_is_rejected(self):
        def change(data):
            manifest = json.loads(data[self.paths['manifest']])
            manifest['payloadPath'] = '../outside/Newtonsoft.Json.dll'
            data[self.paths['manifest']] = json.dumps(manifest).encode()
        with self.assertRaises(ValueError): release.inspect_client_package(self.mutate(self.mono, change))
    @unittest.skipUnless(os.name == 'nt', 'Windows ACL regression')
    def test_release_artifacts_inherit_directory_permissions(self):
        # The desktop user must be able to open files created by the sandbox account.
        for artifact in (self.mono, self.il2cpp, self.output / 'SHA256SUMS.txt'):
            script = """$ErrorActionPreference = 'Stop'
$path = $args[0]
$acl = [System.IO.File]::GetAccessControl($path)
if ($acl.AreAccessRulesProtected) { throw 'Release artifact has a protected DACL' }
$parent = [System.IO.Directory]::GetAccessControl([System.IO.Path]::GetDirectoryName($path))
$sid = [System.Security.Principal.SecurityIdentifier]
$expected = @($parent.GetAccessRules($true, $true, $sid) | Where-Object {
    $_.AccessControlType -eq 'Allow' -and
    ($_.InheritanceFlags -band [System.Security.AccessControl.InheritanceFlags]::ObjectInherit) -ne 0
})
$actual = @($acl.GetAccessRules($true, $true, $sid) | Where-Object { $_.IsInherited })
if ($expected.Count -eq 0) { throw 'Release directory has no inheritable file access' }
foreach ($rule in $expected) {
    $matching = @($actual | Where-Object {
        $_.IdentityReference -eq $rule.IdentityReference -and $_.AccessControlType -eq 'Allow'
    })
    if ($matching.Count -eq 0) { throw 'Release artifact is missing inherited access' }
}
"""
            check = self.scratch / 'check-release-acl.ps1'
            check.write_text(script, encoding='utf-8-sig')
            result = subprocess.run([POWERSHELL, '-NoProfile', '-File', str(check), str(artifact)], capture_output=True)
            self.assertEqual(result.returncode, 0, result.stderr.decode(errors='replace'))
    def test_il2cpp_missing_active_dll_fails_even_with_fallback(self):
        path = self.mutate(self.il2cpp, lambda data: data.pop(release.ACTIVE_JSON))
        with self.assertRaises(ValueError): release.inspect_client_package(path)
    def test_missing_fallback_is_rejected(self):
        path = self.mutate(self.mono, lambda data: data.pop(self.paths['payload']))
        with self.assertRaises(ValueError): release.inspect_client_package(path)
    def test_missing_dependency_script_is_rejected(self):
        path = self.mutate(self.mono, lambda data: data.pop(self.paths['script']))
        with self.assertRaises(ValueError): release.inspect_client_package(path)
    def test_missing_manifest_is_rejected(self):
        path = self.mutate(self.il2cpp, lambda data: data.pop(self.paths['manifest']))
        with self.assertRaises(ValueError): release.inspect_client_package(path)
    def test_mono_cannot_shadow_game_assembly(self):
        path = self.mutate(self.mono, lambda data: data.update({release.ACTIVE_JSON: data[self.paths['payload']]}))
        with self.assertRaises(ValueError): release.inspect_client_package(path)
    def test_tampered_dependency_is_rejected(self):
        path = self.mutate(self.mono, lambda data: data.update({self.paths['payload']: b'not a valid DLL'}))
        with self.assertRaises(ValueError): release.inspect_client_package(path)
    def test_interop_proxy_is_rejected(self):
        proxy = release.GAME_ROOT / 'EFToffline - 5.0debug/BepInEx/interop/Newtonsoft.Json.dll'
        with self.assertRaises(ValueError): release.validate_json(release.metadata([proxy])[0])
    def test_wrong_runtime_binary_is_rejected(self):
        with ZipFile(self.mono) as archive:
            wrong = archive.read(release.PLUGIN)
        path = self.mutate(self.il2cpp, lambda data: data.update({release.PLUGIN: wrong}))
        with self.assertRaises(ValueError): release.inspect_client_package(path)
    def test_missing_host_prevents_mono_packaging(self):
        binary = self.scratch / 'ConfigurationManager.zh-cn.dll'
        with ZipFile(self.mono) as archive:
            binary.write_bytes(archive.read(release.PLUGIN))
        with self.assertRaises((FileNotFoundError, subprocess.CalledProcessError)):
            release.preflight(release.RUNTIMES[0], self.scratch, self.scratch / 'missing-host')
    def install_fixture(self):
        folder = self.scratch / ('中文路径-' + self._testMethodName)
        with ZipFile(self.mono) as archive: archive.extractall(folder)
        return folder
    def run_check(self, folder, *options):
        return subprocess.run([POWERSHELL, '-NoProfile', '-File', str(folder / self.paths['script']), '-GameRoot', str(folder), *options], capture_output=True)
    def test_script_discovers_game_root_without_argument(self):
        folder = self.install_fixture()
        result = subprocess.run([POWERSHELL, '-NoProfile', '-File', str(folder / self.paths['script'])], cwd=self.scratch, capture_output=True)
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors='replace'))
        self.assertTrue((folder / release.ACTIVE_JSON).exists())
    def test_installer_only_copies_if_dependency_missing(self):
        folder = self.install_fixture()
        self.assertNotEqual(self.run_check(folder, '-CheckOnly').returncode, 0)
        self.assertEqual(self.run_check(folder).returncode, 0)
        target = folder / release.ACTIVE_JSON
        self.assertEqual(target.read_bytes(), (folder / self.paths['payload']).read_bytes())
        before = target.stat().st_mtime_ns
        self.assertEqual(self.run_check(folder, '-CheckOnly').returncode, 0)
        self.assertEqual(target.stat().st_mtime_ns, before)
    def test_installer_preserves_game_managed_dependency(self):
        folder = self.install_fixture()
        managed = folder / 'EscapeFromTarkov_Data/Managed/Newtonsoft.Json.dll'
        managed.parent.mkdir(parents=True)
        source = release.GAME_ROOT / 'EFToffline - 4.1debug/EscapeFromTarkov_Data/Managed/Newtonsoft.Json.dll'
        shutil.copy2(source, managed)
        before = managed.read_bytes()
        self.assertEqual(self.run_check(folder).returncode, 0)
        self.assertEqual(managed.read_bytes(), before)
        self.assertFalse((folder / release.ACTIVE_JSON).exists())
    def test_installer_refuses_proxy_without_overwriting(self):
        folder = self.install_fixture()
        target = folder / release.ACTIVE_JSON
        proxy = release.GAME_ROOT / 'EFToffline - 5.0debug/BepInEx/interop/Newtonsoft.Json.dll'
        shutil.copy2(proxy, target)
        before = target.read_bytes()
        self.assertNotEqual(self.run_check(folder).returncode, 0)
        self.assertEqual(target.read_bytes(), before)
    def test_installer_refuses_corrupt_fallback(self):
        folder = self.install_fixture()
        (folder / self.paths['payload']).write_bytes(b'bad data')
        self.assertNotEqual(self.run_check(folder).returncode, 0)
        self.assertFalse((folder / release.ACTIVE_JSON).exists())

if __name__ == '__main__': unittest.main(verbosity=2)
