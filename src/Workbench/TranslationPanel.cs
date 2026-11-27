using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using ConfigurationManager;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ConfigurationManagerZhCn
{
    internal enum WorkbenchDomain
    {
        F12Settings,
        ServerGameText
    }

    internal static partial class TranslationPanel
    {
        private static readonly object JobSync = new object();
        private static int _lifecycleGeneration;
        private static volatile bool _shutdown;
        private static ConfigurationManagerZhCnPlugin _plugin;
        private static ManualLogSource _log;
        private static object _manager;
        private static bool _expanded;
        private static volatile bool _refreshRequested;
        private static Vector2 _scroll;
        private static int _selectedTab;
        // The optional server domain is only drawn after a successful SimpleTranslatorCS
        // capabilities handshake. Other games keep the original F12-only workbench.
        private static WorkbenchDomain _domain = WorkbenchDomain.F12Settings;
        private static WorkbenchDomain? _pendingDomain;
        private static readonly HashSet<string> ServerSourceSelection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _serverSourcesInitialized;
        private static int _serverSelectionGeneration = -1;
        private static ServerWorkbenchSnapshot _serverSelectionSnapshot;
        private static ServerWorkbenchSnapshot _serverSnapshotForLayout;
        private static ServerTranslationCapabilities _serverCapabilitiesForLayout;
        private static ServerProviderState _serverProvider;
        private static bool _serverAvailableForLayout, _serverCanDiscoverForLayout, _serverDiscoveryRunningForLayout;
        private static bool _serverLockedForLayout, _serverAdminForLayout;
        private static string _serverDiscoveryStatusForLayout;
        private static bool _chooseServerAiProvider, _chooseServerMachineProvider;
        private static int _serverNextProvider = -1;
        private static string _serverMachineProfile = "default";
        private static JObject _serverMachineOptions;
        private static string _serverProfile = "default";
        private static JObject _serverOptions;
        private static readonly AiModelSelection AiModels = new AiModelSelection();
        private static AiModelSelection ServerModels = new AiModelSelection();
        private static bool _serverAdvanced, _exchangeOpen, _exchangeKeepOriginal;
        private static TranslationExchange _exchange;
        private static string _exchangeText = string.Empty;
        private static int _aiWebsite;
        private static string _serverAiApiKey = string.Empty;
        private static string _serverMachineApiKey = string.Empty;
        private static string _serverMachineAppId = string.Empty;
        private static bool _showServerApiKey;
        private static List<ServerContributionEntry> _serverPending = new List<ServerContributionEntry>();
        private static int _serverPendingIndex;
        private static string _serverPendingValue = string.Empty;
        private static string _serverPendingLoadedId;
        private static ServerReviewPage _serverReviewPage = new ServerReviewPage();
        private static string _serverReviewQuery = "", _serverReviewGroup = "";
        private static int _serverReviewLayer;
        private static bool _serverReviewChangesOnly;
        private static List<ServerJobState> _serverRecentJobs;
        private static int _serverReviewIndex;
        private static string _serverReviewValue = string.Empty;
        private static string _serverReviewLoadedId;
        private static volatile bool _serverJobActive;
        private static string _serverAdminKey = string.Empty;
        private static bool _showServerAdminKey;
        private static int _pluginIndex;
        private static int _editorIndex;
        private static string _manualValue = string.Empty;
        private static Vector2 _manualPreviewScroll;
        private static bool _manualLoaded;
        private static string _machineApiKey = string.Empty;
        private static string _machineAppId = string.Empty;
        private static bool _machineWeb;
        private static int _websiteIndex;
        private static int _webItemIndex;
        private static string _webTranslation = string.Empty;
        private static TranslationWorkItem _webPendingItem;
        private static bool _chooseAiProvider, _chooseMachineProvider, _showBuiltInPrompt, _forceAi;
        private static readonly Dictionary<string, string> AiKeys = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> MachineKeys = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> MachineAppIds = new Dictionary<string, string>();
        private static readonly MachineProviderDefinition[] WebProviders = TranslationProviderCatalog.MachineProviders.Where(p => p.WebsiteUrl != null).ToArray();
        private static IList<TranslationChange> _changes = new List<TranslationChange>();
        private static int _changeIndex;
        private static TranslationLayer _reviewLayer = TranslationLayer.Manual;
        private static string _reviewValue = string.Empty;
        private static string _reviewLoadedId;
        private static bool _hasBaseline;
        private static int _pendingAiCount;
        private static float _nextChangeCheck;
        private static string _changePluginGuid;
        private static string _aiApiKey = string.Empty;
        private static bool _showMachineKey;
        private static bool _showAiKey;
        private static CancellationTokenSource _cancellation;
        private static Task _job;
        private static int? _pendingApiTab;
        private static bool _apiTabAccepted, _paidNoticeForLayout, _paidJob;
        private static int _apiServerGeneration;
        private static volatile bool _serverStopRequested;
        private static int _serverCancelSending;
        private static float _paidNoticeUntil;
        private static string _recoverableJobForLayout;
        private static string _status = "就绪";
        private static int _progressCompleted;
        private static int _progressTotal;
        private static string _progressDetail = string.Empty;
        private static string _progressItems = string.Empty;
        private static bool _showProgressItems;
        private static Vector2 _progressItemsScroll;
        private static Vector2 _statusScroll;
        private static bool _showStatusDetails, _statusDetailsForLayout;
        private static float _scaleDraft = -1f;
        private static DateTime _progressStarted;
        private static TranslationDiagnostics _activeDiagnostics;
        private const int DrawerWindowId = -726031;
        private const int PaidNoticeWindowId = -726032;
        private static bool _drawerVisible;
        private static Rect _drawerRect;
        private static float _openedAt;
        private static int _requestedTab;
        private static bool _aiAdvanced;
        private static bool _machineAdvanced;
        private static string _selectedPluginGuid;
        private static readonly HashSet<string> BatchSelection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, TranslationInventory> Inventories = new Dictionary<string, TranslationInventory>(StringComparer.OrdinalIgnoreCase);
        private static bool _selectionInitialized, _onlyMissing = true;
        private static float _nextBatchCheck;
        private static TranslationBatchPlan _batchPlan = new TranslationBatchPlan();
        private static IList<LocalizationSnapshot> _snapshots = new List<LocalizationSnapshot>();
        private static bool _jobRunningForLayout;
        private static bool _drawerOwnsPointer;
        private static IList<TranslationWorkItem> _editorItems = new List<TranslationWorkItem>();
        private static int _batchItemCount;
        private static TranslationWorkItem _pendingCapture;
        private static TranslationWorkItem _capturedItem;
        private static readonly PropertyInfo VisibleGuiClip = typeof(GUI).Assembly.GetType("UnityEngine.GUIClip")?
            .GetProperty("visibleRect", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly string[] Tabs = { "精翻", "API", "机翻", "显示范围", "翻译更新", "外观" };
        private static readonly string[] ServerAdminTabs = { "人工文件", "API", "机翻", "译文校对", "文件检查", "外观" };
        private static readonly string[] ServerPlayerTabs = { "API", "机翻", "提交说明" };
        private static string[] _colorDraft;
        private static int _expandedColor = -1;

        private static bool _captureActive;
        private static bool _insideManagerContent;
        private static string _currentPluginGuid;
        private static SettingEntryBase _currentSetting;
        private static bool _insideCustomControl;
        private static bool _insideSettingValue;

        private static GUIContent Content(string text)
        {
            return new GUIContent(text);
        }

        internal static GUIContent Content(string text, string tooltip)
        {
            return new GUIContent(text, null, tooltip);
        }

        // IL2CPP Unity 2022 can strip the array overload used by GUILayout.Toolbar and
        // SelectionGrid (GUIContent.Temp(Il2CppStringArray)). Drawing the same controls
        // as individual toggles keeps the selected-state styling without that call.
        private static int DrawSelectionButtons(int selected, string[] labels, int columns, GUIStyle style)
        {
            if (labels == null || labels.Length == 0)
                return selected;

            columns = Math.Max(1, columns);
            for (var row = 0; row < labels.Length; row += columns)
            {
                GUILayout.BeginHorizontal();
                var end = Math.Min(row + columns, labels.Length);
                for (var index = row; index < end; index++)
                {
                    if (GUILayout.Toggle(index == selected, labels[index], style,
                        GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true)))
                        selected = index;
                }
                GUILayout.EndHorizontal();
            }
            return selected;
        }

        private static int DrawSelectionButtons(int selected, GUIContent[] labels, int columns, GUIStyle style)
        {
            if (labels == null || labels.Length == 0)
                return selected;

            columns = Math.Max(1, columns);
            for (var row = 0; row < labels.Length; row += columns)
            {
                GUILayout.BeginHorizontal();
                var end = Math.Min(row + columns, labels.Length);
                for (var index = row; index < end; index++)
                {
                    if (GUILayout.Toggle(index == selected, labels[index], style,
                        GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true)))
                        selected = index;
                }
                GUILayout.EndHorizontal();
            }
            return selected;
        }

        internal static void Initialize(ConfigurationManagerZhCnPlugin plugin, ManualLogSource log)
        {
            Interlocked.Increment(ref _lifecycleGeneration);
            _shutdown = false;
            _plugin = plugin;
            _log = log;
            InitializePluginSelection(plugin.Config);
            _serverAdminKey = plugin.ServerAdminKey.Value ?? string.Empty;
            ServerWorkbenchClient.AdminKey = _serverAdminKey;
            TranslationCredentialStore.Initialize(log);
            AiKeys.Clear();
            MachineKeys.Clear();
            MachineAppIds.Clear();
            if (plugin.RememberApiCredentials.Value)
            {
                TranslationCredentialSnapshot saved;
                if (TranslationCredentialStore.TryLoad(out saved))
                {
                    CopyCredentials(saved.AiKeys, AiKeys);
                    CopyCredentials(saved.MachineKeys, MachineKeys);
                    CopyCredentials(saved.MachineAppIds, MachineAppIds);
                }
                else _status = TranslationCredentialStore.LastError;
            }
            AiKeys.TryGetValue(plugin.AiProvider.Value, out _aiApiKey);
            MachineKeys.TryGetValue(plugin.MachineProvider.Value, out _machineApiKey);
            MachineAppIds.TryGetValue(plugin.MachineProvider.Value, out _machineAppId);
        }

        internal static void Shutdown()
        {
            lock (JobSync)
            {
                _shutdown = true;
                Interlocked.Increment(ref _lifecycleGeneration);
                _cancellation?.Cancel();
            }
            ServerWorkbenchClient.Reset();
            GlossaryEditorWindow.Reset();
            ResetPluginPicker();
            if (_plugin != null && _plugin.RememberApiCredentials.Value) PersistCurrentCredentials();
            ClearServerCredentials();
            _plugin = null;
            _manager = null;
            _job = null;
            _serverJobActive = false;
            ResetApiTab();
            _paidNoticeForLayout = _paidJob = _serverStopRequested = false;
        }

        internal static void RequestManagerRefresh()
        {
            _refreshRequested = true;
            _nextChangeCheck = 0f;
            _nextBatchCheck = 0f;
        }

#if !IL2CPP
        private static bool _betterMenuEscape;
        internal static bool CapturingMenuText => _captureActive;

        internal static bool QueueBetterMenuEscape()
        {
            if (!_expanded && !GlossaryEditorWindow.IsVisible && !_pickerVisible) return false;
            _betterMenuEscape = true;
            return true;
        }

        internal static void DrawBetterMenuButtons()
        {
            if (_plugin == null) return;
            // DrawSidebar already uses BetterBepInEx's scaled skin.
            var buttonStyle = GUI.skin.button;
            var enabled = GUI.enabled;
            try
            {
                if (GlossaryEditorWindow.IsVisible || _pickerVisible) GUI.enabled = false;
                GUILayout.BeginHorizontal();
                var toggleWorkbench = GUILayout.Button(UiLocalization.GetBetterMenuText(_expanded ? "Hide Workbench" : "Workbench"),
                    buttonStyle, GUILayout.ExpandWidth(true));
                var toggleCapture = GUILayout.Button(UiLocalization.GetBetterMenuText(_captureActive ? "Stop Capture" : "Capture Text"),
                    buttonStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                DrawBetterLayerButton("Manual", "Show manual translations", _plugin.EnableManualTranslation, buttonStyle);
                DrawBetterLayerButton("API", "Show saved API translations", _plugin.EnableAiTranslation, buttonStyle);
                DrawBetterLayerButton("MT", "Show machine translations", _plugin.EnableMachineTranslation, buttonStyle);
                GUILayout.EndHorizontal();
                if (toggleWorkbench)
                {
                    if (_expanded) CloseDrawer();
                    else _expanded = true;
                }
                if (toggleCapture) ToggleCapture();
            }
            finally { GUI.enabled = enabled; }
        }

        private static void DrawBetterLayerButton(string layer, string tooltip, BepInEx.Configuration.ConfigEntry<bool> entry, GUIStyle style)
        {
            var label = UiLocalization.GetBetterMenuText(layer) + " " +
                UiLocalization.GetBetterMenuText(entry.Value ? "On" : "Off");
            if (GUILayout.Button(Content(label, UiLocalization.GetBetterMenuText(tooltip)), style, GUILayout.ExpandWidth(true)))
                entry.Value = !entry.Value;
        }
#endif

        internal static void DrawEmbedded(object manager)
        {
            if (_plugin == null || manager == null)
                return;
            _manager = manager;

            try
            {
                var enabledBeforeToolbar = GUI.enabled;
                try { if (GlossaryEditorWindow.IsVisible || _pickerVisible) GUI.enabled = false; DrawEmbeddedToolbar(); }
                finally { GUI.enabled = enabledBeforeToolbar; }
            }
#if IL2CPP
            catch (Il2CppInterop.Runtime.Il2CppException ex) when
                (ex.Message != null && ex.Message.IndexOf("ExitGUIException", StringComparison.Ordinal) >= 0) { throw; }
#else
            catch (ExitGUIException) { throw; }
#endif
            catch (Exception ex)
            {
                if (_log != null)
                    _log.LogError("Embedded translation panel failed: " + ex);
            }
        }

        private static void RequestGuiRefresh()
        {
#if IL2CPP
            // Unity's ExitGUI throws a native ExitGUIException. It cannot cross the
            // managed WindowFunction trampoline, so let this event finish instead.
            // Layout state stays balanced and the next IMGUI pass sees the new state.
            GUI.changed = true;
            var current = Event.current;
            if (current != null && current.type != EventType.Layout && current.type != EventType.Repaint)
                current.Use();
#else
            GUIUtility.ExitGUI();
#endif
        }

        private static void DrawEmbeddedToolbar()
        {
            TranslationPanelTheme.Ensure(GUI.skin, _plugin.Appearance);
            if (!_plugin.Appearance.ToolbarExpanded.Value)
            {
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(Content("汉化 ▸", "展开汉化工具栏；译文模块保持当前状态"),
                    TranslationPanelTheme.Skin.button, GUILayout.Width(66f), GUILayout.Height(20f)))
                {
                    _plugin.Appearance.ToolbarExpanded.Value = true;
                    RequestGuiRefresh();
                }
                GUILayout.EndHorizontal();
                return;
            }
            GUILayout.BeginHorizontal(TranslationPanelTheme.Toolbar);
            if (GUILayout.Button(Content("汉化工作台", "从侧边展开翻译工具"),
                _expanded ? TranslationPanelTheme.Selected : TranslationPanelTheme.Skin.button, GUILayout.Width(104f)))
            {
                if (_expanded) CloseDrawer();
                else _expanded = true;
            }
            DrawEntryButton("人工", "应用人工及旧 JSONC 译文，优先级最高", _plugin.EnableManualTranslation);
            DrawEntryButton("AI 翻译", "应用已保存的 AI 译文", _plugin.EnableAiTranslation);
            DrawEntryButton("机翻", "应用已保存的机翻和当前游戏的网页临时译文", _plugin.EnableMachineTranslation);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(_captureActive ? "结束抓取" : "鼠标抓取",
                _captureActive ? TranslationPanelTheme.Selected : TranslationPanelTheme.Skin.button, GUILayout.Width(82f)))
            {
                ToggleCapture();
                RequestGuiRefresh();
            }
            if (GUILayout.Button(Content("▴", "收起顶部工具栏，记住此次选择"), TranslationPanelTheme.Skin.button, GUILayout.Width(30f)))
            {
                _plugin.Appearance.ToolbarExpanded.Value = false;
                CloseDrawer();
                RequestGuiRefresh();
            }
            GUILayout.EndHorizontal();
        }

        private static void DrawEntryButton(string label, string tooltip, BepInEx.Configuration.ConfigEntry<bool> entry)
        {
            if (GUILayout.Button(Content(label + (entry.Value ? " 开" : " 关"), tooltip + "。点击启用或停用；设置和发起翻译请打开汉化工作台。"),
                entry.Value ? TranslationPanelTheme.Selected : TranslationPanelTheme.Skin.button,
                GUILayout.Width(label == "AI 翻译" ? 82f : 64f)))
            {
                entry.Value = !entry.Value;
                RequestGuiRefresh();
            }
        }

        private static void OpenTab(int tab)
        {
            _expanded = true;
            _requestedTab = tab;
        }

        private static void CloseDrawer()
        {
            ResetApiTab();
            _expanded = false;
            _captureActive = false;
            _pendingCapture = null;
            _capturedItem = null;
            _showAiKey = _showMachineKey = false;
            GUI.FocusControl(null);
        }

        private static void ToggleCapture()
        {
            _captureActive = !_captureActive;
            _capturedItem = null;
            if (_captureActive)
            {
                _domain = WorkbenchDomain.F12Settings;
                _pendingDomain = null;
            }
            OpenTab(0);
            SetStatus(_captureActive ? "点击配置列表中的文本，即可定位人工编辑。" : "鼠标抓取已关闭。");
        }

        // Run before the manager's full-screen dismissal button so drawer clicks cannot close it.
        private static string _lastDrawerError;

        internal static int ViewportWidth => _externalViewport ? (int)(Screen.width / _externalScale) : ConfigurationManagerScale.Width;
        internal static int ViewportHeight => _externalViewport ? (int)(Screen.height / _externalScale) : ConfigurationManagerScale.Height;
        private static bool _externalViewport;
        private static float _externalScale = 1f;

        internal static void DrawSidePanel(object manager, Rect? externalRect = null)
        {
            var originalMatrix = default(Matrix4x4);
            var restoreMatrix = false;
            _externalViewport = externalRect.HasValue;
            try
            {
                if (_externalViewport)
                {
                    // Keep each Layout and its following input and Repaint events in the same coordinates.
                    if (Event.current.type == EventType.Layout)
                        _externalScale = ConfigurationManagerScale.RequestedScale;
                    originalMatrix = GUI.matrix;
                    restoreMatrix = true;
                    GUI.matrix = ConfigurationManagerScale.ScaleMatrix(originalMatrix, _externalScale);
                    externalRect = ConfigurationManagerScale.ToLogicalRect(externalRect.Value, _externalScale);
                }
                DrawSidePanelCore(manager, externalRect);
            }
#if IL2CPP
            catch (Il2CppInterop.Runtime.Il2CppException ex) when
                (ex.Message != null && ex.Message.IndexOf("ExitGUIException", StringComparison.Ordinal) >= 0) { throw; }
#else
            catch (ExitGUIException) { throw; }
#endif
            catch (Exception ex)
            {
                EndManagerContent();
                ResetPluginPicker();
                _expanded = _drawerVisible = _captureActive = false;
                var signature = ex.GetType().FullName + ":" + ex.Message;
                if (_lastDrawerError != signature)
                {
                    _lastDrawerError = signature;
                    _log?.LogError("Translation drawer preparation failed; settings window retained: " + ex);
                }
            }
            finally
            {
                _externalViewport = false;
                if (restoreMatrix) GUI.matrix = originalMatrix;
            }
        }

        internal static void ResetMenuState()
        {
            ResetApiTab();
            _paidNoticeForLayout = false;
#if !IL2CPP
            _betterMenuEscape = false;
#endif
            GlossaryEditorWindow.Suspend();
            ResetPluginPicker();
            _expanded = _drawerVisible = _captureActive = _drawerOwnsPointer = false;
            _pendingCapture = _capturedItem = null;
            _showAiKey = _showMachineKey = false;
        }

        private static void DrawSidePanelCore(object manager, Rect? externalRect)
        {
            EndManagerContent();
            _drawerOwnsPointer = false;
            var configurationManager = manager as ConfigurationManager.ConfigurationManager;
            if (_plugin == null || configurationManager == null)
                return;
            _manager = manager;
            if (!configurationManager.DisplayingWindow)
            {
                ResetMenuState();
                return;
            }

            var current = Event.current;
            if (current.type == EventType.Layout)
            {
#if !IL2CPP
                if (_betterMenuEscape)
                {
                    _betterMenuEscape = false;
                    CloseDrawerOrDialog();
                }
#endif
                _statusDetailsForLayout = _showStatusDetails;
                var previousDomain = _domain;
                var previousAdmin = _serverAdminForLayout;
                if (_pendingDomain.HasValue)
                {
                    if (_domain != _pendingDomain.Value)
                    {
                        _domain = _pendingDomain.Value;
                        _requestedTab = 0;
                    }
                    _pendingDomain = null;
                }
                GlossaryEditorWindow.SyncLayout();
                SyncPluginPickerActions();
                RefreshManagerIfRequested();
                if (_expanded && !_drawerVisible) _openedAt = Time.realtimeSinceStartup;
                _drawerVisible = _expanded;
                _jobRunningForLayout = IsJobRunning();
                _recoverableJobForLayout = _drawerVisible && _domain == WorkbenchDomain.ServerGameText ? ServerWorkbenchClient.RecoverableJobId() : null;
                _serverCapabilitiesForLayout = ServerTranslationFeature.Capabilities;
                _serverAvailableForLayout = _serverCapabilitiesForLayout != null;
                _serverLockedForLayout = _serverAvailableForLayout && !ServerWorkbenchClient.CanUseWorkbench;
                _serverAdminForLayout = ServerWorkbenchClient.IsAdmin;
                _serverCanDiscoverForLayout = ServerTranslationFeature.CanDiscover;
                _serverDiscoveryRunningForLayout = ServerTranslationFeature.DiscoveryRunning;
                _serverDiscoveryStatusForLayout = ServerTranslationFeature.DiscoveryStatus;
                if (_domain == WorkbenchDomain.ServerGameText && !_serverAvailableForLayout)
                {
                    _domain = WorkbenchDomain.F12Settings;
                    _requestedTab = 0;
                }
                var server = _domain == WorkbenchDomain.ServerGameText;
                var blocked = server && _serverLockedForLayout;
                var serverGeneration = ServerTranslationFeature.Generation;
                var contextChanged = previousDomain != _domain || (server &&
                    (previousAdmin != _serverAdminForLayout || _apiServerGeneration != serverGeneration));
                _apiServerGeneration = serverGeneration;
                var apiTab = server && !_serverAdminForLayout ? 0 : 1;
                var defaultTab = apiTab == 0 ? ServerPlayerTabs.Length - 1 : 0;
                if (contextChanged || blocked || !_drawerVisible)
                {
                    ResetApiTab();
                    if (contextChanged || _requestedTab == apiTab) _requestedTab = defaultTab;
                }
                _requestedTab = ClampWorkbenchTab(_requestedTab, _domain, _serverAdminForLayout);
                if (_requestedTab == apiTab && !_apiTabAccepted)
                {
                    _requestedTab = _selectedTab == apiTab ? defaultTab : ClampWorkbenchTab(_selectedTab, _domain, _serverAdminForLayout);
                    _pendingApiTab = apiTab;
                    _paidNoticeUntil = Time.realtimeSinceStartup + 5f;
                }
                if (_requestedTab != apiTab) _apiTabAccepted = false;
                _paidNoticeForLayout = _pendingApiTab.HasValue;
                if (_selectedTab != _requestedTab)
                {
                    _selectedTab = _requestedTab;
                    if (_domain == WorkbenchDomain.F12Settings) _nextBatchCheck = 0f;
                    _scroll = Vector2.zero;
                    GUI.FocusControl(null);
                }
                _serverSnapshotForLayout = _drawerVisible && _domain == WorkbenchDomain.ServerGameText
                    ? ServerWorkbenchClient.Snapshot : null;
                if (_serverSnapshotForLayout != null) UpdateServerSelection(_serverSnapshotForLayout);
                if (_drawerVisible && _domain == WorkbenchDomain.F12Settings)
                {
                    _snapshots = JsoncLocalization.GetSnapshots();
                    if (_pendingCapture != null) _selectedPluginGuid = _pendingCapture.PluginGuid;
                    var selected = _snapshots.ToList().FindIndex(snapshot => snapshot.Guid == _selectedPluginGuid);
                    _pluginIndex = selected >= 0 ? selected : Mathf.Clamp(_pluginIndex, 0, Math.Max(0, _snapshots.Count - 1));
                    if (_snapshots.Count > 0)
                    {
                        var snapshot = _snapshots[_pluginIndex];
                        if (_selectedPluginGuid != snapshot.Guid) ResetEditor();
                        _selectedPluginGuid = snapshot.Guid;
                        if (!_selectionInitialized) { BatchSelection.Add(snapshot.Guid); _selectionInitialized = true; PersistPluginSelection(); }
                        BatchSelection.RemoveWhere(guid => !_snapshots.Any(candidate => candidate.Guid == guid));
                        _editorItems = snapshot.CreateWorkItems();
                        if (Time.realtimeSinceStartup >= _nextBatchCheck)
                        {
                            Inventories.Clear();
                            foreach (var candidate in _snapshots) Inventories[candidate.Guid] = JsoncLocalization.GetInventory(candidate);
                            _batchPlan = CreateBatchPlan(_selectedTab == 1, Inventories);
                            _batchItemCount = _batchPlan.ScopedItems;
                            _pendingAiCount = _batchPlan.TotalItems;
                            _nextBatchCheck = Time.realtimeSinceStartup + 1f;
                        }
                        if (_changePluginGuid != snapshot.Guid || Time.realtimeSinceStartup >= _nextChangeCheck)
                        {
                            _changes = JsoncLocalization.GetChanges(snapshot);
                            _hasBaseline = JsoncLocalization.HasBaseline(snapshot.Guid);
                            _changePluginGuid = snapshot.Guid;
                            _nextChangeCheck = Time.realtimeSinceStartup + 1f;
                            Tabs[4] = _hasBaseline && _changes.Count > 0 ? "翻译更新\n" + _changes.Count + " 项" : "翻译更新";
                        }
                        if (_pendingCapture != null)
                        {
                            var capturedIndex = _editorItems.ToList().FindIndex(item => item.Id == _pendingCapture.Id);
                            if (capturedIndex >= 0) _editorIndex = capturedIndex;
                            _manualLoaded = false;
                            _scroll = Vector2.zero;
                        }
                    }
                    _pendingCapture = null;
                }
                _drawerRect = TranslationDrawerLayout.Calculate(externalRect ?? GetManagerRect(manager), ViewportWidth, ViewportHeight);
                SyncPluginPickerLayout();
                // Animate position only. Layout width remains constant throughout the transition.
                var remaining = 1f - Mathf.Clamp01((Time.realtimeSinceStartup - _openedAt) / 0.16f);
                _drawerRect.x += Mathf.Min(28f, ViewportWidth - _drawerRect.xMax) * remaining * remaining;
            }
            if (! _drawerVisible && !GlossaryEditorWindow.IsVisible && !_pickerVisible) return;

            var oldSkin = GUI.skin;
            var oldColor = GUI.color;
            var oldBackground = GUI.backgroundColor;
            var oldContent = GUI.contentColor;
            var oldEnabled = GUI.enabled;
            var oldDepth = GUI.depth;
            var pointerInside = _paidNoticeForLayout || _pickerVisible || GlossaryEditorWindow.IsVisible || _drawerRect.Contains(current.mousePosition);
            _drawerOwnsPointer = pointerInside;
            try
            {
                TranslationPanelTheme.Ensure(oldSkin, _plugin.Appearance);
                GUI.skin = TranslationPanelTheme.Skin;
                GUI.color = GUI.backgroundColor = GUI.contentColor = Color.white;
                GUI.enabled = true;
                GUI.depth = oldDepth - 10;
                if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
                {
                    if (!externalRect.HasValue) CloseDrawerOrDialog();
                    current.Use();
                }
                if (_paidNoticeForLayout)
                {
                    var width = Mathf.Min(_drawerRect.width, 460);
                    const float height = 310f;
                    var rect = new Rect((ViewportWidth - width) * 0.5f, (ViewportHeight - height) * 0.5f, width, height);
                    GUILayout.Window(PaidNoticeWindowId, rect, (GUI.WindowFunction)DrawPaidNotice, GUIContent.none, GUI.skin.window);
                    GUI.BringWindowToFront(PaidNoticeWindowId);
                }
                else if (_pickerVisible) DrawPluginPicker();
                else if (GlossaryEditorWindow.IsVisible) GlossaryEditorWindow.Draw();
                else
                {
                    GUILayout.Window(DrawerWindowId, _drawerRect, (GUI.WindowFunction)DrawDrawerWindow, GUIContent.none, GUI.skin.window);
                    GUI.BringWindowToFront(DrawerWindowId);
                }
                if (pointerInside)
                {
                    Input.ResetInputAxes();
                    if (current.isMouse || current.type == EventType.ScrollWheel || current.type == EventType.ContextClick)
                        current.Use();
                }
            }
#if IL2CPP
            catch (Il2CppInterop.Runtime.Il2CppException ex) when
                (ex.Message != null && ex.Message.IndexOf("ExitGUIException", StringComparison.Ordinal) >= 0) { throw; }
#else
            catch (ExitGUIException) { throw; }
#endif
            catch (Exception ex)
            {
                _expanded = false;
                _captureActive = false;
                ResetApiTab();
                ResetPluginPicker();
                if (_log != null) _log.LogError("Translation drawer failed: " + ex);
            }
            finally
            {
                GUI.skin = oldSkin;
                GUI.color = oldColor;
                GUI.backgroundColor = oldBackground;
                GUI.contentColor = oldContent;
                GUI.enabled = oldEnabled;
                GUI.depth = oldDepth;
            }
        }

        private static void CloseDrawerOrDialog()
        {
            if (_pendingApiTab.HasValue) { ResetApiTab(); RequestGuiRefresh(); }
            else if (_pickerVisible) QueuePluginPicker(CancelPluginPicker);
            else if (GlossaryEditorWindow.IsVisible) GlossaryEditorWindow.RequestClose();
            else CloseDrawer();
        }

        private static void DrawLayerToggle(BepInEx.Configuration.ConfigEntry<bool> entry, string label, float width)
        {
            var value = TranslationPanelTheme.Toggle(entry.Value, label, GUILayout.Width(width));
            if (value != entry.Value)
                entry.Value = value;
        }

        private static void DrawDrawerWindow(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("汉化工作台", TranslationPanelTheme.Title);
            if (GUILayout.Button(Content("收起", "收起侧栏 · Esc"), GUILayout.Width(54f))) CloseDrawer();
            GUILayout.EndHorizontal();
            DrawDomainSelector();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Content("术语表", "编辑 F12 和网页 AI 共用的本机术语表"), GUILayout.ExpandWidth(true)))
            {
                _captureActive = false;
                GlossaryEditorWindow.Show();
            }
            WebWorkbenchBridge.DrawToolbar();
            GUILayout.EndHorizontal();
            WebWorkbenchBridge.DrawDetails(HasUnsavedManualDraft, _jobRunningForLayout);
            var serverLocked = _domain == WorkbenchDomain.ServerGameText && _serverLockedForLayout;
            var tabLabels = WorkbenchTabs(_domain, _serverAdminForLayout);
            if (!serverLocked)
            {
                var tab = DrawSelectionButtons(_selectedTab, tabLabels, tabLabels.Length, TranslationPanelTheme.Tab);
                if (tab != _selectedTab) _requestedTab = tab;
            }
            GUILayout.Space(8f);

            _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUILayout.ExpandHeight(true));
            GUILayout.BeginVertical(GUILayout.Width(Mathf.Max(1f, _drawerRect.width - 54f)));
            if (!serverLocked && _selectedTab == 5)
                DrawAppearance();
            else if (_domain == WorkbenchDomain.ServerGameText)
                DrawServerGameTextDomain();
            else if (_selectedTab == 3)
                DrawTranslationScope();
            else if (_snapshots.Count == 0)
            {
                DrawNotice("尚无可编辑的插件。请关闭后重新打开 ConfigurationManager，收集插件设置。", false);
            }
            else
            {
                DrawPluginSelector(_snapshots);
                if (_selectedTab == 0)
                    DrawManualSettings();
                else if (_selectedTab == 1)
                    DrawAiSettings();
                else if (_selectedTab == 2)
                    DrawMachineSettings();
                else
                    DrawChangeReview(_snapshots[_pluginIndex]);
            }
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            if (_domain == WorkbenchDomain.F12Settings)
                DrawPrimaryAction();
            DrawJobStatus();
            DrawTooltip();
        }

        private static void DrawDomainSelector()
        {
            // Show detection help only in EFT/SPT; freeze async probe state for this IMGUI pass.
            if (!_serverAvailableForLayout)
            {
                if (_serverCanDiscoverForLayout)
                {
                    GUILayout.Label(_serverDiscoveryStatusForLayout, TranslationPanelTheme.Muted);
                    GUI.enabled = !_serverDiscoveryRunningForLayout;
                    if (GUILayout.Button(_serverDiscoveryRunningForLayout ? "检测中…" : "重新检测服务端文本"))
                    {
                        ServerTranslationFeature.RetryDiscovery();
                        RequestGuiRefresh();
                    }
                    GUI.enabled = true;
                }
                return;
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Content("F12 设置汉化",
                "翻译 BepInEx 插件名称、分类、配置项名称、说明、枚举和自定义控件。"),
                _domain == WorkbenchDomain.F12Settings ? TranslationPanelTheme.Selected : TranslationPanelTheme.Skin.button))
                SelectDomain(WorkbenchDomain.F12Settings);
            if (GUILayout.Button(Content("服务端游戏文本", ServerTranslationFeature.DomainTooltip),
                _domain == WorkbenchDomain.ServerGameText ? TranslationPanelTheme.Selected : TranslationPanelTheme.Skin.button))
                SelectDomain(WorkbenchDomain.ServerGameText);
            GUILayout.EndHorizontal();
        }

        private static void SelectDomain(WorkbenchDomain domain)
        {
            if (domain == WorkbenchDomain.ServerGameText && !ServerTranslationFeature.IsAvailable)
                return;
            if (_domain == domain)
                return;
            _pendingDomain = domain;
            _scroll = Vector2.zero;
            _captureActive = false;
            _pendingCapture = null;
            _capturedItem = null;
            GUI.FocusControl(null);
            RequestGuiRefresh();
        }

        private static string[] WorkbenchTabs(WorkbenchDomain domain, bool admin) =>
            domain == WorkbenchDomain.F12Settings ? Tabs : admin ? ServerAdminTabs : ServerPlayerTabs;

        internal static int ClampWorkbenchTab(int tab, WorkbenchDomain domain, bool admin) =>
            Math.Max(0, Math.Min(tab, WorkbenchTabs(domain, admin).Length - 1));

        private static void DrawServerGameTextDomain()
        {
            var capabilities = _serverCapabilitiesForLayout;
            if (capabilities == null)
                return;

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Content("●  服务端已连接",
                "已检测到兼容的 SimpleTranslatorCS 服务端组件。"),
                TranslationPanelTheme.ConnectionStatus, GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            GUILayout.Label((string.IsNullOrWhiteSpace(capabilities.ServerVersion) ? "未知版本" : "v" + capabilities.ServerVersion) +
                (capabilities.FikaDetected ? " · Fika" : "") +
                " · " + (_serverLockedForLayout ? "待验证" : _serverAdminForLayout ? "管理员" : "普通模式"),
                TranslationPanelTheme.Muted, GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();
            GUILayout.Label(_serverLockedForLayout ? ServerWorkbenchClient.WorkbenchLockedMessage :
                "拉取正式译文，或用自己的 API 密钥提交候选译文。", TranslationPanelTheme.Muted);

            if (!_serverLockedForLayout) DrawServerConnectionActions(capabilities);
            if (capabilities.WriteAuthRequired || capabilities.FikaDetected)
            {
                var validation = ServerWorkbenchClient.AdminValidationState;
                var savedColor = GUI.color;
                GUI.color = validation == "verified" ? new Color(0.65f, 1f, 0.7f) : validation == "rejected" ? new Color(1f, 0.6f, 0.6f) : new Color(1f, 0.9f, 0.55f);
                GUILayout.BeginVertical(GUI.skin.box);
                GUI.color = savedColor;
                var savedContentColor = GUI.contentColor;
                GUI.contentColor = Color.white;
                var statusColor = validation == "verified" ? (TranslationPanelTheme.LightCards ? "#006B2D" : "#79F59A") : validation == "rejected"
                    ? (TranslationPanelTheme.LightCards ? "#A01010" : "#FF7777") : (TranslationPanelTheme.LightCards ? "#805600" : "#FFDD66");
                var statusText = validation == "verified" ? "管理员权限已验证 · 正式译文写入" : validation == "rejected" ? "管理员密钥无效 · 请更换后重新验证"
                    : validation == "empty" ? (_serverLockedForLayout ? "管理员密钥未填写" : "玩家模式 · 管理员密钥未填写")
                    : "管理员密钥尚未验证 · 请点击验证管理员密钥";
                GUILayout.Label("<color=" + statusColor + ">" + statusText + "</color>", TranslationPanelTheme.RichPreview);
                GUI.contentColor = savedContentColor;
                var before = _serverAdminKey;
                GUILayout.Label(Content(capabilities.FikaDetected ? "服务端管理员密钥（Fika）" : "管理员密钥（普通玩家留空）",
                    "管理员可以推送、更新和拉取；密钥位于服务端 workbench-admin.jsonc。"), TranslationPanelTheme.Muted);
                GUILayout.BeginHorizontal();
                _serverAdminKey = _showServerAdminKey
                    ? GUILayout.TextField(_serverAdminKey ?? string.Empty, GUILayout.MinWidth(0f))
                    : GUILayout.PasswordField(_serverAdminKey ?? string.Empty, '●', GUILayout.MinWidth(0f));
                if (GUILayout.Button(_showServerAdminKey ? "隐藏" : "显示", GUILayout.Width(48f)))
                    _showServerAdminKey = !_showServerAdminKey;
                if (GUILayout.Button("清空", GUILayout.Width(48f)))
                    _serverAdminKey = string.Empty;
                GUILayout.EndHorizontal();
                if (before != _serverAdminKey)
                {
                    ServerWorkbenchClient.AdminKey = _serverAdminKey;
                }
                GUI.enabled = !_jobRunningForLayout && !ServerWorkbenchClient.Refreshing &&
                    (!_serverLockedForLayout || !string.IsNullOrWhiteSpace(_serverAdminKey));
                if (GUILayout.Button("验证管理员密钥")) { StartServerRefresh(); RequestGuiRefresh(); }
                GUI.enabled = true;
                GUILayout.EndVertical();
            }
            GUILayout.EndVertical();

            if (_serverLockedForLayout) return;
            if (_recoverableJobForLayout != null && !_jobRunningForLayout)
            {
                if (GUILayout.Button("查询上次翻译任务（不发送 API）"))
                {
                    BeginJob("正在查询上次服务端任务", 0, paid: true);
                    _serverJobActive = true;
                    _serverStopRequested = _plugin.AiStopped.Value;
                    _job = RunServerJobAsync(null, "ai", false, _recoverableJobForLayout);
                }
            }
            if (capabilities.SupportsFeature("contribution-receipts") && GUILayout.Button("查询我的人工投稿回执"))
                StartServerOperation("读取投稿回执", async () =>
                {
                    var response = await ServerWorkbenchClient.GetContributionReceiptAsync(string.Empty).ConfigureAwait(false);
                    var receipts = (response["entries"] as JArray ?? new JArray()).OfType<JObject>().ToArray();
                    if (receipts.Length == 0) return "当前玩家没有可查的人工投稿。";
                    return string.Join("\n\n", receipts.Select(receipt =>
                    {
                        ServerWorkbenchClient.DescribeContributionReceipt(receipt, out var detail, out _);
                        return "投稿 " + (string)receipt["submissionId"] + "\n" + detail;
                    }));
                });
            if (capabilities.SupportsFeature("job-history"))
            {
                if (GUILayout.Button("读取最近的服务端任务"))
                    StartServerOperation("读取任务记录", async () =>
                    {
                        var recent = await ServerWorkbenchClient.GetRecentJobsAsync().ConfigureAwait(false);
                        ServerWorkbenchClient.PublishOperationState(() => _serverRecentJobs = recent);
                        return "已读取 " + recent.Count + " 个任务。";
                    });
                if (_serverRecentJobs != null)
                    foreach (var job in _serverRecentJobs.Take(10))
                    {
                        var state = job.State == "running" ? "进行中" : job.State == "completed" ? "已结束" :
                            job.State == "cancelled" ? "已取消" : "失败";
                        GUILayout.Label(state + " · " + (job.Mode == "ai" ? "AI" : "机翻") + " · " +
                            job.Completed + "/" + job.Total + " · 已保存或待审 " + job.Saved + " · 待复核 " +
                            job.Rejected + " · " + job.JobId, TranslationPanelTheme.Muted);
                    }
            }
            if (capabilities.SupportsFeature("live-sync")) GUILayout.Label("自动同步：" + ServerLocaleSynchronizer.Status, TranslationPanelTheme.Muted);
            if (!_serverAdminForLayout)
            {
                DrawNotice("普通玩家可拉取正式译文，也可自费提交候选译文；候选须经管理员审核才会生效。", false);
            }

            var snapshot = _serverSnapshotForLayout;
            if (snapshot == null)
            {
                DrawNotice(ServerWorkbenchClient.Refreshing ? "正在扫描服务端词条与 JSONC 文件…" :
                    string.IsNullOrWhiteSpace(ServerWorkbenchClient.Error) ? "尚未取得服务端目录，请点击“读取可译目录”。" :
                    "目录读取失败：" + ServerWorkbenchClient.Error, false);
                return;
            }
            if (snapshot.RestartRequired)
                DrawNotice("手动文件启用状态已修改。重启服务端后会按新选择重新构建完整语言层。", true);
            if (capabilities.SupportsFeature("manual-exchange")) DrawExchange(true, snapshot);

            if (!_serverAdminForLayout)
            {
                if (_selectedTab == 0) DrawServerAi(snapshot);
                else if (_selectedTab == 1) DrawServerMachine(snapshot);
                else DrawNotice("每次最多提交 30 条候选译文。提交后管理员会在“译文校对”中审核；在审核前其他玩家看不到这些译文。", false);
            }
            else if (_selectedTab == 0) DrawServerManualFiles(snapshot);
            else if (_selectedTab == 1) DrawServerAi(snapshot);
            else if (_selectedTab == 2) DrawServerMachine(snapshot);
            else if (_selectedTab == 3) DrawServerScope(snapshot);
            else DrawServerBaseline(snapshot);
        }

        private static void DrawServerConnectionActions(ServerTranslationCapabilities capabilities)
        {
            GUILayout.BeginHorizontal();
            GUI.enabled = !_jobRunningForLayout && !ServerWorkbenchClient.Refreshing;
            if (GUILayout.Button(Content(ServerWorkbenchClient.Refreshing ? "读取中…" : _serverAdminForLayout ? "刷新管理目录" : "读取可译目录",
                "读取服务端待翻目录；填写管理员密钥后也会验证管理权限。"), GUILayout.MinWidth(0f)))
            {
                StartServerRefresh();
                RequestGuiRefresh();
            }
            var remaining = ServerWorkbenchClient.PullRemainingSeconds;
            GUI.enabled = !_jobRunningForLayout && remaining == 0;
            if (capabilities.SupportsFeature("pull-sync") && GUILayout.Button(Content(
                remaining > 0 ? "等待 " + remaining + " 秒" : "拉取译文",
                "只同步服务端已生效的文本，不重新读取磁盘文件。普通玩家受服务端冷却限制。"), GUILayout.MinWidth(0f)))
            {
                StartServerOperation("拉取服务端译文", ServerWorkbenchClient.PullAsync);
                RequestGuiRefresh();
            }
            GUI.enabled = !_jobRunningForLayout && _serverAdminForLayout;
            if (capabilities.SupportsFeature("hot-reload") && GUILayout.Button(Content("更新并拉取",
                "管理员：重新读取服务端 JSONC，并将已生效文本同步到当前客户端。"), GUILayout.MinWidth(0f)))
            {
                StartServerOperation("重新加载服务端译文", ServerWorkbenchClient.ReloadAsync);
                RequestGuiRefresh();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private static void DrawServerManualFiles(ServerWorkbenchSnapshot snapshot)
        {
            GUILayout.Label("手动精翻文件", TranslationPanelTheme.Section);
            GUILayout.Label("列出 translations/ch 和兼容目录 db/locales/ch 中的手写 JSONC。@legacy/ 表示旧路径。开关同步游戏文本；对话文件仍可能需要重启服务端。",
                TranslationPanelTheme.Muted);
            var files = snapshot.Files.Where(file => file.Kind == "manual").ToList();
            if (files.Count == 0)
            {
                DrawNotice("当前 translations/ch 和 db/locales/ch 中没有可选择的手动 JSONC 文件。", false);
                return;
            }
            foreach (var file in files)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUI.enabled = !_jobRunningForLayout;
                GUILayout.BeginHorizontal();
                var enabled = TranslationPanelTheme.Toggle(file.Enabled, Content(file.Path,
                    file.RequiresRestart
                        ? "此 Dialogue 人工 JSONC 在服务端启动时应用；切换后需重启服务端。关闭不会删除文件。"
                        : "启用后此人工 JSONC 参与服务端启动预加载与热加载。关闭不会删除文件。"));
                if (GUILayout.Button(Content("打开", FileLocationOpener.ServerPathTooltip(file.Path)), GUILayout.Width(54f)))
                    RunUiAction(() => FileLocationOpener.OpenServerTranslation(file.Path));
                GUILayout.EndHorizontal();
                GUI.enabled = true;
                GUILayout.Label(file.EntryCount + " 个键值 · " +
                    (!file.BaselineKnown ? "尚未建立基准" : file.ChangedCount == 0 ? "原文基准无变化" :
                        "与上次基准不同 " + file.ChangedCount + " 个") +
                    (file.RequiresRestart ? " · Dialogue 需重启生效" : string.Empty), TranslationPanelTheme.Muted);
                GUILayout.EndVertical();
                if (enabled != file.Enabled)
                {
                    var path = file.Path;
                    StartServerOperation("保存文件开关 · " + path,
                        () => ServerWorkbenchClient.SetFileEnabledAsync(path, enabled));
                    RequestGuiRefresh();
                }
            }
        }

        private static void DrawServerAi(ServerWorkbenchSnapshot snapshot)
        {
            GUILayout.Label("服务端 AI 翻译", TranslationPanelTheme.Section);
            GUILayout.Label(_serverProvider?.GlossarySource == "server"
                ? "本次在服务端执行的 AI 翻译使用服务器术语表。"
                : "服务端 AI 翻译由服务器提供术语参考。", TranslationPanelTheme.Muted);
            DrawServerRangeSelection(snapshot);
            DrawServerProvider("ai");
            DrawServerStart(snapshot, true);
        }

        private static void DrawServerMachine(ServerWorkbenchSnapshot snapshot)
        {
            GUILayout.Label("服务端机器翻译", TranslationPanelTheme.Section);
            DrawServerRangeSelection(snapshot);
            DrawServerProvider("machine");
            DrawServerStart(snapshot, false);
        }

        private static void DrawServerProvider(string mode)
        {
            if (!ServerTranslationFeature.Capabilities.SupportsFeature("client-api-key"))
            {
                DrawNotice("当前 SimpleTranslatorCS 尚不支持自带 API 密钥，请更新服务端组件。", true);
                return;
            }
            GUILayout.BeginVertical(GUI.skin.box);
            if (_serverProvider == null)
                GUILayout.Label("尚未校验服务端供应商配置。", TranslationPanelTheme.Muted);
            else if (mode == "ai" || ServerTranslationFeature.Capabilities.SupportsFeature("provider-catalog"))
            {
                DrawServerProviderSelection(mode);
            }
            else GUILayout.Label(_serverProvider.MachineName + " · " + _serverProvider.MachineEndpointHost, TranslationPanelTheme.Muted);
            if (mode == "ai" && _serverProvider != null && ServerTranslationFeature.Capabilities.SupportsFeature("ai-task-options"))
                DrawServerAiOptions();
            if (_serverProvider != null)
            {
                var required = mode == "ai" ? (string)_serverOptions?["provider"] != "Ollama 本地模型" : (string)_serverMachineOptions?["provider"] != "LibreTranslate";
                GUILayout.Label(required ? "我的 API 密钥（必填，本次会话）" : "我的 API 密钥（可选，本次会话）",
                    TranslationPanelTheme.Muted);
                GUILayout.BeginHorizontal();
                var current = mode == "ai" ? _serverAiApiKey : _serverMachineApiKey;
                var changed = _showServerApiKey
                    ? GUILayout.TextField(current ?? string.Empty, GUILayout.MinWidth(0f))
                    : GUILayout.PasswordField(current ?? string.Empty, '●', GUILayout.MinWidth(0f));
                if (mode == "ai") _serverAiApiKey = changed; else _serverMachineApiKey = changed;
                if (GUILayout.Button(_showServerApiKey ? "隐藏" : "显示", GUILayout.Width(48f)))
                    _showServerApiKey = !_showServerApiKey;
                if (GUILayout.Button("清空", GUILayout.Width(48f)))
                { if (mode == "ai") _serverAiApiKey = string.Empty; else _serverMachineApiKey = string.Empty; }
                GUILayout.EndHorizontal();
            }
            if (mode == "machine" && _serverProvider != null && ServerMachineAppIdRequired())
            {
                GUILayout.Label("我的应用 ID", TranslationPanelTheme.Muted);
                _serverMachineAppId = GUILayout.TextField(_serverMachineAppId ?? string.Empty);
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("校验配置"))
            {
                StartServerOperation("校验服务端供应商", async () =>
                {
                    var receivedState = await ServerWorkbenchClient.GetProviderStatusAsync().ConfigureAwait(false);
                    ServerWorkbenchClient.PublishOperationState(() => _serverProvider = receivedState);
                    return "已读取服务端供应商状态。";
                });
                RequestGuiRefresh();
            }
            GUI.enabled = ServerCredentialsReady(mode) && !_jobRunningForLayout;
            if (GUILayout.Button("测试 API"))
            {
                StartServerJob(null, mode, true);
                RequestGuiRefresh();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("密钥只保留在本次客户端会话；执行时会发给当前服务器，服务器运营者可以接触到它。请仅连接可信服务器。",
                TranslationPanelTheme.Muted);
            GUILayout.EndVertical();
            if (_serverNextProvider >= 0)
            {
                var presets = ServerPresets(mode);
                var next = _serverNextProvider; _serverNextProvider = -1;
                if (mode == "ai") { _serverProfile=(string)presets[next]["id"]; _serverOptions=null; _serverAiApiKey=""; ServerModels=new AiModelSelection(); _chooseServerAiProvider=false; }
                else { _serverMachineProfile=(string)presets[next]["id"]; _serverMachineOptions=null; _serverMachineApiKey=_serverMachineAppId=""; _chooseServerMachineProvider=false; }
                RequestGuiRefresh();
            }
        }

        private static JObject[] ServerPresets(string mode)
        {
            return new[] { new JObject { ["id"]="default", ["name"]="服务端默认", ["settings"]=mode == "ai" ? _serverProvider.AiSettings : _serverProvider.MachineSettings } }
                .Concat((mode == "ai" ? _serverProvider.Profiles : _serverProvider.MachineProfiles ?? new JArray()).OfType<JObject>()).ToArray();
        }
        private static bool ServerMachineAppIdRequired()
        {
            var name = (string)_serverMachineOptions?["provider"];
            return name == null ? _serverProvider.MachineAppIdRequired : name == "百度翻译" || name == "有道翻译";
        }
        private static void DrawServerProviderSelection(string mode)
        {
            var ai = mode == "ai";
            var presets = ServerPresets(mode);
            var profile = ai ? _serverProfile : _serverMachineProfile;
            var index = Math.Max(0, Array.FindIndex(presets, p => (string)p["id"] == profile));
            if ((string)presets[index]["id"] != profile)
            {
                if (ai) { _serverProfile="default"; _serverOptions=null; _serverAiApiKey=""; }
                else { _serverMachineProfile="default"; _serverMachineOptions=null; _serverMachineApiKey=_serverMachineAppId=""; }
            }
            var drawExpanded = ai ? _chooseServerAiProvider : _chooseServerMachineProvider;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Content((string)presets[index]["name"] + " ▾", "选择供应商预设；切换后请填写对应 API 密钥"), TranslationPanelTheme.PluginButton, GUILayout.MinHeight(32f)))
            {
                if (ai) _chooseServerAiProvider=!_chooseServerAiProvider; else _chooseServerMachineProvider=!_chooseServerMachineProvider;
                RequestGuiRefresh();
            }
            var presetId = (string)presets[index]["id"];
            var docs = (ai ? TranslationFoundation.TranslationProviderPresets.Ai : TranslationFoundation.TranslationProviderPresets.Machine)
                .FirstOrDefault(p => "builtin:" + p.Id == presetId)?.Documentation;
            if (!string.IsNullOrEmpty(docs) && GUILayout.Button("官方文档", GUILayout.Width(78f))) Application.OpenURL(docs);
            GUILayout.EndHorizontal();
            if (drawExpanded)
            {
                var next = DrawSelectionButtons(-1, presets.Select(p => (string)p["name"]).ToArray(), 2, TranslationPanelTheme.PluginButton);
                if (next >= 0 && next != index) _serverNextProvider=next;
            }
            var settings = presets[index]["settings"] as JObject ?? new JObject();
            if (ai && _serverOptions == null) { _serverOptions=(JObject)settings.DeepClone(); _serverOptions["extraPrompt"]=""; }
            if (!ai && _serverMachineOptions == null) _serverMachineOptions=(JObject)settings.DeepClone();
            var options = ai ? _serverOptions : _serverMachineOptions;
            GUILayout.Label("本次接口：" + (string)options["endpoint"], TranslationPanelTheme.Muted);
            if (!ai)
            {
                if ((string)options["provider"] == "Microsoft Translator")
                {
                    GUILayout.Label("Azure 资源区域（按资源要求填写）", TranslationPanelTheme.Muted);
                    options["region"]=GUILayout.TextField((string)options["region"] ?? "");
                }
                if (ServerWorkbenchClient.IsAdmin)
                {
                    GUILayout.Label("接口地址（可配置 DeepL Pro / 自建 LibreTranslate）", TranslationPanelTheme.Muted);
                    options["endpoint"]=GUILayout.TextField((string)options["endpoint"] ?? "");
                }
            }
        }

        private static void DrawServerAiOptions()
        {
            var provider = _serverProvider;
            if (provider == null) return;
            var presets = ServerPresets("ai");
            var index = Math.Max(0, Array.FindIndex(presets, item => (string)item["id"] == _serverProfile));
            var next = index;
            var active = presets[index]["settings"] as JObject;
            GUILayout.Label("本次接口：" + (string)active?["endpoint"] + " · " + (string)_serverOptions["model"], TranslationPanelTheme.Muted);
            var drawAdvanced = _serverAdvanced;
            _serverAdvanced = TranslationPanelTheme.Toggle(_serverAdvanced, "本次任务高级选项");
            if (ServerWorkbenchClient.IsAdmin)
            {
                GUILayout.Label("模型 ID（支持手动输入）", TranslationPanelTheme.Muted);
                _serverOptions["model"] = GUILayout.TextField((string)_serverOptions["model"] ?? "");
                if (drawAdvanced)
                {
                    GUILayout.Label("本次接口地址（仅管理员可更改）", TranslationPanelTheme.Muted);
                    _serverOptions["endpoint"] = GUILayout.TextField((string)_serverOptions["endpoint"] ?? "");
                    GUILayout.Label("参数适配器（代理接口可手动指定）", TranslationPanelTheme.Muted);
                    var adapters = new[] { "auto", "generic", "openai", "deepseek", "qwen", "gemini" };
                    var adapter = Math.Max(0, Array.IndexOf(adapters, (string)_serverOptions["adapter"]));
                    _serverOptions["adapter"] = adapters[DrawSelectionButtons(adapter, adapters, 3, GUI.skin.button)];
                }
            }
            if (!drawAdvanced) { ApplyServerProfileSelection(presets, index, next); return; }
            var models = new[] { (string)active?["model"] }.Concat((active?["allowedModels"] as JArray ?? new JArray()).Values<string>()).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
            var key = _serverAiApiKey; var profileId = _serverProfile; var revision = provider.Revision;
            var modelOptions = (JObject)_serverOptions.DeepClone();
            var ready = ServerTranslationFeature.Capabilities.SupportsFeature("model-discovery") &&
                (string.IsNullOrWhiteSpace(ServerWorkbenchClient.AdminKey) || ServerWorkbenchClient.IsAdmin) &&
                ((string)active?["provider"] == "Ollama 本地模型" || !string.IsNullOrWhiteSpace(key));
            var context = TranslationFoundation.AiModelCatalog.Context((string)modelOptions["endpoint"] + profileId + revision + ServerWorkbenchClient.IsAdmin, key, false);
            _serverOptions["model"] = DrawModelSelection(ServerModels, context, ready,
                refresh => ServerWorkbenchClient.GetModelsAsync(key, profileId, revision, refresh, modelOptions),
                (string)_serverOptions["model"], models);
            GUILayout.Label(ServerWorkbenchClient.IsAdmin ? "管理员可选择查询到的模型或手动输入 ID。" : "玩家查询和手动输入均限管理员批准的模型范围。", TranslationPanelTheme.Muted);
            GUILayout.Label("思考强度", TranslationPanelTheme.Muted);
            var levels = new[] { "default", "off", "low", "medium", "high" };
            var reasoning = Math.Max(0, Array.IndexOf(levels, (string)_serverOptions["reasoning"]));
            _serverOptions["reasoning"] = levels[DrawSelectionButtons(reasoning, levels, 5, GUI.skin.button)];
            ServerOptionSlider("每批条数", "batchSize", 1, 60, 40);
            ServerOptionSlider("输入字符预算", "maxInputCharacters", 1000, 60000, 12000);
            ServerOptionSlider("输出 token 上限", "maxOutputTokens", 256, 32768, 8192);
            ServerOptionSlider("失败重试", "retryCount", 0, 3, 1);
            ServerOptionSlider("请求超时（秒）", "timeoutSeconds", 10, 600, 90);
            ServerOptionSlider("请求次数上限", "maxRequests", 0, 1000, 100);
            ServerOptionSlider("任务 token 预算", "maxReportedTokens", 0, 1000000, 0);
            _serverOptions["jsonOutput"] = TranslationPanelTheme.Toggle((bool?)_serverOptions["jsonOutput"] != false, "JSON 输出模式");
            GUILayout.Label("服务端 AI 读取服务端 Mod/glossary/eft.zh-CN.tsv；由运营者编辑，玩家不能改写。仅发送当前批次匹配词条。", TranslationPanelTheme.Muted);
            GUILayout.Label("附加术语要求（保留管理员术语）", TranslationPanelTheme.Muted);
            _serverOptions["extraPrompt"] = GUILayout.TextArea((string)_serverOptions["extraPrompt"] ?? string.Empty, GUILayout.MinHeight(40f));
            GUILayout.Label("本次预算只能收紧管理员上限。off 是否能关闭思考取决于接口和模型；超时与格式错误不会自动重发。", TranslationPanelTheme.Muted);
            ApplyServerProfileSelection(presets, index, next);
        }

        private static void ApplyServerProfileSelection(JObject[] presets, int previous, int next)
        {
            if (previous == next) return;
            _serverProfile = (string)presets[next]["id"];
            _serverOptions = null;
            RequestGuiRefresh();
        }

        private static void ServerOptionSlider(string label, string name, int min, int max, int fallback)
        {
            var value = Math.Max(min, Math.Min(max, (int?)_serverOptions[name] ?? fallback));
            GUILayout.Label(label + "：" + value, TranslationPanelTheme.Muted);
            _serverOptions[name] = (int)GUILayout.HorizontalSlider(value, min, max);
        }

        private static void DrawExchange(bool server, ServerWorkbenchSnapshot snapshot)
        {
            var drawExchange = _exchangeOpen;
            var packForLayout = _exchange;
            _exchangeOpen = TranslationPanelTheme.Toggle(_exchangeOpen, "AI 网页 / 离线手动回填（无需 API 密钥）");
            if (!drawExchange) return;
            GUILayout.BeginVertical(GUI.skin.box);
            var names = new[] { "DeepSeek", "ChatGPT", "Gemini", "通义" };
            var urls = new[] { "https://chat.deepseek.com/", "https://chatgpt.com/", "https://gemini.google.com/", "https://www.qianwen.com/" };
            _aiWebsite = DrawSelectionButtons(_aiWebsite, names, 4, GUI.skin.button);
            GUILayout.Label(server ? "按当前来源导出待译文本；管理员可保存，玩家可提交待审核。" : "导出当前插件所选范围内的缺失文本，回填后保存为人工译文。", TranslationPanelTheme.Muted);
            GUI.enabled = !_jobRunningForLayout;
            if (GUILayout.Button("复制待译 JSON 提示词并打开网页"))
            {
                RunUiAction(() =>
                {
                    _exchange = new TranslationExchange();
                    if (server)
                    {
                        _exchange.Revision = snapshot.Revision;
                        _exchange.Server.AddRange(snapshot.Entries.Where(item => ! _serverSourcesInitialized || ServerSourceSelection.Contains(item.SourceGroup))
                            .Take(ServerWorkbenchClient.IsAdmin ? 100 : 30));
                    }
                    else
                    {
                        var inventory = JsoncLocalization.GetInventory(_snapshots[_pluginIndex]);
                        if (inventory.Error != null) throw new InvalidOperationException(inventory.Error);
                        _exchange.Local.AddRange(_snapshots[_pluginIndex].CreateWorkItems(GetTranslationScope()).Where(item => !inventory.ValidIds.Contains(item.Id)).Take(100));
                    }
                    if (_exchange.Local.Count + _exchange.Server.Count == 0) throw new InvalidOperationException("当前范围没有待译文本。");
                    GUIUtility.systemCopyBuffer = _exchange.Prompt();
                    _exchangeText = string.Empty;
                    Application.OpenURL(urls[_aiWebsite]);
                    SetStatus("提示词已复制，请在网页粘贴并翻译。网站登录和额度由平台决定。");
                });
            }
            if (packForLayout != null && server == (packForLayout.Server.Count > 0))
            {
                GUILayout.Label("已导出 " + (_exchange.Local.Count + _exchange.Server.Count) + " 条。将 JSON 译文贴回，校验后确认保存：", TranslationPanelTheme.Muted);
                _exchangeText = GUILayout.TextArea(_exchangeText, GUILayout.MinHeight(80f), GUILayout.MinWidth(0f));
                _exchangeKeepOriginal = TranslationPanelTheme.Toggle(_exchangeKeepOriginal, "明确保留与原文相同的词条（专有名词）");
                if (GUILayout.Button("校验回填 JSON")) RunUiAction(() => SetStatus("校验通过 " + _exchange.Parse(_exchangeText, _exchangeKeepOriginal).Count + " 条；请确认内容后保存。"));
                if (GUILayout.Button(server ? "校验并提交人工译文" : "校验并保存人工译文", TranslationPanelTheme.Primary))
                {
                    RunUiAction(() =>
                    {
                        var values = _exchange.Parse(_exchangeText, _exchangeKeepOriginal);
                        if (server)
                        {
                            var pack = _exchange; var keep = _exchangeKeepOriginal;
                            StartServerOperation("提交人工回填", () => ServerWorkbenchClient.SubmitManualAsync(pack, values, keep));
                        }
                        else
                        {
                            var selected = _snapshots.FirstOrDefault(item => item.Guid == _exchange.Local[0].PluginGuid);
                            if (selected == null) throw new InvalidOperationException("插件已变化，请重新导出。");
                            var current = TranslationWorkItem.Index(selected.CreateWorkItems());
                            if (_exchange.Local.Any(item => !current.ContainsKey(item.Id) || current[item.Id].Source != item.Source))
                                throw new InvalidOperationException("插件原文已变化，请重新导出。");
                            foreach (var group in _exchange.Local.Where(item => values.ContainsKey(item.Id)).GroupBy(item => item.PluginGuid))
                                JsoncLocalization.SaveLayer(group.Key, TranslationLayer.Manual, group, values);
                            RequestManagerRefresh(); SetStatus("人工回填已保存 " + values.Count + " 条。");
                        }
                    });
                }
            }
            GUI.enabled = true;
            GUILayout.EndVertical();
        }

        private static bool ServerCredentialsReady(string mode)
        {
            if (_serverProvider == null || (!string.IsNullOrWhiteSpace(ServerWorkbenchClient.AdminKey) && !ServerWorkbenchClient.IsAdmin)) return false;
            if (mode == "ai")
            {
                var preset = _serverOptions;
                if (preset == null) return _serverProvider.AiConfigured && (!_serverProvider.AiKeyRequired || !string.IsNullOrWhiteSpace(_serverAiApiKey));
                Uri destination;
                return !string.IsNullOrWhiteSpace((string)preset["model"]) && Uri.TryCreate((string)preset["endpoint"], UriKind.Absolute, out destination) &&
                    (destination.Scheme == "https" || destination.Scheme == "http" && (string)preset["provider"] == "Ollama 本地模型") &&
                    ((string)preset["provider"] == "Ollama 本地模型" || !string.IsNullOrWhiteSpace(_serverAiApiKey));
            }
            var machine = _serverMachineOptions ?? _serverProvider.MachineSettings;
            if (machine == null) return _serverProvider.MachineConfigured && (!_serverProvider.MachineKeyRequired || !string.IsNullOrWhiteSpace(_serverMachineApiKey)) && (!_serverProvider.MachineAppIdRequired || !string.IsNullOrWhiteSpace(_serverMachineAppId));
            Uri endpoint;
            return machine != null && Uri.TryCreate((string)machine["endpoint"], UriKind.Absolute, out endpoint) &&
                (endpoint.Scheme == "https" || endpoint.Scheme == "http" && (string)machine["provider"] == "LibreTranslate" && string.IsNullOrWhiteSpace(_serverMachineApiKey)) &&
                ((string)machine["provider"] == "LibreTranslate" || !string.IsNullOrWhiteSpace(_serverMachineApiKey)) &&
                (!ServerMachineAppIdRequired() || !string.IsNullOrWhiteSpace(_serverMachineAppId));
        }

        internal static void ClearServerCredentials()
        {
            _serverAdminKey = _serverAiApiKey = _serverMachineApiKey = _serverMachineAppId = string.Empty;
            ServerWorkbenchClient.AdminKey = string.Empty;
            _serverProvider = null;
            _serverOptions = null;
            _serverProfile = _serverMachineProfile = "default";
            _serverMachineOptions = null;
            _chooseServerAiProvider = _chooseServerMachineProvider = false; _serverNextProvider = -1;
            ServerModels = new AiModelSelection();
            _serverReviewPage = new ServerReviewPage();
            _serverReviewIndex = 0;
            _serverReviewLoadedId = null;
            _serverRecentJobs = null;
            _serverPending = new List<ServerContributionEntry>();
            _exchange = null;
        }

        private static void UpdateServerSelection(ServerWorkbenchSnapshot snapshot)
        {
            if (_serverSelectionGeneration != ServerTranslationFeature.Generation)
            {
                _serverSelectionGeneration = ServerTranslationFeature.Generation;
                _serverSelectionSnapshot = null;
                _serverSourcesInitialized = false;
                ServerSourceSelection.Clear();
            }
            if (ReferenceEquals(_serverSelectionSnapshot, snapshot)) return;
            _serverSelectionSnapshot = snapshot;
            if (!_serverSourcesInitialized)
            {
                ServerSourceSelection.UnionWith(snapshot.SourceGroups.Select(group => group.Id));
                _serverSourcesInitialized = true;
            }
            var known = new HashSet<string>(snapshot.SourceGroups.Select(group => group.Id), StringComparer.OrdinalIgnoreCase);
            ServerSourceSelection.IntersectWith(known);
        }

        private static void DrawServerRangeSelection(ServerWorkbenchSnapshot snapshot)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("自动扫描结果", TranslationPanelTheme.Section);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Content("全选", "选择原版/未归属分组以及扫描到的所有 Mod 文件夹。")))
            {
                ServerSourceSelection.Clear();
                ServerSourceSelection.UnionWith(snapshot.SourceGroups.Select(group => group.Id));
                RequestGuiRefresh();
            }
            if (GUILayout.Button(Content("清空", "取消所有来源；开始翻译按钮会保持不可用。")))
            {
                ServerSourceSelection.Clear();
                RequestGuiRefresh();
            }
            GUILayout.EndHorizontal();
            foreach (var group in snapshot.SourceGroups)
            {
                var selectedBefore = ServerSourceSelection.Contains(group.Id);
                var selectedAfter = TranslationPanelTheme.Toggle(selectedBefore, Content(
                    group.Name + " · 待补 " + group.EntryCount + " 条",
                    group.Kind == "base"
                        ? "未在 SPT_Runtime/user/mods 的 Mod 文件中可靠定位到来源的内容，通常包含原版游戏文本。"
                        : "按 SPT_Runtime/user/mods 一级文件夹扫描推断的来源。引用同一 ID 的多个 Mod 可能导致归属近似。"));
                if (selectedAfter) ServerSourceSelection.Add(group.Id); else ServerSourceSelection.Remove(group.Id);
            }
            var selected = snapshot.CountEntries(ServerSourceSelection);
            GUILayout.Label("当前将处理 " + selected + " 条严格缺失中文的键值。", TranslationPanelTheme.Muted);
            GUILayout.EndVertical();
            if (selected >= 1000)
                DrawNotice("本次文本数量较大。任务会在服务端后台分批执行；可按来源文件夹缩小范围。", true);
            else
                DrawNotice("只处理英文存在，且中文缺失、为空或与英文完全相同的键值；已有有效中文不会被覆盖。", false);
        }

        private static void DrawServerStart(ServerWorkbenchSnapshot snapshot, bool ai)
        {
            var count = snapshot.CountEntries(ServerSourceSelection);
            var ready = ServerCredentialsReady(ai ? "ai" : "machine");
            var supported = ServerTranslationFeature.Capabilities.SupportsFeature("client-api-key");
            GUILayout.Label(ServerWorkbenchClient.IsAdmin
                ? "将处理 " + count + " 条缺失中文的游戏文本；服务端按批保存。"
                : "本次最多处理 " + Math.Min(30, count) + " / " + count + " 条，结果仅进入管理员待审队列。",
                TranslationPanelTheme.Muted);
            GUILayout.BeginHorizontal();
            if (ai) DrawPaidStopButton();
            GUI.enabled = !_jobRunningForLayout && supported && ready && count > 0 && (!ai || !_plugin.AiStopped.Value);
            if (GUILayout.Button(Content(_jobRunningForLayout ? "翻译进行中…" : ServerWorkbenchClient.IsAdmin
                    ? ai ? "开始 API 精翻" : "开始机器翻译" : "自费翻译并提交审核",
                "由 SimpleTranslatorCS 在服务端扫描、调用 API 和校验；普通玩家的结果等待管理员审核。"),
                TranslationPanelTheme.Primary, GUILayout.ExpandWidth(true)))
            {
                StartServerJob(snapshot, ai ? "ai" : "machine", false);
                RequestGuiRefresh();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private static void DrawServerScope(ServerWorkbenchSnapshot snapshot)
        {
            GUILayout.Label("服务端译文校对", TranslationPanelTheme.Section);
            DrawServerContributions();
            GUILayout.BeginVertical(GUI.skin.box);
            var modern = ServerTranslationFeature.Capabilities.SupportsFeature("review-catalog");
            GUILayout.Label(modern ? "查看已有译文和已知原文版本。点击查询应用筛选。" :
                "显示服务端生成记录。修改后由服务端校验原文、占位符与富文本。",
                TranslationPanelTheme.Muted);
            if (modern)
            {
                GUILayout.Label("搜索编号、原文、译文或来源", TranslationPanelTheme.Muted);
                _serverReviewQuery = GUILayout.TextField(_serverReviewQuery, 120);
                GUILayout.Label("来源（留空为全部，填写来源名称）", TranslationPanelTheme.Muted);
                _serverReviewGroup = GUILayout.TextField(_serverReviewGroup, 256);
                foreach (var group in _serverReviewPage.Groups.Where(name =>
                    name.IndexOf(_serverReviewGroup, StringComparison.OrdinalIgnoreCase) >= 0).Take(3))
                    if (GUILayout.Button(group)) _serverReviewGroup = group;
                _serverReviewLayer = DrawSelectionButtons(_serverReviewLayer,
                    new[] { "全部", "人工", "AI", "机翻", "已有中文" }, 5, TranslationPanelTheme.Tab);
                _serverReviewChangesOnly = TranslationPanelTheme.Toggle(_serverReviewChangesOnly, "仅原文已变化");
            }
            if (GUILayout.Button("刷新校对列表"))
            {
                ReadServerReviewPage(1);
                RequestGuiRefresh();
            }
            var page = _serverReviewPage;
            if (modern && page.Total > page.PageSize)
            {
                var totalPages = (page.Total + page.PageSize - 1) / page.PageSize;
                GUILayout.BeginHorizontal();
                if (page.Page > 1 && GUILayout.Button("上一页")) ReadServerReviewPage(page.Page - 1);
                GUILayout.Label("第 " + page.Page + "/" + totalPages + " 页 · " + page.Total + " 条");
                if (page.Page < totalPages && GUILayout.Button("下一页")) ReadServerReviewPage(page.Page + 1);
                GUILayout.EndHorizontal();
            }
            var entries = page.Entries;
            if (entries.Count == 0) DrawNotice("当前筛选范围没有可校对译文。", false);
            else
            {
                _serverReviewIndex = Math.Max(0, Math.Min(_serverReviewIndex, entries.Count - 1));
                var entry = entries[_serverReviewIndex];
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("上一条", GUILayout.Width(72f))) { _serverReviewIndex = Math.Max(0, _serverReviewIndex - 1); RequestGuiRefresh(); }
                GUILayout.Label((_serverReviewIndex + 1) + " / " + entries.Count, TranslationPanelTheme.Muted);
                if (GUILayout.Button("下一条", GUILayout.Width(72f))) { _serverReviewIndex = Math.Min(entries.Count - 1, _serverReviewIndex + 1); RequestGuiRefresh(); }
                GUILayout.EndHorizontal();
                var loadedId = entry.Mode + ":" + entry.Id;
                if (_serverReviewLoadedId != loadedId) { _serverReviewLoadedId = loadedId; _serverReviewValue = entry.Translation ?? string.Empty; }
                GUILayout.Label(entry.Id + " · " + ServerDomainLabel(entry.Domain) + " · " +
                    (entry.Mode == "ai" ? "AI" : entry.Mode == "machine" ? "机翻" : entry.Mode == "manual" ? "人工" : "已有中文") +
                    (string.IsNullOrEmpty(entry.SourceGroup) ? "" : " · " + entry.SourceGroup),
                    TranslationPanelTheme.Muted);
                if (entry.Stale)
                {
                    DrawNotice("英文原文已变化，请对照后确认译文是否仍适用。", true);
                    GUILayout.Label("基准原文", TranslationPanelTheme.Muted);
                    GUILayout.TextArea(entry.PreviousSource ?? string.Empty, GUILayout.MinHeight(45f));
                }
                if (entry.Mode == "published") GUILayout.Label("保存后建立一条人工译文覆盖。", TranslationPanelTheme.Muted);
                GUILayout.Label("英文原文", TranslationPanelTheme.Muted);
                GUILayout.TextArea(entry.Source ?? string.Empty, GUILayout.MinHeight(52f));
                GUILayout.Label("校对后的中文", TranslationPanelTheme.Muted);
                _serverReviewValue = GUILayout.TextArea(_serverReviewValue ?? string.Empty, GUILayout.MinHeight(84f));
                GUI.enabled = !_jobRunningForLayout && (modern || !entry.Stale) && !string.IsNullOrWhiteSpace(_serverReviewValue);
                if (GUILayout.Button("保存校对译文", TranslationPanelTheme.Primary))
                {
                    var selected = entry;
                    var value = _serverReviewValue;
                    var read = ServerReviewQuery(page.Page);
                    StartServerOperation("保存校对译文", async () =>
                    {
                        var result = await ServerWorkbenchClient.SaveReviewAsync(selected, value).ConfigureAwait(false);
                        return await ServerWorkbenchClient.WithFollowupAsync(result, async () =>
                        {
                            var refreshed = await read().ConfigureAwait(false);
                            SetServerReviewPage(refreshed);
                            await ServerWorkbenchClient.RefreshAsync().ConfigureAwait(false);
                        }).ConfigureAwait(false);
                    });
                    RequestGuiRefresh();
                }
                GUI.enabled = true;
            }
            GUILayout.EndVertical();
        }

        private static Func<Task<ServerReviewPage>> ServerReviewQuery(int page)
        {
            var text = _serverReviewQuery.Trim();
            var group = _serverReviewGroup.Trim();
            var layer = new[] { "", "manual", "ai", "machine", "published" }[_serverReviewLayer];
            var changedOnly = _serverReviewChangesOnly;
            return () => ServerWorkbenchClient.GetReviewAsync(page, text, layer, group, changedOnly);
        }

        private static void ReadServerReviewPage(int page)
        {
            var read = ServerReviewQuery(page);
            StartServerOperation("读取校对列表", async () =>
            {
                var result = await read().ConfigureAwait(false);
                SetServerReviewPage(result);
                return "已读取 " + result.Entries.Count + " 条译文。";
            });
            RequestGuiRefresh();
        }

        private static void SetServerReviewPage(ServerReviewPage page)
        {
            ServerWorkbenchClient.PublishOperationState(() =>
            {
                _serverReviewPage = page;
                _serverReviewIndex = 0;
                _serverReviewLoadedId = null;
            });
        }

        private static void DrawServerContributions()
        {
            if (!ServerTranslationFeature.Capabilities.SupportsFeature("contribution-review")) return;
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("玩家候选译文 · 已加载 " + _serverPending.Count + " 条", TranslationPanelTheme.Section);
            if (GUILayout.Button("刷新待审列表"))
            {
                StartServerOperation("读取玩家候选译文", async () =>
                {
                    var receivedState = await ServerWorkbenchClient.GetPendingContributionsAsync().ConfigureAwait(false);
                    ServerWorkbenchClient.PublishOperationState(() => _serverPending = receivedState);
                    _serverPendingIndex = 0;
                    _serverPendingLoadedId = null;
                    return "已读取 " + _serverPending.Count + " 条候选译文。";
                });
                RequestGuiRefresh();
            }
            if (_serverPending.Count == 0)
                GUILayout.Label("目前没有待审核的玩家译文。", TranslationPanelTheme.Muted);
            else
            {
                _serverPendingIndex = Math.Max(0, Math.Min(_serverPendingIndex, _serverPending.Count - 1));
                var entry = _serverPending[_serverPendingIndex];
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("上一条", GUILayout.Width(72f)))
                { _serverPendingIndex = Math.Max(0, _serverPendingIndex - 1); RequestGuiRefresh(); }
                GUILayout.Label((_serverPendingIndex + 1) + " / " + _serverPending.Count, TranslationPanelTheme.Muted);
                if (GUILayout.Button("下一条", GUILayout.Width(72f)))
                { _serverPendingIndex = Math.Min(_serverPending.Count - 1, _serverPendingIndex + 1); RequestGuiRefresh(); }
                GUILayout.EndHorizontal();
                if (_serverPendingLoadedId != entry.SubmissionId)
                { _serverPendingLoadedId = entry.SubmissionId; _serverPendingValue = entry.Translation; }
                GUILayout.Label((entry.Mode == "ai" ? "AI" : entry.Mode == "manual" ? "人工" : "机翻") + " · " + entry.Player + " · " + entry.Id,
                    TranslationPanelTheme.Muted);
                if (entry.Stale) DrawNotice("原文或缺失状态已变化；此条只能拒绝。", true);
                GUILayout.Label("英文原文", TranslationPanelTheme.Muted);
                GUILayout.TextArea(entry.Source ?? string.Empty, GUILayout.MinHeight(52f));
                GUILayout.Label("候选译文（批准前可修改）", TranslationPanelTheme.Muted);
                _serverPendingValue = GUILayout.TextArea(_serverPendingValue ?? string.Empty, GUILayout.MinHeight(84f));
                GUILayout.BeginHorizontal();
                GUI.enabled = !_jobRunningForLayout && !entry.Stale && !string.IsNullOrWhiteSpace(_serverPendingValue);
                if (GUILayout.Button("批准并生效", TranslationPanelTheme.Primary))
                    DecideServerContribution(entry, true, _serverPendingValue);
                GUI.enabled = !_jobRunningForLayout;
                if (GUILayout.Button("拒绝")) DecideServerContribution(entry, false, string.Empty);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
        }

        private static void DecideServerContribution(ServerContributionEntry entry, bool approve, string translation)
        {
            StartServerOperation(approve ? "批准候选译文" : "拒绝候选译文", async () =>
            {
                var result = await ServerWorkbenchClient.DecideContributionAsync(entry, approve, translation).ConfigureAwait(false);
                return await ServerWorkbenchClient.WithFollowupAsync(result, async () =>
                {
                    var receivedState = await ServerWorkbenchClient.GetPendingContributionsAsync().ConfigureAwait(false);
                    ServerWorkbenchClient.PublishOperationState(() => _serverPending = receivedState);
                    _serverPendingLoadedId = null;
                }).ConfigureAwait(false);
            });
            RequestGuiRefresh();
        }

        private static void DrawServerBaseline(ServerWorkbenchSnapshot snapshot)
        {
            GUILayout.Label("文件原文基准", TranslationPanelTheme.Section);
            GUILayout.Label("基准记录每个译文键上次对应的英文原文。游戏或 Mod 更新后，变化数量会按文件显示；更新基准不会删除译文。",
                TranslationPanelTheme.Muted);
            foreach (var file in snapshot.Files)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label((file.Kind == "manual" ? "人工" : file.Kind == "ai" ? "AI" : "机翻") + " · " + file.Path);
                GUILayout.Label(file.EntryCount + " 个键值 · " + (!file.BaselineKnown ? "尚无基准" :
                    file.ChangedCount == 0 ? "无变化" : "变化 " + file.ChangedCount + " 个"), TranslationPanelTheme.Muted);
                if (GUILayout.Button(Content("打开文件位置", FileLocationOpener.ServerPathTooltip(file.Path))))
                    RunUiAction(() => FileLocationOpener.OpenServerTranslation(file.Path));
                GUILayout.EndVertical();
            }
            GUI.enabled = !_jobRunningForLayout;
            if (GUILayout.Button("以当前英文原文更新手动文件基准", TranslationPanelTheme.Primary))
            {
                StartServerOperation("更新服务端原文基准", ServerWorkbenchClient.UpdateBaselineAsync);
                RequestGuiRefresh();
            }
            GUI.enabled = true;
        }

        private static void StartServerRefresh()
        {
            StartServerOperation("刷新服务端目录", () => Task.Run(async () =>
            {
                var snapshot = await ServerWorkbenchClient.RefreshAsync().ConfigureAwait(false);
                if (snapshot != null && ServerTranslationFeature.Capabilities.SupportsFeature("provider-status"))
                {
                    var receivedState = await ServerWorkbenchClient.GetProviderStatusAsync().ConfigureAwait(false);
                    ServerWorkbenchClient.PublishOperationState(() => _serverProvider = receivedState);
                }
                if (!string.IsNullOrWhiteSpace(ServerWorkbenchClient.AdminKey) && !ServerWorkbenchClient.IsAdmin)
                    return "管理员密钥未通过验证。请填写服务端 workbench-admin.jsonc 当前的 adminKey，并重新验证。";
                if (!ServerWorkbenchClient.CanUseWorkbench) return ServerWorkbenchClient.WorkbenchLockedMessage;
                return snapshot == null ? "当前为普通玩家模式：可手动拉取译文；管理员请核对密钥。"
                    : (ServerWorkbenchClient.IsAdmin ? "管理员目录" : "玩家可译目录") +
                      "已加载：发现 " + snapshot.Entries.Count + " 个待翻键值。";
            }));
        }

        private static void StartServerJob(ServerWorkbenchSnapshot snapshot, string mode, bool test)
        {
            if (IsJobRunning()) return;
            if (ServerWorkbenchClient.RecoverableJobId() != null)
            {
                SetStatus("请先查询上次服务端任务，确认任务结束后再开始新的翻译。");
                return;
            }
            if (mode == "ai" && (!CanStartApi() || !ServerTranslationFeature.IsCurrentConnection(_apiServerGeneration))) return;
            BeginJob(test ? "服务端 API 校验" : "服务端翻译任务", test ? 1 : snapshot.Entries.Count, paid: mode == "ai");
            _serverJobActive = true;
            _job = RunServerJobAsync(snapshot, mode, test);
        }

        private static async Task RunServerJobAsync(ServerWorkbenchSnapshot snapshot, string mode, bool test, string recoverId = null)
        {
            var lifecycle = _lifecycleGeneration;
            var token = _cancellation.Token;
            var diagnostics = new TranslationDiagnostics("服务端 " + mode + (test ? " API 测试" : " 翻译"),
                Enumerable.Empty<TranslationWorkItem>(), new[] { _serverAiApiKey, _serverMachineApiKey, _serverMachineAppId, _serverAdminKey });
            _activeDiagnostics = diagnostics;
            var serverFinished = false;
            try
            {
                using (ServerWorkbenchClient.BeginOperation(token))
                {
                    var groups = test || snapshot == null ? Enumerable.Empty<string>() : ServerSourceSelection.ToArray();
                    var apiKey = mode == "ai" ? _serverAiApiKey : _serverMachineApiKey;
                    var appId = mode == "machine" && _serverProvider != null && ServerMachineAppIdRequired()
                        ? _serverMachineAppId : string.Empty;
                    var job = recoverId != null ? await ServerWorkbenchClient.GetJobStatusAsync(recoverId).ConfigureAwait(false) :
                        await ServerWorkbenchClient.StartJobAsync(mode, snapshot == null ? string.Empty : snapshot.Revision,
                        groups, test, apiKey, appId, mode == "ai" ? _serverProfile : _serverMachineProfile, mode == "ai" ? _serverOptions : null, providerRevision: _serverProvider?.Revision ?? string.Empty, machineOptions: _serverMachineOptions).ConfigureAwait(false);
                    var jobId = job.JobId;
                    diagnostics.Write("server-job", new JObject { ["jobId"] = jobId });
                    if (_serverStopRequested && job.State == "running") _ = RequestServerCancelAsync();
                    while (job.State == "running")
                    {
                        diagnostics.ServerProgress(job);
                        lock (JobSync)
                        {
                            _progressDetail = (string)job.Diagnostics?["detail"] ?? "服务端正在处理当前批次";
                            _progressItems = string.Join("\n", ((JArray)job.Diagnostics?["currentItems"] ?? new JArray()).Values<string>().ToArray());
                        }
                        UpdateProgress(job.Completed, job.Total);
                        SetStatus((test ? "接口校验" : !job.Contribution ? "服务端翻译" : "候选译文") +
                            (!job.Contribution ? " · 已保存 " : " · 待审 ") + job.Saved +
                            " · 待人工复核 " + job.Rejected);
                        await Task.Delay(1000, token).ConfigureAwait(false);
                        job = await ServerWorkbenchClient.GetJobStatusAsync().ConfigureAwait(false);
                        if (job.JobId != jobId) throw new InvalidOperationException("服务端当前任务已切换；请按日志中的任务 ID 查看服务端诊断记录。");
                    }
                    diagnostics.ServerProgress(job);
                    ServerWorkbenchClient.ClearRecoverableJob();
                    diagnostics.Finish(job.State == "completed" && job.Rejected > 0 ? "partial" : job.State, job.Error);
                    UpdateProgress(job.Completed, job.Total);
                    if (job.State == "failed") throw new InvalidOperationException(job.Error);
                    serverFinished = true;
                    var message = test ? "API 校验" + (job.State == "cancelled" ? "已取消" : "完成") :
                        (!job.Contribution ? "服务端翻译" : "候选译文提交") +
                        (job.State == "cancelled" ? "已取消" : "完成") + "：" +
                        (!job.Contribution ? "保存 " : "待审 ") + job.Saved +
                        " 条，隔离 " + job.Rejected + " 条。";
                    SetStatus(test ? message : await ServerWorkbenchClient.WithFollowupAsync(message,
                        ServerWorkbenchClient.RefreshAsync, ServerWorkbenchClient.IsAdmin ?
                            (Func<Task<string>>)ServerWorkbenchClient.PullAsync : null).ConfigureAwait(false));
                }
            }
            catch (OperationCanceledException)
            {
                if (lifecycle == _lifecycleGeneration)
                {
                    if (!serverFinished) diagnostics.Finish("client_cancelled_or_disconnected");
                    SetStatus("已停止等待服务端任务；已受理的任务是否取消，请核对服务端记录。");
                }
            }
            catch (ServerWorkbenchException ex) when (recoverId != null && ex.JobMissing && lifecycle == _lifecycleGeneration)
            {
                try
                {
                    await ServerWorkbenchClient.CancelJobAsync(recoverId).ConfigureAwait(false);
                    ServerWorkbenchClient.ClearRecoverableJob();
                    diagnostics.Finish("not_found", ex.Message);
                    SetStatus("服务端未找到旧任务，已阻止迟到的启动请求。重新翻译时会跳过已保存的译文。");
                }
                catch (Exception stopError)
                {
                    diagnostics.Finish("unknown", stopError.Message);
                    SetStatus("无法确认旧任务，保留任务 ID，可稍后再查。");
                }
            }
            catch (Exception ex)
            {
                if (lifecycle != _lifecycleGeneration) return;
                if (!serverFinished) diagnostics.Finish("client_failed_or_disconnected", ex.Message);
                else diagnostics.Write("client-followup-error", new JObject { ["error"] = ex.Message });
                SetStatus(serverFinished ? "服务端任务已结束。客户端刷新或同步未完成：" + ex.Message :
                    "服务端任务失败：" + ex.Message);
                if (_log != null) _log.LogError("SimpleTranslatorCS server task failed: " + ex);
            }
            finally { if (lifecycle == _lifecycleGeneration) _serverJobActive = false; }
        }

        private static void StartServerOperation(string status, Func<Task<string>> action)
        {
            if (IsJobRunning()) return;
            BeginJob(status, 1);
            _job = RunServerOperationAsync(action);
        }

        private static async Task RunServerOperationAsync(Func<Task<string>> action)
        {
            var lifecycle = _lifecycleGeneration;
            try
            {
                using (ServerWorkbenchClient.BeginOperation(_cancellation == null ? CancellationToken.None : _cancellation.Token))
                {
                    var message = await action().ConfigureAwait(false);
                    ServerWorkbenchClient.CheckOperation();
                    if (lifecycle != _lifecycleGeneration) return;
                    UpdateProgress(1, 1);
                    SetStatus(message);
                }
            }
            catch (OperationCanceledException) { if (lifecycle == _lifecycleGeneration) SetStatus("操作已取消或连接已切换；服务端已受理的写入请核对执行记录。"); }
            catch (Exception ex)
            {
                if (lifecycle != _lifecycleGeneration) return;
                SetStatus("失败：" + ex.Message);
                var workbench = ex as ServerWorkbenchException;
                if (_log != null)
                {
                    if (workbench != null && workbench.Unauthorized)
                        _log.LogWarning("Server workbench write was rejected: " + ex.Message);
                    else
                        _log.LogError("Server workbench operation failed: " + ex);
                }
            }
        }

        private static string ServerDomainLabel(string domain)
        {
            if (string.Equals(domain, "item", StringComparison.OrdinalIgnoreCase)) return "物品";
            if (string.Equals(domain, "quest", StringComparison.OrdinalIgnoreCase)) return "任务";
            if (string.Equals(domain, "achievement", StringComparison.OrdinalIgnoreCase)) return "成就";
            if (string.Equals(domain, "trader", StringComparison.OrdinalIgnoreCase)) return "商人";
            if (domain == "customization") return "外观";
            if (domain == "handbook") return "手册";
            if (domain == "locale") return "其他语言表文本";
            return domain;
        }

        private static string PluginDisplayName(LocalizationSnapshot snapshot)
        {
            var item = JsoncLocalization.GetPluginWorkItem(snapshot.Guid);
            return item == null ? snapshot.PluginName : JsoncLocalization.GetDisplayValue(item);
        }

        private static void DrawInventory(string guid)
        {
            TranslationInventory inventory;
            if (!Inventories.TryGetValue(guid, out inventory)) return;
            var previous = GUI.contentColor;
            var light = TranslationPanelTheme.LightCards;
            GUI.contentColor = inventory.Error != null ? (light ? new Color(0.7f, 0.12f, 0.12f) : new Color(1f, 0.5f, 0.5f)) :
                !inventory.HasFiles ? (light ? new Color(0.3f, 0.35f, 0.4f) : new Color(0.75f, 0.8f, 0.85f)) :
                inventory.Stale == 0 && inventory.Total > 0 && inventory.Translated == inventory.Total ?
                    (light ? new Color(0f, 0.4f, 0.18f) : new Color(0.5f, 1f, 0.7f)) :
                    (light ? new Color(0.5f, 0.28f, 0f) : new Color(1f, 0.8f, 0.4f));
            GUILayout.Label(Content(inventory.Label, inventory.Details), TranslationPanelTheme.StatusLabel);
            GUI.contentColor = previous;
        }

        private static TranslationBatchPlan CreateBatchPlan(bool ai, IDictionary<string, TranslationInventory> inventories = null)
        {
            return TranslationBatchPlan.Create(_snapshots, BatchSelection, GetTranslationScope(), ai, CurrentAiProfile(), _forceAi, _onlyMissing, inventories);
        }

        private static void DrawAppearance()
        {
            var appearance = _plugin.Appearance;
            if (_scaleDraft < 1f) _scaleDraft = appearance.GuiScale.Value;
            GUILayout.Label("F12 界面缩放 · " + (_scaleDraft * 100f).ToString("F0") + "%", TranslationPanelTheme.Section);
            GUILayout.Label(_externalViewport
                ? "缩放汉化工作台及其弹窗。4K 建议 200%。BetterBepInEx 菜单有独立的缩放设置。"
                : "同时放大设置窗口、文字和工作台。4K 建议 200%。", TranslationPanelTheme.Muted);
            _scaleDraft = (float)Math.Round(GUILayout.HorizontalSlider(_scaleDraft, 1f, 3f) * 20f) / 20f;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("应用缩放")) appearance.GuiScale.Value = _scaleDraft;
            if (GUILayout.Button("恢复 100%")) { _scaleDraft = 1f; appearance.GuiScale.Value = 1f; }
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);
            var drawExpandedColor = _expandedColor;
            GUILayout.Label("工作台配色", TranslationPanelTheme.Section);
            GUILayout.Label("模板即选即用；自定义颜色可输入 HEX 或展开 RGB 滑块，点击应用后保存。", TranslationPanelTheme.Muted);
            var selected = Array.IndexOf(WorkbenchAppearance.Presets, appearance.Preset.Value);
            var preset = DrawSelectionButtons(selected, WorkbenchAppearance.Presets, 3, TranslationPanelTheme.Tab);
            if (preset != selected)
            {
                appearance.Preset.Value = WorkbenchAppearance.Presets[preset];
                _colorDraft = null;
                _expandedColor = -1;
                RequestGuiRefresh();
            }
            if (_colorDraft == null) _colorDraft = appearance.GetPalette();
            GUILayout.Space(6f);
            var valid = true;
            for (var index = 0; index < _colorDraft.Length; index++)
            {
                Color color;
                var parsed = WorkbenchAppearance.TryParse(_colorDraft[index], out color);
                GUILayout.BeginHorizontal();
                GUILayout.Label(WorkbenchAppearance.ColorNames[index], GUILayout.Width(74f));
                var edited = GUILayout.TextField(_colorDraft[index], 7, GUILayout.MinWidth(0f));
                if (edited != _colorDraft[index]) { _colorDraft[index] = edited; RequestGuiRefresh(); }
                var swatch = GUILayoutUtility.GetRect(18f, 24f, GUILayout.Width(18f));
                if (Event.current.type == EventType.Repaint)
                {
                    var previousColor = GUI.color;
                    GUI.color = parsed ? color : Color.white;
                    GUI.DrawTexture(swatch, Texture2D.whiteTexture);
                    GUI.color = previousColor;
                }
                var oldBackground = GUI.backgroundColor;
                GUI.backgroundColor = parsed ? color : Color.white;
                if (GUILayout.Button(Content(_expandedColor == index ? "−" : "+", "展开 / 收起 RGB 调色滑块"), GUILayout.Width(36f)))
                {
                    _expandedColor = _expandedColor == index ? -1 : index;
                    GUI.backgroundColor = oldBackground;
                    RequestGuiRefresh();
                }
                GUI.backgroundColor = oldBackground;
                GUILayout.EndHorizontal();
                if (drawExpandedColor == index)
                {
                    var red = DrawColorChannel("R", color.r);
                    var green = DrawColorChannel("G", color.g);
                    var blue = DrawColorChannel("B", color.b);
                    if (red != color.r || green != color.g || blue != color.b)
                    {
                        _colorDraft[index] = WorkbenchAppearance.ToHex(new Color(red, green, blue));
                        RequestGuiRefresh();
                    }
                }
                if (!WorkbenchAppearance.TryParse(_colorDraft[index], out color)) valid = false;
            }
            if (!valid) GUILayout.Label("请输入六位十六进制颜色，例如 #2E6EA8。", TranslationPanelTheme.Muted);
            GUI.enabled = valid;
            if (GUILayout.Button("应用并保存自定义配色", TranslationPanelTheme.Primary))
            {
                appearance.ApplyCustom(_colorDraft);
                SetStatus("自定义配色已保存，重启游戏后仍保留。");
                RequestGuiRefresh();
            }
            GUI.enabled = true;
            if (GUILayout.Button("撤销未应用的颜色")) { _colorDraft = null; RequestGuiRefresh(); }
            if (_externalViewport) return;
            GUILayout.Space(8f);
            GUILayout.Label("顶部工具栏", TranslationPanelTheme.Section);
            var expanded = TranslationPanelTheme.Toggle(appearance.ToolbarExpanded.Value, "启动时展开（自动记住上次状态）");
            if (expanded != appearance.ToolbarExpanded.Value)
            {
                appearance.ToolbarExpanded.Value = expanded;
                if (!expanded) CloseDrawer();
                RequestGuiRefresh();
            }
            GUILayout.Label("收起后保留“汉化 ▸”入口，已启用的译文模块继续生效。", TranslationPanelTheme.Muted);
        }

        private static float DrawColorChannel(string label, float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(16f));
            var channel = GUILayout.HorizontalSlider(value, 0f, 1f);
            GUILayout.Label(Mathf.RoundToInt(channel * 255).ToString(), GUILayout.Width(28f));
            GUILayout.EndHorizontal();
            return channel;
        }

        private static void DrawManualSettings()
        {
            GUILayout.Label("人工校对", TranslationPanelTheme.Section);
            GUILayout.Label("选择文本、输入译文、确认预览，然后保存。", TranslationPanelTheme.Muted);
            DrawExchange(false, null);
            if (GUILayout.Button(_captureActive ? "结束抓取 · 返回正常操作" : "鼠标抓取 · 点击配置列表中的目标",
                _captureActive ? TranslationPanelTheme.Selected : GUI.skin.button))
            {
                ToggleCapture();
                RequestGuiRefresh();
            }
            if (_captureActive)
                DrawNotice("抓取已开启：悬浮描边预览范围，点击后保留加粗选中框。可选插件名、分类、设置名及自定义控件；自定义按钮和开关不会执行原操作。", true);

            DrawLayerToggle(_plugin.EnableManualTranslation, "显示人工译文（优先使用）", 250f);
            var autoAdvance = TranslationPanelTheme.Toggle(_plugin.ManualAutoAdvance.Value, "保存后下一项");
            if (autoAdvance != _plugin.ManualAutoAdvance.Value)
                _plugin.ManualAutoAdvance.Value = autoAdvance;
            if (_snapshots.Count > 0)
                DrawClientFileButtons(_snapshots[_pluginIndex]);

            var items = _editorItems;
            if (items.Count == 0)
                return;
            _editorIndex = Mathf.Clamp(_editorIndex, 0, items.Count - 1);
            var item = items[_editorIndex];
            if (!_manualLoaded)
            {
                _manualValue = JsoncLocalization.GetStoredValue(item, TranslationLayer.Manual);
                _manualOriginalValue = _manualValue;
                _manualDraftItem = item;
                _manualLoaded = true;
            }

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label("文本 " + (_editorIndex + 1) + " / " + items.Count, TranslationPanelTheme.Section);
            if (GUILayout.Button("上一项", GUILayout.Width(62f)))
            {
                MoveEditor(items, -1);
                RequestGuiRefresh();
            }
            if (GUILayout.Button("下一项", GUILayout.Width(62f)))
            {
                MoveEditor(items, 1);
                RequestGuiRefresh();
            }
            GUILayout.EndHorizontal();

            DrawRelatedFields(item, items);
            GUILayout.Label(item.Context, TranslationPanelTheme.Muted);
            GUILayout.Label("英文原文");
            GUILayout.Label(item.Source ?? string.Empty, GUI.skin.textArea, GUILayout.MinHeight(56f), GUILayout.MinWidth(0f));
            GUILayout.Label("人工译文");
            _manualValue = GUILayout.TextArea(_manualValue ?? string.Empty, GUILayout.MinHeight(100f), GUILayout.MinWidth(0f));
            var formatted = TranslationSafety.PreserveOuterFormatting(item.Source, _manualValue);
            GUILayout.BeginHorizontal();
            GUILayout.Label("富文本预览", TranslationPanelTheme.Section);
            GUILayout.FlexibleSpace();
            GUILayout.Label(formatted != _manualValue ? "保存时沿用原文格式" : "实时预览 · 不会自动保存",
                TranslationPanelTheme.Muted, GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();
            GUILayout.BeginVertical(GUI.skin.box);
            _manualPreviewScroll = GUILayout.BeginScrollView(_manualPreviewScroll, false, true,
                GUILayout.MinHeight(92f), GUILayout.MaxHeight(132f));
            if (string.IsNullOrWhiteSpace(_manualValue))
                GUILayout.Label("在上方输入译文后，这里会显示最终富文本效果。", TranslationPanelTheme.Muted,
                    GUILayout.MinHeight(66f), GUILayout.ExpandWidth(true));
            else
                GUILayout.Label(PrepareRichTextPreview(formatted), TranslationPanelTheme.RichPreview,
                    GUILayout.MinHeight(66f), GUILayout.ExpandWidth(true));
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.EndVertical();
            if (GUILayout.Button("检查原文更新")) _requestedTab = 4;
        }

        private static void DrawRelatedFields(TranslationWorkItem current, IList<TranslationWorkItem> items)
        {
            if (current.Kind != "Setting")
                return;
            GUILayout.BeginHorizontal();
            GUILayout.Label("当前字段", GUILayout.Width(58f));
            DrawFieldButton("名称", "DispName", current, items);
            DrawFieldButton("分类", "Category", current, items);
            DrawFieldButton("说明", "Description", current, items);
            GUILayout.EndHorizontal();
        }

        private static void DrawFieldButton(string label, string field, TranslationWorkItem current, IList<TranslationWorkItem> items)
        {
            var target = items.FirstOrDefault(item => item.Kind == "Setting" && item.EntryKey == current.EntryKey && item.Field == field);
            GUI.enabled = target != null;
            var oldColor = GUI.color;
            if (current.Field == field)
                GUI.color = new Color(0.68f, 0.88f, 1f, 1f);
            var clicked = GUILayout.Button(label) && target != null;
            GUI.color = oldColor;
            GUI.enabled = true;
            if (clicked)
            {
                SelectEditorItem(items, target);
                RequestGuiRefresh();
            }
        }

        private static void DrawAiSettings()
        {
            var provider = TranslationProviderCatalog.GetAiProvider(_plugin.AiProvider.Value);
            GUILayout.Label("AI 语境精翻", TranslationPanelTheme.Section);
            GUILayout.Label("适合说明、术语和长文本，可补充用词要求。", TranslationPanelTheme.Muted);
            DrawLayerToggle(_plugin.EnableAiTranslation, "显示 AI 译文", 180f);
            GUILayout.BeginVertical(GUI.skin.box);
            DrawAiProviderSelector(provider);
            DrawTextSetting("模型 ID", _plugin.AiModel);
            if (DrawSecretSetting(provider.ApiKeyRequired ? "API 密钥" : "API 密钥（可选）", ref _aiApiKey, ref _showAiKey))
                PersistCurrentCredentials();
            DrawCredentialPersistenceToggle();
            var endpoint = _plugin.AiEndpoint.Value; var key = _aiApiKey;
            var context = TranslationFoundation.AiModelCatalog.Context(endpoint, key, provider.AnthropicAuthentication);
            var model = DrawModelSelection(AiModels, context, !string.IsNullOrWhiteSpace(endpoint) && (!provider.ApiKeyRequired || !string.IsNullOrWhiteSpace(key)),
                refresh => TranslationClients.GetModelsAsync(provider, endpoint, key, CancellationToken.None, refresh), _plugin.AiModel.Value, new string[0]);
            if (model != _plugin.AiModel.Value) _plugin.AiModel.Value = model;
            DrawTextSetting("译成", _plugin.TargetLanguage);
            GUILayout.Label("术语与风格要求（可选）", TranslationPanelTheme.Muted);
            GUILayout.Label("术语表按本批原文匹配，仅发送相关条目；编辑后下一次任务读取。已有译文不会自动付费重翻。", TranslationPanelTheme.Muted);
            if (GUILayout.Button("编辑本地 AI 术语表")) { _captureActive = false; GlossaryEditorWindow.Show(); }
            var prompt = GUILayout.TextArea(_plugin.AiExtraPrompt.Value ?? string.Empty, GUILayout.MinHeight(60f), GUILayout.MinWidth(0f));
            if (prompt != _plugin.AiExtraPrompt.Value) _plugin.AiExtraPrompt.Value = prompt;
            GUILayout.EndVertical();
            var forceAi = TranslationPanelTheme.Toggle(_forceAi, Content("强制重新翻译", "重新请求相同 API 已完成的文本。若要更新有效译文，还需关闭上方的“跳过已有有效汉化”。"));
            if (forceAi != _forceAi) { _forceAi = forceAi; _nextBatchCheck = 0f; RequestGuiRefresh(); }
            GUILayout.Label("本次待处理 " + _pendingAiCount + " / " + _batchItemCount + " 条 · 缓存与相同语境文本自动复用", TranslationPanelTheme.Muted);
            GUILayout.Label("设置名、说明和选项分别计条；按条数与字符预算分批保存。漏译仅记录失败，不自动花费 token 重翻。", TranslationPanelTheme.Muted);
            var drawAdvanced = _aiAdvanced;
            DrawAdvancedToggle(ref _aiAdvanced);
            if (drawAdvanced)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                DrawTextSetting("接口地址", _plugin.AiEndpoint);
                var drawPrompt = _showBuiltInPrompt;
                if (GUILayout.Button(_showBuiltInPrompt ? "收起内置提示词" : "查看自动提示词"))
                {
                    _showBuiltInPrompt = !_showBuiltInPrompt;
                    RequestGuiRefresh();
                }
                if (drawPrompt)
                    GUILayout.Label(TranslationSafety.BuildPrompt(_plugin.TargetLanguage.Value, _plugin.AiExtraPrompt.Value), GUI.skin.textArea);
                DrawSliderSetting("每批文本", _plugin.AiBatchSize, 1, 60, " 条");
                DrawSliderSetting("字符预算", _plugin.AiMaxInputCharacters, 1000, 60000, " 字符");
                DrawSliderSetting("临时失败重试", _plugin.AiRetryCount, 0, 3, " 次");
                GUILayout.Label("仅重试 HTTP 429 / 5xx；设为 0 可关闭。超时、断网或格式错误不会自动重发。", TranslationPanelTheme.Muted);
                DrawSliderSetting("请求超时", _plugin.RequestTimeoutSeconds, 10, 600, " 秒");
                DrawTextSetting("参数适配器", _plugin.AiAdapter);
                DrawTextSetting("思考强度", _plugin.AiReasoning);
                DrawSliderSetting("输出 token 上限", _plugin.AiMaxOutputTokens, 256, 32768, " token");
                DrawBooleanSetting(_plugin.AiJsonOutput, "结构化 JSON 输出（接口支持时）");
                DrawSliderSetting("任务 token 预算（0 为默认）", _plugin.AiMaxReportedTokens, 0, 1000000, " token");
                DrawSliderSetting("任务请求上限（0 为默认）", _plugin.AiMaxRequests, 0, 1000, " 次");
                DrawSliderSetting("请求间隔", _plugin.AiRequestDelayMs, 0, 10000, " ms");
                GUILayout.Label("预算按已报告用量停止新请求，无法撤回正在执行的调用；不报告用量的接口仍受请求上限约束。", TranslationPanelTheme.Muted);
                GUILayout.EndVertical();
            }
            DrawNotice("仅发送所选文本与字段上下文，不包含配置当前值和默认值。", false);
            var missingTitles = BatchSelection.Count(guid => Inventories.TryGetValue(guid, out var inventory) &&
                inventory.Error == null && !inventory.ValidIds.Contains("plugin-name"));
            if (missingTitles > 0)
            {
                var enabled = GUI.enabled;
                GUI.enabled = enabled && !_jobRunningForLayout && !string.IsNullOrWhiteSpace(_plugin.AiModel.Value) &&
                    (!provider.ApiKeyRequired || !string.IsNullOrWhiteSpace(_aiApiKey));
                if (GUILayout.Button(Content("仅补译已选插件标题（" + missingTitles + "）",
                    "按批发送已勾选插件的缺译标题，可能计费。")))
                    StartBatchJob(true, true);
                GUI.enabled = enabled;
            }
            if (GUILayout.Button("检查插件更新 / 译文基准")) _requestedTab = 4;
        }

        private static void DrawMachineSettings()
        {
            var provider = TranslationProviderCatalog.GetMachineProvider(_plugin.MachineProvider.Value);
            var drawWeb = _machineWeb;
            GUILayout.Label("快速机器翻译", TranslationPanelTheme.Section);
            var mode = DrawSelectionButtons(_machineWeb ? 1 : 0, new[]
            {
                Content("官方接口", "首次填写平台应用信息与密钥，程序自动批量请求；平台可能按量计费。译文会保存到 zh-cn/translations/MT。"),
                Content("网页翻译", "插件不需要 API 密钥：复制原文并打开网站，再手动贴回译文。网站的登录与使用限制由平台决定；译文仍不落盘。")
            }, 2, TranslationPanelTheme.Skin.button);
            if ((mode == 1) != _machineWeb) { _machineWeb = mode == 1; RequestGuiRefresh(); }
            GUILayout.Label(drawWeb ? "网页回填是临时译文 · 退出游戏即清空 · 不写入 JSONC" :
                "接口机翻会保存到 zh-cn/translations/MT · 原文变化后自动暂停旧译文", TranslationPanelTheme.Muted);
            DrawLayerToggle(_plugin.EnableMachineTranslation, "显示机翻译文", 180f);
            if (drawWeb) { DrawWebTranslation(); return; }
            GUILayout.BeginVertical(GUI.skin.box);
            DrawMachineProviderSelector(provider);
            if (provider.AppIdRequired)
            {
                GUILayout.Label(provider.Name == "百度翻译" ? "APP ID" : "应用 ID / appKey", TranslationPanelTheme.Muted);
                var appId = GUILayout.TextField(_machineAppId, GUILayout.MinWidth(0f));
                if (appId != _machineAppId) { _machineAppId = appId; PersistCurrentCredentials(); }
            }
            if (DrawSecretSetting(provider.ApiKeyRequired ? "API 密钥" : "API 密钥（可选）", ref _machineApiKey, ref _showMachineKey))
                PersistCurrentCredentials();
            DrawCredentialPersistenceToggle();
            DrawTextSetting("源语言", _plugin.MachineSourceLanguage);
            DrawTextSetting("目标语言", _plugin.MachineTargetLanguage);
            if (provider.Name == "Microsoft Translator")
                DrawTextSetting("资源区域", _plugin.MachineRegion);
            GUILayout.EndVertical();
            var drawAdvanced = _machineAdvanced;
            DrawAdvancedToggle(ref _machineAdvanced);
            if (drawAdvanced)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                DrawTextSetting("接口地址", _plugin.MachineEndpoint);
                DrawSliderSetting("每批文本", _plugin.MachineBatchSize, 1, 50, " 条");
                DrawSliderSetting("请求超时", _plugin.RequestTimeoutSeconds, 10, 600, " 秒");
                GUILayout.EndVertical();
            }
            DrawNotice("选择预设会自动填入接口与语言。首次使用需自行开通平台服务；可用性取决于当前网络和账号权限。", false);
            DrawClearTemporary();
        }

        private static void DrawTranslationScope()
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("界面显示", TranslationPanelTheme.Section);
            var display = DrawSelectionButtons((int)_plugin.DisplayMode.Value,
                new[] { "中文", "英文", "双语换行" }, 3, TranslationPanelTheme.Skin.button);
            if (display != (int)_plugin.DisplayMode.Value)
                _plugin.DisplayMode.Value = (LocalizationDisplayMode)display;
            GUILayout.Space(6f);
            GUILayout.Label("参与显示的译文", TranslationPanelTheme.Section);
            DrawBooleanSetting(_plugin.EnableManualTranslation, "人工译文 · 优先使用");
            DrawBooleanSetting(_plugin.EnableAiTranslation, "AI 译文 · 补充人工未译项");
            DrawBooleanSetting(_plugin.EnableMachineTranslation, "机翻译文 · 补齐其余内容");
            GUILayout.Label("可同时启用。关闭显示不删除已有译文。", TranslationPanelTheme.Muted);
            GUILayout.EndVertical();
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("批量翻译范围", TranslationPanelTheme.Section);
            GUILayout.Label("同时用于 AI 和机翻，人工编辑不受限制。", TranslationPanelTheme.Muted);
            DrawBooleanSetting(_plugin.TranslatePluginNames, "插件名称");
            DrawBooleanSetting(_plugin.TranslateSettingNames, "设置名称");
            DrawBooleanSetting(_plugin.TranslateCategories, "分类");
            DrawBooleanSetting(_plugin.TranslateDescriptions, "说明");
            DrawBooleanSetting(_plugin.TranslateEnums, "枚举值");
            DrawBooleanSetting(_plugin.TranslateCustomControls, "鼠标抓取到的自定义按钮/开关");
            GUILayout.EndVertical();
            DrawNotice("鼠标抓取不受批量范围限制；它始终可以定位并人工编辑单个字段。自定义控件只有被界面绘制过后才能进入批量列表。", false);
        }

        private static void DrawAdvancedToggle(ref bool expanded)
        {
            if (GUILayout.Button(expanded ? "高级设置  −" : "高级设置  +"))
            {
                expanded = !expanded;
                RequestGuiRefresh();
            }
        }

        private static void DrawWebTranslation()
        {
            var selected = DrawSelectionButtons(_websiteIndex, WebProviders.Select(p => p.Name).ToArray(), 3,
                TranslationPanelTheme.Skin.button);
            if (selected != _websiteIndex) { _websiteIndex = selected; RequestGuiRefresh(); }
            if (_editorItems.Count == 0) return;
            _webItemIndex = Mathf.Clamp(_webItemIndex, 0, _editorItems.Count - 1);
            var item = _editorItems[_webItemIndex];
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("上一条")) { _webItemIndex = (_webItemIndex - 1 + _editorItems.Count) % _editorItems.Count; RequestGuiRefresh(); }
            GUILayout.Label((_webItemIndex + 1) + " / " + _editorItems.Count);
            if (GUILayout.Button("下一条")) { _webItemIndex = (_webItemIndex + 1) % _editorItems.Count; RequestGuiRefresh(); }
            GUILayout.EndHorizontal();
            GUILayout.Label(item.Context, TranslationPanelTheme.Muted);
            GUILayout.Label(item.Source, GUI.skin.textArea, GUILayout.MinHeight(55f), GUILayout.MinWidth(0f));
            if (GUILayout.Button(Content("复制原文并打开 " + WebProviders[_websiteIndex].Name,
                "会将上方原文复制到剪贴板并打开官方网站。请在网页粘贴、翻译，再将结果贴回下方。")))
            {
                GUIUtility.systemCopyBuffer = item.Source;
                _webPendingItem = item;
                _webTranslation = string.Empty;
                Application.OpenURL(WebProviders[_websiteIndex].WebsiteUrl);
                RequestGuiRefresh();
            }
            if (_webPendingItem != null)
            {
                DrawNotice("待回填：" + (_webPendingItem.PluginName ?? _webPendingItem.PluginGuid) + "\n" + _webPendingItem.Context + "\n" + _webPendingItem.Source, false);
                _webTranslation = GUILayout.TextArea(_webTranslation, GUILayout.MinHeight(80f), GUILayout.MinWidth(0f));
                GUI.enabled = !_jobRunningForLayout && !string.IsNullOrWhiteSpace(_webTranslation);
                if (GUILayout.Button("应用本条临时译文", TranslationPanelTheme.Primary))
                {
                    RunUiAction(() =>
                    {
                        JsoncLocalization.StoreTemporary(_webPendingItem.PluginGuid, new[] { _webPendingItem },
                            new Dictionary<string, string> { [_webPendingItem.Id] = _webTranslation });
                        RequestManagerRefresh();
                        SetStatus("网页译文已临时应用，退出游戏后清空。");
                    });
                }
                GUI.enabled = true;
            }
            DrawClearTemporary();
        }

        private static void DrawClearTemporary()
        {
            GUI.enabled = !_jobRunningForLayout;
            if (GUILayout.Button("清空当前插件的临时机翻"))
            {
                JsoncLocalization.ClearTemporary(_snapshots[_pluginIndex].Guid);
                RequestManagerRefresh();
                SetStatus("当前插件的临时机翻已清空。");
            }
            GUI.enabled = true;
        }

        private static void DrawChangeReview(LocalizationSnapshot snapshot)
        {
            GUILayout.Label("插件更新与翻译基准", TranslationPanelTheme.Section);
            GUILayout.Label("当前插件版本：" + snapshot.PluginVersion, TranslationPanelTheme.Muted);
            DrawClientFileButtons(snapshot);
            if (!_hasBaseline)
            {
                DrawNotice("旧译文没有记录翻译时的原文，无法推断历史变化。检查现有译文后，以当前插件文本建立起点；之后的变化会自动提示。", false);
                GUI.enabled = !_jobRunningForLayout;
                if (GUILayout.Button("以当前原文建立基准", TranslationPanelTheme.Primary))
                {
                    RunUiAction(() => { JsoncLocalization.EstablishBaseline(snapshot); RequestManagerRefresh(); SetStatus("已建立当前原文基准。"); });
                    RequestGuiRefresh();
                }
                GUI.enabled = true;
                return;
            }
            GUILayout.Label("待核对 " + _changes.Count + " 条 · 已知过期译文暂停显示", TranslationPanelTheme.Muted);
            if (_changes.Count == 0) { DrawNotice("原文与基准一致，无需处理。", false); return; }
            _changeIndex = Mathf.Clamp(_changeIndex, 0, _changes.Count - 1);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("上一条")) { _changeIndex = (_changeIndex - 1 + _changes.Count) % _changes.Count; _reviewLoadedId = null; RequestGuiRefresh(); }
            GUILayout.Label((_changeIndex + 1) + " / " + _changes.Count);
            if (GUILayout.Button("下一条")) { _changeIndex = (_changeIndex + 1) % _changes.Count; _reviewLoadedId = null; RequestGuiRefresh(); }
            GUILayout.EndHorizontal();
            var change = _changes[_changeIndex];
            GUILayout.Label(change.State + " · " + change.Context, TranslationPanelTheme.Section);
            GUILayout.Label("基准版本 " + (change.OldVersion ?? "未记录") + " → 当前 " + snapshot.PluginVersion, TranslationPanelTheme.Muted);
            GUILayout.Label("基准原文", TranslationPanelTheme.Muted);
            GUILayout.Label(change.OldSource ?? "（无）", GUI.skin.textArea, GUILayout.MinWidth(0f));
            GUILayout.Label("当前原文", TranslationPanelTheme.Muted);
            GUILayout.Label(change.NewSource ?? "（当前未发现）", GUI.skin.textArea, GUILayout.MinWidth(0f));
            if (change.Current == null)
            {
                DrawNotice(change.State == "未观测" ? "自定义控件可能尚未绘制，请先展开对应插件，不能据此认定已移除。"
                    : "字段可能被删除或重命名。旧译文保留在 JSONC 与历史记录中，不会自动迁移到新键。", false);
                GUI.enabled = !_jobRunningForLayout;
                if (GUILayout.Button("确认移除并归档此基准"))
                {
                    RunUiAction(() => { JsoncLocalization.AcknowledgeRemoved(snapshot.Guid, change.Id); _reviewLoadedId = null; RequestManagerRefresh(); });
                    RequestGuiRefresh();
                }
                GUI.enabled = true;
                return;
            }
            var layer = DrawSelectionButtons(_reviewLayer == TranslationLayer.Manual ? 0 : 1,
                new[] { "人工译文", "API / AI 译文" }, 2, TranslationPanelTheme.Skin.button);
            var nextLayer = layer == 0 ? TranslationLayer.Manual : TranslationLayer.AI;
            if (nextLayer != _reviewLayer) { _reviewLayer = nextLayer; _reviewLoadedId = null; RequestGuiRefresh(); }
            var identity = snapshot.Guid + "|" + change.Id + "|" + _reviewLayer;
            if (_reviewLoadedId != identity)
            {
                _reviewValue = JsoncLocalization.GetStoredValue(change.Current, _reviewLayer);
                _reviewLoadedId = identity;
            }
            GUILayout.Label("检查或修改译文，然后确认适用于当前原文。", TranslationPanelTheme.Muted);
            _reviewValue = GUILayout.TextArea(_reviewValue, GUILayout.MinHeight(90f), GUILayout.MinWidth(0f));
            GUI.enabled = !_jobRunningForLayout && !string.IsNullOrWhiteSpace(_reviewValue);
            if (GUILayout.Button("保存并确认此译文", TranslationPanelTheme.Primary))
            {
                RunUiAction(() =>
                {
                    JsoncLocalization.SaveLayer(snapshot.Guid, _reviewLayer, new[] { change.Current },
                        new Dictionary<string, string> { [change.Id] = _reviewValue });
                    _reviewLoadedId = null;
                    RequestManagerRefresh();
                    SetStatus("已保存此层译文，并确认当前原文；其他层仍独立检查。");
                });
                RequestGuiRefresh();
            }
            GUI.enabled = true;
            if (GUILayout.Button("转到 API 翻译新增 / 变化项")) _requestedTab = 1;
        }

        private static void DrawClientFileButtons(LocalizationSnapshot snapshot)
        {
            GUILayout.BeginHorizontal();
            var legacyPath = JsoncLocalization.GetLegacyTranslationPath(snapshot.Guid);
            if (File.Exists(legacyPath) && GUILayout.Button(Content("打开旧版", "在资源管理器中定位：" + legacyPath)))
                RunUiAction(() => FileLocationOpener.Open(legacyPath));
            foreach (var fileLayer in new[] { TranslationLayer.Manual, TranslationLayer.AI, TranslationLayer.Machine })
            {
                var path = JsoncLocalization.GetTranslationPath(snapshot.Guid, fileLayer);
                if (!File.Exists(path)) continue;
                var label = fileLayer == TranslationLayer.Manual ? "打开人工" : fileLayer == TranslationLayer.AI ? "打开 API" : "打开 MT";
                if (GUILayout.Button(Content(label, "在资源管理器中定位：" + path)))
                    RunUiAction(() => FileLocationOpener.Open(path));
            }
            var history = JsoncLocalization.GetHistoryFolder(snapshot.Guid);
            if (Directory.Exists(history) && GUILayout.Button(Content("打开历史", "打开自动备份目录：" + history)))
                RunUiAction(() => FileLocationOpener.Open(history));
            GUILayout.EndHorizontal();
        }

        private static void RunUiAction(Action action)
        {
            try { action(); }
            catch (Exception ex) { SetStatus("操作失败：" + ex.Message); }
        }

        private static void DrawTooltip()
        {
            var tooltip = GUI.tooltip;
            if (string.IsNullOrWhiteSpace(tooltip) || Event.current.type != EventType.Repaint) return;
            var width = Mathf.Min(340f, _drawerRect.width - 28f);
            var content = Content(tooltip);
            var height = TranslationPanelTheme.Notice.CalcHeight(content, width);
            var pointer = Event.current.mousePosition;
            var x = Mathf.Clamp(pointer.x, 10f, _drawerRect.width - width - 10f);
            var y = pointer.y + 24f + height < _drawerRect.height ? pointer.y + 24f : Mathf.Max(8f, pointer.y - height - 8f);
            GUI.Label(new Rect(x, y, width, height), content, TranslationPanelTheme.Notice);
            GUI.tooltip = string.Empty;
        }

        private static string CurrentAiProfile()
        {
            return TranslationSafety.Profile(_plugin.AiProvider.Value, _plugin.AiEndpoint.Value, _plugin.AiModel.Value,
                _plugin.TargetLanguage.Value, _plugin.AiExtraPrompt.Value) + CurrentAiOptions().ProfileTag;
        }

        private static AiExecutionOptions CurrentAiOptions() => new AiExecutionOptions
        {
            Adapter = _plugin.AiAdapter.Value, Reasoning = _plugin.AiReasoning.Value,
            MaxOutputTokens = _plugin.AiMaxOutputTokens.Value, JsonOutput = _plugin.AiJsonOutput.Value,
            MaxReportedTokens = _plugin.AiMaxReportedTokens.Value, MaxRequests = _plugin.AiMaxRequests.Value,
            RequestDelayMs = _plugin.AiRequestDelayMs.Value
        };

        private static void DrawSliderSetting(string label, BepInEx.Configuration.ConfigEntry<int> entry, int min, int max, string unit)
        {
            GUILayout.Label(Content(label + "  " + entry.Value + unit, entry.Description.Description), TranslationPanelTheme.Muted);
            var value = Mathf.RoundToInt(GUILayout.HorizontalSlider(entry.Value, min, max));
            if (value != entry.Value) entry.Value = value;
        }

        private static void DrawPrimaryAction()
        {
            if (_selectedTab >= 3 || _snapshots.Count == 0 || (_selectedTab == 2 && _machineWeb)) return;
            GUILayout.Space(6f);
            if (_selectedTab == 0)
            {
                var items = _editorItems;
                if (items.Count == 0) return;
                var item = items[Mathf.Clamp(_editorIndex, 0, items.Count - 1)];
                GUILayout.BeginHorizontal();
                GUI.enabled = !_jobRunningForLayout && !string.IsNullOrWhiteSpace(_manualValue);
                if (GUILayout.Button(_plugin.ManualAutoAdvance.Value ? "保存并下一项" : "保存译文", TranslationPanelTheme.Primary))
                {
                    SaveManual(item, items);
                    RequestGuiRefresh();
                }
                GUI.enabled = !_jobRunningForLayout;
                if (GUILayout.Button(Content("清除覆盖", "仅清除工作台人工译文，保留旧 JSONC 译文"), GUILayout.Width(86f)))
                {
                    _manualValue = string.Empty;
                    SaveManual(item, items);
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
                return;
            }
            var count = _batchPlan.TotalItems;
            GUILayout.BeginHorizontal();
            GUILayout.Label("已选 " + _batchPlan.SelectedPlugins + " 个插件 · 待翻 " + count + " 条", TranslationPanelTheme.Muted);
            if (GUILayout.Button("修改范围", GUILayout.Width(82f))) _requestedTab = 3;
            GUILayout.EndHorizontal();
            var ai = _selectedTab == 1;
            var provider = TranslationProviderCatalog.GetAiProvider(_plugin.AiProvider.Value);
            var machine = TranslationProviderCatalog.GetMachineProvider(_plugin.MachineProvider.Value);
            var missingKey = ai ? provider.ApiKeyRequired && string.IsNullOrWhiteSpace(_aiApiKey)
                : machine.ApiKeyRequired && string.IsNullOrWhiteSpace(_machineApiKey);
            var endpoint = ai ? _plugin.AiEndpoint.Value : _plugin.MachineEndpoint.Value;
            Uri uri;
            var validEndpoint = Uri.TryCreate(endpoint, UriKind.Absolute, out uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
            var hint = _batchPlan.SelectedPlugins == 0 ? "请展开插件列表，勾选需要翻译的插件。" :
                _batchPlan.Errors.Count > 0 ? "选中插件的 JSONC 异常，请修复或取消勾选。" :
                count == 0 ? "没有待翻文本：检查翻译范围或已有译文保护。" : missingKey ? "填写 API 密钥后即可开始。" :
                !ai && machine.AppIdRequired && string.IsNullOrWhiteSpace(_machineAppId) ? "请填写平台应用 ID。" :
                !validEndpoint ? "请在高级设置中填写完整 HTTP / HTTPS 接口地址。" :
                ai && string.IsNullOrWhiteSpace(_plugin.AiModel.Value) ? "请填写模型 ID。" : null;
            GUILayout.Label(ai && _plugin.AiStopped.Value ? "API 翻译已暂停。" : hint ?? "准备就绪", TranslationPanelTheme.Muted, GUILayout.Height(34f));
            var enabled = ai ? _plugin.EnableAiTranslation.Value : _plugin.EnableMachineTranslation.Value;
            GUILayout.Label(enabled ? ai ? "AI 译文将保存到 zh-cn/API。" : "机翻译文将保存到 zh-cn/translations/MT。"
                : "此译文层已关闭显示；完成后可勾选显示。", TranslationPanelTheme.Muted);
            GUILayout.BeginHorizontal();
            if (ai) DrawPaidStopButton();
            GUI.enabled = !_jobRunningForLayout && hint == null && (!ai || !_plugin.AiStopped.Value);
            if (GUILayout.Button(Content(_jobRunningForLayout ? "翻译进行中…" : ai ? "开始 API 精翻" : "开始机器翻译",
                "只处理当前所选插件、字段范围以及缺少有效中文的条目；完成内容会立即写入对应 JSONC。"), TranslationPanelTheme.Primary, GUILayout.ExpandWidth(true)))
            {
                StartBatchJob(ai);
                RequestGuiRefresh();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private static void DrawBooleanSetting(BepInEx.Configuration.ConfigEntry<bool> entry, string label)
        {
            var value = TranslationPanelTheme.Toggle(entry.Value, Content(label, entry.Description.Description));
            if (value != entry.Value)
                entry.Value = value;
        }

        private static void DrawTextSetting(string label, BepInEx.Configuration.ConfigEntry<string> entry)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Content(label, entry.Description.Description), GUILayout.Width(78f));
            var value = GUILayout.TextField(entry.Value ?? string.Empty, GUILayout.MinWidth(0f));
            if (value != entry.Value) entry.Value = value;
            GUILayout.EndHorizontal();
        }

        private static bool DrawSecretSetting(string label, ref string value, ref bool show, bool persisted = false)
        {
            var before = value;
            GUILayout.Label(label, TranslationPanelTheme.Muted);
            GUILayout.BeginHorizontal();
            value = show ? GUILayout.TextField(value ?? string.Empty, GUILayout.MinWidth(0f))
                : GUILayout.PasswordField(value ?? string.Empty, '●', GUILayout.MinWidth(0f));
            if (GUILayout.Button(show ? "隐藏" : "显示", GUILayout.Width(48f)))
                show = !show;
            if (GUILayout.Button("清空", GUILayout.Width(48f)))
                value = string.Empty;
            GUILayout.EndHorizontal();
            GUILayout.Label(persisted ? "保存在本机 BepInEx 配置中；只应由服务器管理员填写。" :
                (_plugin != null && _plugin.RememberApiCredentials.Value ? "已选择加密记忆；凭据会绑定当前 Windows 用户。" : "仅本次游戏有效，不保存密钥。"), TranslationPanelTheme.Muted);
            return !string.Equals(before, value, StringComparison.Ordinal);
        }

        private static void DrawCredentialPersistenceToggle()
        {
            if (_plugin == null) return;
            var tooltip = _plugin.RememberApiCredentials.Description.Description;
            var enabled = TranslationPanelTheme.Toggle(_plugin.RememberApiCredentials.Value,
                Content("记住 API 凭据", tooltip));
            if (enabled != _plugin.RememberApiCredentials.Value)
            {
                _plugin.RememberApiCredentials.Value = enabled;
                RequestGuiRefresh();
            }
        }

        private static void DrawMachineProviderSelector(MachineProviderDefinition current)
        {
            var providers = TranslationProviderCatalog.MachineProviders;
            var drawExpanded = _chooseMachineProvider;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Content(current.Name + " ▾", "一键选择平台，自动填写接口与默认语言"), TranslationPanelTheme.PluginButton, GUILayout.MinHeight(32f)))
            { _chooseMachineProvider = !_chooseMachineProvider; RequestGuiRefresh(); }
            if (GUILayout.Button(Content("官方文档", "查看应用开通、密钥、语言代码与额度说明"), GUILayout.Width(78f))) Application.OpenURL(current.DocumentationUrl);
            GUILayout.EndHorizontal();
            if (drawExpanded)
            {
                var selected = DrawSelectionButtons(-1, providers.Select(p => p.Name).ToArray(), 2,
                    TranslationPanelTheme.PluginButton);
                if (selected >= 0) SelectMachineProvider(providers[selected]);
            }
        }

        private static void DrawAiProviderSelector(TranslationProviderDefinition current)
        {
            var providers = TranslationProviderCatalog.AiProviders;
            var drawExpanded = _chooseAiProvider;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Content(current.Name + " ▾", "选择 API 服务商，自动填入接口与模型预设；模型可自行修改"), TranslationPanelTheme.PluginButton, GUILayout.MinHeight(32f)))
            { _chooseAiProvider = !_chooseAiProvider; RequestGuiRefresh(); }
            if (GUILayout.Button(Content("官方文档", current.Name == "自定义兼容接口" ? "查看兼容协议；具体模型和地址请查询你所用服务的文档" : "查看此服务商的官方接入说明"), GUILayout.Width(78f)))
                Application.OpenURL(current.DocumentationUrl);
            GUILayout.EndHorizontal();
            if (drawExpanded)
            {
                var selected = DrawSelectionButtons(-1, providers.Select(p => p.Name).ToArray(), 2,
                    TranslationPanelTheme.PluginButton);
                if (selected >= 0) SelectAiProvider(providers[selected]);
            }
        }

        private static string DrawModelSelection(AiModelSelection selection, string context, bool ready,
            Func<bool, Task<string[]>> fetch, string model, string[] presets)
        {
            DrawBooleanSetting(_plugin.AutoFetchAiModels, "自动获取模型列表");
            selection.Ensure(context, ready, _plugin.AutoFetchAiModels.Value, fetch);
            GUILayout.BeginHorizontal();
            var expanded = selection.Expanded;
            selection.Expanded = GUILayout.Toggle(selection.Expanded, "选择模型 ▾", GUI.skin.button);
            GUI.enabled = ready && !selection.Busy;
            if (GUILayout.Button("刷新模型", GUILayout.Width(90f))) selection.Fetch(fetch, true);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label(selection.Status, TranslationPanelTheme.Muted);
            if (presets.Length > 0) model = GUILayout.TextField(model ?? "", GUILayout.MinWidth(0f));
            if (!expanded) return model;
            selection.Filter = GUILayout.TextField(selection.Filter, GUILayout.MinWidth(0f));
            // Async metadata must not change the control count between Layout and Repaint.
            if (Event.current.type == EventType.Layout)
            {
                var all = presets.Concat(selection.Models).Distinct().Where(id => id.IndexOf(selection.Filter, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                selection.VisibleTotal = all.Length;
                selection.VisibleModels = all.Take(20).ToArray();
            }
            var visible = selection.VisibleModels;
            if (selection.VisibleTotal > 20) GUILayout.Label("显示前 20 项，共 " + selection.VisibleTotal + " 项；输入关键词筛选。", TranslationPanelTheme.Muted);
            if (visible.Length > 0)
            {
                var selected = DrawSelectionButtons(Array.IndexOf(visible, model), visible, 2, GUI.skin.button);
                if (selected >= 0) model = visible[selected];
            }
            return model;
        }

        private static void SelectMachineProvider(MachineProviderDefinition provider)
        {
            MachineKeys[_plugin.MachineProvider.Value] = _machineApiKey;
            MachineAppIds[_plugin.MachineProvider.Value] = _machineAppId;
            SaveCredentialMapsIfEnabled();
            _plugin.MachineProvider.Value = provider.Name;
            _plugin.MachineEndpoint.Value = provider.Endpoint;
            _plugin.MachineSourceLanguage.Value = "en";
            _plugin.MachineTargetLanguage.Value = provider.TargetLanguage;
            string key;
            _machineApiKey = MachineKeys.TryGetValue(provider.Name, out key) ? key : string.Empty;
            _machineAppId = MachineAppIds.TryGetValue(provider.Name, out key) ? key : string.Empty;
            _showMachineKey = _chooseMachineProvider = false;
            RequestGuiRefresh();
        }

        private static void SelectAiProvider(TranslationProviderDefinition provider)
        {
            AiKeys[_plugin.AiProvider.Value] = _aiApiKey;
            SaveCredentialMapsIfEnabled();
            _plugin.AiProvider.Value = provider.Name;
            _plugin.AiEndpoint.Value = provider.Endpoint;
            _plugin.AiModel.Value = provider.DefaultModel;
            string key;
            _aiApiKey = AiKeys.TryGetValue(provider.Name, out key) ? key : string.Empty;
            _showAiKey = _chooseAiProvider = false;
            RequestGuiRefresh();
        }

        internal static void SetRememberApiCredentials(bool enabled)
        {
            if (enabled) PersistCurrentCredentials();
            else if (!TranslationCredentialStore.TryClear()) SetStatus(TranslationCredentialStore.LastError);
            else SetStatus("已停止记住 API 凭据，并删除本机加密凭据文件；当前输入保留到本次游戏结束。");
        }

        private static void PersistCurrentCredentials()
        {
            if (_plugin == null) return;
            AiKeys[_plugin.AiProvider.Value] = _aiApiKey ?? string.Empty;
            MachineKeys[_plugin.MachineProvider.Value] = _machineApiKey ?? string.Empty;
            MachineAppIds[_plugin.MachineProvider.Value] = _machineAppId ?? string.Empty;
            SaveCredentialMapsIfEnabled();
        }

        private static void SaveCredentialMapsIfEnabled()
        {
            if (_plugin == null || !_plugin.RememberApiCredentials.Value) return;
            if (!TranslationCredentialStore.TrySave(AiKeys, MachineKeys, MachineAppIds))
                SetStatus(TranslationCredentialStore.LastError);
        }

        private static void CopyCredentials(IDictionary<string, string> source, IDictionary<string, string> target)
        {
            foreach (var pair in source) target[pair.Key] = pair.Value;
        }

        private static void StartBatchJob(bool ai, bool titleOnly = false)
        {
            if (IsJobRunning()) return;
            if (ai && !CanStartApi()) return;
            var plan = !titleOnly ? CreateBatchPlan(ai) : TranslationBatchPlan.Create(_snapshots,
                BatchSelection, new TranslationScope { PluginNames = true },
                true, CurrentAiProfile(), false, true);
            if (plan.Errors.Count > 0)
            {
                var failure = string.Join("\n", plan.Errors.ToArray());
                _activeDiagnostics = new TranslationDiagnostics(ai ? "F12 AI" : "F12 机翻",
                    plan.Targets.SelectMany(target => target.Items), new[] { _aiApiKey, _machineApiKey, _machineAppId });
                _activeDiagnostics.Finish("preflight_failed", failure);
                SetStatus(failure);
                return;
            }
            if (plan.TotalItems == 0) { SetStatus("没有待翻文本，请检查勾选插件、翻译范围和已有译文保护。"); return; }
            // Freeze settings and credentials before the first await; editing the UI only affects the next job.
            var profile = CurrentAiProfile();
            var endpoint = ai ? _plugin.AiEndpoint.Value : _plugin.MachineEndpoint.Value;
            var key = ai ? _aiApiKey : _machineApiKey;
            var timeout = _plugin.RequestTimeoutSeconds.Value;
            var diagnostics = new TranslationDiagnostics(ai ? "F12 AI" : "F12 机翻",
                plan.Targets.SelectMany(target => target.Items), new[] { key, _machineAppId, _serverAdminKey });
            diagnostics.Write("configuration", new JObject
            {
                ["provider"] = ai ? _plugin.AiProvider.Value : _plugin.MachineProvider.Value,
                ["model"] = ai ? _plugin.AiModel.Value : string.Empty,
                ["batchSize"] = ai ? _plugin.AiBatchSize.Value : _plugin.MachineBatchSize.Value,
                ["maxInputCharacters"] = ai ? _plugin.AiMaxInputCharacters.Value : 0,
                ["retryCount"] = ai ? _plugin.AiRetryCount.Value : 0,
                ["timeoutSeconds"] = timeout
            });
            Func<TranslationBatchTarget, Action<int, int>, CancellationToken, Task<TranslationResult>> translate;
            Func<IList<TranslationWorkItem>, CancellationToken, Task<TranslationResult>> translateTitles = null;
            if (ai)
            {
                var provider = TranslationProviderCatalog.GetAiProvider(_plugin.AiProvider.Value);
                var model = _plugin.AiModel.Value;
                var language = _plugin.TargetLanguage.Value;
                var prompt = _plugin.AiExtraPrompt.Value;
                diagnostics.Write("input", new JObject { ["prompt"] = TranslationSafety.BuildPrompt(language, prompt) });
                var size = _plugin.AiBatchSize.Value;
                var characterLimit = _plugin.AiMaxInputCharacters.Value;
                var retries = _plugin.AiRetryCount.Value;
                var executionOptions = CurrentAiOptions();
                if (_forceAi) TranslationClients.ClearAiRequestCache();
                Action<TranslationActivity> activity = update => { diagnostics.Observe(update); ShowTranslationActivity(update); };
                translateTitles = (titles, token) => TranslationClients.TranslatePluginTitlesAsync(titles, provider,
                    endpoint, model, key, language, prompt, size, characterLimit, retries, timeout, token, activity, executionOptions);
                translate = (target, progress, token) => TranslationClients.TranslateAiAsync(target.Items, provider,
                    endpoint, model, key, language, prompt, size, characterLimit, retries, timeout, progress, token,
                    activity, executionOptions);
            }
            else
            {
                var provider = TranslationProviderCatalog.GetMachineProvider(_plugin.MachineProvider.Value);
                var appId = _machineAppId;
                var region = _plugin.MachineRegion.Value;
                var source = _plugin.MachineSourceLanguage.Value;
                var language = _plugin.MachineTargetLanguage.Value;
                var size = _plugin.MachineBatchSize.Value;
                translate = (target, progress, token) =>
                {
                    var activity = new TranslationActivity { Items = target.Items, Phase = "等待机翻接口返回" };
                    diagnostics.Observe(activity);
                    ShowTranslationActivity(activity);
                    return TranslationClients.TranslateMachineAsync(target.Items, provider,
                        endpoint, key, appId, region, source, language, size, timeout, progress, token);
                };
            }
            BeginJob("开始批量翻译 · " + plan.Targets.Count + " 个插件", plan.TotalItems, paid: ai, localDeadline: ai);
            _activeDiagnostics = diagnostics;
            _job = RunBatchAsync(plan, ai ? TranslationLayer.AI : TranslationLayer.Machine, profile, translate,
                _cancellation.Token, ai ? Math.Max(1, Math.Min(60, _plugin.AiBatchSize.Value)) : Math.Max(1, Math.Min(50, _plugin.MachineBatchSize.Value)), diagnostics,
                ai ? TranslationClients.AiInputBudget(_plugin.AiMaxInputCharacters.Value, _plugin.TargetLanguage.Value, _plugin.AiExtraPrompt.Value) : 0,
                translateTitles);
        }

        private static TranslationScope GetTranslationScope()
        {
            return new TranslationScope
            {
                PluginNames = _plugin.TranslatePluginNames.Value,
                Categories = _plugin.TranslateCategories.Value,
                SettingNames = _plugin.TranslateSettingNames.Value,
                Descriptions = _plugin.TranslateDescriptions.Value,
                Enums = _plugin.TranslateEnums.Value,
                CustomControls = _plugin.TranslateCustomControls.Value
            };
        }

        private static async Task RunBatchAsync(TranslationBatchPlan plan, TranslationLayer layer, string profile,
            Func<TranslationBatchTarget, Action<int, int>, CancellationToken, Task<TranslationResult>> translate,
            CancellationToken token, int checkpointSize, TranslationDiagnostics diagnostics, int checkpointCharacters,
            Func<IList<TranslationWorkItem>, CancellationToken, Task<TranslationResult>> translateTitles = null)
        {
            var lifecycle = _lifecycleGeneration;
            try
            {
                var result = await TranslationBatchRunner.RunAsync(plan, translate, (target, translated) =>
                {
                    lock (JobSync)
                    {
                        token.ThrowIfCancellationRequested();
                        if (_shutdown || lifecycle != _lifecycleGeneration) throw new OperationCanceledException(token);
                        JsoncLocalization.SaveLayer(target.Guid, layer, target.Items, translated.Values, profile);
                        _refreshRequested = true;
                    }
                }, (name, done, total) =>
                {
                    if (lifecycle != _lifecycleGeneration) return;
                    SetStatus("批量翻译 · " + name);
                    UpdateProgress(done, total);
                }, token, checkpointSize, diagnostics, checkpointCharacters, translateTitles).ConfigureAwait(false);
                if (lifecycle != _lifecycleGeneration) return;
                SetStatus((result.Cancelled ? "已取消" : result.Failures.Count > 0 ? "部分失败" : "完成") +
                    "：已完成 " + result.CompletedPlugins + " / " + plan.Targets.Count + " 个插件，" + result.SavedItems + " 条。" +
                    (layer == TranslationLayer.AI ? "已完成的译文已保存。" : "机翻译文已保存到 zh-cn/translations/MT。") +
                    (result.Failures.Count > 0 ? "\n" + string.Join("\n", result.Failures.ToArray()) : string.Empty));
                if (result.Failures.Count > 0 && _log != null) _log.LogWarning("Batch translation: " + string.Join("; ", result.Failures.ToArray()));
                _refreshRequested = true;
            }
            catch (Exception ex)
            {
                if (lifecycle != _lifecycleGeneration) return;
                diagnostics.Finish("failed", ex.Message);
                SetStatus("失败：" + ex.Message);
                if (_log != null)
                    _log.LogError("Translation failed: " + ex);
            }
        }

        private static void BeginJob(string status, int total, bool paid = false, bool localDeadline = false)
        {
            if (_cancellation != null) _cancellation.Dispose();
            _cancellation = new CancellationTokenSource();
            _paidJob = paid;
            _serverStopRequested = false;
            if (localDeadline) _cancellation.CancelAfter(TimeSpan.FromMinutes(10));
            lock (JobSync)
            {
                _status = status;
                _progressCompleted = 0;
                _progressTotal = total;
                _progressDetail = string.Empty;
                _progressItems = string.Empty;
                _showProgressItems = false;
                _progressItemsScroll = Vector2.zero;
                _progressStarted = DateTime.UtcNow;
                _activeDiagnostics = null;
            }
        }

        private static void ShowTranslationActivity(TranslationActivity activity)
        {
            var items = activity.Items ?? new List<TranslationWorkItem>();
            var labels = items.Select(item => (item.PluginName ?? item.PluginGuid) + " · " +
                (item.EntryKey ?? item.EnumMember ?? item.Kind) + " · " + (item.Field ?? item.Kind));
            lock (JobSync)
            {
                _progressItems = string.Join("\n", labels.ToArray());
                _progressDetail = activity.Phase + "（本批 " + items.Count + " 条）\n" +
                    string.Join("\n", labels.Take(3).ToArray());
            }
        }

        private static void UpdateProgress(int completed, int total)
        {
            lock (JobSync)
            {
                _progressCompleted = completed;
                _progressTotal = total;
            }
        }

        private static void DrawJobStatus()
        {
            string status;
            int completed;
            int total;
            lock (JobSync)
            {
                status = _status;
                completed = _progressCompleted;
                total = _progressTotal;
            }
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            // Never pass the full error as a tooltip: the tooltip itself can cover the drawer.
            GUILayout.Label(JobStatusSummary(status), TranslationPanelTheme.Muted, GUILayout.Height(34f));
            if (_jobRunningForLayout && GUILayout.Button("取消", GUILayout.Width(52f)))
            {
                if (_serverJobActive)
                {
                    _ = RequestServerCancelAsync();
                    SetStatus("正在请求服务端取消任务…");
                }
                else _cancellation.Cancel();
            }
            GUILayout.EndHorizontal();
            var fraction = total > 0 ? Mathf.Clamp01((float)completed / total) : 0f;
            if (_jobRunningForLayout)
                GUILayout.Label(completed + " / " + total + " 条 · " + (fraction * 100f).ToString("F0") + "%", TranslationPanelTheme.Muted, GUILayout.Height(18f));
            _showStatusDetails = TranslationPanelTheme.Toggle(_showStatusDetails, "展开任务详情 / 错误日志");
            if (!_statusDetailsForLayout) { GUILayout.EndVertical(); return; }
            _statusScroll = GUILayout.BeginScrollView(_statusScroll, GUILayout.Height(Mathf.Min(120f, _drawerRect.height * 0.2f)));
            GUILayout.Label(JobStatusDetails(status), TranslationPanelTheme.Muted);
            if (_jobRunningForLayout)
            {
                var rect = GUILayoutUtility.GetRect(10f, 7f, GUILayout.ExpandWidth(true));
                GUI.Box(rect, string.Empty);
                var fill = new Rect(rect.x + 1f, rect.y + 1f, Mathf.Max(0f, (rect.width - 2f) * fraction), rect.height - 2f);
                var oldColor = GUI.color;
                GUI.color = new Color(0.35f, 0.72f, 0.95f, 1f);
                GUI.DrawTexture(fill, Texture2D.whiteTexture);
                GUI.color = oldColor;
                GUILayout.Label(completed + " / " + total + " 条", TranslationPanelTheme.Muted);
                string detail;
                lock (JobSync) detail = _progressDetail;
                GUILayout.Label("已处理 " + (fraction * 100f).ToString("F0") + "% · 用时 " +
                    Math.Max(0, (int)(DateTime.UtcNow - _progressStarted).TotalSeconds) + " 秒", TranslationPanelTheme.Muted);
                if (!string.IsNullOrEmpty(detail)) GUILayout.Label(detail, TranslationPanelTheme.Muted);
                var drawProgressItems = _showProgressItems;
                _showProgressItems = TranslationPanelTheme.Toggle(_showProgressItems, "展开本批全部词条 / 设置");
                if (drawProgressItems)
                {
                    string allItems;
                    lock (JobSync) allItems = _progressItems;
                    _progressItemsScroll = GUILayout.BeginScrollView(_progressItemsScroll, GUILayout.Height(120f));
                    GUILayout.Label(allItems, TranslationPanelTheme.Muted);
                    GUILayout.EndScrollView();
                }
            }
            var diagnostics = _activeDiagnostics;
            if (diagnostics != null)
            {
                GUILayout.Label("AI 请求 " + diagnostics.Requests + " 次 · " + (diagnostics.UsageResponses == 0
                    ? "供应商尚未返回 token 用量" : "已报告输入/输出 token：" + diagnostics.InputTokens + " / " + diagnostics.OutputTokens),
                    TranslationPanelTheme.Muted);
                GUILayout.Label(diagnostics.WriteError ?? "诊断日志：游戏根目录/API翻译日志/ConfigurationManager.zh-cn", TranslationPanelTheme.Muted);
                if (GUILayout.Button("定位本次翻译总览")) RunUiAction(() => FileLocationOpener.Open(diagnostics.ReportPath));
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        internal static string JobStatusSummary(string status)
        {
            var text = status ?? string.Empty;
            var end = text.IndexOfAny(new[] { '\r', '\n' });
            if (end >= 0) text = text.Substring(0, Math.Min(end, 100));
            return text.Length <= 100 ? text : text.Substring(0, 100) + "…";
        }

        internal static string JobStatusDetails(string status)
        {
            var text = status ?? string.Empty;
            return text.Length <= 8000 ? text : text.Substring(0, 8000) + "\n…界面详情已截断，完整记录请查看 API翻译日志。";
        }

        private static void ResetApiTab()
        {
            _pendingApiTab = null;
            _apiTabAccepted = false;
        }

        private static bool CanStartApi()
        {
            if (!_apiTabAccepted) { SetStatus("请先阅读并同意 API 使用声明。"); return false; }
            if (_plugin.AiStopped.Value) { SetStatus("API 翻译已暂停，请先点击恢复 API。"); return false; }
            return true;
        }

        private static void DrawPaidNotice(int id)
        {
            GUILayout.Label("API 自费与免责声明", TranslationPanelTheme.Title);
            GUILayout.Label("API 使用你提供的第三方账户与密钥，费用由你承担，以服务商账单为准。预计 Token 用量仅供参考。已送达的请求即使取消、超时或未获得可用译文，也可能计费。请仅提交你有权发送的内容。依法应由你或服务商承担的费用、损失及法律责任，作者不承担。依法不得免除的责任仍由责任方承担。", TranslationPanelTheme.Notice);
            var seconds = Mathf.Max(0, Mathf.CeilToInt(_paidNoticeUntil - Time.realtimeSinceStartup));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("取消", TranslationPanelTheme.SecondaryAction)) { ResetApiTab(); RequestGuiRefresh(); }
            GUI.enabled = seconds == 0 && _pendingApiTab.HasValue;
            if (GUILayout.Button(seconds > 0 ? "同意（" + seconds + " 秒）" : "同意并进入", TranslationPanelTheme.Primary))
            {
                _requestedTab = _pendingApiTab.Value;
                _pendingApiTab = null;
                _apiTabAccepted = true;
                RequestGuiRefresh();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (GUILayout.Button("紧急停止 API", TranslationPanelTheme.DangerAction)) TogglePaidStop();
        }

        private static void DrawPaidStopButton()
        {
            var canResume = _plugin.AiStopped.Value && !(_paidJob && IsJobRunning());
            var style = canResume ? TranslationPanelTheme.SecondaryAction : TranslationPanelTheme.DangerAction;
            if (GUILayout.Button(canResume ? "恢复 API" : "紧急停止 API", style, GUILayout.ExpandWidth(false))) TogglePaidStop();
        }

        private static void TogglePaidStop()
        {
            if (_plugin.AiStopped.Value && !(_paidJob && IsJobRunning()) && !_pendingApiTab.HasValue)
            {
                _plugin.AiStopped.Value = false;
                SetStatus("已恢复 API 翻译，可以发起新任务。");
                return;
            }
            _plugin.AiStopped.Value = true;
            if (_pendingApiTab.HasValue) ResetApiTab();
            if (_paidJob && IsJobRunning())
            {
                if (_serverJobActive) _ = RequestServerCancelAsync();
                else _cancellation?.Cancel();
            }
            else if (!IsJobRunning() && ServerWorkbenchClient.RecoverableJobId() is string recoverId && ServerWorkbenchClient.CanUseWorkbench)
            {
                BeginJob("正在停止上次服务端任务", 0, paid: true);
                _serverJobActive = true;
                _serverStopRequested = true;
                _job = RunServerJobAsync(null, "ai", false, recoverId);
            }
            SetStatus("已暂停新的 API 翻译请求。已经送达供应商的请求可能计费。");
            RequestGuiRefresh();
        }

        private static async Task RequestServerCancelAsync()
        {
            _serverStopRequested = true;
            if (!ServerWorkbenchClient.HasJobId || Interlocked.CompareExchange(ref _serverCancelSending, 1, 0) != 0) return;
            var lifecycle = _lifecycleGeneration;
            try { await ServerWorkbenchClient.CancelJobAsync().ConfigureAwait(false); }
            catch (Exception ex) { if (lifecycle == _lifecycleGeneration) SetStatus("取消未确认，继续查询服务端状态，可再次按停止：" + ex.Message); }
            finally { Interlocked.Exchange(ref _serverCancelSending, 0); }
        }

        private static void SaveManual(TranslationWorkItem item, IList<TranslationWorkItem> items)
        {
            try
            {
                var formatted = TranslationSafety.PreserveOuterFormatting(item.Source, _manualValue);
                JsoncLocalization.SaveLayer(item.PluginGuid, TranslationLayer.Manual, new[] { item },
                    new Dictionary<string, string> { [item.Id] = formatted });
                _manualValue = formatted;
                SetStatus(string.IsNullOrWhiteSpace(_manualValue)
                    ? "已清除工作台人工覆盖；旧版 JSONC 译文仍会保留。"
                    : "已保存当前人工精翻译文。");
                _manualOriginalValue = _manualValue;
                RequestManagerRefresh();
                if (_plugin.ManualAutoAdvance.Value && !string.IsNullOrWhiteSpace(_manualValue))
                    MoveEditor(items, 1);
            }
            catch (Exception ex)
            {
                SetStatus("保存失败：" + ex.Message);
                if (_log != null)
                    _log.LogError("Failed to save manual translation: " + ex);
            }
        }

        private static void MoveEditor(IList<TranslationWorkItem> items, int offset)
        {
            if (items.Count == 0) return;
            var target = items[(_editorIndex + offset + items.Count) % items.Count];
            QueuePluginPicker(() => GuardEditorNavigation(() => ApplyEditorItem(target),
                () => _editorItems.Any(item => item.Id == target.Id && item.PluginGuid == target.PluginGuid)));
        }

        private static void SelectEditorItem(IList<TranslationWorkItem> items, TranslationWorkItem target)
        {
            QueuePluginPicker(() => GuardEditorNavigation(() => ApplyEditorItem(target),
                () => _editorItems.Any(item => item.Id == target.Id && item.PluginGuid == target.PluginGuid)));
        }

        private static void ResetEditor()
        {
            _editorIndex = 0;
            _manualLoaded = false;
            _manualPreviewScroll = Vector2.zero;
        }

        internal static string PrepareRichTextPreview(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            // TextMeshPro commonly writes <size=16px>; IMGUI expects <size=16>.
            return System.Text.RegularExpressions.Regex.Replace(value,
                @"(?i)<size\s*=\s*([0-9]+(?:\.[0-9]+)?)px\s*>", "<size=$1>");
        }

        private static string FormatCooldown(int seconds)
        {
            if (seconds <= 0) return "无限制";
            if (seconds % 60 == 0) return seconds / 60 + " 分钟";
            return seconds + " 秒";
        }

        private static bool IsJobRunning()
        {
            return _job != null && !_job.IsCompleted;
        }

        private static void SetStatus(string value)
        {
            lock (JobSync)
                _status = value;
        }

        private static void DrawNotice(string text, bool capture)
        {
            var oldColor = GUI.color;
            if (capture)
                GUI.color = new Color(1f, 0.9f, 0.58f, 1f);
            GUILayout.Label(text, TranslationPanelTheme.Notice);
            GUI.color = oldColor;
        }

        internal static void WebTranslationsSaved()
        {
            _manualLoaded = false;
            _nextChangeCheck = _nextBatchCheck = 0f;
            RequestManagerRefresh();
        }

        private static void RefreshManagerIfRequested()
        {
            if (!_refreshRequested || _manager == null)
                return;
            _refreshRequested = false;
#if !IL2CPP
            if (BetterBepInExCompatibility.TryRefresh()) return;
#endif
            var build = _manager.GetType().GetMethod("BuildSettingList", BindingFlags.Instance | BindingFlags.Public);
            if (build != null)
                build.Invoke(_manager, null);
        }

        private static Rect GetManagerRect(object manager)
        {
            var property = manager.GetType().GetProperty("SettingWindowRect", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return property != null ? (Rect)property.GetValue(manager, null) : new Rect(100f, 100f, 650f, 600f);
        }

        // Capture context ----------------------------------------------------

        private static bool _editorDisabledManager, _managerEnabledBeforeEditor;
        internal static void BeginManagerContent()
        {
            _insideManagerContent = true;
            if (GlossaryEditorWindow.IsVisible || _pickerVisible || _paidNoticeForLayout)
            { _managerEnabledBeforeEditor = GUI.enabled; _editorDisabledManager = true; GUI.enabled = false; }
        }

        internal static void EndManagerContent()
        {
            if (_editorDisabledManager) { GUI.enabled = _managerEnabledBeforeEditor; _editorDisabledManager = false; }
            _insideManagerContent = false;
            _currentPluginGuid = null;
            _currentSetting = null;
            _insideCustomControl = false;
        }

        internal static void BeginPlugin(object pluginData)
        {
            _currentPluginGuid = ReadPluginGuid(pluginData);
        }

#if !IL2CPP
        internal static void BeginBetterMenuPlugin(string guid)
        {
            _currentPluginGuid = guid;
        }
#endif

        internal static void EndPlugin()
        {
            _currentPluginGuid = null;
            _currentSetting = null;
            _insideSettingValue = false;
            _insideCustomControl = false;
        }

        internal static void BeginSetting(SettingEntryBase setting)
        {
            _currentSetting = setting;
        }

        internal static void EndSetting()
        {
            _currentSetting = null;
            _insideSettingValue = false;
            _insideCustomControl = false;
        }

        internal static void BeginSettingValue(SettingEntryBase setting)
        {
            _currentSetting = setting;
            _insideSettingValue = setting != null;
            _insideCustomControl = setting != null && (setting.CustomDrawer != null || setting.CustomHotkeyDrawer != null);
        }

        internal static void EndSettingValue()
        {
            _insideSettingValue = false;
            _insideCustomControl = false;
        }

        internal static bool ShouldInterceptCustomButton
        {
            get { return _insideManagerContent && _insideCustomControl && _currentSetting != null && _captureActive; }
        }

        internal static string LocalizeCustomButton(string originalText)
        {
            if (!_insideManagerContent || !_insideCustomControl || _currentSetting == null)
                return originalText;
            return JsoncLocalization.GetCustomControlDisplay(_currentSetting, originalText);
        }

        internal static string LocalizeSettingChoice(object value)
        {
            var originalText = value != null ? value.ToString() : null;
            if (!_insideManagerContent || !_insideSettingValue || _currentSetting == null || string.IsNullOrWhiteSpace(originalText))
                return originalText;
            return JsoncLocalization.GetCustomControlDisplay(_currentSetting, originalText);
        }

        internal static bool CanLocalizeCustomControl
        {
            get { return _insideManagerContent && _insideCustomControl && _currentSetting != null; }
        }

        internal static void CaptureCustomButton(string originalText)
        {
            if (!ShouldInterceptCustomButton)
                return;
            SelectCapturedItem(JsoncLocalization.RegisterCustomControl(_currentSetting, originalText, true), "已抓取自定义控件：" + originalText);
        }

        internal static void CaptureCustomLabel(string originalText, Rect rect)
        {
            if (!ShouldInterceptCustomButton)
                return;
            var item = JsoncLocalization.RegisterCustomControl(_currentSetting, originalText, true);
            DrawCaptureHighlight(item, rect);
            if (WasLeftClicked(rect)) SelectCapturedItem(item, "已抓取自定义文本：" + originalText);
        }

        internal static void HighlightCustomControl(string originalText, Rect rect)
        {
            if (ShouldInterceptCustomButton && Event.current.type == EventType.Repaint)
                DrawCaptureHighlight(JsoncLocalization.RegisterCustomControl(_currentSetting, originalText, true), rect);
        }

        internal static bool CapturePluginHeader(bool clicked, Rect rect)
        {
            if (!_captureActive || string.IsNullOrWhiteSpace(_currentPluginGuid))
                return clicked;
            var item = JsoncLocalization.GetPluginWorkItem(_currentPluginGuid);
            DrawCaptureHighlight(item, rect);
            if (clicked && !_drawerOwnsPointer) SelectCapturedItem(item, "已抓取插件名称。");
            return false;
        }

        internal static void CaptureCategory(string displayedText, Rect rect)
        {
            if (!_captureActive || string.IsNullOrWhiteSpace(_currentPluginGuid))
                return;
            var item = JsoncLocalization.FindCategoryWorkItem(_currentPluginGuid, displayedText);
            DrawCaptureHighlight(item, rect);
            if (WasLeftClicked(rect)) SelectCapturedItem(item, "已抓取配置分类。");
        }

        internal static void CaptureSettingName(SettingEntryBase setting, Rect rect)
        {
            if (!_captureActive || setting == null || setting.HideSettingName)
                return;
            var item = JsoncLocalization.GetSettingWorkItem(setting, "DispName");
            DrawCaptureHighlight(item, rect);
            if (WasLeftClicked(rect)) SelectCapturedItem(item, "已抓取配置项；可切换名称、分类和说明。");
        }

#if !IL2CPP
        internal static void CaptureBetterMenuCategory(SettingEntryBase setting, Rect rect)
        {
            var item = JsoncLocalization.GetSettingWorkItem(setting, "Category");
            DrawCaptureHighlight(item, rect);
            if (WasLeftClicked(rect)) SelectCapturedItem(item, "已抓取配置分类。");
        }
#endif

        private static void DrawCaptureHighlight(TranslationWorkItem item, Rect rect)
        {
            if (item == null || !_captureActive || Event.current.type != EventType.Repaint || rect.width <= 0 || rect.height <= 0) return;
            var selected = _capturedItem != null && _capturedItem.PluginGuid == item.PluginGuid && _capturedItem.Id == item.Id;
            var hover = IsCapturePointerOver(rect);
            if (!hover && !selected) return;
            var previous = GUI.color;
            var previousEnabled = GUI.enabled;
            try
            {
                GUI.enabled = true;
                var color = TranslationPanelTheme.CaptureColor;
                color.a = selected ? 0.18f : 0.1f;
                GUI.color = color;
                GUI.DrawTexture(rect, Texture2D.whiteTexture);
                color.a = 1f;
                GUI.color = color;
                // Immediate-mode drawing shares the control's clip and coordinates, including scroll offsets.
                var thickness = Mathf.Min(selected ? 3f : 2f, rect.width / 2f, rect.height / 2f);
                GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), Texture2D.whiteTexture);
            }
            finally { GUI.color = previous; GUI.enabled = previousEnabled; }
        }

        private static void SelectCapturedItem(TranslationWorkItem item, string status)
        {
            if (HasUnsavedManualDraft)
            {
                QueuePluginPicker(() => GuardEditorNavigation(() => ApplyCapturedItem(item, status),
                    () => item != null && _snapshots.Any(snapshot => snapshot.Guid == item.PluginGuid &&
                        snapshot.CreateWorkItems().Any(candidate => candidate.Id == item.Id))));
                return;
            }
            ApplyCapturedItem(item, status);
        }

        private static void ApplyCapturedItem(TranslationWorkItem item, string status)
        {
            if (item == null)
            {
                SetStatus("此文本无法收集：可能无法识别所属设置，或已达到自定义文本的长度/数量上限。");
                return;
            }
            // Apply selection on Layout; manager capture happens after the drawer has drawn.
            _pendingCapture = item;
            _capturedItem = item;
            _domain = WorkbenchDomain.F12Settings;
            _pendingDomain = null;
            _captureActive = false;
            OpenTab(0);
            SetStatus(status + " 已打开对应的人工精翻编辑项。");
            if (Event.current != null && Event.current.isMouse) Event.current.Use();
        }

        private static bool WasLeftClicked(Rect rect)
        {
            var current = Event.current;
            return current != null && current.type == EventType.MouseDown && current.button == 0 && IsCapturePointerOver(rect);
        }

        private static bool IsCapturePointerOver(Rect rect)
        {
            if (_drawerOwnsPointer || Event.current == null || !rect.Contains(Event.current.mousePosition)) return false;
            // A row scrolled behind the header must not be selectable through the clipped area.
            return VisibleGuiClip == null || ((Rect)VisibleGuiClip.GetValue(null, null)).Contains(Event.current.mousePosition);
        }

        private static string ReadPluginGuid(object pluginData)
        {
            if (pluginData == null)
                return null;
            var field = pluginData.GetType().GetField("Info", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var info = field != null ? field.GetValue(pluginData) as BepInPlugin : null;
            return info != null ? info.GUID : null;
        }
    }
}
