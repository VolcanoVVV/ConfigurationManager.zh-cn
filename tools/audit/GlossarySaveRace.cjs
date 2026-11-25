// Controlled DOM/file doubles. No browser, disk writes, network or user data.
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const assert = require('node:assert/strict');
const elements = new Map();
function element() {
  return { value: '', checked: false, handlers: {}, textContent: '', className: '',
    addEventListener(type, handler) { this.handlers[type] = handler; },
    append() {}, replaceChildren() {}, setAttribute() {}, click() {}, remove() {} };
}
const document = {
  getElementById(id) { if (!elements.has(id)) elements.set(id, element()); return elements.get(id); },
  createElement: element, body: { append() {} }
};
const initial = '#@enabled=true\n#@maxcharacters=800\n#@maxentries=24\nraid\t战局\n';
let currentBytes = new TextEncoder().encode(initial);
let saved = '', completeWrite, writes = 0, pickerCalls = 0, failWrite = false;
const handle = {
  name: 'eft.zh-CN.tsv',
  async getFile() { const bytes = currentBytes; return { size: bytes.length, async arrayBuffer() { return bytes.buffer; } }; },
  async createWritable() { return {
    write(text) { writes++; saved = text; if (failWrite) return Promise.reject(new Error('fixture write failed')); return new Promise(resolve => { completeWrite = resolve; }); },
    async close() { currentBytes = new TextEncoder().encode(saved); }, async abort() {}
  }; }
};
const context = { document, TextEncoder, TextDecoder, Blob, module: { exports: {} },
  URL: { createObjectURL() { return 'blob:test'; }, revokeObjectURL() {} },
  setTimeout() {}, addEventListener() {}, confirm() { return true; },
  async showOpenFilePicker() { pickerCalls++; return [handle]; }
};
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../glossary-editor/glossary-editor.js'), 'utf8'), context);
const flush = () => new Promise(resolve => setImmediate(resolve));
(async () => {
  elements.get('open').handlers.click(); await flush();
  elements.get('save').handlers.click(); await flush();
  if (!completeWrite) throw new Error('Save did not reach the controlled pending write');
  elements.get('characters').value = '1200';
  elements.get('characters').handlers.input();
  elements.get('save').handlers.click(); await flush();
  assert.equal(writes, 1, 'Concurrent saves must not start another write');
  elements.get('open').handlers.click(); await flush();
  assert.equal(pickerCalls, 1, 'File connection must not change during a pending save');
  completeWrite(); await flush();
  assert.equal(/maxcharacters=(\d+)/.exec(saved)[1], '800');
  assert.equal(elements.get('characters').value, '1200');
  assert.equal(elements.get('dirty').textContent, '有未保存草稿', 'Edits made during save remain dirty');
  elements.get('save').handlers.click(); await flush(); completeWrite(); await flush();
  assert.equal(elements.get('dirty').textContent, '草稿未更改');
  assert.match(new TextDecoder().decode(currentBytes), /maxcharacters=1200/);
  elements.get('characters').value = '1400'; elements.get('characters').handlers.input();
  failWrite = true; elements.get('save').handlers.click(); await flush();
  assert.equal(elements.get('dirty').textContent, '有未保存草稿', 'Failed saves keep unsaved changes');
  assert.equal(elements.get('save').disabled, false, 'Save button recovers after errors');
  assert.match(new TextDecoder().decode(currentBytes), /maxcharacters=1200/, 'Failed write retains previous file');
  failWrite = false; currentBytes = new TextEncoder().encode(initial + '# external change\n');
  const previousWrites = writes;
  elements.get('save').handlers.click(); await flush();
  assert.equal(writes, previousWrites, 'External changes are rejected before writing');
  console.log('PASS: 11 offline-editor save, concurrent edit, cancellation-safe connection and write-failure assertions.');
})().catch(error => { console.error(error); process.exitCode = 1; });
