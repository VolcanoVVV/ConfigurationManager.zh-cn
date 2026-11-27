import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { createServer } from 'node:net';
import { join } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const [workbench, browser, temp] = process.argv.slice(2);
assert(workbench && browser && temp, 'Expected the workbench DLL, Chromium executable and test directory');
const home = join(temp, 'browser-fixture');
const dataDir = join(home, 'BepInEx', 'plugins', 'zh-cn', 'web-workbench');
await mkdir(dataDir, { recursive: true });
const revision = '1'.repeat(32);
await writeFile(join(dataDir, 'f12.snapshot.json'), JSON.stringify({
  schema: 1, domain: 'f12', revision, draftVersion: 2, capturedAt: new Date().toISOString(),
  rows: Array.from({ length: 73 }, (_, i) => ({
    id: 'row-' + i, group: 'fixture', name: 'Fixture', field: ['Description', 'DispName', 'Text'][i % 3],
    source: 'Option ' + i, context: 'Test'
  }))
}));
const portProbe = createServer();
await new Promise(resolve => portProbe.listen(0, '127.0.0.1', resolve));
const port = portProbe.address().port;
await new Promise(resolve => portProbe.close(resolve));
const site = `http://127.0.0.1:${port}`;
const app = spawn('dotnet', [workbench, home, String(port)], { stdio: 'ignore', windowsHide: true });
const browserProfile = join(home, 'chrome-profile');
let chrome, connection;
try {
  let listening = false;
  for (let tries = 0; tries < 50; ++tries) {
    try { listening = (await fetch(site)).ok; } catch { }
    if (listening) break;
    await sleep(100);
  }
  assert(listening, 'web workbench started');
  chrome = spawn(browser, [
    '--headless=new', '--no-sandbox', '--disable-gpu', '--disable-background-networking', '--disable-component-update',
    '--no-first-run', '--window-size=1440,960', '--remote-debugging-port=0', `--user-data-dir=${browserProfile}`, site
  ], { stdio: 'ignore', windowsHide: true });
  let debugPort;
  for (let tries = 0; tries < 60; ++tries) {
    try { debugPort = (await readFile(join(browserProfile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]; } catch { }
    if (debugPort) break;
    await sleep(100);
  }
  assert(debugPort, 'Chrome exposes DevTools');
  const targets = await (await fetch(`http://127.0.0.1:${debugPort}/json/list`)).json();
  connection = new WebSocket(targets.find(target => target.type === 'page').webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { connection.addEventListener('open', resolve); connection.addEventListener('error', reject); });
  let id = 0;
  async function evaluate(expression) {
    const messageId = ++id;
    const result = await new Promise(resolve => {
      const onMessage = event => {
        const message = JSON.parse(event.data);
        if (message.id !== messageId) return;
        connection.removeEventListener('message', onMessage);
        resolve(message);
      };
      connection.addEventListener('message', onMessage);
      connection.send(JSON.stringify({ id: messageId, method: 'Runtime.evaluate', params: { expression, awaitPromise: true, returnByValue: true } }));
    });
    assert(!result.error && !result.result.exceptionDetails, JSON.stringify(result));
    return result.result.result.value;
  }
  for (let tries = 0; tries < 30 && !await evaluate("typeof confirmPaidApi === 'function'"); ++tries) await sleep(100);
  assert(await evaluate(`(() => {
    const menu = document.querySelector('#toolsMenu');
    const summary = menu.querySelector('summary');
    const closed = getComputedStyle(summary).backgroundColor;
    summary.click();
    const highlighted = menu.open && getComputedStyle(summary).backgroundColor !== closed;
    summary.click();
    return highlighted && !menu.open && getComputedStyle(summary).backgroundColor === closed;
  })()`));
  assert(await evaluate(`(async () => {
    const fetchFromHost = window.fetch;
    let fika = false;
    let probes = 0;
    const attempts = [];
    window.fetch = (path, ...options) => {
      if (path === '/api/direct/probe') {
        probes++;
        return Promise.resolve(new Response(JSON.stringify({ fika, needsKey: fika }), { status: 200 }));
      }
      if (path === '/api/direct/connect') {
        attempts.push(JSON.parse(options[0].body));
        return Promise.resolve(new Response(null, { status: 204 }));
      }
      if (path === '/api/models')
        return Promise.resolve(new Response(JSON.stringify({ models: ['fixture-model'] }), { status: 200 }));
      return fetchFromHost.call(window, path, ...options);
    };
    try {
      document.querySelector('#directServer').click();
      const dialog = document.querySelector('#directServerDialog');
      const keyField = document.querySelector('#directKeyField');
      const key = document.querySelector('#directKey');
      const connect = document.querySelector('#directConnect');
      const local = dialog.open && key.type === 'password' && keyField.hidden && !connect.disabled
        && probes === 0
        && document.querySelector('#directAddress').value === 'https://127.0.0.1:6969';
      await changeDirectConnection(true);
      const autoConnected = probes === 1 && attempts.length === 1 && attempts[0].adminKey === '' && !dialog.open;
      await new Promise(resolve => setTimeout(resolve, 0));
      fika = true;
      document.querySelector('#directServer').click();
      await changeDirectConnection(true);
      const protectedServer = probes === 2 && attempts.length === 1 && !keyField.hidden && connect.disabled;
      key.value = 'fixture';
      key.dispatchEvent(new Event('input'));
      const ready = !connect.disabled;
      await changeDirectConnection(true);
      document.querySelector('#model').value = 'manual-model';
      await document.querySelector('#modelRefresh').onclick();
      const model = document.querySelector('#modelList').options[0]?.value === 'fixture-model'
        && document.querySelector('#model').value === 'manual-model';
      return local && autoConnected && protectedServer && ready && attempts[1]?.adminKey === 'fixture' && model;
    } finally {
      document.querySelector('#directClose').click();
      window.fetch = fetchFromHost;
    }
  })()`));
  const layout = await evaluate(`(() => {
    const stop = document.querySelector('#apiStop');
    const start = document.querySelector('#translate');
    const offlineStop = document.querySelector('#cfgApiStop');
    const title = document.querySelector('#editorTitle');
    const sidebar = document.querySelector('.controls').getBoundingClientRect();
    const bar = document.querySelector('.task-bar');
    return {
      buttonOrder: !stop.closest('header') && stop.nextElementSibling === start && offlineStop.nextElementSibling.id === 'cfgTranslate',
      manualMode: !document.querySelector('#manualEditor').hidden && document.querySelector('#providerControls').hidden
        && document.querySelector('#apiSelection').hidden && document.querySelector('#taskBar').hidden,
      proportionalWidth: window.innerWidth <= 900 || sidebar.width <= 331,
      taskBarAboveEditor: Boolean(bar.compareDocumentPosition(title) & Node.DOCUMENT_POSITION_FOLLOWING)
        && getComputedStyle(bar).position === 'sticky',
      menu: document.querySelector('#toolsMenu > summary').textContent,
      modesInSidebar: title.closest('.controls') === document.querySelector('#manualMode').closest('.controls')
    };
  })()`);
  assert.deepEqual(layout, { buttonOrder: true, manualMode: true, proportionalWidth: true, taskBarAboveEditor: true, menu: '更多', modesInSidebar: true });
  const queue = await evaluate(`(async () => {
    document.querySelector('#apiMode').click();
    for (let i = 0; i < 50 && (state.rows.length !== 24 || !state.revision); i++)
      await new Promise(resolve => setTimeout(resolve, 20));
    async function waitForTotal(total) {
      for (let i = 0; i < 50 && state.total !== total; i++)
        await new Promise(resolve => setTimeout(resolve, 20));
      return state.total === total;
    }
    document.querySelector('#fieldPicker').open = true;
    document.querySelector('#fieldAll').click();
    if (!await waitForTotal(0)) return { phase: 'clear-fields', total: state.total };
    for (const [label, count] of [['说明', 25], ['设置名', 49]]) {
      const option = [...document.querySelectorAll('#fieldChoices label')].find(item => item.textContent === label);
      option.querySelector('input').click();
      if (!await waitForTotal(count)) return { phase: 'pick-fields', total: state.total };
    }
    await document.querySelector('#selectMatching').onclick();
    if (state.selected.size !== 49 || [...state.selected.values()].some(row => row.field === 'Text'))
      return { phase: 'select-fields', count: state.selected.size };
    document.querySelector('#fieldAll').click();
    if (!await waitForTotal(73)) return { phase: 'all-fields', total: state.total };
    document.querySelector('#fieldPicker').open = false;
    await document.querySelector('#selectMatching').onclick();
    const choices = () => document.querySelectorAll('#rows input[type=checkbox]');
    if (state.selected.size !== 73 || choices().length !== 24 || document.querySelector('#taskBar').hidden
        || !document.querySelector('#manualEditor').hidden)
      return { phase: 'select', count: state.selected.size, boxes: choices().length, status: document.querySelector('#status').textContent };
    choices()[0].click();
    state.page = 2;
    await load();
    choices()[0].click();
    await document.querySelector('#selectMatching').onclick();
    const batch = await prepareQueueBatch();
    if (state.selected.size !== 71 || batch.length !== 30 ||
        batch.some(row => row.id === 'row-0' || row.id === 'row-24')) return { phase: 'exclude', count: state.selected.size, batch };
    await document.querySelector('#apiDraftView').onclick();
    if (state.selected.size !== 71 || !showingDraft() || !document.querySelector('#pageSelection').hidden) return { phase: 'draft' };
    const fetchFromHost = window.fetch;
    const realConfirm = confirmPaidApi;
    let sent;
    confirmPaidApi = async () => true;
    window.fetch = (path, ...options) => {
      if (path !== '/api/f12/translate') return fetchFromHost.call(window, path, ...options);
      sent = JSON.parse(options[0].body).edits;
      const values = sent.slice(0, 2).map(row => ({ id: row.id, group: row.group, text: '模拟译文' }));
      return Promise.resolve(new Response(JSON.stringify({ values, failed: 28 }), { status: 200 }));
    };
    try {
      await document.querySelector('#translate').onclick();
      const next = await prepareQueueBatch();
      return { sent: sent?.length, remaining: state.selected.size, edited: state.edits.size, draft: showingDraft(),
        next: next.length, rotatesFailures: !!sent && !next.some(row => row.id === sent[2].id) };
    } finally {
      confirmPaidApi = realConfirm;
      window.fetch = fetchFromHost;
    }
  })()`);
  assert.deepEqual(queue, { sent: 30, remaining: 69, edited: 2, draft: true, next: 30, rotatesFailures: true });
  assert(await evaluate(`(() => {
    const root = document.documentElement;
    const previous = root.style.zoom;
    root.style.zoom = '200%';
    try {
      return ['apiStop', 'translate', 'cancel', 'clear', 'apiMode'].every(id => {
        const button = document.getElementById(id);
        return button.clientWidth > 0 && button.scrollWidth <= button.clientWidth + 1;
      });
    } finally { root.style.zoom = previous; }
  })()`));
  assert(await evaluate(`(() => {
    window.noticeResult = confirmPaidApi('测试');
    const accept = document.querySelector('#apiNoticeAccept');
    accept.click();
    return accept.disabled && accept.textContent.includes('5') && document.querySelector('#apiNotice').open;
  })()`));
  await evaluate("document.querySelector('#apiNoticeCancel').click()");
  assert.equal(await evaluate('window.noticeResult'), false);
  await evaluate("window.noticeResult = confirmPaidApi('首次确认'); undefined");
  await sleep(5100);
  assert.equal(await evaluate("document.querySelector('#apiNoticeAccept').disabled"), false);
  await evaluate("document.querySelector('#apiNoticeSkip').checked = true; document.querySelector('#apiNoticeAccept').click()");
  assert.equal(await evaluate('window.noticeResult'), true);
  assert.equal(await evaluate("confirmPaidApi('同一浏览器记住选择')"), true);
  await evaluate('setAiStop(true)');
  assert.equal(await evaluate("confirmPaidApi('停止后不允许继续')"), false);
  assert.equal(await evaluate("document.querySelector('#apiStop').textContent"), '恢复 API');
  assert.equal(await evaluate("document.querySelector('#cfgApiStop').textContent"), '恢复 API');
  await evaluate('setAiStop(false)');
  assert.equal(await evaluate("document.querySelector('#apiStop').textContent"), '紧急停止');
  assert(await evaluate(`(() => {
    state.dirty = false;
    const fetchFromHost = window.fetch;
    // Simulate browsers that drop the pagehide request as a tab closes.
    window.fetch = (path, ...options) => path === '/api/browser-tab' && options[0]?.keepalive
      ? Promise.resolve(new Response(null, { status: 204 }))
      : fetchFromHost.call(window, path, ...options);
    return browserLease?.readyState === WebSocket.OPEN;
  })()`));
  const appExit = new Promise(resolve => app.once('exit', resolve));
  const closed = await fetch(`http://127.0.0.1:${debugPort}/json/close/${targets.find(target => target.type === 'page').id}`);
  assert(closed.ok, 'Chromium closed the last workbench tab');
  assert.equal(await Promise.race([appExit, sleep(5500).then(() => 'still running')]), 0);
  console.log('PASS: Chromium sidebar controls at 200%, cross-page queue, API stop/resume, and last-tab service exit.');
} finally {
  connection?.close();
  chrome?.kill();
  app.kill();
}
