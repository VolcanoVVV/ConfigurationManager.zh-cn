'use strict';

const $ = id => document.getElementById(id);
const session = document.querySelector('meta[name=workbench-session]').content;
const captions = { f12: 'F12 插件设置', server: '服务端游戏文本' };
const fieldNames = { PluginName: '插件名', Category: '分类', DispName: '设置名', Description: '说明', Enum: '枚举', EnumValue: '枚举', Text: '自定义文本' };
const layerNames = { manual: '人工', ai: 'AI', machine: '机翻' };
const state = { view: 'f12', domain: 'f12', workMode: 'manual', layer: 'manual', page: 1, revision: '',
  rows: [], previewRows: [], groups: [], fields: [], focus: null, selected: new Map(), edits: new Map(), editRows: new Map(),
  excluded: new Set(), previous: null, dirty: false, query: 0, busy: null, saving: false, picking: false, exchange: [], syncKey: '' };
let providers = { ai: [], machine: [] };
const cfg = { input: null, plan: null, values: new Map(), failures: new Map(), busy: null, reading: false, dirty: false, page: 1 };
let checkingSync = false;
let hostStopped = false;
let browserTab = crypto.randomUUID();
let browserLease = null;
let reconnectLease = null;
let serverConnection = { id: null, locked: false };
let directBusy = false;
let directProbe = null;
let aiPaused = false;
let aiStopPending = false;
let aiStopUnconfirmed = false;
const aiNotice = $('apiNotice');
const noticeKey = 'volcano-paid-api-notice-v1';

function confirmPaidApi(description) {
  if (aiPaused || aiStopPending || aiNotice.open) return Promise.resolve(false);
  let preference = '';
  try { preference = localStorage.getItem(noticeKey) || ''; } catch { }
  if (preference === 'skip') return Promise.resolve(true);
  const accept = $('apiNoticeAccept');
  const deadline = performance.now() + (preference === 'seen' ? 0 : 5000);
  $('apiNoticeContext').textContent = description;
  $('apiNoticeSkip').checked = false;
  const update = () => {
    const remaining = Math.max(0, Math.ceil((deadline - performance.now()) / 1000));
    accept.disabled = remaining > 0 || aiPaused;
    accept.textContent = remaining > 0 ? `我已悉知（${remaining} 秒）` : '我已悉知';
  };
  update();
  aiNotice.showModal();
  return new Promise(resolve => {
    const timer = setInterval(update, 100);
    aiNotice.addEventListener('close', () => {
      clearInterval(timer);
      resolve(aiNotice.returnValue === 'accept' && !aiPaused);
    }, { once: true });
    $('apiNoticeCancel').onclick = () => aiNotice.close('cancel');
    accept.onclick = () => {
      if (performance.now() < deadline || aiPaused || aiStopPending) return;
      try { localStorage.setItem(noticeKey, $('apiNoticeSkip').checked ? 'skip' : 'seen'); } catch { }
      aiNotice.close('accept');
    };
    aiNotice.returnValue = 'cancel';
  });
}

function showAiStop() {
  for (const id of ['apiStop', 'cfgApiStop']) {
    const button = $(id);
    const label = id === 'apiStop' ? '' : ' API';
    button.textContent = aiStopUnconfirmed ? '重试停止' + label : aiPaused ? '恢复 API' : '紧急停止' + label;
    button.setAttribute('aria-pressed', String(aiPaused));
    button.disabled = aiStopPending;
  }
  updateButtons();
  renderCfg();
}

async function setAiStop(paused) {
  if (aiStopPending) return;
  aiStopPending = true;
  if (paused) {
    aiPaused = true;
    if (aiNotice.open) aiNotice.close('cancel');
    cfg.busy?.abort();
    if (state.busyMode === 'ai') state.busy?.abort();
  }
  showAiStop();
  try {
    const result = await api('ai-stop', { paused });
    aiPaused = result.paused;
    aiStopUnconfirmed = false;
    const status = paused ? '已暂停新的 API 翻译。已经送达的请求可能计费。' : '已恢复 API 翻译。';
    say(status);
    cfgSay(status);
  } catch (error) {
    aiStopUnconfirmed = paused;
    const status = '本地停止状态未确认：' + error.message;
    say(status);
    cfgSay(status);
  } finally { aiStopPending = false; showAiStop(); }
}

$('apiStop').onclick = () => setAiStop(aiStopUnconfirmed || !aiPaused);
$('cfgApiStop').onclick = $('apiStop').onclick;
$('apiNoticeStop').onclick = () => setAiStop(true);
let connectionRequest = 0;

function serverBlocked() {
  return state.domain === 'server' && (!serverConnection.id || serverConnection.locked);
}

function updateWorkspaceVisibility() {
  const auxiliaryVisible = !$('glossaryPanel').hidden || !$('cfgPanel').hidden;
  const blocked = serverBlocked();
  $('translationSummary').hidden = auxiliaryVisible || blocked;
  $('translationWorkspace').hidden = auxiliaryVisible || blocked;
  $('serverNotice').hidden = auxiliaryVisible || !blocked;
  $('serverNotice').textContent = !blocked ? '' : serverConnection.id
    ? '当前服务器已启用 Fika。请在游戏内 F12 验证服务端管理员密钥，验证后本页会自动解锁。'
    : '当前游戏未连接支持网页工作台的 SimpleTranslatorCS。';
  $('reload').hidden = auxiliaryVisible;
  $('reload').textContent = state.domain === 'server' && serverConnection.direct ? '重新拉取服务端原文' : '刷新快照与回执';
}

async function refreshServerConnection() {
  const request = ++connectionRequest;
  const next = await api('server-connection');
  if (request !== connectionRequest) return false;
  const changed = next.id !== serverConnection.id || next.locked !== serverConnection.locked || next.direct !== serverConnection.direct;
  if (state.domain === 'server' && (next.id !== serverConnection.id || next.direct !== serverConnection.direct)) {
    state.selected.clear();
    state.excluded.clear();
  }
  serverConnection = next;
  $('serverReviewOption').hidden = !next.id;
  $('serverReviewOption').disabled = !next.id;
  if (state.domain === 'server' && changed) {
    state.query++;
    if (serverBlocked()) state.busy?.abort();
    if (!next.id && !state.dirty && !state.busy && !state.saving && $('glossaryPanel').hidden && $('cfgPanel').hidden)
      switchView('f12');
  }
  updateWorkspaceVisibility();
  return changed;
}

function syncKey(revision, draftId, receiptId, receiptState, receiptDetail) {
  return JSON.stringify([revision || '', draftId || '', receiptId || '', receiptState || '', receiptDetail || '']);
}

function say(text) { $('status').textContent = text; }
function keyOf(row) { return JSON.stringify([row.group, row.id]); }
function inApiMode() { return state.workMode === 'api'; }
function showingDraft() { return state.view === 'review' && $('reviewScope').value === 'draft'; }
function currentLayer() {
  return !inApiMode() || (state.domain === 'server' && !serverConnection.direct) ? 'manual' : $('mode').value;
}
function draftMode(draft) { return draft?.workMode || (draft?.layer === 'manual' ? 'manual' : 'api'); }
function draftLimit() { return state.domain === 'server' ? 30 : 100; }
let sourceScope = null;
let fieldScope = null;
const inScope = (scope, id) => scope === null || scope.has(id);
const rowInScope = row => inScope(sourceScope, row.group) && inScope(fieldScope, row.field ?? '');
function fieldName(value) { return Object.hasOwn(fieldNames, value) ? fieldNames[value] : value || '文本'; }
function option(value, name) { return new Option(name, value); }
function setOptions(id, options, selected) {
  const select = $(id);
  select.replaceChildren(...options);
  if ([...select.options].some(item => item.value === selected)) select.value = selected;
}
async function api(path, body, signal, keepalive = false) {
  const headers = { 'X-Workbench-Session': session };
  if (body) headers['Content-Type'] = 'application/json';
  const response = await fetch('/api/' + path, {
    method: body ? 'POST' : 'GET', headers, body: body ? JSON.stringify(body) : undefined,
    cache: 'no-store', credentials: 'omit', redirect: 'error', signal, keepalive
  });
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.error || '本地连接失败，请重新打开工作台。');
  return data;
}

function renewBrowserTab() {
  if (browserTab && !hostStopped && (!browserLease || browserLease.readyState !== WebSocket.OPEN))
    api('browser-tab', { id: browserTab, open: true }).catch(() => {});
}
function openBrowserLease() {
  if (!browserTab || hostStopped || browserLease) return;
  const id = browserTab;
  const url = new URL('/browser-lease?id=' + id, window.location.href);
  url.protocol = 'ws:';
  try {
    const socket = new WebSocket(url, session);
    browserLease = socket;
    socket.addEventListener('close', () => {
      if (browserLease !== socket) return;
      browserLease = null;
      if (browserTab === id && !hostStopped) reconnectLease = window.setTimeout(openBrowserLease, 500);
    });
  } catch {
    // The heartbeat remains available if the browser cannot open a socket.
  }
}

window.addEventListener('pagehide', () => {
  if (!browserTab) return;
  const id = browserTab;
  browserTab = null;
  if (!hostStopped) api('browser-tab', { id, open: false }, undefined, true).catch(() => {});
  clearTimeout(reconnectLease);
  browserLease?.close();
  browserLease = null;
});
window.addEventListener('pageshow', event => {
  if (!event.persisted) return;
  browserTab = crypto.randomUUID();
  openBrowserLease();
  renewBrowserTab();
});
function discardConfirmed() {
  return !state.dirty || window.confirm('当前页面有未保存修改。继续会放弃这些修改。');
}
function updateButtons() {
  const apiMode = inApiMode();
  const working = !!state.busy || state.saving || state.picking;
  const count = Math.min(state.selected.size, 30, Math.max(0, draftLimit() - state.edits.size));
  $('taskBar').hidden = $('providerControls').hidden = $('apiExchange').hidden = !apiMode;
  $('manualEditor').hidden = apiMode;
  $('apiSelection').hidden = $('clear').hidden = !apiMode;
  $('pageSelection').hidden = $('selectMatching').hidden = showingDraft();
  $('manualMode').setAttribute('aria-pressed', !apiMode);
  $('apiMode').setAttribute('aria-pressed', apiMode);
  $('manualMode').disabled = $('apiMode').disabled = working;
  $('mode').disabled = working;
  $('apiStop').hidden = $('mode').value !== 'ai' && state.busyMode !== 'ai';
  $('counts').textContent = (apiMode ? '队列 ' + state.selected.size + ' 条 · ' : '') +
    '草稿 ' + state.edits.size + ' / ' + draftLimit();
  $('save').disabled = state.edits.size === 0 || !state.dirty || working || !state.revision;
  $('directPublish').hidden = state.domain !== 'server' || !serverConnection.direct;
  $('directPublish').disabled = !state.previous || state.receiptId === state.previous || state.dirty || working ||
    !state.revision || state.draftRevision !== state.revision || state.savedMode !== state.workMode || state.savedLayer !== state.layer;
  $('translate').disabled = !apiMode || count === 0 || working ||
    ($('mode').value === 'ai' && (aiPaused || aiStopPending));
  $('translate').textContent = state.selected.size > count && count > 0 ? '翻译下一批' : '开始翻译';
  $('translate').title = count ? ('本批最多 ' + count + ' 条') : '请选中待译条目，或先应用当前草稿。';
  $('promptBtn').disabled = !apiMode || count === 0 || working;
  $('selectMatching').disabled = !state.total || working || !state.revision;
  $('apiDraftView').disabled = working;
  $('apiDraftView').textContent = showingDraft() ? '返回待翻译' : '校对草稿';
  $('clear').disabled = working || (!state.selected.size && !state.excluded.size);
  $('cancel').disabled = !state.busy;
  const selectable = state.rows.filter(row => !row.removed && row.stamp);
  $('pageSelect').disabled = working || !selectable.length;
  $('pageSelect').checked = selectable.length > 0 && selectable.every(row => state.selected.has(keyOf(row)));
  $('pageSelect').indeterminate = !$('pageSelect').checked && selectable.some(row => state.selected.has(keyOf(row)));
}
function editValue(row) { return state.edits.get(keyOf(row))?.text ?? row[state.layer] ?? ''; }
function patch(row, text) {
  const key = keyOf(row);
  if (text === (row[state.layer] || '') && row.source !== undefined) {
    state.edits.delete(key);
    state.editRows.delete(key);
  } else {
    state.edits.set(key, { id: row.id, group: row.group, text });
    state.editRows.set(key, row);
  }
  state.dirty = true;
  updateButtons();
}
function selectRow(row, checked) {
  if (row.removed || !row.stamp) return;
  const key = keyOf(row);
  if (checked) {
    state.selected.set(key, { id: row.id, group: row.group, field: row.field ?? '', stamp: row.stamp });
    state.excluded.delete(key);
  } else {
    state.selected.delete(key);
    state.excluded.add(key);
  }
}
function node(tag, className, text) {
  const result = document.createElement(tag);
  if (className) result.className = className;
  if (text !== undefined) result.textContent = text;
  return result;
}
function settingUnit(row) {
  const last = row.id.lastIndexOf('|');
  return row.id.startsWith('setting|') && last > 'setting|'.length ? row.id.slice(0, last) : row.id;
}
function effective(row) {
  for (const layer of ['manual', 'ai', 'machine']) {
    const value = (layer === state.layer ? editValue(row) : row[layer])?.trim();
    if (value) return { value, layer };
  }
  return { value: row.source || '', layer: '' };
}
function previewText(row) {
  const language = $('previewLanguage').value;
  return language === 'en' ? row.source : effective(row).value;
}
function isFocused(row) { return state.focus !== null && keyOf(row) === keyOf(state.focus); }
function fieldButton(row, text, className) {
  const button = node('button', className, text);
  button.type = 'button';
  button.title = fieldName(row.field) + ' · ' + (row.source || '');
  button.dataset.rowKey = keyOf(row);
  button.setAttribute('aria-pressed', isFocused(row));
  button.onclick = () => focusRow(row);
  return button;
}
function previewButton(row, className) {
  const text = previewText(row);
  const button = fieldButton(row, text, className);
  if ($('previewLanguage').value === 'both' && row.source && text !== row.source)
    button.append(node('span', 'f12-original', row.source));
  return button;
}
const collapsedCategories = new Set();
function focusRow(row) {
  state.focus = row;
  for (const button of $('rows').querySelectorAll('button[data-row-key]'))
    button.setAttribute('aria-pressed', button.dataset.rowKey === keyOf(row));
  renderEditor();
  if (window.matchMedia('(max-width: 900px)').matches) $('editorTitle').scrollIntoView({ block: 'start' });
}
function updateEffective() {
  const row = state.focus;
  if (row === null) return;
  const display = effective(row);
  $('editorEffective').textContent = state.domain === 'f12'
    ? '预览使用' + (display.layer ? layerNames[display.layer] + '层' : '原文') + ' · ' + display.value
    : serverConnection.direct ? '修改后保存草稿，再确认发布。' : '修改后保存草稿，再回游戏确认玩家投稿。';
}
function renderEditor() {
  const row = inApiMode() ? null : state.focus;
  $('editorEmpty').hidden = row !== null;
  $('editor').hidden = row === null;
  if (row !== null) {
    const related = state.view === 'f12'
      ? state.previewRows.filter(candidate => candidate.group === row.group && settingUnit(candidate) === settingUnit(row))
      : [row];
    $('editorFields').replaceChildren(...related.map(candidate =>
      fieldButton(candidate, fieldName(candidate.field), 'editor-field')));
    $('editorSource').textContent = row.source;
    $('editorText').value = editValue(row);
    updateEffective();
  }
  updateButtons();
}
function renderPreview(root, group, previewRows) {
  const frame = node('div', 'f12-frame');
  const heading = node('div', 'f12-title');
  const plugin = previewRows.find(row => row.id === 'plugin-name');
  if (plugin) heading.append(previewButton(plugin, 'f12-plugin'));
  else if (previewRows.length) heading.append(node('strong', '', previewRows[0].name));
  frame.append(heading);

  const units = new Map();
  for (const row of previewRows) {
    if (row.id === 'plugin-name') continue;
    const unit = settingUnit(row);
    if (!units.has(unit)) units.set(unit, []);
    units.get(unit).push(row);
  }
  const categories = new Map();
  for (const fields of units.values()) {
    const category = fields.find(row => row.field === 'Category');
    const label = category ? previewText(category) : fields[0].id.startsWith('setting|') ? '常规设置' : '其他可翻译文本';
    const key = JSON.stringify([group, category?.source || label, label]);
    if (!categories.has(key)) {
      const section = node('section', 'f12-category');
      const bar = node('div', 'f12-category-bar');
      const collapsed = collapsedCategories.has(key);
      const toggle = node('button', 'f12-collapse', collapsed ? '+' : '−');
      toggle.setAttribute('aria-label', (collapsed ? '展开 ' : '折叠 ') + label);
      toggle.setAttribute('aria-expanded', !collapsed);
      toggle.onclick = () => {
        if (collapsedCategories.has(key)) collapsedCategories.delete(key);
        else collapsedCategories.add(key);
        renderRows();
      };
      bar.append(toggle, category ? previewButton(category, 'f12-category-title') : node('h3', 'f12-category-title', label));
      section.append(bar);
      frame.append(section);
      categories.set(key, section);
    }
    if (collapsedCategories.has(key)) continue;
    const item = node('div', 'f12-setting');
    const name = fields.find(row => row.field === 'DispName');
    const description = fields.find(row => row.field === 'Description');
    if (name) item.append(previewButton(name, 'f12-setting-name'));
    if (description) item.append(previewButton(description, 'f12-description'));
    for (const row of fields.filter(row => row !== name && row !== description && row !== category))
      item.append(previewButton(row, 'f12-setting-name'));
    if (fields.length === 1 && category) item.append(previewButton(category, 'f12-setting-name'));
    if (fields.some(row => state.selected.has(keyOf(row))))
      item.append(node('span', 'selection-mark', '已选'));
    categories.get(key).append(item);
  }
  root.append(frame);
}
const collapsedPlugins = new Set();
function renderPluginList(root) {
  const language = $('previewLanguage').value;
  const visible = new Map();
  for (const row of state.previewRows) {
    if (!visible.has(row.group)) visible.set(row.group, []);
    visible.get(row.group).push(row);
  }
  for (const group of state.groups) {
    const rows = visible.get(group.id);
    if (!rows) continue;
    const original = group.name || group.id;
    const titleRow = rows.find(row => row.id === 'plugin-name');
    const localized = titleRow ? effective(titleRow).value : group.localizedName || original;
    const open = !collapsedPlugins.has(group.id);
    const title = (language === 'en' ? original : language === 'zh' || original === localized ? localized : original + ' · ' + localized)
        + (group.version ? '  ' + group.version : '');
    const section = node('section', 'plugin-block');
    const button = node('button', 'plugin-heading', title);
    button.title = group.id;
    button.setAttribute('aria-expanded', String(open));
    button.append(node('span', 'plugin-marker', open ? '收起' : '…'));
    button.onclick = () => {
      if (open) collapsedPlugins.add(group.id);
      else collapsedPlugins.delete(group.id);
      renderRows();
    };
    section.append(button);
    if (open) {
      renderPreview(section, group.id, rows);
    }
    root.append(section);
  }
  if (!visible.size) root.append(node('p', 'empty', '当前来源没有匹配的文字。'));
}
function renderReview(root) {
  const wrap = node('div', 'table-wrap');
  const table = node('table', 'review-table');
  const selectable = inApiMode() && !showingDraft();
  const editing = inApiMode() && showingDraft();
  const header = node('tr');
  if (selectable) header.append(node('th', '', '选中'));
  header.append(node('th', '', '原文与变化'), node('th', '', editing ? '译文（可编辑）' : '当前译文'));
  const head = node('thead'); head.append(header); table.append(head);
  const body = node('tbody');
  for (const row of state.rows) {
    const tr = node('tr');
    if (selectable) {
      const pick = node('td');
      const checkbox = node('input');
      checkbox.type = 'checkbox';
      checkbox.disabled = !!row.removed || !row.stamp;
      checkbox.checked = state.selected.has(keyOf(row));
      checkbox.setAttribute('aria-label', '选中 ' + row.source);
      checkbox.onchange = () => {
        if (!state.busy && !state.saving && !state.picking) selectRow(row, checkbox.checked);
        checkbox.checked = state.selected.has(keyOf(row));
        updateButtons();
      };
      pick.append(checkbox);
      tr.append(pick);
    }
    const source = node('td');
    const original = row.removed ? '当前快照中已移除' : row.source;
    source.append(node('span', 'row-meta', row.name + ' · ' + fieldName(row.field)), !inApiMode() && !row.removed
      ? fieldButton(row, original, 'review-source') : node('div', 'review-source', original));
    if (row.change) source.prepend(node('span', 'change-tag', row.change));
    if (row.previousSource && row.previousSource !== row.source)
      source.append(node('p', 'existing', '上次原文 · ' + row.previousSource));
    const target = node('td');
    if (editing) {
      const text = node('textarea');
      text.setAttribute('aria-label', '校对 · ' + row.source);
      text.rows = 2; text.maxLength = 4000; text.spellcheck = false;
      text.value = editValue(row);
      text.oninput = () => { patch(row, text.value); };
      target.append(text);
    } else {
      const current = effective(row);
      target.append(node('span', 'review-target', current.layer ? current.value : '—'));
    }
    tr.append(source, target);
    body.append(tr);
  }
  table.append(body); wrap.append(table); root.append(wrap);
}
function renderRows({ editor = true } = {}) {
  const root = $('rows');
  root.replaceChildren();
  if (!inApiMode() && state.view === 'f12' && state.groups.length) renderPluginList(root);
  else if (!state.rows.length) root.append(node('div', 'empty', state.revision
    ? state.view === 'review' ? '当前范围没有可检查的变化或草稿。' : '没有匹配的原文。'
    : '请先在游戏中导出此工作区的原文快照。'));
  else if (state.view === 'review' || inApiMode()) renderReview(root);
  else {
    for (const row of state.rows) {
      const item = node('div', 'server-row');
      item.append(node('span', 'row-meta', row.name + ' · ' + fieldName(row.field)), previewButton(row, 'server-text'));
      root.append(item);
    }
  }
  if (editor) renderEditor();
  else updateButtons();
}
function catalogParams() {
  const reviewing = state.view === 'review';
  const review = reviewing && !showingDraft()
    ? $('reviewScope').value === 'updates' ? 'updates'
      : state.domain === 'server' ? 'published' : state.layer === 'manual' ? 'published' : state.layer
    : '';
  const params = new URLSearchParams({ group: '', scope: sourceScope === null ? 'all' : 'selected', q: $('search').value, field: '',
    missing: String(!reviewing && $('missing').checked), page: String(state.page),
    view: !inApiMode() && state.view === 'f12' ? 'preview' : '', review });
  if (sourceScope !== null) for (const source of sourceScope) params.append('source', source);
  if (fieldScope !== null) {
    params.set('fieldScope', 'selected');
    for (const field of fieldScope) params.append('fieldType', field);
  }
  return params;
}
async function load({ restore = false, background = false } = {}) {
  if (serverBlocked()) return;
  const query = ++state.query;
  const domain = state.domain;
  const reviewing = state.view === 'review';
  const draftOnly = showingDraft();
  const params = catalogParams();
  const review = params.get('review');
  const [catalog, draftState] = await Promise.all([api(domain + '/catalog?' + params), api(domain + '/draft')]);
  if (query !== state.query || (background && !canRefreshInBackground())) return;
  if (state.revision && catalog.revision !== state.revision && state.dirty) {
    say('检测到新快照。当前编辑已保留，请先复制需要的译文，再刷新工作台。');
    return;
  }
  if (state.revision !== (catalog.revision || '')) {
    state.edits.clear();
    state.editRows.clear();
    state.focus = null;
    state.exchange = [];
    restore = true;
  }
  state.revision = catalog.revision || '';
  state.rows = catalog.rows || [];
  state.groups = catalog.groups || [];
  state.fields = catalog.fields || [];
  state.previewRows = catalog.previewRows || [];
  if (state.focus !== null && !draftOnly) {
    const candidates = state.view === 'f12' ? state.previewRows : state.rows;
    state.focus = candidates.find(row => isFocused(row)) ?? null;
  }
  state.page = catalog.page || 1;
  const draft = draftState.draft;
  const receipt = draftState.receipt;
  state.receiptId = receipt?.id;
  state.draftRevision = draft?.revision;
  state.savedMode = draft ? draftMode(draft) : null;
  state.savedLayer = draft?.layer;
  state.syncKey = syncKey(state.revision, draft?.id, receipt?.id, receipt?.state, receipt?.detail);
  if ((draft?.id ?? null) !== state.previous && state.dirty) {
    say('磁盘草稿已由另一个页面修改，请核对后重新载入。');
  } else {
    state.previous = draft?.id ?? null;
  }
  if (restore && !state.dirty) {
    state.edits.clear();
    state.editRows.clear();
    if (draft?.revision === state.revision && receipt?.id !== draft.id && draftMode(draft) === state.workMode &&
        draft.layer === state.layer) {
      for (const row of draft.edits) {
        const group = (catalog.groups || []).find(item => item.id === row.group);
        state.edits.set(keyOf(row), { id: row.id, group: row.group, text: row.text });
        state.editRows.set(keyOf(row), {
          ...row, name: row.name || group?.name || row.group,
          field: row.field || (row.id === 'plugin-name' ? 'PluginName' : row.id.split('|').at(-1)), [draft.layer]: row.base
        });
      }
    }
  }
  renderScopes();
  $('title').textContent = reviewing ? '翻译更新检查' : captions[domain];
  $('sourceInfo').textContent = catalog.available
    ? '本机快照 · ' + new Date(catalog.capturedAt).toLocaleString() + (domain === 'server' ? serverConnection.direct ? ' · 管理员直连' : ' · 玩家投稿' : ' · 可离线编辑')
    : domain === 'server' ? '请回到 F12，点击“刷新网页服务端快照”。' : '尚未导出快照';
  if (catalog.available && review === 'updates') $('sourceInfo').textContent = catalog.updateBasis === 'f12-tracking'
    ? '与插件已记录的原文基准比对。'
    : catalog.updateBasis === 'server-previous-export' ? '与本次连接的上一次服务端目录比对。'
      : domain === 'server' ? '同一连接再次导出服务端目录后即可比对。' : '请在游戏中重新导出 F12 快照，以获得原文变化。';
  $('receipt').textContent = draft && receipt?.id === draft.id ? receipt.detail
    : draft && draft.revision !== state.revision ? '磁盘草稿来自旧快照，请重新核对原文。'
    : draft && (draftMode(draft) !== state.workMode || draft.layer !== state.layer) ? '另一翻译模式或译文层有已保存的草稿。'
    : draft ? serverConnection.direct && domain === 'server' ? '已保存草稿 · 等待管理员发布' : '已保存草稿 · 等待游戏确认' : '';
  let count = catalog.total || 0;
  let pageTotal = catalog.pageTotal ?? count;
  const size = catalog.size || 24;
  if (draftOnly) {
    const queryText = $('search').value.trim().toLowerCase();
    const draftRows = [...state.editRows.values()].filter(row => inApiMode() || (rowInScope(row) &&
      [row.source, row.context, editValue(row)].some(value => (value || '').toLowerCase().includes(queryText))));
    count = pageTotal = draftRows.length;
    state.page = Math.max(1, Math.min(Number(params.get('page')), Math.ceil(count / size)));
    state.rows = draftRows.slice((state.page - 1) * size, state.page * size);
    if (state.focus) state.focus = state.rows.find(isFocused) ?? null;
  }
  state.total = count;
  updateView();
  $('total').textContent = count + ' 条';
  const pages = Math.max(1, Math.ceil(pageTotal / size));
  $('page').textContent = state.page + ' / ' + pages;
  $('prev').disabled = state.page === 1;
  $('next').disabled = state.page >= pages;
  $('layerHelp').textContent = inApiMode()
    ? '保存到' + layerNames[state.layer] + '层。跨页队列每次最多发送 30 条。'
    : '精翻保存到人工层。清空可恢复下层译文。';
  $('saveHelp').textContent = domain === 'server'
    ? serverConnection.direct ? '先保存校对后的草稿，再确认发布到服务器。' : '保存后请在游戏中预览并确认投稿。'
    : '保存后请在游戏中点击“应用网页 F12 草稿”。';
  renderRows();
}
function run(action) { return Promise.resolve().then(action).catch(error => { if (error.name !== 'AbortError') say(error.message); }); }
function canRefreshInBackground() {
  return !hostStopped && !document.hidden && $('glossaryPanel').hidden && $('cfgPanel').hidden &&
    !state.dirty && !state.busy && !state.saving && !state.picking &&
    !document.activeElement?.matches('input, textarea, select');
}
async function refreshInBackground() {
  if (checkingSync || hostStopped || document.hidden) return;
  checkingSync = true;
  try {
    const connectionChanged = await refreshServerConnection();
    if (serverBlocked()) return;
    if (connectionChanged && state.domain === 'server' && !$('translationWorkspace').hidden &&
        !state.dirty && !state.busy && !state.saving) {
      await load({ restore: true });
      return;
    }
    if (!canRefreshInBackground()) return;
    const domain = state.domain;
    const query = state.query;
    const next = await api(domain + '/status');
    if (domain === state.domain && query === state.query && canRefreshInBackground() &&
        state.syncKey !== syncKey(next.revision, next.draftId, next.receiptId, next.receiptState, next.receiptDetail))
      await load({ restore: true, background: true });
  } catch {
    // Retry while the page stays open. Manual refresh reports errors.
  } finally { checkingSync = false; }
}
function resetEditing({ keepSelection = false } = {}) {
  if (!keepSelection) {
    state.selected.clear();
    state.excluded.clear();
  }
  state.edits.clear(); state.editRows.clear(); state.exchange = []; state.focus = null; state.dirty = false;
  $('exchangePrompt').value = $('exchange').value = '';
}
function renderScope(id, caption, options, scope, update) {
  const included = options.filter(option => inScope(scope, option.id));
  const all = $(id + 'All');
  all.checked = options.length > 0 && included.length === options.length;
  all.indeterminate = included.length > 0 && !all.checked;
  all.disabled = options.length === 0 || !!state.busy || state.saving || state.picking;
  all.onchange = () => update(all.checked ? null : new Set());
  $(id + 'Summary').textContent = all.checked ? '全部' + caption : caption + ' ' + included.length + ' / ' + options.length;
  const choices = options.map(option => {
    const label = node('label', 'checkbox');
    const input = node('input');
    input.type = 'checkbox';
    input.checked = inScope(scope, option.id);
    input.disabled = !!state.busy || state.saving || state.picking;
    input.onchange = () => {
      const next = new Set(included.map(item => item.id));
      if (input.checked) next.add(option.id);
      else next.delete(option.id);
      update(next.size === options.length ? null : next);
    };
    label.append(input, node('span', '', option.label));
    return label;
  });
  $(id + 'Choices').replaceChildren(...choices);
}
function renderScopes() {
  renderScope('source', '来源', state.groups.map(group => ({ id: group.id, label: group.name + ' · ' + group.count })),
    sourceScope, next => changeScope('source', next));
  renderScope('field', '字段', state.fields.map(field => ({ id: field, label: fieldName(field) })),
    fieldScope, next => changeScope('field', next));
}
function changeScope(id, next) {
  if (state.busy || state.saving || state.picking) { renderScopes(); return; }
  if (id === 'source') sourceScope = next;
  else fieldScope = next;
  for (const [key, row] of state.selected) if (!rowInScope(row)) state.selected.delete(key);
  if (state.focus && !rowInScope(state.focus)) state.focus = null;
  state.page = 1;
  renderScopes();
  run(load);
}
function updateView() {
  const review = state.view === 'review';
  const apiDraft = inApiMode() && showingDraft();
  for (const view of ['f12', 'server', 'review']) $(view + 'Tab').setAttribute('aria-pressed', view === state.view);
  $('reviewTools').hidden = !review || apiDraft;
  $('catalogFilters').hidden = $('catalogSearch').hidden = apiDraft;
  $('previewTools').hidden = review || inApiMode();
  $('collapsePlugins').hidden = state.view !== 'f12' || inApiMode();
  $('missingFilter').hidden = review;
  $('reviewDomain').value = state.domain;
  $('title').textContent = apiDraft ? 'API 草稿校对' : review ? '翻译更新检查' : captions[state.domain];
  updateWorkspaceVisibility();
}
function switchView(view, domain = view === 'review' ? state.domain : view) {
  if (domain === 'server' && !serverConnection.id) {
    if (view !== 'review') return;
    domain = 'f12';
  }
  if (!$('cfgPanel').hidden) {
    showCfg(false);
    updateView();
  }
  if (!$('glossaryPanel').hidden) {
    showGlossary(false);
    updateView();
  }
  if (state.view === view && state.domain === domain) return;
  if (state.saving || state.picking || (state.domain !== domain && (state.busy || !discardConfirmed()))) {
    $('reviewDomain').value = state.domain;
    return;
  }
  const changedDomain = state.domain !== domain;
  if (changedDomain) {
    resetEditing();
    sourceScope = null;
    fieldScope = null;
    collapsedPlugins.clear();
    Object.assign(state, { domain, revision: '', previous: null });
    state.layer = currentLayer();
    $('receipt').textContent = '';
  }
  if (view === 'review') {
    $('reviewScope').value = state.edits.size ? 'draft' : 'updates';
  }
  Object.assign(state, { view, page: 1, rows: [], previewRows: [], groups: [], fields: [], focus: null });
  $('search').value = ''; $('missing').checked = false;
  updateView();
  $('sourceInfo').textContent = '正在读取快照…';
  renderRows();
  say('');
  run(() => load({ restore: changedDomain }));
}
function switchWorkMode(mode) {
  if (state.workMode === mode || state.busy || state.saving || state.picking || !discardConfirmed()) return;
  const draftVisible = showingDraft();
  resetEditing({ keepSelection: true });
  state.workMode = mode;
  state.layer = currentLayer();
  if (draftVisible) state.view = state.domain;
  state.page = 1;
  state.rows = [];
  state.previewRows = [];
  updateView();
  renderRows();
  run(() => load({ restore: true }));
}
$('manualMode').onclick = () => switchWorkMode('manual');
$('apiMode').onclick = () => switchWorkMode('api');
async function toggleApiDraft(open) {
  state.view = open ? 'review' : state.domain;
  if (open) $('reviewScope').value = 'draft';
  state.page = 1;
  updateView();
  await load();
}
$('apiDraftView').onclick = () => run(() => toggleApiDraft(!showingDraft()));
function providerChanged() {
  const provider = providers[$('mode').value].find(item => item.id === $('provider').value);
  $('key').value = '';
  $('appId').value = '';
  $('model').value = provider?.model || '';
  clearModelList('model');
}
function clearModelList(model) {
  $(model + 'List').replaceChildren();
  $(model + 'Status').textContent = '';
}
function setupModelRefresh(model, provider, key) {
  $(key).addEventListener('input', () => clearModelList(model));
  $(model + 'Refresh').onclick = async () => {
    const selected = $(provider).value;
    const credential = $(key).value;
    const status = $(model + 'Status');
    const button = $(model + 'Refresh');
    button.disabled = true;
    status.textContent = '正在查询…';
    try {
      const { models } = await api('models', { provider: selected, key: credential });
      if ($(provider).value !== selected || $(key).value !== credential) return;
      $(model + 'List').replaceChildren(...models.map(value => option(value, value)));
      status.textContent = '找到 ' + models.length + ' 个模型，可选择或自行输入。';
      $(model).focus();
    } catch (error) {
      if ($(provider).value === selected && $(key).value === credential) status.textContent = error.message;
    } finally { button.disabled = false; }
  };
}
setupModelRefresh('model', 'provider', 'key');
setupModelRefresh('cfgModel', 'cfgProvider', 'cfgKey');
function modeChanged() {
  const mode = $('mode').value;
  setOptions('provider', providers[mode].map(item => option(item.id, item.name)), mode === 'ai' ? 'deepseek' : 'microsoft');
  $('aiMain').hidden = $('aiOptions').hidden = mode !== 'ai';
  $('machineFields').hidden = mode === 'ai';
  providerChanged();
  updateButtons();
}
$('f12Tab').onclick = () => switchView('f12');
$('serverTab').onclick = () => serverConnection.id ? switchView('server') : $('directServer').click();
for (const button of $('toolsMenu').querySelectorAll('button')) button.addEventListener('click', () => { $('toolsMenu').open = false; });
function updateDirectConnect() {
  $('directConnect').textContent = directProbe?.needsKey ? '验证并连接' : '连接';
  $('directConnect').disabled = directBusy || !$('directAddress').value.trim() ||
    (directProbe?.needsKey && !$('directKey').value.trim());
}
function resetDirectPrompt() {
  directProbe = null;
  $('directKey').value = '';
  $('directKeyField').hidden = true;
  $('directStatus').textContent = '';
  $('directCertificateField').hidden = !directAddressOptions().localHttps;
  updateDirectConnect();
}
function directAddressOptions() {
  const address = $('directAddress').value.trim();
  let localHttps = false;
  try {
    const url = new URL(address);
    localHttps = url.protocol === 'https:' && (/^127(?:\.\d{1,3}){3}$/.test(url.hostname) || url.hostname === '[::1]');
  } catch {}
  return { address, localHttps, trustLocalCertificate: localHttps && $('directLocalCertificate').checked };
}
$('directServer').onclick = () => {
  $('toolsMenu').open = false;
  $('directDisconnect').hidden = !serverConnection.direct;
  if (serverConnection.direct) $('directAddress').value = serverConnection.address;
  resetDirectPrompt();
  if (serverConnection.direct) $('directStatus').textContent = '已连接 ' + serverConnection.address;
  $('directServerDialog').showModal();
};
$('directServerDialog').addEventListener('close', resetDirectPrompt);
$('directServerDialog').addEventListener('cancel', event => { if (directBusy) event.preventDefault(); });
$('directClose').onclick = () => $('directServerDialog').close();
$('directAddress').oninput = resetDirectPrompt;
$('directLocalCertificate').onchange = resetDirectPrompt;
$('directKey').oninput = updateDirectConnect;
async function changeDirectConnection(connect) {
  if (directBusy || state.busy || state.saving || (connect && $('directConnect').disabled) || !discardConfirmed()) return;
  const { address, trustLocalCertificate } = directAddressOptions();
  directBusy = true;
  for (const id of ['directConnect', 'directDisconnect', 'directClose', 'directAddress', 'directKey', 'directLocalCertificate'])
    $(id).disabled = true;
  try {
    if (connect && !directProbe) {
      $('directStatus').textContent = '正在检测服务端…';
      directProbe = await api('direct/probe', { address, trustLocalCertificate });
      if (directProbe.needsKey) {
        $('directKeyField').hidden = false;
        $('directStatus').textContent = directProbe.fika ? '检测到 Fika，请填写服务端管理员密钥。'
          : '远程连接需要管理员密钥。';
        return;
      }
    }
    const adminKey = connect && directProbe?.needsKey ? $('directKey').value.trim() : '';
    $('directKey').value = '';
    $('directStatus').textContent = connect ? '正在授权并拉取原文…' : '正在断开…';
    await api(connect ? 'direct/connect' : 'direct/disconnect', connect ? { address, adminKey, trustLocalCertificate } : {});
    resetEditing();
    sourceScope = null;
    fieldScope = null;
    state.previous = null;
    state.revision = '';
    await refreshServerConnection();
    state.layer = currentLayer();
    $('directServerDialog').close();
    if (connect) {
      if (state.domain === 'server' && state.view === 'server') {
        state.page = 1;
        await load({ restore: true });
      } else switchView('server');
    } else if (state.domain === 'server' && !serverBlocked()) {
      await load({ restore: true });
    }
  } catch (error) {
    if (!directProbe?.needsKey) directProbe = null;
    $('directStatus').textContent = error.message;
  } finally {
    directBusy = false;
    for (const id of ['directDisconnect', 'directClose', 'directAddress', 'directKey', 'directLocalCertificate']) $(id).disabled = false;
    updateDirectConnect();
    if (directProbe?.needsKey) $('directKey').focus();
  }
}
$('directConnect').onclick = () => changeDirectConnection(true);
$('directDisconnect').onclick = () => changeDirectConnection(false);
$('reviewTab').onclick = () => switchView('review');
$('reviewDomain').onchange = () => switchView('review', $('reviewDomain').value);
$('reviewScope').onchange = () => { state.page = 1; run(load); };
$('previewLanguage').onchange = () => renderRows();
$('clearSearch').onclick = () => { $('search').value = ''; state.page = 1; run(load); };
$('collapsePlugins').onclick = () => {
  const groups = [...new Set(state.previewRows.map(row => row.group))];
  const collapse = groups.some(group => !collapsedPlugins.has(group));
  for (const group of groups) if (collapse) collapsedPlugins.add(group); else collapsedPlugins.delete(group);
  $('collapsePlugins').textContent = collapse ? '展开本页' : '全部折叠';
  renderRows();
};
$('editorText').oninput = () => {
  if (!state.focus) return;
  patch(state.focus, $('editorText').value);
  updateEffective();
  renderRows({ editor: false });
};
$('reload').onclick = () => run(async () => {
  await refreshServerConnection();
  if (serverBlocked()) return;
  if (state.busy || state.saving || state.picking || !discardConfirmed()) return;
  if (state.domain === 'server' && serverConnection.direct) await api('direct/refresh', {});
  resetEditing({ keepSelection: true });
  await load({ restore: true });
});
$('shutdown').onclick = () => run(async () => {
  if (state.saving || state.busy || !discardConfirmed()) return;
  if (glossary.loading || (glossary.dirty && !window.confirm('词表有未保存修改。仍要停止本地网页服务？'))) return;
  await api('shutdown', {});
  hostStopped = true;
  state.dirty = false;
  glossary.dirty = false;
  say('本地服务已退出。可关闭此页，或从 F12 重新打开。');
  $('shutdown').disabled = true;
});
$('missing').onchange = () => { state.page = 1; run(load); };
let searchTimer;
$('search').oninput = () => { clearTimeout(searchTimer); searchTimer = setTimeout(() => { state.page = 1; run(load); }, 220); };
$('prev').onclick = () => { state.page--; run(load); };
$('next').onclick = () => { state.page++; run(load); };
$('mode').onchange = () => {
  const layer = currentLayer();
  if (layer !== state.layer && !discardConfirmed()) {
    $('mode').value = state.layer;
    return;
  }
  if (layer !== state.layer) resetEditing({ keepSelection: true });
  state.layer = layer;
  modeChanged();
  run(() => load({ restore: true }));
};
$('provider').onchange = providerChanged;
$('pageSelect').onchange = () => {
  for (const row of state.rows) selectRow(row, $('pageSelect').checked);
  renderRows();
};
$('clear').onclick = () => { state.selected.clear(); state.excluded.clear(); renderRows(); };
$('selectMatching').onclick = () => run(async () => {
  if (!inApiMode() || showingDraft() || state.busy || state.saving || state.picking) return;
  const domain = state.domain;
  const revision = state.revision;
  const params = catalogParams();
  const filter = params.toString();
  params.set('selection', 'matching');
  params.set('revision', revision);
  state.picking = true;
  updateButtons();
  try {
    const { rows } = await api(domain + '/catalog?' + params);
    if (state.domain !== domain || state.revision !== revision || !inApiMode() || catalogParams().toString() !== filter) return;
    state.selected.clear();
    for (const row of rows) if (!state.excluded.has(keyOf(row))) state.selected.set(keyOf(row), row);
    renderRows();
    say('已跨页选中 ' + state.selected.size + ' 条。可取消勾选单条，每次最多翻译 30 条。');
  } finally { state.picking = false; updateButtons(); }
});
async function prepareQueueBatch() {
  const limit = Math.min(30, draftLimit() - state.edits.size);
  const revision = state.revision;
  const domain = state.domain;
  const edits = [...state.selected.values()].slice(0, limit);
  if (limit <= 0 || !edits.length) return [];
  state.picking = true;
  updateButtons();
  try {
    const { rows } = await api(domain + '/selection-resolve', { revision, edits });
    if (revision !== state.revision || domain !== state.domain || !inApiMode()) return [];
    const present = new Set(rows.filter(row => row.source.length <= 30000).map(keyOf));
    const dropped = edits.filter(row => !present.has(keyOf(row)));
    for (const row of dropped) {
      state.selected.delete(keyOf(row));
      state.excluded.add(keyOf(row));
    }
    let characters = 0;
    const batch = [];
    for (const row of rows) {
      if (!present.has(keyOf(row))) continue;
      if (characters + row.source.length > 30000) break;
      characters += row.source.length;
      batch.push(row);
    }
    if (dropped.length) {
      renderRows();
      say('已移除 ' + dropped.length + ' 条已变化或超过长度上限的原文。');
    }
    return batch;
  } finally { state.picking = false; updateButtons(); }
}
$('save').onclick = () => run(async () => {
  const domain = state.domain;
  const previous = state.previous;
  if (previous && !window.confirm('保存后会替换此工作区的上一份磁盘草稿。继续？')) return;
  const sent = [...state.edits.values()];
  state.saving = true;
  updateButtons();
  try {
    const result = await api(domain + '/stage', { revision: state.revision, previous, layer: state.layer, workMode: state.workMode, edits: sent });
    if (state.domain !== domain) return;
    state.previous = result.id;
    state.dirty = JSON.stringify(sent) !== JSON.stringify([...state.edits.values()]);
    say('已保存 ' + result.count + ' 条草稿。' +
      (domain === 'server' && serverConnection.direct ? '校对后可直接发布。' : '请到游戏确认。'));
    await load();
  } finally { state.saving = false; updateButtons(); }
});
$('directPublish').onclick = () => run(async () => {
  if ($('directPublish').disabled || !serverConnection.direct) return;
  if (!window.confirm('将当前已保存草稿发布到 ' + serverConnection.address + '？')) return;
  state.saving = true;
  updateButtons();
  try {
    const result = await api('direct/publish', { id: state.previous, revision: state.revision });
    resetEditing({ keepSelection: true });
    await load({ restore: true });
    say(result.detail + ' 下一批发布前请重新拉取原文。');
  } finally { state.saving = false; updateButtons(); }
});
$('translate').onclick = () => run(async () => {
  if (!inApiMode() || state.busy || state.saving || state.picking || $('translate').disabled) return;
  const mode = $('mode').value;
  const selected = await prepareQueueBatch();
  if (!selected.length) return;
  const editsBefore = new Map(state.edits);
  const revision = state.revision;
  const domain = state.domain;
  const provider = providers[mode].find(item => item.id === $('provider').value);
  if (!provider) return;
  const description = '将 ' + selected.length + ' 条原文发送给 ' + provider.name + '，可能计费。';
  if (mode === 'ai' ? !await confirmPaidApi(description) : !window.confirm(description + '继续？')) return;
  if (state.busy || revision !== state.revision || domain !== state.domain || !inApiMode() || mode !== $('mode').value) return;
  const body = { revision, edits: selected.map(({ id, group, stamp }) => ({ id, group, stamp })), mode, provider: provider.id };
  for (const id of ['key', 'model', 'reasoning', 'prompt', 'appId', 'region', 'sourceLanguage']) body[id] = $(id).value;
  for (const id of ['timeout', 'batch', 'input', 'output', 'requests', 'tokens', 'delay', 'retries']) body[id] = $(id).valueAsNumber;
  body.json = $('json').checked;
  const controller = new AbortController();
  state.busy = controller;
  state.busyMode = mode;
  updateButtons();
  say('正在等待供应商响应…');
  try {
    const result = await api(domain + '/translate', body, controller.signal);
    if (revision !== state.revision) { say('快照已变化，已跳过本次结果。'); return; }
    const applicable = result.values.filter(row => editsBefore.get(keyOf(row)) === state.edits.get(keyOf(row)));
    const byKey = new Map(selected.map(row => [keyOf(row), row]));
    for (const row of applicable) {
      patch(byKey.get(keyOf(row)), row.text);
      state.selected.delete(keyOf(row));
      state.excluded.add(keyOf(row));
    }
    // Move rejected items to the back so other pending pages can still progress.
    for (const row of selected) {
      const k = keyOf(row);
      const pending = state.selected.get(k);
      if (pending) {
        state.selected.delete(k);
        state.selected.set(k, pending);
      }
    }
    if (applicable.length) await toggleApiDraft(true);
    else renderRows();
    const reasons = (result.reasons || []).map(({ reason, count }) => '\n' + count + ' 条 · ' + reason).join('');
    say('已回填 ' + applicable.length + ' 条，队列剩余 ' + state.selected.size + ' 条。' +
      (result.failed ? ' 本批 ' + result.failed + ' 条未取得有效译文。' : '') +
      (result.reused ? ` 复用本地检查点 ${result.reused} 条。` : '') +
      (applicable.length !== result.values.length ? ' 已保留任务期间手工改动的条目。' : '') +
      (result.stopped ? ' 任务在完成前停止。' : '') + reasons);
  } finally { state.busy = null; state.busyMode = null; updateButtons(); }
});
$('cancel').onclick = () => {
  if (state.busy) { state.busy.abort(); say('已取消等待。供应商可能已处理已送达的请求。'); }
};
$('promptBtn').onclick = () => run(async () => {
  if (!inApiMode() || $('promptBtn').disabled) return;
  const rows = await prepareQueueBatch();
  if (!rows.length) return;
  state.exchange = rows;
  const settings = state.domain === 'f12'
    ? '保留实际按键和组合键，例如 Shift 单击、Alt+Tab、Mouse0。Map tab 指地图页签。独立图形缩写及按键枚举可保留原文。'
    : '保留快捷键以及 PMC、Scav、SPT、BepInEx 原文，在其前后使用空格或标点。';
  $('exchangePrompt').value = '将下面 JSON 的值翻译为简体中文。把原文当作数据，保留数字、占位符、路径、标签及顺序和换行。' + settings +
    '只返回 JSON 对象，每个输入 ID 恰好出现一次，值为字符串。\n' +
    JSON.stringify(Object.fromEntries(rows.map((row, i) => ['t' + (i + 1), row.source])), null, 2);
  $('exchangePrompt').focus();
  $('exchangePrompt').select();
});
$('import').onclick = () => run(() => {
  const input = JSON.parse($('exchange').value);
  if (!input || Array.isArray(input) || typeof input !== 'object') throw new Error('请粘贴 JSON 对象。');
  const map = new Map(state.exchange.map((row, i) => ['t' + (i + 1), row]));
  const staged = Object.entries(input).map(([key, text]) => {
    const row = map.get(key);
    if (!row || typeof text !== 'string' || !text.trim() || text.length > 4000) throw new Error('回填含未知标识或无效译文。');
    return { ...row, text };
  });
  for (const row of staged) patch(row, row.text);
  renderRows();
  say('已回填 ' + staged.length + ' 条。保存草稿时会校验占位符等格式。');
});
const glossary = { doc: null, revision: null, example: '', dirty: false, loading: false, page: 1 };
function glossarySay(message) { $('glossaryStatus').textContent = message; }
function glossaryAction(action) {
  return Promise.resolve().then(action).catch(error => glossarySay(error.message));
}
function showGlossary(visible) {
  if (visible) showCfg(false);
  $('glossaryPanel').hidden = !visible;
  updateWorkspaceVisibility();
  $('glossaryTab').setAttribute('aria-pressed', visible);
  if (visible) for (const view of ['f12', 'server', 'review']) $(view + 'Tab').setAttribute('aria-pressed', 'false');
}
async function loadGlossary() {
  if (glossary.loading || (glossary.dirty && !window.confirm('重新读取将替换未保存的术语。继续？'))) return;
  glossary.loading = true;
  renderGlossary();
  try {
    const data = await api('glossary');
    glossary.example = data.example;
    glossary.revision = data.revision ?? null;
    const schema = window.GlossaryTools;
    glossary.doc = schema.parse(data.error ? '' : data.text);
    glossary.dirty = false;
    glossary.page = 1;
    glossarySay(data.error ? '现有词表无法解析，可导入修复后的 TSV。保存前会备份原文件。' : '');
  } finally {
    glossary.loading = false;
    renderGlossary();
  }
}
function renderGlossary() {
  const doc = glossary.doc;
  for (const id of ['glossaryAdd', 'glossaryPreset', 'glossaryExport', 'glossarySave', 'glossaryCheck',
    'glossaryEnabled', 'glossaryChars', 'glossaryLimit', 'glossaryImport'])
    $(id).disabled = !doc || glossary.loading;
  $('glossaryReload').disabled = glossary.loading;
  if (!doc) return;
  $('glossaryEnabled').checked = doc.enabled;
  $('glossaryChars').value = doc.maxcharacters;
  $('glossaryLimit').value = doc.maxentries;
  const query = $('glossarySearch').value.trim().toLowerCase();
  const filtered = doc.rows.filter(row => [row.source, row.target].some(value => value.toLowerCase().includes(query)));
  const size = 16;
  const pages = Math.max(1, Math.ceil(filtered.length / size));
  glossary.page = Math.max(1, Math.min(glossary.page, pages));
  $('glossaryPage').textContent = glossary.page + ' / ' + pages + ' · ' + filtered.length + ' 条';
  $('glossaryPrev').disabled = glossary.page <= 1 || glossary.loading;
  $('glossaryNext').disabled = glossary.page >= pages || glossary.loading;
  const rows = $('glossaryRows'); rows.replaceChildren();
  for (const entry of filtered.slice((glossary.page - 1) * size, glossary.page * size)) {
    const tr = node('tr');
    const enabled = node('input');
    enabled.type = 'checkbox'; enabled.checked = entry.enabled;
    enabled.setAttribute('aria-label', '启用术语');
    enabled.onchange = () => { entry.enabled = enabled.checked; markGlossary(); };
    const remove = node('button', '', '删除');
    remove.onclick = () => {
      doc.rows.splice(doc.rows.indexOf(entry), 1);
      markGlossary(); renderGlossary();
    };
    const controls = [enabled];
    for (const [field, limit, label] of [['source', 80, '术语原文'], ['target', 120, '指定译名']]) {
      const input = node('input');
      input.maxLength = limit; input.value = entry[field];
      input.setAttribute('aria-label', label);
      input.oninput = () => { entry[field] = input.value; markGlossary(); };
      controls.push(input);
    }
    for (const control of [...controls, remove]) {
      control.disabled = glossary.loading;
      const td = node('td'); td.append(control); tr.append(td);
    }
    rows.append(tr);
  }
}
function markGlossary() {
  glossary.dirty = true;
  $('glossaryHits').textContent = '';
  glossarySay('术语表有未保存修改。');
}
$('glossaryTab').onclick = () => {
  showGlossary(true);
  if (!glossary.doc) glossaryAction(loadGlossary);
};
$('glossaryReload').onclick = () => glossaryAction(loadGlossary);
$('glossarySearch').oninput = () => { glossary.page = 1; renderGlossary(); };
$('glossaryPrev').onclick = () => { glossary.page--; renderGlossary(); };
$('glossaryNext').onclick = () => { glossary.page++; renderGlossary(); };
$('glossaryEnabled').onchange = () => { glossary.doc.enabled = $('glossaryEnabled').checked; markGlossary(); };
$('glossaryChars').oninput = () => { glossary.doc.maxcharacters = $('glossaryChars').value; markGlossary(); };
$('glossaryLimit').oninput = () => { glossary.doc.maxentries = $('glossaryLimit').value; markGlossary(); };
$('glossaryAdd').onclick = () => {
  glossary.doc.rows.unshift({ enabled: true, source: '', target: '' });
  $('glossarySearch').value = ''; glossary.page = 1; markGlossary(); renderGlossary();
  $('glossaryRows').querySelector('input[aria-label="术语原文"]')?.focus();
};
$('glossaryPreset').onclick = () => glossaryAction(() => {
  if (!glossary.example) throw new Error('未找到示例术语表。');
  if (glossary.doc.rows.length && !window.confirm('用示例替换当前编辑的词表？')) return;
  glossary.doc = window.GlossaryTools.parse(glossary.example);
  glossary.page = 1; markGlossary(); renderGlossary();
});
$('glossaryImport').onchange = () => glossaryAction(async () => {
  const input = $('glossaryImport');
  try {
    const file = input.files[0];
    if (!file) return;
    if (file.size > 262144) throw new Error('术语表超过 256 KiB。');
    if (glossary.doc.rows.length && !window.confirm('载入 TSV 将替换当前编辑的词表。继续？')) return;
    glossary.doc = window.GlossaryTools.parse(await file.text());
    glossary.page = 1; markGlossary(); renderGlossary();
  } finally { input.value = ''; }
});
$('glossaryExport').onclick = () => glossaryAction(() => {
  const data = window.GlossaryTools.serialize(glossary.doc);
  const link = node('a');
  const url = URL.createObjectURL(new Blob([data], { type: 'text/tab-separated-values;charset=utf-8' }));
  link.href = url; link.download = 'eft.zh-CN.tsv';
  document.body.append(link); link.click(); link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
});
$('glossarySave').onclick = () => glossaryAction(async () => {
  const text = window.GlossaryTools.serialize(glossary.doc);
  glossary.loading = true; renderGlossary();
  try {
    const saved = await api('glossary', { text, revision: glossary.revision });
    glossary.revision = saved.revision; glossary.dirty = false;
    glossarySay('已保存。接下来执行网页 AI 翻译时会采用这份词表。');
  } finally { glossary.loading = false; renderGlossary(); }
});
$('glossaryCheck').onclick = () => glossaryAction(() => {
  $('glossaryHits').textContent = window.GlossaryTools.preview(glossary.doc, $('glossaryProbe').value, 12000) || '无命中术语。';
});
function showCfg(visible) {
  if (visible) showGlossary(false);
  $('cfgPanel').hidden = !visible;
  $('cfgTab').setAttribute('aria-pressed', visible);
  if (visible) {
    for (const tab of ['f12Tab', 'serverTab', 'reviewTab', 'glossaryTab']) $(tab).setAttribute('aria-pressed', 'false');
    renderCfg();
  }
  updateWorkspaceVisibility();
}
function cfgScope() {
  const query = $('cfgSearch').value.trim().toLowerCase();
  return (cfg.plan?.items || []).filter(row => !query ||
    [row.id, row.source, row.section, cfg.values.get(row.id)].some(text => text?.toLowerCase().includes(query)));
}
function renderCfg() {
  const pageSize = 15;
  const rows = cfgScope();
  const pages = Math.max(1, Math.ceil(rows.length / pageSize));
  cfg.page = Math.min(Math.max(1, cfg.page), pages);
  $('cfgRows').replaceChildren(...rows.slice((cfg.page - 1) * pageSize, cfg.page * pageSize).map(row => {
    const tr = node('tr');
    tr.append(node('td', '', fieldName(row.field) + (row.section ? ' · ' + row.section : '')),
      node('td', '', row.source), node('td', '', cfg.values.get(row.id) || cfg.failures.get(row.id) || '待翻译'));
    return tr;
  }));
  $('cfgTable').hidden = $('cfgPages').hidden = !cfg.plan;
  document.querySelector('#cfgPanel .cfg-filters').hidden = !cfg.plan;
  $('cfgPage').textContent = rows.length + ' 个字段 · ' + cfg.page + ' / ' + pages + ' 页';
  $('cfgPrev').disabled = cfg.page === 1;
  $('cfgNext').disabled = cfg.page >= pages;
  const locked = Boolean(cfg.busy) || cfg.reading;
  const ready = cfg.plan && $('cfgGuid').value.trim() === cfg.plan.guid;
  $('cfgFile').disabled = $('cfgGuid').disabled = locked;
  $('cfgRead').disabled = !cfg.input || locked;
  $('cfgTranslate').disabled = locked || aiPaused || aiStopPending || !ready || !rows.some(row => !cfg.values.has(row.id)) || !providers.ai.length;
  $('cfgDownload').disabled = locked || !ready || cfg.values.size === 0;
  $('cfgCancel').disabled = !cfg.busy;
  if (cfg.plan) $('cfgSource').textContent = cfg.input.file + ' · ' + cfg.plan.name + ' · 已译 ' +
    cfg.values.size + ' / ' + cfg.plan.items.length + ' 个静态字段' +
    (cfg.plan.skippedTypes ? ' · ' + cfg.plan.skippedTypes + ' 组候选值由 F12 确认' : '');
}
function cfgSay(text) { $('cfgStatus').textContent = text; }
function cfgAction(action) {
  return Promise.resolve().then(action).catch(error => {
    cfgSay(error.name === 'AbortError' ? '已取消等待。供应商可能已处理已经发出的请求。' : error.message);
  });
}
async function cfgRead(input, guid) {
  if (cfg.busy || cfg.reading || (cfg.dirty && !window.confirm('重新读取将丢弃本页尚未下载的译文。继续？'))) return;
  cfg.reading = true;
  renderCfg();
  try {
    const plan = await api('cfg/preview', { ...input, guid });
    Object.assign(cfg, { plan, input: { ...input, guid: plan.guid }, values: new Map(), failures: new Map(), page: 1, dirty: false });
    $('cfgGuid').value = plan.guid;
    $('cfgSearch').value = '';
    cfgSay('请核对插件 GUID，按需筛选要试译的字段。');
  } finally { cfg.reading = false; renderCfg(); }
}
function cfgProviderChanged() {
  const provider = providers.ai.find(item => item.id === $('cfgProvider').value);
  $('cfgKey').value = '';
  $('cfgModel').value = provider?.model || '';
  clearModelList('cfgModel');
}
$('cfgTab').onclick = () => showCfg(true);
$('cfgProvider').onchange = cfgProviderChanged;
$('cfgGuid').oninput = renderCfg;
$('cfgSearch').oninput = () => { cfg.page = 1; renderCfg(); };
$('cfgPrev').onclick = () => { cfg.page--; renderCfg(); };
$('cfgNext').onclick = () => { cfg.page++; renderCfg(); };
$('cfgFile').onchange = () => cfgAction(async () => {
  const input = $('cfgFile');
  const file = input.files[0];
  try {
    if (!file) return;
    if (file.size > 262144) throw new Error('CFG 文件超过 256 KiB。');
    let text;
    try { text = new TextDecoder('utf-8', { fatal: true }).decode(await file.arrayBuffer()); }
    catch { throw new Error('CFG 需要使用 UTF-8 编码。'); }
    await cfgRead({ file: file.name, cfg: text }, '');
  } finally { input.value = ''; }
});
$('cfgRead').onclick = () => cfgAction(() => cfgRead(cfg.input, $('cfgGuid').value.trim()));
$('cfgCancel').onclick = () => cfg.busy?.abort();
$('cfgTranslate').onclick = () => cfgAction(async () => {
  if ($('cfgTranslate').disabled) return;
  const max = $('cfgCount').valueAsNumber;
  if (!Number.isInteger(max) || max < 1 || max > 120) throw new Error('本次字段数须为 1 至 120。');
  const rows = cfgScope().filter(row => !cfg.values.has(row.id));
  const ids = [...rows.filter(row => !cfg.failures.has(row.id)), ...rows.filter(row => cfg.failures.has(row.id))]
    .slice(0, max).map(row => row.id);
  const provider = providers.ai.find(item => item.id === $('cfgProvider').value);
  if (!provider || !ids.length || !await confirmPaidApi('将最多 ' + ids.length + ' 个静态字段发送给 ' + provider.name + '，可能计费。')) return;
  const controller = new AbortController();
  cfg.busy = controller;
  renderCfg();
  cfgSay('正在翻译本次字段…');
  try {
    const result = await api('cfg/translate', { ...cfg.input, ids, provider: provider.id, key: $('cfgKey').value,
      model: $('cfgModel').value, reasoning: 'off', batch: 12 }, controller.signal);
    for (const [id, reason] of Object.entries(result.failures)) cfg.failures.set(id, reason);
    const received = Object.entries(result.values);
    for (const [id, text] of received) {
      cfg.values.set(id, text);
      cfg.failures.delete(id);
    }
    if (received.length) cfg.dirty = true;
    cfgSay('本次完成 ' + received.length + ' 条，累计完成 ' + cfg.values.size + ' 条。' +
      (result.reused ? ` 复用本地检查点 ${result.reused} 条。` : '') +
      (Object.keys(result.failures).length ? ' 失败原因可在列表查看。' : '') + (result.stopped ? ' 已达到本次任务限制。' : ''));
  } finally { cfg.busy = null; renderCfg(); }
});
$('cfgDownload').onclick = () => cfgAction(async () => {
  if ($('cfgDownload').disabled) return;
  cfg.reading = true;
  renderCfg();
  try {
    const result = await api('cfg/export', { ...cfg.input, values: Object.fromEntries(cfg.values) });
    const link = node('a');
    const url = URL.createObjectURL(new Blob([result.text], { type: 'application/json;charset=utf-8' }));
    link.href = url;
    link.download = result.name;
    document.body.append(link); link.click(); link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    cfg.dirty = false;
    cfgSay('已导出 ' + result.name + '。请先检查内容。');
  } finally { cfg.reading = false; renderCfg(); }
});
window.addEventListener('beforeunload', event => {
  if (state.dirty || glossary.dirty || state.busy || state.saving || cfg.dirty || cfg.busy) { event.preventDefault(); event.returnValue = ''; }
});
run(async () => {
  $('shutdown').hidden = document.querySelector('meta[name=workbench-launch]').content !== 'desktop';
  openBrowserLease();
  renewBrowserTab();
  aiPaused = (await api('ai-stop')).paused;
  showAiStop();
  window.setInterval(renewBrowserTab, 15000);
  window.setInterval(refreshInBackground, 5000);
  document.addEventListener('visibilitychange', () => {
    if (!document.hidden) renewBrowserTab();
    refreshInBackground();
  });
  providers = await api('providers');
  setOptions('cfgProvider', providers.ai.map(item => option(item.id, item.name)));
  cfgProviderChanged();
  modeChanged();
  updateView();
  await refreshServerConnection();
  await load({ restore: true });
  if (document.querySelector('meta[name=workbench-mode]').content === 'standalone' && !serverConnection.id)
    $('directServer').click();
});
