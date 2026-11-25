"""Run the workbench's local regression suites and write a reader-friendly report."""

import argparse
from datetime import datetime
from hashlib import sha256
import os
from pathlib import Path
import shutil
import subprocess
import sys
from tempfile import TemporaryDirectory
from time import monotonic
from zipfile import BadZipFile, ZipFile


PROJECT = Path(__file__).resolve().parents[1]
GAME_ROOT = PROJECT.parent.parent
SERVER = PROJECT.with_name('SimpleTranslatorCS')
CLIENT_DLL = 'BepInEx/plugins/ConfigurationManager.zh-cn.dll'


def latest_client_release():
    return max((p for p in (PROJECT / 'releases').rglob('*.zip')
                if '-Mono-SPT4x-' in p.name and (p.parent / 'SHA256SUMS.txt').is_file()),
               key=lambda p: p.stat().st_mtime, default=None)


def check_archives(folder):
    checks = dict(line.split('  ', 1)[::-1]
                  for line in (folder / 'SHA256SUMS.txt').read_text(encoding='utf-8-sig').splitlines())
    archives = [next(folder.glob(f'*-{runtime}-中文版.zip')) for runtime in ('Mono-SPT4x', 'IL2CPP-SPT5.0be')]
    for archive in archives:
        if sha256(archive.read_bytes()).hexdigest() != checks.get(archive.name):
            raise ValueError(f'安装包校验失败：{archive.name}')
        with ZipFile(archive) as package:
            if CLIENT_DLL not in package.namelist() or package.testzip() is not None:
                raise ValueError(f'无法完整读取：{archive.name}')
    return archives[0]


def server_fixture(destination):
    for source in SERVER.iterdir():
        if source.is_file() and (source.suffix in ('.cs', '.csproj') or source.name in
                               ('Directory.Build.props', 'README.md', 'README.zh-CN.md')):
            shutil.copy2(source, destination)
    for name in ('Shared', 'Properties', 'docs', 'tools/glossary-editor'):
        shutil.copytree(SERVER / name, destination / name)
    glossary = destination / 'glossary'
    glossary.mkdir()
    shutil.copy2(SERVER / 'glossary/eft.zh-CN.example.tsv', glossary)
    tests = Path('tests/SimpleTranslatorCS.Tests')
    (destination / tests).mkdir(parents=True)
    for source in (SERVER / tests).iterdir():
        if source.is_file() and source.suffix in ('.cs', '.csproj'):
            shutil.copy2(source, destination / tests)
    return destination / tests / 'SimpleTranslatorCS.Tests.csproj'


def find_browser():
    locations = ('Google/Chrome/Application/chrome.exe', 'Microsoft/Edge/Application/msedge.exe')
    for env in ('ProgramFiles', 'ProgramFiles(x86)', 'LocalAppData'):
        base = os.environ.get(env)
        if base:
            for location in locations:
                browser = Path(base) / location
                if browser.is_file():
                    return browser
    return None


class Report:
    def __init__(self, directory):
        self.directory = directory
        self.results = []

    def note(self, name, status, detail):
        self.results.append((name, status, detail))
        print(f'[{status}] {name}', flush=True)

    def run(self, name, args, *, env=None, seconds=180):
        log = self.directory / f'{len(self.results) + 1:02d}.log'
        print(f'[测试中] {name}', flush=True)
        start = monotonic()
        with log.open('wb') as output:
            try:
                result = subprocess.run([str(arg) for arg in args], cwd=PROJECT, env=env,
                                        stdout=output, stderr=subprocess.STDOUT, timeout=seconds)
                passed = result.returncode == 0
            except (OSError, subprocess.TimeoutExpired) as error:
                output.write(str(error).encode('utf-8'))
                passed = False
        self.note(name, '通过' if passed else '失败', f'{monotonic() - start:.0f} 秒 · {log.name}')
        return passed

    def finish(self, package):
        counts = {status: sum(row[1] == status for row in self.results) for status in ('通过', '失败', '未测')}
        report = self.directory / '测试报告.txt'
        lines = ['汉化工作台自测报告', f'时间　{datetime.now():%Y-%m-%d %H:%M:%S}',
                 f'客户端包　{package or "未找到"}',
                 '结果　' + '　'.join(f'{status} {count} 项' for status, count in counts.items()), '']
        lines.extend(f'[{status}] {name}　{detail}' for name, status, detail in self.results)
        lines.extend(['', '这些测试使用临时目录和模拟翻译接口，不打开游戏、不调用付费 API。',
                      '', '需要在游戏里确认的项目',
                      '1. F12 缩放到 200%，打开「色调映射」下拉框。重新打开 API 确认弹窗，核对按钮高度和红色停止按钮。',
                      '2. 安装 BetterBepInEx 后打开 F12，确认侧栏汉化、鼠标抓取以及切回原版菜单。',
                      '3. 两个 Fika 客户端在线时，人工发布一条译文再撤回。检查另一端显示，以及空闲时服务端日志是否还有 /workbench/live 轮询。',
                      '', '向 AI 或维护者求助时，先发本报告和失败项对应的编号日志。'])
        report.write_text('\n'.join(lines) + '\n', encoding='utf-8-sig')
        return report, counts['失败'] > 0


def run_suites(report, folder, temp, quick):
    try:
        mono = check_archives(folder)
    except (OSError, ValueError, BadZipFile, StopIteration, KeyError) as error:
        report.note('客户端安装包完整性', '失败', f'{type(error).__name__}。请检查安装包目录和 SHA256SUMS.txt。')
        return None
    report.note('客户端安装包完整性', '通过', 'Mono 和 IL2CPP 的 ZIP 与 SHA256 已核对。')
    dotnet = shutil.which('dotnet')
    if not dotnet:
        report.note('自动测试', '未测', '这台电脑找不到 dotnet。')
        return mono

    mono_game = GAME_ROOT / 'EFToffline - 4.1debug'
    il2cpp_game = GAME_ROOT / 'EFToffline - 5.0debug'
    env = dict(os.environ, CM_RELEASE_DIR=str(folder), CM_WEB_PACKAGE=str(mono), CM_PACKAGE_LANGUAGE='zh-CN')
    report.run('网页草稿、停止入口和安装包回归', [sys.executable, '-m', 'unittest', 'discover', '-s', 'tests', '-p', 'test_*.py'], env=env)

    with ZipFile(mono) as archive:
        binary = temp / 'ConfigurationManager.zh-cn.dll'
        binary.write_bytes(archive.read(CLIENT_DLL))
        for entry in archive.namelist():
            if entry.startswith('网页汉化工作台/'):
                archive.extract(entry, temp)

    local_feed = temp / 'empty-feed'
    local_feed.mkdir()
    build_args = ['-c', 'Release', f'-p:RestoreSources={local_feed}', '-p:NuGetAudit=false', '-v:q']
    if (mono_game / 'BepInEx/core/BepInEx.dll').is_file():
        output = temp / 'client-tests'
        tests = output / 'ConfigurationManager.zh-cn.Tests.dll'
        if report.run('F12 测试准备', [dotnet, 'build', 'tests/RegressionTests.csproj', *build_args, '-o', output,
                                       f'-p:GamePath={mono_game}', f'-p:PluginAssemblyPath={binary}']):
            report.run('F12 翻译、拦截付费请求、取消与同步逻辑', [dotnet, tests])
            native = sorted((mono_game / 'BepInEx/plugins').rglob('ConfigurationManager.dll'))
            if len(native) == 1:
                shutil.copy2(native[0], output / 'ConfigurationManager.dll')
                report.run('原版 F12 缩放与下拉框补丁匹配', [dotnet, tests, '--check-scale'])
                better = mono_game / 'BepInEx/plugins/BetterBepInEx.dll'
                if better.is_file():
                    report.run('BetterBepInEx 适配入口与升级回归', [dotnet, tests, '--check-better-bepinex', better])
                else:
                    report.note('BetterBepInEx 适配', '未测', '4.1 测试目录未安装 BetterBepInEx。')
            else:
                report.note('原版 F12 与 BetterBepInEx 补丁匹配', '未测', '4.1 测试目录缺少 ConfigurationManager 本体或有多个副本。')
        else:
            report.note('F12 与 BetterBepInEx 回归', '未测', '请先看「F12 测试准备」对应日志。')
    else:
        report.note('F12 与 BetterBepInEx 回归', '未测', '找不到 4.1 测试目录里的 BepInEx。')

    browser = find_browser()
    node = shutil.which('node')
    if browser and node:
        report.run('浏览器实测停止按钮、5 秒倒计时和免提示', [node, '--experimental-websocket', 'tests/check_api_notice.mjs',
                   temp / '网页汉化工作台/网页汉化工作台.dll', browser, temp])
    else:
        report.note('网页按钮与确认弹窗的浏览器实测', '未测', '需要本机有 Chrome 或 Edge，以及支持 WebSocket 的 Node.js。')

    if (il2cpp_game / 'BepInEx/core/BepInEx.Core.dll').is_file():
        report.run('IL2CPP 模拟翻译、术语表与模型列表', [dotnet, 'run', '--project', 'tests/Il2CppApiSmoke/Il2CppApiSmoke.csproj', *build_args])
    else:
        report.note('IL2CPP 模拟翻译', '未测', '找不到 5.0 测试目录里的 BepInEx。')

    if quick or not (SERVER / 'SimpleTranslatorCS.csproj').is_file():
        report.note('SimpleTranslatorCS 服务端回归', '未测', '本次选择快速模式。' if quick else '找不到 SimpleTranslatorCS 源码目录。')
    else:
        fixture = temp / 'server'
        fixture.mkdir()
        try:
            server_tests = server_fixture(fixture)
        except OSError as error:
            report.note('SimpleTranslatorCS 服务端回归', '未测', f'服务端测试文件准备失败：{type(error).__name__}。')
        else:
            for target in ('4.0', '4.1', '5.0be'):
                if target == '5.0be' and not (il2cpp_game / 'SPT_Runtime/SPTarkov.Server.Core.dll').is_file():
                    report.note('SimpleTranslatorCS 5.0 BE 回归', '未测', '找不到 5.0 服务端程序集。')
                    continue
                report.run(f'SimpleTranslatorCS {target} 预算、取消、权限与译文增量',
                           [dotnet, 'test', server_tests, *build_args, f'-p:SptTarget={target}',
                            f'-p:Spt50RuntimePath={il2cpp_game / "SPT_Runtime"}'])
    return mono


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--open-report', action='store_true')
    parser.add_argument('--quick', action='store_true', help='Skip the server matrix.')
    parser.add_argument('--package-dir', type=Path)
    args = parser.parse_args()
    directory = PROJECT / 'obj' / 'selftest-reports' / datetime.now().strftime('%Y-%m-%d_%H%M%S_%f')
    directory.mkdir(parents=True)
    report = Report(directory)
    latest = latest_client_release() if args.package_dir is None else None
    folder = args.package_dir.resolve() if args.package_dir else latest.parent if latest else None
    package = None
    if folder is None:
        report.note('客户端安装包', '未测', 'releases 下找不到附 SHA256 清单的 Mono 安装包。')
    else:
        try:
            with TemporaryDirectory(prefix='cm-selftest-', ignore_cleanup_errors=True) as scratch:
                package = run_suites(report, folder, Path(scratch), args.quick)
        except OSError as error:
            detail = directory / '测试工具错误.log'
            detail.write_text(str(error) + '\n', encoding='utf-8-sig')
            report.note('测试工具中断', '失败', f'请查看 {detail.name}。')
    path, failed = report.finish(package)
    print(f'报告已生成：{path}', flush=True)
    if args.open_report and hasattr(os, 'startfile'):
        os.startfile(path)
    return int(failed)


if __name__ == '__main__':
    raise SystemExit(main())
