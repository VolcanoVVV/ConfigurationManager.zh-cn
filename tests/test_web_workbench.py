"""Exercise the real loopback host and its disk handoff using an isolated game tree."""
import http.client
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import tempfile
import time
import unittest
import uuid
from zipfile import ZipFile

ROOT = Path(__file__).resolve().parents[1]
WEB_NAME = '网页汉化工作台'
APP = ROOT / 'tools/web-workbench/bin/Release/net8.0' / f'{WEB_NAME}.dll'
DOTNET = shutil.which('dotnet') or str(Path(os.environ.get('ProgramFiles', 'C:/Program Files')) / 'dotnet/dotnet.exe')


class WebWorkbenchTests(unittest.TestCase):
    def test_catalog_auth_and_draft_workflow(self):
        revision = uuid.uuid4().hex
        connection = uuid.uuid4().hex
        with tempfile.TemporaryDirectory(prefix='cm-web-') as temporary:
            home = Path(temporary)
            archive_path = os.environ.get('CM_WEB_PACKAGE')
            command = [DOTNET, str(APP)]
            if archive_path:
                with ZipFile(archive_path) as archive:
                    web_name = WEB_NAME if f'{WEB_NAME}/{WEB_NAME}.exe' in archive.namelist() else 'WebWorkbench'
                    for entry in archive.namelist():
                        if entry.startswith(web_name + '/'):
                            archive.extract(entry, home)
                command = [str(home / web_name / (web_name + '.exe'))]
            data = home / 'BepInEx/plugins/zh-cn/web-workbench'
            data.mkdir(parents=True)

            def publish_connection(identifier=connection, locked=False, seconds=15):
                expires = datetime.now(timezone.utc) + timedelta(seconds=seconds)
                ticks = (expires - datetime(1, 1, 1, tzinfo=timezone.utc)) // timedelta(microseconds=1) * 10
                (data / 'server.connection.json').write_text(json.dumps({
                    'schema': 1, 'domain': 'server', 'id': identifier, 'locked': locked, 'expiresUtcTicks': ticks,
                }), encoding='utf-8')

            for domain in ('f12', 'server'):
                rows = [{'group': 'fixture', 'id': 'a', 'name': 'Fixture', 'field': 'Description',
                         'source': 'Enable {0}', 'context': 'Config', 'manual': ''}]
                if domain == 'f12':
                    rows.extend([
                        {'group': 'fixture', 'id': 'plugin-name', 'name': 'Fixture', 'field': 'PluginName', 'source': 'Fixture'},
                        {'group': 'fixture', 'id': 'setting|Probe|Category', 'name': 'Fixture', 'field': 'Category', 'source': 'Graphics'},
                        {'group': 'fixture', 'id': 'setting|Probe|DispName', 'name': 'Fixture', 'field': 'DispName',
                         'source': 'Color', 'ai': '颜色', 'change': '已修改', 'previousSource': 'Colour'},
                        {'group': 'fixture', 'id': 'setting|Probe|Description', 'name': 'Fixture', 'field': 'Description',
                         'source': 'Change saturation', 'machine': '改变饱和度'},
                        {'group': 'fixture', 'id': 'enum|UIFixes.ModifierKey|Shift', 'name': 'Fixture', 'field': None,
                         'source': 'Shift'},
                        {'group': 'fixture', 'id': 'setting|Zoom|Description', 'name': 'Fixture', 'field': 'Description',
                         'source': 'Zoom for the Map tab (0 to 1)'},
                    ])
                (data / f'{domain}.snapshot.json').write_text(json.dumps({
                    'schema': 1, 'domain': domain, 'revision': revision, 'capturedAt': '2026-11-20T00:00:00Z',
                    'connection': connection if domain == 'server' else None,
                    'rows': rows,
                    'updateBasis': 'f12-tracking' if domain == 'f12' else None,
                    'removed': [{'group': 'fixture', 'id': 'old', 'source': '', 'change': '已移除',
                                 'previousSource': 'Old setting', 'removed': True}] if domain == 'f12' else [],
                }), encoding='utf-8')
            with socket.socket() as probe:
                probe.bind(('127.0.0.1', 0))
                port = probe.getsockname()[1]
            origin = f'http://127.0.0.1:{port}'
            with subprocess.Popen([*command, str(home), str(port)], cwd=home,
                                  stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                  creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0)) as child:
                try:
                    def request(path, token=None, body=None, **headers):
                        handler = http.client.HTTPConnection('127.0.0.1', port, timeout=4)
                        try:
                            if token: headers['X-Workbench-Session'] = token
                            if body is not None:
                                headers.setdefault('Origin', origin)
                                headers.setdefault('Content-Type', 'application/json')
                            handler.request('GET' if body is None else 'POST', path,
                                            None if body is None else json.dumps(body), headers)
                            response = handler.getresponse()
                            return response.status, response.read().decode('utf-8'), dict(response.getheaders())
                        finally:
                            handler.close()

                    for _ in range(50):
                        try:
                            status, html, headers = request('/')
                            break
                        except OSError:
                            if child.poll() is not None:
                                self.fail(child.stdout.read().decode(errors='replace'))
                            time.sleep(.1)
                    else:
                        self.fail('Workbench did not listen')
                    self.assertEqual(status, 200)
                    self.assertIn('AI 翻译术语表', html)
                    if archive_path:
                        self.assertNotIn('offlineTools', html)
                        self.assertIn('background:#121820', request('/workbench.css')[1])
                    token = re.search(r'name="workbench-session" content="([A-F0-9]{64})"', html).group(1)
                    self.assertIn('id="apiNotice"', html)
                    self.assertIn('紧急停止 API', html)
                    self.assertNotIn('id="apiStop"', html.split('</header>', 1)[0])
                    for stop, start in (('apiStop', 'translate'), ('cfgApiStop', 'cfgTranslate')):
                        self.assertRegex(html, rf'<div class="actions api-actions"><button id="{stop}"[^>]*>[^<]+</button><button id="{start}"')
                    self.assertEqual(request('/api/ai-stop', body={'paused': True})[0], 403)
                    self.assertEqual(json.loads(request('/api/ai-stop', token, {'paused': True})[1]), {'paused': True})
                    self.assertEqual(json.loads(request('/api/ai-stop', token)[1]), {'paused': True})
                    self.assertIn('离线 CFG 汉化', html)
                    config = {
                        'file': 'fixture.cfg',
                        'cfg': ('## Settings file was created by plugin Fixture v1.2.3\n'
                                '## Plugin GUID: example.fixture\n'
                                '[Map]\n## Marker fade controls opacity.\n# Setting type: String\n'
                                '# Default value: secret-default\n# Acceptable values: One, Two\nFade = secret-current\n'
                                '[Audio]\n## Sound fade adjusts volume.\n# Setting type: String\nFade = never-send-current\n'
                                '# Setting type: PlaybackMode\n# Acceptable values: Normal, Fast\nMode = Fast\n')}
                    self.assertEqual(request('/api/cfg/preview', body=config)[0], 403)
                    status, raw_preview, _ = request('/api/cfg/preview', token, config)
                    self.assertEqual(status, 200, raw_preview)
                    preview_cfg = json.loads(raw_preview)
                    self.assertEqual(preview_cfg['guid'], 'example.fixture')
                    by_id = {row['id']: row for row in preview_cfg['items']}
                    self.assertEqual(by_id['setting|[Map]Fade|Description']['source'], 'Marker fade controls opacity.')
                    self.assertEqual(by_id['setting|[Audio]Fade|Description']['source'], 'Sound fade adjusts volume.')
                    self.assertIn('enum|PlaybackMode|Fast', by_id)
                    status, stopped, _ = request('/api/cfg/translate', token, {**config,
                        'ids': ['setting|[Map]Fade|Description'], 'provider': 'deepseek', 'key': 'fixture-never-send'})
                    self.assertEqual(status, 400, stopped)
                    self.assertIn('API 翻译已暂停', json.loads(stopped)['error'])
                    self.assertEqual(json.loads(request('/api/ai-stop', token, {'paused': False})[1]), {'paused': False})
                    self.assertNotIn('enum|String|One', by_id)
                    for secret in ('secret-current', 'secret-default', 'never-send-current'):
                        self.assertNotIn(secret, raw_preview)
                    translations = {'setting|[Map]Fade|DispName': '图标淡出',
                                    'setting|[Audio]Fade|DispName': '声音渐变',
                                    'enum|PlaybackMode|Fast': '快速'}
                    status, exported, _ = request('/api/cfg/export', token, {**config, 'values': translations})
                    self.assertEqual(status, 200, exported)
                    export = json.loads(exported)
                    self.assertEqual(export['name'], 'example.fixture_API.jsonc')
                    layer = json.loads(export['text'])['AI']
                    self.assertEqual(layer['ConfigEntries']['[Map]Fade']['DispName'], '图标淡出')
                    self.assertEqual(layer['ConfigEntries']['[Audio]Fade']['DispName'], '声音渐变')
                    self.assertEqual(layer['Enum']['PlaybackMode']['Fast'], '快速')
                    self.assertEqual(request('/api/cfg/export', token, {**config, 'values': {'unknown': '译文'}})[0], 400)
                    self.assertEqual(request('/api/cfg/preview', token, {**config, 'guid': '../other'})[0], 400)
                    self.assertEqual(request('/api/cfg/translate', token, {**config, 'ids': ['unknown']})[0], 400)
                    self.assertFalse((home / 'BepInEx/plugins/zh-cn/translations').exists())
                    first_tab = str(uuid.uuid4())
                    tab = '/api/browser-tab'
                    self.assertEqual(request(tab, body={'id': first_tab, 'open': True})[0], 403)
                    self.assertEqual(request(tab, token, {'id': first_tab, 'open': 'yes'})[0], 400)
                    self.assertEqual(request(tab, token, {'id': first_tab, 'open': True})[0], 204)
                    self.assertIn("frame-ancestors 'none'", headers['Content-Security-Policy'])
                    self.assertEqual(request('/workbench.js')[0], 200)
                    self.assertIn('GlossaryTools', request('/glossary-schema.js')[1])
                    self.assertEqual(request('/api/providers')[0], 403)
                    self.assertEqual(request('/api/providers', token, Host='rebind.test')[0], 403)
                    self.assertEqual(request('/api/providers', token, **{'Sec-Fetch-Site': 'cross-site'})[0], 403)
                    self.assertEqual(request('/api/providers', token)[0], 200)
                    self.assertEqual(request('/api/f12/status')[0], 403)
                    self.assertRegex(html, r'<button id="serverTab"[^>]+hidden')
                    self.assertRegex(html, r'<option id="serverReviewOption"[^>]+hidden')
                    self.assertEqual(request('/api/server-connection')[0], 403)
                    self.assertEqual(json.loads(request('/api/server-connection', token)[1]), {'id': None, 'locked': False})
                    server_catalog = '/api/server/catalog?group=&q=&field=&missing=false&page=1'
                    self.assertEqual(request(server_catalog, token)[0], 404)
                    self.assertEqual(request('/api/server/draft', token)[0], 404)
                    publish_connection(locked=True)
                    self.assertEqual(json.loads(request('/api/server-connection', token)[1]), {'id': connection, 'locked': True})
                    self.assertEqual(request(server_catalog, token)[0], 403)
                    self.assertEqual(request('/api/server/draft', token)[0], 403)
                    self.assertEqual(request('/api/server/status', token)[0], 403)
                    self.assertEqual(request('/api/server/stage', token, {})[0], 403)
                    self.assertEqual(request('/api/server/translate', token, {})[0], 403)
                    initial = json.loads(request('/api/f12/status', token)[1])
                    self.assertEqual(initial['revision'], revision)
                    self.assertIsNone(initial['draftId'])
                    if archive_path:
                        index = home / web_name / 'wwwroot/index.html'
                        index.write_text(index.read_text(encoding='utf-8') + '<!-- refreshed template -->', encoding='utf-8')
                        self.assertIn('<!-- refreshed template -->', request('/')[1])
                    self.assertEqual(request('/BepInEx/plugins/zh-cn/web-workbench/f12.snapshot.json', token)[0], 404)
                    catalog = '/api/f12/catalog?group=fixture&q=&field=Description&missing=false&page=1'
                    status, response, _ = request(catalog + '&view=preview', token)
                    self.assertEqual(status, 200)
                    preview = json.loads(response)
                    self.assertEqual({row['field'] for row in preview['rows']}, {'Description'})
                    self.assertEqual({row['field'] for row in preview['previewRows']}, {'Description', 'Category', 'DispName', 'PluginName'})
                    ai = json.loads(request(catalog.replace('field=Description', 'field=') + '&review=ai', token)[1])
                    mt = json.loads(request(catalog + '&review=machine', token)[1])
                    self.assertEqual([row['source'] for row in ai['rows']], ['Color'])
                    self.assertEqual([row['source'] for row in mt['rows']], ['Change saturation'])
                    changes = json.loads(request(catalog.replace('field=Description', 'field=') + '&review=updates', token)[1])
                    self.assertEqual(changes['updateBasis'], 'f12-tracking')
                    self.assertEqual({row['change'] for row in changes['rows']}, {'已修改', '已移除'})
                    self.assertEqual(changes['rows'][0]['previousSource'], 'Colour')
                    glossary = '/api/glossary'
                    self.assertEqual(request(glossary)[0], 403)
                    self.assertEqual(json.loads(request(glossary, token)[1])['revision'], 'missing')
                    content = {'revision': 'missing', 'text': 'Ground Zero\t中心区\n'}
                    self.assertEqual(request(glossary, token, content, Origin='http://other.test')[0], 403)
                    self.assertEqual(request(glossary, token, {**content, 'text': 'broken'})[0], 400)
                    status, saved, _ = request(glossary, token, content)
                    self.assertEqual(status, 200)
                    self.assertIn('Ground Zero\t中心区', json.loads(saved)['text'])
                    self.assertEqual(request(glossary, token, content)[0], 400)
                    self.assertIn('Ground Zero\t中心区', (home / 'BepInEx/plugins/zh-cn/glossary/eft.zh-CN.tsv').read_text(encoding='utf-8'))
                    publish_connection()
                    self.assertEqual(json.loads(request('/api/server-connection', token)[1]), {'id': connection, 'locked': False})
                    for domain in ('f12', 'server'):
                        catalog = f'/api/{domain}/catalog?group=&q=Enable&field=&missing=true&page=1'
                        status, response, _ = request(catalog, token)
                        self.assertEqual(status, 200)
                        self.assertEqual(json.loads(response)['rows'][0]['source'], 'Enable {0}')
                        stage = f'/api/{domain}/stage'
                        edit = {'group': 'fixture', 'id': 'a', 'text': '启用 {0}', 'source': 'forged'}
                        draft = {'revision': revision, 'previous': None, 'layer': 'manual', 'edits': [edit]}
                        self.assertEqual(request(stage, token, draft, Origin='http://other.test')[0], 403)
                        self.assertEqual(request(stage, token, {**draft, 'revision': uuid.uuid4().hex})[0], 400)
                        self.assertEqual(request(stage, token, {**draft, 'edits': [edit, edit]})[0], 400)
                        self.assertEqual(request(stage, token, {**draft, 'edits': [{**edit, 'text': '启用'}]})[0], 400)
                        self.assertEqual(request(stage, token, draft)[0], 200)
                        self.assertEqual(request(stage, token, draft)[0], 400)
                        stored = json.loads((data / f'{domain}.draft.json').read_text(encoding='utf-8'))
                        self.assertEqual(stored['edits'][0]['source'], 'Enable {0}')
                        self.assertEqual(stored['edits'][0]['text'], '启用 {0}')
                        self.assertNotIn('adminKey', stored)
                        (data / f'{domain}.receipt.json').write_text(json.dumps({
                            'schema': 1, 'domain': domain, 'id': stored['id'], 'state': 'saved', 'detail': '已应用'
                        }), encoding='utf-8')
                        progress = json.loads(request(f'/api/{domain}/status', token)[1])
                        self.assertEqual(progress['draftId'], stored['id'])
                        self.assertEqual(progress['receiptId'], stored['id'])
                        self.assertEqual(progress['receiptDetail'], '已应用')
                    previous = json.loads((data / 'f12.draft.json').read_text(encoding='utf-8'))['id']
                    f12_rules = {'revision': revision, 'previous': previous, 'layer': 'ai', 'edits': [
                        {'group': 'fixture', 'id': 'enum|UIFixes.ModifierKey|Shift', 'text': 'Shift'},
                        {'group': 'fixture', 'id': 'setting|Zoom|Description', 'text': '地图页签的缩放比例（0至1）'},
                    ]}
                    self.assertEqual(request('/api/f12/stage', token, f12_rules)[0], 200)
                    publish_connection(locked=True)
                    self.assertEqual(request('/api/server/draft', token)[0], 403)
                    publish_connection()
                    self.assertIsNotNone(json.loads(request('/api/server/status', token)[1])['draftId'])
                    publish_connection(identifier=uuid.uuid4().hex)
                    self.assertEqual(json.loads(request(server_catalog, token)[1]), {'available': False})
                    self.assertEqual(json.loads(request('/api/server/draft', token)[1]), {'draft': None, 'receipt': None})
                    publish_connection(seconds=-1)
                    self.assertEqual(json.loads(request('/api/server-connection', token)[1]), {'id': None, 'locked': False})
                    self.assertEqual(request('/api/server/status', token)[0], 404)
                    custom = {'revision': revision, 'mode': 'ai', 'provider': 'custom',
                              'endpoint': 'http://127.0.0.1', 'edits': [{'group': 'fixture', 'id': 'a'}]}
                    self.assertEqual(request('/api/f12/translate', token, custom)[0], 400)
                    self.assertEqual(request(tab, token, {'id': first_tab, 'open': False})[0], 204)
                    status, refreshed, _ = request('/')
                    self.assertEqual(status, 200)
                    self.assertIn(token, refreshed)
                    second_tab, third_tab = str(uuid.uuid4()), str(uuid.uuid4())
                    for tab_id in (second_tab, third_tab):
                        self.assertEqual(request(tab, token, {'id': tab_id, 'open': True})[0], 204)
                    self.assertEqual(request(tab, token, {'id': second_tab, 'open': False})[0], 204)
                    with self.assertRaises(subprocess.TimeoutExpired):
                        child.wait(timeout=2.5)
                    self.assertEqual(request(tab, token, {'id': third_tab, 'open': False})[0], 204)
                    self.assertEqual(request(tab, token, {'id': third_tab, 'open': True})[0], 204)
                    self.assertEqual(child.wait(timeout=6), 0)
                    if archive_path:
                        for item in (web_name + '.exe', web_name + '.dll', 'Newtonsoft.Json.dll'):
                            with (home / web_name / item).open('r+b'):
                                pass
                finally:
                    if child.poll() is None:
                        child.terminate()
                    child.wait(timeout=8)


if __name__ == '__main__':
    unittest.main()
