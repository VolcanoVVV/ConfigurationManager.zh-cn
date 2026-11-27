"""Dependency-checked client ZIPs; also used by the combined server/client release script."""
from __future__ import annotations
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
from zipfile import ZipFile, ZIP_DEFLATED

ROOT = Path(__file__).resolve().parents[1]
_content_spec = importlib.util.spec_from_file_location('client_release_content', ROOT / 'tools/release_content.py')
CONTENT = importlib.util.module_from_spec(_content_spec)
_content_spec.loader.exec_module(CONTENT)
GAME_ROOT = ROOT.parent.parent
DEPENDENCY = ROOT / 'Dependencies/Newtonsoft.Json/Newtonsoft.Json.dll'
PIN = ROOT / 'Dependencies/Newtonsoft.Json/manifest.json'
PROBE_PROJECT = ROOT / 'tools/DependencyProbe/DependencyProbe.csproj'
PROBE = ROOT / 'tools/DependencyProbe/bin/Release/net8.0/DependencyProbe.dll'
DOTNET = shutil.which('dotnet') or str(Path(os.environ.get('ProgramFiles', 'C:/Program Files')) / 'dotnet/dotnet.exe')
PAYLOAD = 'Dependencies/Newtonsoft.Json/Newtonsoft.Json.dll'
ACTIVE_JSON = 'BepInEx/plugins/Newtonsoft.Json.dll'
PLUGIN = 'BepInEx/plugins/ConfigurationManager.zh-cn.dll'
RUNTIMES = ('Mono-SPT4x', 'IL2CPP-SPT5.0be')
MONO_HOSTS = (('3.11', '3.11.0'), ('4.0', '4.0'), ('4.1', '4.1'))
DEFAULT_OUTPUT = ROOT / 'releases/separate'
WEB_PROJECT = ROOT / 'tools/web-workbench/WebWorkbench.csproj'
WEB_DIST = ROOT / 'obj' / CONTENT.WEB_NAME
_probe_ready = False

def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()

def inherit_release_permissions(path: Path) -> None:
    # Windows sandbox-created files can have a protected, owner-only DACL.
    # Inherit this release directory's existing permissions without granting new principals.
    if os.name == 'nt':
        icacls = str(Path(os.environ.get('SystemRoot', 'C:/Windows')) / 'System32/icacls.exe')
        subprocess.run([icacls, str(path), '/inheritance:e'], check=True, capture_output=True)

def metadata(paths: list[Path]) -> list[dict]:
    global _probe_ready
    if not _probe_ready:
        subprocess.run([DOTNET, 'build', str(PROBE_PROJECT), '-c', 'Release', '-v:q'], check=True, capture_output=True, text=True, encoding="utf-8", errors="replace")
        _probe_ready = True
    result = subprocess.run([DOTNET, str(PROBE), *map(str, paths)], check=True, capture_output=True, text=True, encoding="utf-8", errors="replace")
    return json.loads(result.stdout)

def validate_json(info: dict, reference: dict | None = None) -> None:
    reference = reference or {'Version': '13.0.0.0', 'Token': '30ad4fe6b2a6aeed'}
    if (info['Name'] != 'Newtonsoft.Json' or info['Version'] != reference['Version'] or
        info['PublicKeyToken'] != reference['Token'] or info['IsInterop'] or not info['HasJsonApi'] or
        info['TargetFramework'] != '.NETStandard,Version=v2.0'):
        raise ValueError('Incompatible CLR Newtonsoft.Json (interop proxies are forbidden): ' + info['Path'])

def validate_plugin(info: dict, runtime: str) -> dict:
    expected = '.NETFramework,Version=v4.7.1' if runtime == RUNTIMES[0] else '.NETCoreApp,Version=v6.0'
    if info['Name'] != 'ConfigurationManager.zh-cn' or info['TargetFramework'] != expected or info['IsInterop'] != (runtime == RUNTIMES[1]):
        raise ValueError('Plugin binary/runtime mismatch: ' + runtime)
    references = [item for item in info['References'] if item['Name'] == 'Newtonsoft.Json']
    if len(references) != 1 or references[0]['Token'] != '30ad4fe6b2a6aeed':
        raise ValueError('Client must reference exactly one signed CLR Newtonsoft.Json')
    return references[0]

def preflight(runtime: str, binary_dir: Path, game_root: Path = GAME_ROOT) -> dict:
    if runtime not in RUNTIMES:
        raise ValueError('Unknown client runtime: ' + runtime)
    pin = json.loads(PIN.read_text(encoding='utf-8-sig'))
    if sha(DEPENDENCY.read_bytes()) != pin['sha256']:
        raise ValueError('Vendored Newtonsoft.Json checksum does not match its manifest')
    dependency, plugin = metadata([DEPENDENCY, binary_dir / 'ConfigurationManager.zh-cn.dll'])
    reference = validate_plugin(plugin, runtime)
    validate_json(dependency, reference)
    hosts = []
    if runtime == RUNTIMES[0]:
        loader_ref = next((item['Version'] for item in plugin['References'] if item['Name'] == 'BepInEx'), None)
        if loader_ref is None:
            raise ValueError('Mono client must reference BepInEx 5')
        target = tuple(map(int, loader_ref.split('.')))
        for version, folder_version in MONO_HOSTS:
            host = game_root / ('EFToffline - ' + folder_version + 'debug')
            path = host / 'EscapeFromTarkov_Data/Managed/Newtonsoft.Json.dll'
            loader, info = metadata([host / 'BepInEx/core/BepInEx.dll', path])
            current = tuple(map(int, loader['Version'].split('.')))
            # Chainloader compares major/minor/build and ignores the fourth version component.
            if loader['Name'] != 'BepInEx' or target[0] != 5 or current[0] != target[0] or target[1:3] > current[1:3]:
                raise ValueError(f'Mono client targets BepInEx {loader_ref}, incompatible with SPT {version} loader {loader["Version"]}')
            validate_json(info, reference)
            hosts.append({'spt': version, 'path': 'EscapeFromTarkov_Data/Managed/Newtonsoft.Json.dll', 'sha256': sha(path.read_bytes())})
    return {'runtime': runtime, 'pluginVersion': plugin['Version'], 'pluginMvid': plugin['Mvid'],
        'pluginSha256': sha((binary_dir / 'ConfigurationManager.zh-cn.dll').read_bytes()), 'newtonsoft': {
        'version': dependency['Version'], 'token': dependency['PublicKeyToken'], 'packageVersion': pin['packageVersion'],
        'framework': dependency['TargetFramework'], 'sha256': pin['sha256']}, 'providedByHosts': hosts,
        'policy': 'Use the game-provided Mono assembly; install fallback only if absent. IL2CPP requires the bundled CLR copy in plugins, never interop.'}

def archive_layout(names: list[str]) -> tuple[str, dict]:
    candidates = [(language, CONTENT.layout(language)) for language in CONTENT.ARCHIVE_LANGUAGES]
    candidates.append(('legacy', dict(manifest='DEPENDENCIES.txt', payload=PAYLOAD,
        script='Check-Dependencies.ps1', license='Dependencies/Newtonsoft.Json/LICENSE.md')))
    found = [(language, paths) for language, paths in candidates if paths['manifest'] in names]
    if len(found) != 1:
        raise ValueError('Missing or ambiguous package manifest')
    return found[0]


def workbench_paths(language: str, layout_version: int = CONTENT.LAYOUT_VERSION) -> set[str]:
    root, files = CONTENT.web_payload(layout_version)
    paths = {root + name for name in files}
    if layout_version in (3, 4):
        paths.add(CONTENT.WEB_GUIDE_PATH[language])
    if layout_version == 3:
        paths.add('说明与工具/网页汉化工作台.html' if language == 'zh-CN' else 'Extras/Web-Workbench.html')
    return paths


def workbench_files(host_dir: Path) -> dict[str, bytes]:
    root, names = CONTENT.web_payload()
    files = {root + name: (host_dir / name).read_bytes() for name in names}
    if files[CONTENT.WEB_INSTALL_DIR + 'Newtonsoft.Json.dll'] != DEPENDENCY.read_bytes():
        raise ValueError('The web workbench must include the pinned CLR dependency')
    return files


def inspect_client_package(path: Path) -> dict:
    with ZipFile(path) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)) or archive.testzip() is not None:
            raise ValueError('Corrupt/duplicate ZIP entries')
        language, paths = archive_layout(names)
        manifest = json.loads(archive.read(paths['manifest']))
        layout_version = manifest.get('packageLayoutVersion')
        if language != 'legacy' and layout_version in (2, 3, 4):
            paths = CONTENT.layout(language, layout_version)
        required = {PLUGIN, *paths.values(), 'BepInEx/plugins/zh-cn/UI.json'}
        if not required.issubset(names):
            raise ValueError('Missing mandatory client package files: ' + str(required - set(names)))
        json_files_expected = {'BepInEx/plugins/zh-cn/UI.json'}
        if language != 'legacy':
            if (manifest.get('packageLanguage') != language or layout_version not in (2, 3, 4, 5, 6, 7, CONTENT.LAYOUT_VERSION)
                    or layout_version >= CONTENT.CHINESE_WEB_LAYOUT_VERSION and language not in CONTENT.LANGUAGES
                    or manifest.get('payloadPath') != paths['payload']):
                raise ValueError('Package language/layout mismatch')
            has_web = layout_version in (3, 4, 6, 7) or (layout_version == 5 and 'webWorkbench' in manifest)
            if manifest.get('webWorkbenchSeparate') is True:
                if layout_version not in (7, CONTENT.LAYOUT_VERSION) or 'webWorkbench' in manifest:
                    raise ValueError('Invalid standalone web package declaration')
                has_web = False
            elif layout_version == CONTENT.LAYOUT_VERSION:
                raise ValueError('Current client packages require separate web distribution')
            web_root, web_names = CONTENT.web_payload(layout_version)
            expected = required | {'BepInEx/plugins/zh-cn/glossary/eft.zh-CN.example.tsv'}
            if manifest['runtime'] == RUNTIMES[1]: expected.add(ACTIVE_JSON)
            translations = 'BepInEx/plugins/zh-cn/translations/'
            if layout_version >= CONTENT.FLAT_TRANSLATIONS_LAYOUT_VERSION:
                expected.add(translations)
            else:
                expected.update(translations + layer + '/' for layer in ('Manual', 'API', 'MT'))
            if has_web:
                expected.update(workbench_paths(language, layout_version))
                json_files_expected.update(web_root + name for name in web_names if name.endswith('.json'))
            if set(names) != expected:
                raise ValueError('Unexpected or missing user package files: ' + str(set(names) ^ expected))
            if has_web:
                web_hashes = manifest.get('webWorkbench', {})
                if (not isinstance(web_hashes, dict) or set(web_hashes) != workbench_paths(language, layout_version) or
                        any(sha(archive.read(name)) != digest for name, digest in web_hashes.items())):
                    raise ValueError('Web workbench build identity does not match its manifest')
                if archive.read(web_root + 'Newtonsoft.Json.dll') != archive.read(paths['payload']):
                    raise ValueError('Web workbench dependency differs from verified payload')
        pin = json.loads(PIN.read_text(encoding='utf-8-sig'))
        if manifest['newtonsoft']['sha256'] != pin['sha256']:
            raise ValueError('Package dependency is not the pinned, verified CLR build')
        runtime = manifest['runtime']
        if runtime not in RUNTIMES:
            raise ValueError('Unknown archive runtime')
        active = [name for name in names if name.lower().startswith('bepinex/') and name.lower().endswith('/newtonsoft.json.dll')]
        if active != ([ACTIVE_JSON] if runtime == RUNTIMES[1] else []):
            raise ValueError('Wrong active Newtonsoft layout; Mono must not shadow the game and IL2CPP must include CLR plugins copy')
        json_files = {name for name in names if name.lower().endswith(('.json', '.jsonc'))}
        if json_files != json_files_expected:
            raise ValueError('Unexpected runtime JSON/config/private metadata in client package')
        if any(part.lower() in {'cache', 'logs', 'log'} or part.lower().endswith(('.bak', '.pdb', '.credentials.bin')) for name in names for part in Path(name).parts):
            raise ValueError('Private or debug files in package')
        if any(name.endswith('/eft.zh-CN.tsv') for name in names):
            raise ValueError('Personal glossary in package')
        if sha(archive.read(paths['payload'])) != manifest['newtonsoft']['sha256']:
            raise ValueError('Bundled dependency hash mismatch')
        if runtime == RUNTIMES[1] and archive.read(ACTIVE_JSON) != archive.read(paths['payload']):
            raise ValueError('Active dependency differs from verified payload')
        with tempfile.TemporaryDirectory(prefix='cm-package-verify-') as folder:
            probe_paths = [Path(folder) / 'Newtonsoft.Json.dll', Path(folder) / 'ConfigurationManager.zh-cn.dll']
            for source, target in zip((paths['payload'], PLUGIN), probe_paths):
                target.write_bytes(archive.read(source))
            dependency, plugin = metadata(probe_paths)
            reference = validate_plugin(plugin, runtime)
            if manifest.get('pluginMvid', plugin['Mvid']) != plugin['Mvid'] or manifest.get('pluginSha256', sha(archive.read(PLUGIN))) != sha(archive.read(PLUGIN)):
                raise ValueError('Client build identity does not match its manifest')
            validate_json(dependency, reference)
            if manifest['newtonsoft']['version'] != dependency['Version'] or manifest['newtonsoft']['token'] != dependency['PublicKeyToken']:
                raise ValueError('Dependency manifest identity mismatch')
    return manifest

def client_package(runtime: str, binary_dir: Path, output: Path, game_root: Path = GAME_ROOT,
                   language: str = 'zh-CN') -> Path:
    if language not in CONTENT.LANGUAGES:
        raise ValueError('Only the Chinese client edition is published')
    paths = CONTENT.layout(language)
    manifest = preflight(runtime, binary_dir, game_root)  # No ZIP is published on missing or invalid inputs.
    manifest.update(packageLanguage=language, packageLayoutVersion=CONTENT.LAYOUT_VERSION,
                    payloadPath=paths['payload'], webWorkbenchSeparate=True)
    version = re.search(r'PluginVersion\s*=\s*"([^"]+)"', (ROOT / 'ConfigurationManagerZhCnPlugin.cs').read_text(encoding='utf-8-sig')).group(1)
    if manifest['pluginVersion'] != version + '.0':
        raise ValueError('Stale client assembly version')
    output.mkdir(parents=True, exist_ok=True)
    destination = output / f'ConfigurationManager.zh-cn-{version}-{runtime}-中文版.zip'
    with tempfile.TemporaryDirectory(prefix='.client-package-', dir=output) as folder:
        inherit_release_permissions(Path(folder))
        candidate = Path(folder) / destination.name
        with ZipFile(candidate, 'w', compression=ZIP_DEFLATED) as archive:
            sources = {PLUGIN: binary_dir / 'ConfigurationManager.zh-cn.dll', paths['payload']: DEPENDENCY,
                paths['license']: DEPENDENCY.parent / 'LICENSE.md', 'BepInEx/plugins/zh-cn/UI.json': ROOT / 'zh-cn/UI.json',
                paths['agent']: ROOT / f'docs/AI_AGENT_GUIDE.{language}.md',
                'BepInEx/plugins/zh-cn/glossary/eft.zh-CN.example.tsv': ROOT / 'zh-cn/glossary/eft.zh-CN.example.tsv'}
            if runtime == RUNTIMES[1]:
                sources[ACTIVE_JSON] = DEPENDENCY
            for target, source in sources.items():
                archive.writestr(target, source.read_bytes())
            script = (ROOT / 'tools/Check-Dependencies.ps1').read_text(encoding='utf-8-sig').replace("'DEPENDENCIES.txt'", repr(Path(paths['manifest']).name))
            archive.writestr(paths['script'], script.encode('utf-8-sig'))
            archive.writestr(paths['manifest'], json.dumps(manifest, ensure_ascii=False, indent=2).encode('utf-8'))
            installation = CONTENT.installation(language, runtime, version)
            archive.writestr(paths['readme'], installation.encode('utf-8-sig'))
            archive.writestr('BepInEx/plugins/zh-cn/translations/', b'')
        inspect_client_package(candidate)
        inherit_release_permissions(candidate)
        os.replace(candidate, destination)
    return destination


def publish_web() -> None:
    subprocess.run([DOTNET, 'publish', str(WEB_PROJECT), '-c', 'Release', '--self-contained', 'false',
                    '-p:PublishStandaloneLauncher=true', '-o', str(WEB_DIST), '-v:q'], check=True)


def web_package(output: Path) -> Path:
    payload = workbench_files(WEB_DIST)
    version = re.search(r'PluginVersion\s*=\s*"([^"]+)"', (ROOT / 'ConfigurationManagerZhCnPlugin.cs').read_text(encoding='utf-8-sig')).group(1)
    host = WEB_DIST / (CONTENT.WEB_NAME + '.dll')
    if metadata([host])[0]['Version'] != version + '.0':
        raise ValueError('Stale web workbench assembly version')
    output.mkdir(parents=True, exist_ok=True)
    destination = output / f'网页汉化工作台-{version}-Windows.zip'
    files = {**payload,
             CONTENT.WEB_NAME + '.exe': (WEB_DIST / 'launcher' / (CONTENT.WEB_NAME + '.exe')).read_bytes(),
             '网页工作台安装说明.txt': CONTENT.web_installation().encode('utf-8-sig'),
             CONTENT.WEB_INSTALL_DIR + 'eft.zh-CN.example.tsv': (ROOT / 'zh-cn/glossary/eft.zh-CN.example.tsv').read_bytes(),
             CONTENT.WEB_INSTALL_DIR + '说明与工具/Newtonsoft.Json-许可证.md': (DEPENDENCY.parent / 'LICENSE.md').read_bytes()}
    with tempfile.TemporaryDirectory(prefix='.web-package-', dir=output) as folder:
        inherit_release_permissions(Path(folder))
        candidate = Path(folder) / destination.name
        with ZipFile(candidate, 'w', compression=ZIP_DEFLATED) as archive:
            for name, content in files.items():
                archive.writestr(name, content)
        with ZipFile(candidate) as archive:
            if set(archive.namelist()) != set(files) or archive.testzip() is not None:
                raise ValueError('Standalone web package verification failed')
        inherit_release_permissions(candidate)
        os.replace(candidate, destination)
    return destination

def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument('--language', choices=(*CONTENT.LANGUAGES, 'all'), default='zh-CN', help='Generate the Chinese edition for both runtimes')
    parser.add_argument('--target', choices=('client', 'web', 'all'), default='client', help='Select client ZIPs, standalone web ZIP, or both')
    parser.add_argument('--no-build', action='store_true', help='Reuse prebuilt client binaries and the published web host')
    args = parser.parse_args()
    output = args.output.resolve()
    if not output.is_relative_to(ROOT / 'releases'):
        raise ValueError('Client release output must stay inside the client releases directory')
    if not args.no_build and args.target != 'web':
        subprocess.run([DOTNET, 'msbuild', str(ROOT / 'ConfigurationManager.zh-cn.csproj'), '/p:Configuration=Release', '/v:minimal', '/nologo'], check=True)
        subprocess.run([DOTNET, 'build', str(ROOT / 'ConfigurationManager.zh-cn.IL2CPP.csproj'), '--no-restore', '-c', 'Release', '-v:q'], check=True)
    if not args.no_build and args.target != 'client':
        publish_web()
    languages = CONTENT.LANGUAGES if args.language == 'all' else (args.language,)
    paths = [client_package(runtime, ROOT / binary, output, language=language)
        for runtime, binary in zip(RUNTIMES, ('bin/Release', 'bin/IL2CPP/Release')) for language in languages] if args.target != 'web' else []
    if args.target != 'client':
        paths.append(web_package(output))
    checksums = output / 'SHA256SUMS.txt'
    checksums.write_text(''.join(sha(path.read_bytes()) + '  ' + path.name + '\n' for path in paths), encoding='utf-8')
    inherit_release_permissions(checksums)
    for path in paths:
        print(path)

if __name__ == '__main__':
    main()
