using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ConfigurationManagerZhCn
{
    internal static partial class TranslationPanel
    {
        private const int PluginPickerWindowId = -726033;
        private static ConfigEntry<string> _lastPluginConfig, _batchPluginsConfig;
        private static bool _pickerRequested, _pickerVisible, _pickerBatch, _pickerConfirm, _pickerConfirmForLayout, _pickerReturnToList, _pickerFocus;
        private static string _pickerSearch = "", _pickerError = "", _manualOriginalValue = "";
        private static TranslationWorkItem _manualDraftItem;
        private static readonly HashSet<string> PickerDraft = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static Action _pickerAction, _pendingEditorNavigation;
        private static Func<bool> _editorNavigationAvailable;
        private static Vector2 _pickerScroll;
        private static Vector2 _pickerConfirmScroll;
        private static Rect _pickerRect;
        private sealed class PickerRow { internal string Guid, Name, Chinese, Status; }
        private static PickerRow[] _pickerRows = new PickerRow[0];
        private static bool HasUnsavedManualDraft => _manualLoaded && _manualDraftItem != null &&
            !string.Equals(_manualValue ?? "", _manualOriginalValue ?? "", StringComparison.Ordinal);

        internal static bool UsesBatchPluginSelection(int tab, bool webMachine) => tab == 1 || tab == 2 && !webMachine;

        private static void InitializePluginSelection(ConfigFile config)
        {
            _lastPluginConfig = config.Bind("工作台选择", "上次编辑插件", "", "记住人工、网页机翻和更新检查的当前插件。");
            _batchPluginsConfig = config.Bind("工作台选择", "批量翻译插件", "", "批量目标 GUID 列表；空白表示首次使用，[] 表示用户明确清空。");
            _selectedPluginGuid = _lastPluginConfig.Value;
            BatchSelection.Clear(); _selectionInitialized = false;
            if (!string.IsNullOrWhiteSpace(_batchPluginsConfig.Value))
            {
                try
                {
                    foreach (var id in JArray.Parse(_batchPluginsConfig.Value).Values<string>().Where(id => !string.IsNullOrWhiteSpace(id))) BatchSelection.Add(id);
                    _selectionInitialized = true;
                }
                catch (Exception ex) { _log?.LogWarning("Invalid saved plugin selection: " + ex.GetType().Name); }
            }
            ResetPluginPicker();
            _manualLoaded = false; _manualDraftItem = null;
        }

        private static void PersistPluginSelection()
        {
            if (_lastPluginConfig != null) _lastPluginConfig.Value = _selectedPluginGuid ?? "";
            if (_batchPluginsConfig != null && _selectionInitialized)
                _batchPluginsConfig.Value = new JArray(BatchSelection.OrderBy(id => id, StringComparer.OrdinalIgnoreCase)).ToString(Newtonsoft.Json.Formatting.None);
        }

        private static void ResetPluginPicker()
        {
            _pickerRequested = _pickerVisible = _pickerConfirm = _pickerConfirmForLayout = false;
            _pickerAction = _pendingEditorNavigation = null;
            _editorNavigationAvailable = null;
            PickerDraft.Clear(); _pickerRows = new PickerRow[0]; _pickerError = "";
        }

        private static void QueuePluginPicker(Action action) { _pickerAction = action; }
        private static void SyncPluginPickerActions()
        {
            if (_pickerAction == null) return;
            var action = _pickerAction; _pickerAction = null;
            try { action(); }
            catch (Exception ex) { _pickerError = "操作未完成：" + ex.Message; }
        }

        private static void OpenPluginPicker(bool batch)
        {
            _pickerBatch = batch; _pickerRequested = true; _pickerConfirm = false;
            _pickerSearch = _pickerError = ""; _pickerScroll = Vector2.zero; _pickerFocus = true;
            PickerDraft.Clear(); PickerDraft.UnionWith(BatchSelection);
            _captureActive = false;
        }

        private static void CancelPluginPicker()
        {
            if (_pickerConfirm)
            {
                _pickerConfirm = false; _pendingEditorNavigation = null; _editorNavigationAvailable = null; _pickerRequested = _pickerReturnToList;
            }
            else { _pickerRequested = false; PickerDraft.Clear(); }
            _pickerError = "";
        }

        private static void SyncPluginPickerLayout()
        {
            _pickerVisible = _pickerRequested; _pickerConfirmForLayout = _pickerConfirm;
            if (!_pickerVisible) return;
            _pickerRect = PluginPickerRect(ViewportWidth, ViewportHeight);
            var rows = new List<PickerRow>();
            foreach (var snapshot in _snapshots)
            {
                var display = PluginDisplayName(snapshot);
                if (!MatchesPluginSearch(snapshot.PluginName, display, snapshot.Guid, _pickerSearch)) continue;
                TranslationInventory inventory; Inventories.TryGetValue(snapshot.Guid, out inventory);
                var chinese = display.StartsWith(snapshot.PluginName ?? "", StringComparison.OrdinalIgnoreCase)
                    ? display.Substring((snapshot.PluginName ?? "").Length).Trim() : display;
                rows.Add(new PickerRow { Guid = snapshot.Guid, Name = snapshot.PluginName, Chinese = chinese, Status = inventory?.Label ?? "待检查" });
            }
            _pickerRows = rows.OrderByDescending(row => !_pickerBatch && row.Guid == _selectedPluginGuid)
                .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }

        internal static Rect PluginPickerRect(float width, float height)
        {
            var w = Math.Max(1f, Math.Min(620f, width - 16f));
            var h = Math.Max(1f, Math.Min(640f, height - 16f));
            return new Rect(Math.Max(0f, (width - w) / 2f), Math.Max(0f, (height - h) / 2f), w, h);
        }

        internal static bool MatchesPluginSearch(string name, string translated, string guid, string search)
        {
            return new[] { name, translated, guid }.Any(text => (text ?? "").IndexOf((search ?? "").Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void DrawPluginSelector(IList<LocalizationSnapshot> snapshots)
        {
            var batch = UsesBatchPluginSelection(_selectedTab, _machineWeb);
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(batch ? "批量翻译目标" : "当前编辑的插件", TranslationPanelTheme.Section);
            var title = batch ? BatchSelection.Count == 0 ? "尚未选择插件" : "已选择 " + BatchSelection.Count + " 个插件"
                : PluginDisplayName(snapshots[_pluginIndex]);
            if (GUILayout.Button(Content(title + (batch ? "    选择插件 ▾" : "    更换插件 ▾"), "搜索名称、中文名或 GUID，打开独立插件选择窗口"),
                TranslationPanelTheme.PluginButton, GUILayout.MinHeight(36f), GUILayout.MinWidth(0f)))
                QueuePluginPicker(() => OpenPluginPicker(batch));
            if (!batch) DrawInventory(snapshots[_pluginIndex].Guid);
            else
            {
                var names = snapshots.Where(snapshot => BatchSelection.Contains(snapshot.Guid)).Select(snapshot => snapshot.PluginName).ToArray();
                GUILayout.Label(string.Join("、", names.Take(2).ToArray()) + (names.Length > 2 ? " 等 " + names.Length + " 个插件" : ""), TranslationPanelTheme.Muted);
                var onlyMissing = TranslationPanelTheme.Toggle(_onlyMissing, Content("跳过已有有效汉化", "默认保护已有译文；强制重翻也遵守此保护。"));
                if (onlyMissing != _onlyMissing) { _onlyMissing = onlyMissing; _nextBatchCheck = 0f; RequestGuiRefresh(); }
                GUILayout.Label("已跳过 " + _batchPlan.SkippedItems + " 条 · 待翻译 " + _batchPlan.TotalItems + " 条", TranslationPanelTheme.Muted);
                if (_batchPlan.ManualOverlaps > 0 || _batchPlan.AiOverwrites > 0)
                    DrawNotice("与人工译文重复 " + _batchPlan.ManualOverlaps + " 条，显示仍以人工优先。" +
                        (_selectedTab == 1 ? "将更新 AI 译文 " + _batchPlan.AiOverwrites + " 条。" : ""), false);
                if (_batchPlan.Errors.Count > 0) DrawNotice(string.Join("\n", _batchPlan.Errors.ToArray()), false);
            }
            GUILayout.EndVertical();
        }

        private static void DrawPluginPicker()
        {
            GUILayout.Window(PluginPickerWindowId, _pickerRect, (GUI.WindowFunction)DrawPluginPickerWindow, GUIContent.none, GUI.skin.window);
            GUI.BringWindowToFront(PluginPickerWindowId);
        }

        private static void DrawPluginPickerWindow(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(_pickerConfirmForLayout ? "人工译文尚未保存" : _pickerBatch ? "选择批量翻译的插件" : "选择要编辑的插件", TranslationPanelTheme.Section);
            if (GUILayout.Button("取消", GUILayout.Width(60f))) QueuePluginPicker(CancelPluginPicker);
            GUILayout.EndHorizontal();
            if (_pickerConfirmForLayout)
            {
                GUILayout.Label("当前人工译文有未保存修改。请选择保存后切换、放弃修改或取消。", TranslationPanelTheme.Muted);
                _pickerConfirmScroll = GUILayout.BeginScrollView(_pickerConfirmScroll, GUILayout.ExpandHeight(true));
                GUILayout.Label(_manualDraftItem?.Source ?? "", GUI.skin.textArea, GUILayout.MinWidth(0f));
                GUILayout.Label(_manualValue ?? "", GUI.skin.textArea, GUILayout.MinWidth(0f));
                GUILayout.EndScrollView();
                if (!string.IsNullOrEmpty(_pickerError)) GUILayout.Label(_pickerError, TranslationPanelTheme.Notice);
                if (GUILayout.Button("保存后切换", TranslationPanelTheme.Primary)) QueuePluginPicker(() => ResolveEditorNavigation(true));
                if (GUILayout.Button("放弃修改并切换")) QueuePluginPicker(() => ResolveEditorNavigation(false));
                return;
            }
            GUILayout.Label("搜索名称、中文名或 GUID", TranslationPanelTheme.Muted);
            GUI.SetNextControlName("PluginPickerSearch");
            var search = GUILayout.TextField(_pickerSearch, GUILayout.MinWidth(0f));
            if (search != _pickerSearch) { _pickerSearch = search; _pickerScroll = Vector2.zero; }
            if (_pickerFocus && Event.current.type == EventType.Repaint) { GUI.FocusControl("PluginPickerSearch"); _pickerFocus = false; }
            if (_pickerBatch)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("全选搜索结果")) foreach (var row in _pickerRows) PickerDraft.Add(row.Guid);
                if (GUILayout.Button("清空全部选择")) PickerDraft.Clear();
                GUILayout.EndHorizontal();
            }
            _pickerScroll = GUILayout.BeginScrollView(_pickerScroll, false, false, GUILayout.ExpandHeight(true));
            foreach (var row in _pickerRows)
            {
                GUILayout.BeginHorizontal();
                var selected = _pickerBatch ? PickerDraft.Contains(row.Guid) : row.Guid == _selectedPluginGuid;
                var label = row.Name + (string.IsNullOrEmpty(row.Chinese) ? "" : "\n" + row.Chinese);
                if (_pickerBatch)
                {
                    var next = TranslationPanelTheme.Toggle(selected, Content(label, row.Guid), TranslationPanelTheme.PickerToggle, GUILayout.ExpandWidth(true), GUILayout.MinHeight(48f));
                    if (next) PickerDraft.Add(row.Guid); else PickerDraft.Remove(row.Guid);
                }
                else if (GUILayout.Button(Content(label, row.Guid), TranslationPanelTheme.PickerRow,
                    GUILayout.ExpandWidth(true), GUILayout.MinHeight(48f)))
                {
                    var guid = row.Guid; QueuePluginPicker(() => RequestPluginChange(guid));
                }
                GUILayout.Label(row.Status, TranslationPanelTheme.Muted, GUILayout.Width(Mathf.Min(105f, _pickerRect.width * 0.25f)));
                GUILayout.EndHorizontal();
                if (Event.current.type == EventType.Repaint) DrawPickerOutline(GUILayoutUtility.GetLastRect(), selected);
            }
            if (_pickerRows.Length == 0) GUILayout.Label("没有匹配的插件", TranslationPanelTheme.Muted);
            GUILayout.EndScrollView();
            if (!string.IsNullOrEmpty(_pickerError)) GUILayout.Label(_pickerError, TranslationPanelTheme.Notice);
            GUILayout.BeginHorizontal();
            GUILayout.Label(_pickerBatch ? "已选 " + PickerDraft.Count + " / " + _snapshots.Count : "点击插件后直接切换", TranslationPanelTheme.Muted);
            if (_pickerBatch && GUILayout.Button("应用选择", TranslationPanelTheme.Primary, GUILayout.Width(120f))) QueuePluginPicker(ApplyPluginBatchSelection);
            GUILayout.EndHorizontal();
        }

        private static void DrawPickerOutline(Rect rect, bool selected)
        {
            if (rect.width < 6f || rect.height < 6f || (!selected && !rect.Contains(Event.current.mousePosition))) return;
            var previous = GUI.color;
            var color = TranslationPanelTheme.PickerOutlineColor;
            if (!selected) color.a *= 0.55f;
            GUI.color = color;
            try
            {
                var thickness = selected ? 2f : 1f;
                GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), Texture2D.whiteTexture);
            }
            finally { GUI.color = previous; }
        }

        private static void ApplyPluginBatchSelection()
        {
            BatchSelection.Clear(); BatchSelection.UnionWith(PickerDraft.Where(guid => _snapshots.Any(snapshot => snapshot.Guid == guid)));
            _selectionInitialized = true; _pickerRequested = false; _nextBatchCheck = 0f;
            PersistPluginSelection();
        }

        private static void RequestPluginChange(string guid)
        {
            if (guid == _selectedPluginGuid) { _pickerRequested = false; return; }
            if (!_snapshots.Any(snapshot => snapshot.Guid == guid)) { _pickerError = "该插件已不可用，请重新选择。"; return; }
            GuardEditorNavigation(() =>
            {
                var index = _snapshots.ToList().FindIndex(snapshot => snapshot.Guid == guid);
                if (index < 0) { _pickerError = "该插件已不可用，请重新选择。"; return; }
                _pluginIndex = index; _selectedPluginGuid = guid; ResetEditor();
                _editorItems = _snapshots[index].CreateWorkItems();
                _reviewLoadedId = null; _changeIndex = _webItemIndex = 0; _scroll = Vector2.zero;
                _nextChangeCheck = _nextBatchCheck = 0f; _pickerRequested = false;
                PersistPluginSelection();
            }, () => _snapshots.Any(snapshot => snapshot.Guid == guid));
        }

        private static void GuardEditorNavigation(Action action, Func<bool> available = null)
        {
            if (!HasUnsavedManualDraft) { action(); return; }
            _pickerReturnToList = _pickerRequested; _pickerConfirm = _pickerRequested = true;
            _pickerConfirmScroll = Vector2.zero;
            _pendingEditorNavigation = action; _pickerError = ""; _captureActive = false;
            _editorNavigationAvailable = available;
        }

        private static void ResolveEditorNavigation(bool save)
        {
            if (_editorNavigationAvailable != null && !_editorNavigationAvailable())
            { _pickerError = "目标插件或文本已不可用，草稿仍保留。"; return; }
            if (save)
            {
                try
                {
                    var current = _snapshots.FirstOrDefault(snapshot => snapshot.Guid == _manualDraftItem.PluginGuid)?.CreateWorkItems()
                        .FirstOrDefault(item => item.Id == _manualDraftItem.Id);
                    if (current == null || current.Source != _manualDraftItem.Source || current.Context != _manualDraftItem.Context)
                        throw new InvalidOperationException("插件原文已变化，请保留草稿并重新核对。");
                    JsoncLocalization.SaveLayer(_manualDraftItem.PluginGuid, TranslationLayer.Manual, new[] { _manualDraftItem },
                        new Dictionary<string, string> { [_manualDraftItem.Id] = _manualValue });
                    _manualOriginalValue = _manualValue; RequestManagerRefresh();
                }
                catch (Exception ex) { _pickerError = "保存失败，草稿仍保留：" + ex.Message; return; }
            }
            var navigation = _pendingEditorNavigation;
            _pendingEditorNavigation = null; _pickerConfirm = _pickerRequested = false; _manualLoaded = false;
            _editorNavigationAvailable = null;
            navigation?.Invoke();
        }

        private static void ApplyEditorItem(TranslationWorkItem target)
        {
            var index = _editorItems.ToList().FindIndex(item => item.Id == target.Id && item.PluginGuid == target.PluginGuid);
            if (index < 0) return;
            _editorIndex = index; _manualLoaded = false; _manualPreviewScroll = Vector2.zero;
        }
    }
}
