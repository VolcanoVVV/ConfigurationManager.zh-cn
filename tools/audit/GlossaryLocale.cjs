// Exercise localized messages without changing glossary protocol or translation data.
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const assert = require('node:assert/strict');
const source = fs.readFileSync(path.join(__dirname, '../glossary-editor/glossary-editor.js'), 'utf8');
const flush = () => new Promise(resolve => setImmediate(resolve));
function load(lang) {
  const elements = new Map();
  const node = () => ({ value: '', checked: false, textContent: '', handlers: {},
    addEventListener(event, handler) { this.handlers[event] = handler; },
    append() {}, replaceChildren() {}, setAttribute() {} });
  const document = { documentElement: { lang }, createElement: node,
    getElementById(id) { if (!elements.has(id)) elements.set(id, node()); return elements.get(id); } };
  document.getElementById('raw');
  const context = { document, TextEncoder, TextDecoder, addEventListener() {}, confirm() { return true; } };
  vm.runInNewContext(source, context);
  return { elements, api: context.GlossaryTools };
}
(async () => {
  const en = load('en'), zh = load('zh-CN');
  assert.equal(en.elements.get('dirty').textContent, 'Draft unchanged');
  assert.equal(zh.elements.get('dirty').textContent, '草稿未更改');
  for (const editor of [en, zh]) { editor.elements.get('sample').handlers.click(); }
  await flush();
  assert.equal(en.elements.get('dirty').textContent, 'Unsaved draft');
  assert.equal(zh.elements.get('dirty').textContent, '有未保存草稿');
  assert.match(en.elements.get('status').textContent, /^Sample terms loaded/);
  en.elements.get('characters').value = 'not a number';
  en.elements.get('characters').handlers.input();
  en.elements.get('validate').handlers.click(); await flush();
  assert.equal(en.elements.get('status').textContent, 'Character limit must be an integer.');
  en.elements.get('raw').value = 'raid\t战局\nRAID\t突袭\n';
  en.elements.get('import-text').handlers.click(); await flush();
  assert.equal(en.elements.get('status').textContent, 'Duplicate source: RAID');
  en.elements.get('raw').value = 'r'.repeat(81) + '\t战局\n';
  en.elements.get('import-text').handlers.click(); await flush();
  assert.match(en.elements.get('status').textContent, /^Row 1: source and translation are required/);
  const data = 'raid\t战局\n';
  assert.equal(en.api.serialize(en.api.parse(data)), zh.api.serialize(zh.api.parse(data)));
  const previewEn = en.api.preview(en.api.parse(data), 'raid', 8000);
  assert.equal(previewEn, zh.api.preview(zh.api.parse(data), 'raid', 8000));
  assert.match(previewEn, /相关术语表.*战局/);
  console.log('PASS: 11 bilingual editor message, validation and unchanged glossary protocol assertions.');
})().catch(error => { console.error(error); process.exitCode = 1; });
