using System;
using System.IO;
using System.Linq;
using TranslationFoundation;
using UnityEngine;

namespace ConfigurationManagerZhCn
{
    // Structural changes are queued until Layout, including visibility and confirmation controls.
    internal static class GlossaryEditorWindow
    {
        private const int WindowId = -726032, PageSize = 12;
        private static bool _requestedVisible, _visible, _dirty, _confirmClose, _confirmForLayout, _rawMode;
        private static Action _pending;
        private static GlossaryEditorSession _session;
        private static GlossaryRow[] _pageRows = new GlossaryRow[0];
        private static string _search = "", _characters = "800", _entries = "24", _status = "", _raw = "", _previewSource = "GroundZero", _preview = "";
        private static int _page, _pages = 1;
        private static Vector2 _scroll;
        private static Rect _rect;
        internal static bool IsVisible => _visible;
        internal static void Show() { _requestedVisible = true; }
        internal static void Suspend() { _requestedVisible = _visible = false; } // Keep draft when F12 is hidden.
        internal static void Reset() { Suspend(); _session = null; _dirty = _confirmClose = false; _pending = null; }
        internal static void RequestClose() { Queue(() => { if (_dirty) _confirmClose = true; else Suspend(); }); }
        private static void MarkDirty() { _dirty = true; _preview = ""; }
        private static void Queue(Action action) { _pending = action; }
        private static void Attempt(Action action)
        {
            try { action(); }
            catch (Exception ex) { _status = "未保存：" + ex.Message; }
        }
        internal static Rect CalculateRect(float width, float height)
        {
            var w = Math.Max(1f, Math.Min(880f, width - 16f));
            var h = Math.Max(1f, Math.Min(730f, height - 16f));
            return new Rect(Math.Max(0f, (width - w) / 2f), Math.Max(0f, (height - h) / 2f), w, h);
        }
        internal static void SyncLayout()
        {
            if (_pending != null) { var action = _pending; _pending = null; Attempt(action); GUI.FocusControl(null); }
            if (_requestedVisible && _session == null)
            {
                Attempt(() =>
                {
                    try { ClientGlossary.Load(); } catch (InvalidDataException) { /* Open a repair draft below. */ }
                    _session = new GlossaryEditorSession(ClientGlossary.FilePath);
                    LoadDraft();
                });
            }
            _visible = _requestedVisible;
            _confirmForLayout = _confirmClose;
            if (!_visible) return;
            _rect = CalculateRect(TranslationPanel.ViewportWidth, TranslationPanel.ViewportHeight);
            var document = _session == null ? null : _session.Document;
            _rawMode = document == null;
            var matches = document == null ? new GlossaryRow[0] : document.Rows.Where(r =>
                string.IsNullOrEmpty(_search) || r.Source.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                r.Target.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            _pages = Math.Max(1, (matches.Length + PageSize - 1) / PageSize);
            _page = Math.Max(0, Math.Min(_page, _pages - 1));
            _pageRows = matches.Skip(_page * PageSize).Take(PageSize).ToArray();
        }
        private static void LoadDraft()
        {
            _dirty = _confirmClose = false; _page = 0; _preview = "";
            _raw = _session.OriginalText;
            if (_session.Document == null) _status = "读取失败，可粘贴正确 TSV 后导入修复：" + _session.LoadError;
            else
            {
                _characters = _session.Document.MaxCharacters.ToString(); _entries = _session.Document.MaxEntries.ToString();
                _status = "已读取。保存后下一次 F12 AI 任务使用，不会自动重翻。";
            }
        }
        private static void ValidateSettings()
        {
            if (_session == null || _session.Document == null) throw new InvalidDataException("请先导入有效术语表草稿。");
            int characters, entries;
            if (!int.TryParse(_characters, out characters) || !int.TryParse(_entries, out entries))
                throw new InvalidDataException("预算请输入整数。");
            _session.Document.MaxCharacters = characters; _session.Document.MaxEntries = entries;
            _session.Document.Serialize();
        }
        private static void Save()
        {
            ValidateSettings();
            var backup = _session.Save(); _dirty = false;
            _status = "已保存。下一任务读取。" + (backup.Length == 0 ? "" : "备份：" + Path.GetFileName(backup));
        }
        private static void Import(string text)
        {
            var document = GlossaryDocument.Parse(text);
            _session.Document = document; _characters = document.MaxCharacters.ToString(); _entries = document.MaxEntries.ToString();
            MarkDirty(); _page = 0; _status = "已导入草稿，尚未写入文件。";
        }
        internal static void Draw()
        {
            GUILayout.Window(WindowId, _rect, (GUI.WindowFunction)DrawContents, GUIContent.none, GUI.skin.window);
            GUI.BringWindowToFront(WindowId);
        }
        private static void DrawContents(int id)
        {
            GUILayout.Label("本地 F12 AI 术语表", TranslationPanelTheme.Title);
            GUILayout.Label("只编辑本机 F12 参照表；服务端游戏文本使用服务器自己的表，玩家不能通过此窗口改写。", TranslationPanelTheme.Muted);
            _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUILayout.ExpandHeight(true));
            GUILayout.BeginVertical(GUILayout.Width(Math.Max(1f, _rect.width - 64f)));
            GUILayout.Label(ClientGlossary.FilePath, TranslationPanelTheme.Muted);
            GUILayout.Label("仅发送当前原文命中的词条；实际字符预算还受任务输入预算的 10% 限制。字符数不等于 token 数。", TranslationPanelTheme.Muted);
            var document = _session == null ? null : _session.Document;
            if (!_rawMode && document != null)
            {
                var enabled = TranslationPanelTheme.Toggle(document.Enabled, "启用术语参考（关闭不删除词条）");
                if (enabled != document.Enabled) { document.Enabled = enabled; MarkDirty(); }
                GUILayout.BeginHorizontal();
                GUILayout.Label("参考字符上限 0–2000", GUILayout.Width(160f));
                var characters = GUILayout.TextField(_characters, GUILayout.Width(72f));
                GUILayout.Label("条目上限 0–64", GUILayout.Width(118f));
                var entries = GUILayout.TextField(_entries, GUILayout.Width(60f));
                GUILayout.EndHorizontal();
                if (characters != _characters || entries != _entries) { _characters = characters; _entries = entries; MarkDirty(); }
                GUILayout.BeginHorizontal();
                GUILayout.Label("搜索", GUILayout.Width(42f));
                _search = GUILayout.TextField(_search);
                if (GUILayout.Button("新增词条", GUILayout.Width(90f))) Queue(() => { document.Rows.Add(new GlossaryRow()); _search = ""; _page = (document.Rows.Count - 1) / PageSize; MarkDirty(); });
                GUILayout.EndHorizontal();
                GUILayout.Label("启用 / 原文（最多80字符） / 译文（最多120字符）", TranslationPanelTheme.Muted);
                foreach (var row in _pageRows)
                {
                    GUILayout.BeginHorizontal();
                    var active = TranslationPanelTheme.Toggle(row.Enabled, "", GUILayout.Width(26f));
                    var source = GUILayout.TextField(row.Source, GUILayout.MinWidth(40f));
                    var target = GUILayout.TextField(row.Target, GUILayout.MinWidth(40f));
                    if (GUILayout.Button("删除", GUILayout.Width(50f))) { var captured = row; Queue(() => { document.Rows.Remove(captured); MarkDirty(); }); }
                    GUILayout.EndHorizontal();
                    if (active != row.Enabled || source != row.Source || target != row.Target)
                    { row.Enabled = active; row.Source = source; row.Target = target; MarkDirty(); }
                }
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("上一页")) Queue(() => _page = Math.Max(0, _page - 1));
                GUILayout.Label((_page + 1) + " / " + _pages + " 页 · 共 " + document.Rows.Count + " 条", GUILayout.Width(190f));
                if (GUILayout.Button("下一页")) Queue(() => _page = Math.Min(_pages - 1, _page + 1));
                GUILayout.EndHorizontal();
                GUILayout.Label("匹配预览（点击时读取草稿与任务预算，不调用 API）", TranslationPanelTheme.Section);
                _previewSource = GUILayout.TextArea(_previewSource, GUILayout.MinHeight(45f));
                if (GUILayout.Button("预览将附加的术语")) Attempt(() =>
                {
                    ValidateSettings(); var plugin = ConfigurationManagerZhCnPlugin.Instance;
                    _preview = document.Preview(_previewSource, plugin == null ? 8000 : plugin.AiMaxInputCharacters.Value);
                    if (_preview.Length == 0) _preview = "没有匹配项，或预算不足/术语表已关闭；本批不附加术语。";
                    _status = "预览完成，未发送网络请求。";
                });
                GUILayout.Label(_preview, TranslationPanelTheme.Muted);
            }
            else
            {
                GUILayout.Label("原文件未通过检查。导入正确内容后才允许保存，保存时会备份原文件。", TranslationPanelTheme.Notice);
                var raw = GUILayout.TextArea(_raw, GUILayout.MinHeight(180f));
                if (raw != _raw) { _raw = raw; MarkDirty(); }
                if (GUILayout.Button("从文本框导入修复草稿")) Queue(() => { if (_session == null) throw new IOException("请先修复无法读取的文件，再重新打开窗口。"); Import(_raw); });
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("从剪贴板导入草稿")) Queue(() =>
            {
                if (_session == null) throw new IOException("原文件不可读取，请先修复文件。");
                if (_dirty) throw new IOException("有未保存草稿，请先保存或关闭并放弃，再导入。");
                Import(GUIUtility.systemCopyBuffer);
            });
            if (GUILayout.Button("导出到剪贴板")) Attempt(() =>
            {
                ValidateSettings(); GUIUtility.systemCopyBuffer = document.Serialize(); _status = "已复制有效 TSV；未写入文件。";
            });
            if (GUILayout.Button("定位文件")) Attempt(() => FileLocationOpener.Open(ClientGlossary.FilePath));
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            GUILayout.Label((_dirty ? "有未保存更改。 " : "") + _status, TranslationPanelTheme.Muted);
            if (_confirmForLayout)
            {
                GUILayout.Label("关闭前处理未保存草稿：", TranslationPanelTheme.Notice);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("保存并关闭")) Queue(() => { Save(); _confirmClose = false; Suspend(); });
                if (GUILayout.Button("放弃并关闭")) Queue(() => { _session.Reload(); LoadDraft(); Suspend(); });
                if (GUILayout.Button("继续编辑")) Queue(() => _confirmClose = false);
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("校验草稿")) Attempt(() => { ValidateSettings(); _status = "校验通过，尚未保存。"; });
                if (GUILayout.Button("保存", TranslationPanelTheme.Primary)) Attempt(Save);
                if (GUILayout.Button("重新读取文件")) Queue(() => { if (_dirty) throw new IOException("有未保存草稿，请先导出或关闭并放弃。"); _session.Reload(); LoadDraft(); });
                if (GUILayout.Button("关闭")) RequestClose();
                GUILayout.EndHorizontal();
            }
        }
    }
}
