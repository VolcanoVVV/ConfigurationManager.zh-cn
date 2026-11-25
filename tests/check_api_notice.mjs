import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdir, readFile } from 'node:fs/promises';
import { createServer } from 'node:net';
import { join } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const [workbench, browser, temp] = process.argv.slice(2);
assert(workbench && browser && temp, 'Expected the workbench DLL, Chromium executable and test directory');
const home = join(temp, 'browser-fixture');
await mkdir(join(home, 'BepInEx', 'plugins'), { recursive: true });
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
    '--no-first-run', '--remote-debugging-port=0', `--user-data-dir=${browserProfile}`, site
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
    const stop = document.querySelector('#apiStop');
    const start = document.querySelector('#translate');
    const offlineStop = document.querySelector('#cfgApiStop');
    return !stop.closest('header') && stop.nextElementSibling === start && offlineStop.nextElementSibling.id === 'cfgTranslate';
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
  assert.equal(await evaluate("document.querySelector('#apiStop').textContent"), '紧急停止 API');
  console.log('PASS: real Chromium modal, first-use wait, cancel, opt-out and API stop/resume.');
} finally {
  connection?.close();
  chrome?.kill();
  app.kill();
}
