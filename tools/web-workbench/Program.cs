using System.Security.Cryptography;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using ConfigurationManagerZhCn;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TranslationFoundation;

if (args.Length > 2 || (args.Length == 2 && (!int.TryParse(args[1], out var suppliedPort) || suppliedPort is < 1024 or > 65535)))
{
    Console.Error.WriteLine("用法：网页汉化工作台 <游戏目录>|--standalone [端口]");
    return;
}
var openBrowser = args.Length == 0;
var explicitStandalone = !openBrowser && args[0] == "--standalone";
var gameRoot = explicitStandalone ? null : openBrowser
    ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")) : Path.GetFullPath(args[0]);
var plugins = gameRoot == null ? null : Path.Combine(gameRoot, "BepInEx", "plugins");
var standalone = explicitStandalone || (openBrowser && !Directory.Exists(plugins));
if (!standalone && !Directory.Exists(plugins))
{
    ReportStartupError("游戏目录应包含 BepInEx/plugins。也可传入 --standalone 启动独立工作台。");
    return;
}
if (standalone)
    plugins = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConfigurationManager.zh-cn", "网页汉化工作台");
var port = args.Length == 2 ? int.Parse(args[1]) : standalone ? 17834 : openBrowser ? InstalledPort() : 17832;
var profileRoot = standalone ? plugins : gameRoot;
var profile = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profileRoot.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())));
var staticRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
string build;
try { build = WorkbenchLaunch.BuildId(staticRoot); }
catch (Exception ex) when (openBrowser && (ex is IOException or UnauthorizedAccessException))
{
    ReportStartupError("网页工作台组件无法读取。请完整解压安装包，再双击启动。");
    return;
}
var readyRoute = "/ready/v7/" + profile + "/" + build;
var ports = WorkbenchLaunch.Ports(port);
var files = new WebWorkbenchFiles(plugins);
if (!standalone && await WorkbenchLaunch.HasRetiredHostAsync(port, profile))
{
    ReportStartupError("此游戏目录的 2.14.0 旧网页服务仍在运行。请在旧网页点击「停止本地网页服务」，再打开独立网页工作台。");
    Environment.ExitCode = 1;
    return;
}
FileStream profileLock;
try { profileLock = WorkbenchLaunch.TryLockProfile(files.DirectoryPath); }
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
{
    ReportStartupError("无法锁定网页工作台数据目录，请确认当前 Windows 账户有读写权限。");
    Environment.ExitCode = 1;
    return;
}
using var exclusiveProfile = profileLock;
if (exclusiveProfile == null)
{
    if (openBrowser)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var existing = await WorkbenchLaunch.FindRunningAsync(ports, readyRoute, build);
            if (existing != 0) { OpenBrowser(existing); return; }
            await Task.Delay(150);
        }
    }
    ReportStartupError("此工作区已有另一个网页服务。请先关闭旧网页服务，再重新打开。");
    Environment.ExitCode = 1;
    return;
}
if (openBrowser)
{
    port = WorkbenchLaunch.FindFree(ports);
    if (port == 0)
    {
        ReportStartupError("网页工作台的本机端口均被占用，请关闭占用端口的程序后重试。");
        Environment.ExitCode = 1;
        return;
    }
}
var authority = "127.0.0.1:" + port;
var origin = WorkbenchLaunch.Origin(port);
var session = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
ClientGlossary.DirectoryPath = Path.Combine(plugins, "zh-cn", "glossary");
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(System.Net.IPAddress.Loopback, port);
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = WebWorkbenchFiles.MaxDraftBytes;
    options.Limits.MaxConcurrentConnections = 24;
});
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
var browserTabs = new BrowserTabs();
var work = new SemaphoreSlim(1);
var aiStopLock = new object();
var aiStop = new CancellationTokenSource();
var cache = new Dictionary<string, (DateTime Stamp, long Length, JObject Snapshot)>();
var sync = new object();
DirectServerConnection directServer = null;
var aiPresets = TranslationProviderPresets.Ai.Where(item => !item.AdminOnly).ToArray();
var machinePresets = TranslationProviderPresets.Machine.Where(item => !item.AdminOnly).ToArray();
var glossaryPath = Path.Combine(ClientGlossary.DirectoryPath, "eft.zh-CN.tsv");
var glossaryExample = Path.Combine(standalone ? AppContext.BaseDirectory : ClientGlossary.DirectoryPath, "eft.zh-CN.example.tsv");

app.Use(async (context, next) =>
{
    var response = context.Response;
    response.Headers.CacheControl = "no-store";
    response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self' ws://" + authority +
        "; form-action 'none'; base-uri 'none'; frame-ancestors 'none'";
    response.Headers["X-Content-Type-Options"] = "nosniff";
    response.Headers["X-Frame-Options"] = "DENY";
    response.Headers["Referrer-Policy"] = "no-referrer";
    var request = context.Request;
    var requestOrigin = request.Headers.Origin.ToString();
    var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
    if (request.Host.Value != authority || (requestOrigin.Length != 0 && requestOrigin != origin) ||
        (fetchSite.Length != 0 && fetchSite != "same-origin" && fetchSite != "none"))
    {
        response.StatusCode = 403;
        return;
    }
    if (request.Path.StartsWithSegments("/api") &&
        (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(request.Headers["X-Workbench-Session"].ToString()), Encoding.ASCII.GetBytes(session)) ||
         (HttpMethods.IsPost(request.Method) && (requestOrigin != origin || !request.HasJsonContentType()))))
    {
        response.StatusCode = 403;
        return;
    }
    try
    {
        if (request.Path.StartsWithSegments("/api/server"))
        {
            var connection = ServerConnection();
            if (connection == null || (bool)connection["locked"])
            {
                response.StatusCode = connection == null ? 404 : 403;
                await response.WriteAsJsonAsync(new { error = connection == null
                    ? "当前游戏未连接支持网页工作台的 SimpleTranslatorCS。"
                    : "Fika 联机需要先在 F12 验证服务端管理员密钥。" });
                return;
            }
        }
        await next();
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex) when (!response.HasStarted)
    {
        response.StatusCode = ex is BadHttpRequestException bad ? bad.StatusCode : 400;
        // Never return upstream response bodies, absolute paths, or submitted credentials.
        await response.WriteAsJsonAsync(new { error = ex is InputError ? ex.Message : "处理失败。请检查工作台文件、输入与本机服务状态。" });
    }
});

app.MapGet("/", () => browserTabs.ExpectTab()
    ? Results.Content(File.ReadAllText(Path.Combine(staticRoot, "index.html"))
        .Replace("__WORKBENCH_SESSION__", session).Replace("__WORKBENCH_LAUNCH__", openBrowser ? "desktop" : "cli")
        .Replace("__WORKBENCH_MODE__", standalone ? "standalone" : "game"),
        "text/html; charset=utf-8")
    : Results.StatusCode(503));
app.MapGet(readyRoute, (HttpContext context) =>
{
    if (!browserTabs.ExpectTab()) return Results.StatusCode(503);
    context.Response.Headers["X-Workbench-Build"] = build;
    return Results.NoContent();
});
app.MapPost("/api/browser-tab", async (HttpContext context) =>
{
    var body = await Body(context);
    if (!Guid.TryParseExact((string)body["id"], "D", out var id) || id == Guid.Empty || body["open"]?.Type != JTokenType.Boolean)
        throw new InputError("工作台标签状态无效。");
    return browserTabs.Update(id, (bool)body["open"]) ? Results.NoContent() : Results.StatusCode(503);
});
app.MapGet("/browser-lease", async (HttpContext context) =>
{
    var protocols = context.WebSockets.WebSocketRequestedProtocols;
    if (!context.WebSockets.IsWebSocketRequest || context.Request.Headers.Origin.ToString() != origin ||
        protocols.Count != 1 || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(protocols[0]), Encoding.ASCII.GetBytes(session)))
    {
        context.Response.StatusCode = 403;
        return;
    }
    if (!Guid.TryParseExact(context.Request.Query["id"].ToString(), "D", out var id) || id == Guid.Empty)
    {
        context.Response.StatusCode = 400;
        return;
    }
    if (!browserTabs.Attach(id, out var lease))
    {
        context.Response.StatusCode = 503;
        return;
    }
    try
    {
        using var token = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
        using var socket = await context.WebSockets.AcceptWebSocketAsync(session);
        var message = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[1]), token.Token);
        if (message.MessageType == WebSocketMessageType.Close)
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, token.Token);
    }
    catch (WebSocketException) { }
    catch (OperationCanceledException) { }
    finally { browserTabs.Detach(id, lease); }
});
app.MapPost("/api/shutdown", (HttpContext context) =>
{
    if (!openBrowser) return Results.StatusCode(409);
    context.Response.OnCompleted(() =>
    {
        app.Lifetime.StopApplication();
        return Task.CompletedTask;
    });
    return Results.Json(new { stopped = true });
});
app.MapGet("/workbench.js", () => Results.File(Path.Combine(staticRoot, "workbench.js"), "text/javascript; charset=utf-8"));
app.MapGet("/workbench.css", () => Results.File(Path.Combine(staticRoot, "workbench.css"), "text/css; charset=utf-8"));
app.MapGet("/glossary-schema.js", () => Results.Stream(
    typeof(InputError).Assembly.GetManifestResourceStream("GlossaryEditorScript"), "text/javascript; charset=utf-8"));
app.MapGet("/api/providers", () => Results.Json(new { ai = aiPresets.Select(Preset), machine = machinePresets.Select(Preset) }));
app.MapPost("/api/models", async (HttpContext context) =>
{
    var body = await Body(context);
    var provider = aiPresets.FirstOrDefault(item => item.Id == (string)body["provider"])
        ?? throw new InputError("请选择受支持的 AI 供应商。");
    var key = Text(body, "key", 512);
    if (provider.KeyRequired && string.IsNullOrWhiteSpace(key)) throw new InputError("请先填写 API 密钥。也可直接输入模型 ID。");
    try
    {
        var models = await TranslationClients.GetModelsAsync(TranslationProviderCatalog.GetAiProvider(provider.Name),
            provider.Endpoint, key, context.RequestAborted, refresh: true);
        return Results.Json(new { models = models.Where(model => model.Length <= 150).ToArray() });
    }
    catch (InvalidOperationException)
    {
        throw new InputError("供应商未返回可用模型。请核对密钥或直接输入模型 ID。");
    }
});
app.MapPost("/api/cfg/preview", async (HttpContext context) =>
{
    var plan = ReadCfg(await Body(context));
    return Results.Json(new { plan.Guid, plan.Name, plan.SkippedTypes,
        items = plan.Items.Select(item => new { item.Id, item.Source, field = item.Field ?? item.Kind, section = item.OriginalSection }) });
});
app.MapPost("/api/cfg/export", async (HttpContext context) =>
{
    var body = await Body(context);
    var plan = ReadCfg(body);
    if (body["values"] is not JObject values) throw new InputError("译文格式错误。");
    return Results.Json(new { name = plan.Guid + "_API.jsonc", text = plan.Export(values).ToString(Formatting.Indented) + "\n" });
});
app.MapGet("/api/ai-stop", () =>
{
    lock (aiStopLock) return Results.Json(new { paused = aiStop.IsCancellationRequested });
});
app.MapPost("/api/ai-stop", async (HttpContext context) =>
{
    if ((await Body(context))["paused"] is not JValue { Type: JTokenType.Boolean } flag)
        throw new InputError("暂停状态无效。");
    var pause = (bool)flag;
    lock (aiStopLock)
    {
        if (pause) aiStop.Cancel();
        else if (aiStop.IsCancellationRequested) { aiStop.Dispose(); aiStop = new CancellationTokenSource(); }
        return Results.Json(new { paused = aiStop.IsCancellationRequested });
    }
});
app.MapPost("/api/cfg/translate", async (HttpContext context) =>
{
    var body = await Body(context);
    var plan = ReadCfg(body);
    if (body["ids"] is not JArray ids || ids.Count is < 1 or > 120 || ids.Any(id => id.Type != JTokenType.String))
        throw new InputError("每次可翻译 1 至 120 个字段。");
    var selected = new HashSet<string>(ids.Values<string>(), StringComparer.Ordinal);
    var items = plan.Items.Where(item => selected.Contains(item.Id)).ToArray();
    if (items.Length != ids.Count) throw new InputError("选中项已变化，请重新读取 CFG。");
    var provider = aiPresets.FirstOrDefault(item => item.Id == (string)body["provider"])
        ?? throw new InputError("请选择预设供应商。");
    var key = Text(body, "key", 512);
    if (provider.KeyRequired && string.IsNullOrWhiteSpace(key)) throw new InputError("请填写所选供应商的 API 密钥。");
    if (!await work.WaitAsync(0, context.RequestAborted))
        return Results.Json(new { error = "已有翻译任务，完成或取消后再试。" }, statusCode: 409);
    try
    {
        using var deadline = WebJobDeadline(context, true);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        var (result, jobId, reused) = await TranslateWebAi("cfg", items, body, provider.Name, provider.Endpoint, provider.Model, key, deadline.Token);
        return Results.Json(new { values = result.Values, failures = result.Failures, stopped = !string.IsNullOrEmpty(result.StopReason), jobId, reused });
    }
    catch (Exception ex) when (ex is not InputError && !context.RequestAborted.IsCancellationRequested)
    {
        throw new InputError("翻译未完成。请检查供应商、模型、密钥、预算或网络状态。已发出的请求可能计费。");
    }
    finally { work.Release(); }
});
app.MapGet("/api/server-connection", () =>
{
    var connection = ServerConnection();
    if ((bool?)connection?["direct"] == true)
        return Json(connection);
    return Results.Json(new { id = (string)connection?["id"], locked = (bool?)connection?["locked"] == true });
});
app.MapPost("/api/direct/probe", async (HttpContext context) =>
{
    var body = await Body(context);
    var (fika, needsKey) = await DirectServerConnection.ProbeAsync(Text(body, "address", 400).Trim(),
        (bool?)body["trustLocalCertificate"] == true, context.RequestAborted);
    return Results.Json(new { fika, needsKey });
});
app.MapPost("/api/direct/connect", async (HttpContext context) =>
{
    var body = await Body(context);
    await RunServerOperation(context, async token =>
    {
        var connection = await DirectServerConnection.OpenAsync(Text(body, "address", 400).Trim(), Text(body, "adminKey", 256).Trim(),
            plugins, (bool?)body["trustLocalCertificate"] == true, token);
        lock (sync) directServer = connection;
    });
    return Results.NoContent();
});
app.MapPost("/api/direct/refresh", async (HttpContext context) =>
{
    await RunServerOperation(context, async token =>
    {
        var connection = RequireDirect();
        var snapshot = await connection.FetchSnapshotAsync(token);
        lock (sync) connection.Snapshot = snapshot;
    });
    return Results.NoContent();
});
app.MapPost("/api/direct/disconnect", async (HttpContext context) =>
{
    await RunServerOperation(context, _ =>
    {
        lock (sync) directServer = null;
        return Task.CompletedTask;
    });
    return Results.NoContent();
});
app.MapPost("/api/direct/publish", async (HttpContext context) =>
{
    var body = await Body(context);
    JObject result = null;
    await RunServerOperation(context, async token =>
    {
        var connection = RequireDirect();
        JObject draft;
        lock (sync)
        {
            var snapshot = RequireRevision("server", body);
            draft = ReadDraft("server");
            if (draft == null || (string)draft["id"] != (string)body["id"] ||
                (string)draft["revision"] != (string)snapshot["revision"] || ReadReceipt("server", draft) != null ||
                draft["edits"] is not JArray edits || edits.Count is < 1 or > 30)
                throw new InputError("草稿已变化、过期或已发布，请刷新后核对。");
            // Revalidate the saved draft against the live workspace before sending it.
            foreach (var (edit, row) in Select(snapshot, new JObject { ["edits"] = edits }, 30, needText: true))
                if ((string)edit["source"] != (string)row["source"])
                    throw new InputError("草稿原文已变化，请重新拉取。");
        }
        var published = await connection.PublishAsync((string)connection.Snapshot["serverRevision"], (string)draft["layer"], (JArray)draft["edits"], token);
        var count = (int)published["saved"];
        var restart = (bool?)published["restartRequired"] == true;
        var detail = "服务端已保存 " + count + " 条译文。" + (restart ? "部分内容需重启服务端。" :
            (bool?)published["liveApplied"] == true ? "已热加载。" : "如未显示，请在服务端检查加载状态。");
        if (published["activeFiles"] is JArray savedFiles)
            detail += " 文件 " + string.Join("、", savedFiles.Values<string>());
        lock (sync)
        {
            var rows = Rows(connection.Snapshot).ToArray();
            var ids = ((JArray)draft["edits"]).Select(item => (string)item["id"]).ToHashSet(StringComparer.Ordinal);
            var updated = (JObject)connection.Snapshot.DeepClone();
            updated["rows"] = new JArray(rows.Where(row => !ids.Contains((string)row["id"])));
            // A manual refresh is required before the next commit.
            updated["revision"] = Guid.NewGuid().ToString("N");
            updated["serverRevision"] = null;
            connection.Snapshot = updated;
            try
            {
                connection.Files.Write("server", "receipt", new JObject { ["id"] = draft["id"], ["state"] = "applied", ["detail"] = detail });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                detail += " 本机未能保存回执。";
            }
        }
        result = new JObject { ["count"] = count, ["detail"] = detail };
    });
    return Json(result);
});
app.MapGet("/api/glossary", () =>
{
    lock (sync)
    {
        WebWorkbenchFiles.RequireUnlinkedPath(glossaryPath);
        var existing = new GlossaryEditorSession(glossaryPath);
        WebWorkbenchFiles.RequireUnlinkedPath(glossaryExample);
        var example = new GlossaryEditorSession(glossaryExample);
        return Results.Json(new { revision = existing.Revision, example = example.LoadError.Length == 0 ? example.OriginalText : "",
            text = existing.Revision == "missing" ? example.OriginalText : existing.OriginalText, error = existing.LoadError });
    }
});
app.MapPost("/api/glossary", async (HttpContext context) =>
{
    var body = await Body(context);
    lock (sync)
    {
        WebWorkbenchFiles.RequireUnlinkedPath(glossaryPath);
        var editing = new GlossaryEditorSession(glossaryPath);
        if (editing.Revision != Text(body, "revision", 100))
            throw new InputError("术语表已在别处修改。请保留当前内容并重新读取。");
        try
        {
            editing.Document = GlossaryDocument.Parse(Text(body, "text", TranslationGlossary.MaxFileBytes));
            editing.Save();
        }
        catch (InvalidDataException ex) { throw new InputError(ex.Message); }
        return Results.Json(new { revision = editing.Revision, text = editing.OriginalText });
    }
});
app.MapGet("/api/{domain}/catalog", (HttpRequest request, string domain, string group, string q, string field, bool missing, int page,
    string view = "", string review = "") =>
{
    var snapshot = ReadSnapshot(domain);
    if (snapshot == null)
    {
        if (request.Query["selection"] == "matching") throw new InputError("当前没有可用原文，请先刷新快照。");
        return Results.Json(new { available = false });
    }
    var rows = Rows(snapshot).ToArray();
    var groups = rows.GroupBy(row => (string)row["group"], StringComparer.Ordinal)
        .Select(groupRows =>
        {
            var first = groupRows.First();
            var title = groupRows.FirstOrDefault(row => (string)row["id"] == "plugin-name");
            return new
            {
                id = groupRows.Key, name = (string)first["name"], version = (string)first["version"], count = groupRows.Count(),
                localizedName = title == null ? null : new[] { "manual", "ai", "machine" }.Select(layer => (string)title[layer])
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            };
        })
        .OrderBy(item => item.name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    var scope = request.Query["scope"].ToString();
    var picked = request.Query["source"].ToHashSet(StringComparer.Ordinal);
    var filterFields = request.Query["fieldScope"] == "selected";
    var pickedFields = request.Query["fieldType"].ToHashSet(StringComparer.Ordinal);
    if (scope.Length == 0 && group.Length == 0 && domain == "f12") group = groups.FirstOrDefault()?.id ?? "";
    q = q.Trim().Substring(0, Math.Min(120, q.Trim().Length));
    var candidates = review == "updates" && snapshot["removed"] is JArray removed
        ? rows.Concat(removed.OfType<JObject>()) : rows;
    var inGroup = candidates.Where(row => scope switch
    {
        "all" => true,
        "selected" => picked.Contains((string)row["group"]),
        _ => group.Length == 0 || (string)row["group"] == group
    }).ToArray();
    var filtered = inGroup.Where(row =>
        (filterFields ? pickedFields.Contains((string)row["field"] ?? "") : field.Length == 0 || (string)row["field"] == field) &&
        (review switch {
            "ai" => HasTranslation(row, "ai"),
            "machine" => HasTranslation(row, "machine"),
            "api" => HasTranslation(row, "ai") || HasTranslation(row, "machine"),
            "published" => HasTranslation(row, "manual"),
            "updates" => HasTranslation(row, "change"),
            _ => true
        }) &&
        (!missing || !new[] { "manual", "ai", "machine" }.Any(layer => !string.IsNullOrWhiteSpace((string)row[layer]))) &&
        (q.Length == 0 || new[] { "name", "group", "source", "previousSource", "manual", "ai", "machine", "context", "id" }
            .Any(key => ((string)row[key] ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)))).ToArray();
    if (request.Query["selection"] == "matching")
    {
        if (request.Query["revision"] != (string)snapshot["revision"])
            throw new InputError("原文快照已变化，请刷新后再跨页选择。");
        var selectable = filtered.Where(row => (bool?)row["removed"] != true).ToArray();
        if (selectable.Length > 12000) throw new InputError("匹配条目超过 12,000 条，请先缩小来源或搜索范围。");
        return Json(new JObject { ["rows"] = new JArray(selectable.Select(row => new JObject
        {
            ["id"] = row["id"], ["group"] = row["group"], ["field"] = (string)row["field"] ?? "", ["stamp"] = SelectionStamp(row)
        })) });
    }
    const int size = 24;
    var preview = view == "preview" && domain == "f12";
    static (string Group, string Unit) PreviewUnit(JObject row) => ((string)row["group"], SettingUnit(row));
    var units = preview ? filtered.Select(PreviewUnit).Distinct().ToArray() : Array.Empty<(string Group, string Unit)>();
    var pageTotal = preview ? units.Length : filtered.Length;
    page = Math.Clamp(page, 1, Math.Max(1, (pageTotal + size - 1) / size));
    var visibleUnits = units.Skip((page - 1) * size).Take(size).ToHashSet();
    var visibleGroups = visibleUnits.Select(unit => unit.Group).ToHashSet(StringComparer.Ordinal);
    var pageRows = preview ? filtered.Where(row => visibleUnits.Contains(PreviewUnit(row))) : filtered.Skip((page - 1) * size).Take(size);
    return Json(new JObject
    {
        ["available"] = true, ["revision"] = snapshot["revision"], ["capturedAt"] = snapshot["capturedAt"], ["group"] = group,
        ["updateBasis"] = snapshot["updateBasis"], ["draftVersion"] = snapshot["draftVersion"],
        ["groups"] = JArray.FromObject(groups), ["fields"] = new JArray(candidates.Select(row => (string)row["field"] ?? "").Distinct().OrderBy(value => value)),
        ["rows"] = new JArray(pageRows.Select(SelectableRow)), ["total"] = filtered.Length, ["pageTotal"] = pageTotal, ["page"] = page, ["size"] = size,
        ["previewRows"] = preview ? new JArray(inGroup.Where(row => visibleUnits.Contains(PreviewUnit(row)) ||
            ((string)row["id"] == "plugin-name" && visibleGroups.Contains((string)row["group"]))).Select(SelectableRow)) : null
    });
});
app.MapPost("/api/{domain}/selection-resolve", async (HttpContext context, string domain) =>
{
    var body = await Body(context);
    var snapshot = RequireRevision(domain, body);
    if (body["edits"] is not JArray { Count: >= 1 and <= 30 } edits || edits.Any(item => item is not JObject))
        throw new InputError("每批请选择 1 至 30 条文本。");
    var requested = edits.OfType<JObject>().ToDictionary(RowKey);
    var current = Rows(snapshot).Where(row => requested.ContainsKey(RowKey(row))).ToDictionary(RowKey);
    var rows = edits.OfType<JObject>()
        .Select(edit => current.TryGetValue(RowKey(edit), out var row) && (string)edit["stamp"] == SelectionStamp(row) ? row : null)
        .Where(row => row != null).ToArray();
    RequireDraftVersion(domain, snapshot, rows.Select(row => (string)row["group"]).Distinct(StringComparer.Ordinal).Skip(1).Any());
    return Json(new JObject { ["rows"] = new JArray(rows.Select(SelectableRow)) });
});
app.MapGet("/api/{domain}/draft", (string domain) =>
{
    var draft = ReadDraft(domain);
    return Json(new JObject { ["draft"] = draft, ["receipt"] = ReadReceipt(domain, draft) });
});
app.MapGet("/api/{domain}/status", (string domain) =>
{
    var draft = ReadDraft(domain);
    var receipt = ReadReceipt(domain, draft);
    return Results.Json(new
    {
        revision = (string)ReadSnapshot(domain)?["revision"],
        draftId = (string)draft?["id"],
        receiptId = (string)receipt?["id"],
        receiptState = (string)receipt?["state"],
        receiptDetail = (string)receipt?["detail"]
    });
});
app.MapPost("/api/{domain}/stage", async (HttpContext context, string domain) =>
{
    var body = await Body(context);
    var snapshot = RequireRevision(domain, body);
    var layer = Text(body, "layer", 10);
    if (layer is not ("manual" or "ai" or "machine") ||
        (domain == "server" && (bool?)snapshot["direct"] != true && layer != "manual"))
        throw new InputError("译文层无效。");
    var selected = Select(snapshot, body, domain == "server" ? 30 : 100, needText: true);
    var groups = selected.Select(item => (string)item.Row["group"]).Distinct(StringComparer.Ordinal).ToArray();
    RequireDraftVersion(domain, snapshot, groups.Length > 1);
    lock (sync)
    {
        CheckServerSnapshotConnection(domain, snapshot);
        var existing = ReadDraft(domain);
        if ((string)existing?["id"] != (string)body["previous"])
            throw new InputError("已有其他页面修改草稿，请先重新载入。");
        var id = Guid.NewGuid().ToString("N");
        var edits = new JArray();
        foreach (var item in selected)
        {
            var translated = Text(item.Edit, "text", 4000);
            if (domain == "server" && string.IsNullOrWhiteSpace(translated)) throw new InputError("玩家投稿不能清空译文。");
            if (!string.IsNullOrWhiteSpace(translated))
            {
                try
                {
                    var workItem = WorkItem(item.Row, "draft", domain);
                    var manual = domain == "f12" && layer == "manual";
                    if (manual) translated = TranslationSafety.PreserveOuterFormatting(workItem.Source, translated);
                    translated = TranslationSafety.Validate(translated, workItem, manual);
                }
                catch (TranslationValidationException ex) { throw new InputError(ex.Message); }
            }
            edits.Add(new JObject
            {
                ["id"] = item.Row["id"], ["group"] = item.Row["group"], ["source"] = item.Row["source"],
                ["name"] = item.Row["name"], ["field"] = item.Row["field"],
                ["context"] = item.Row["context"], ["base"] = (string)item.Row[layer] ?? "", ["text"] = translated
            });
        }
        CheckServerSnapshotConnection(domain, snapshot);
        var draft = new JObject { ["revision"] = body["revision"], ["id"] = id,
            ["group"] = groups.Length == 1 ? groups[0] : null, ["layer"] = layer, ["edits"] = edits };
        if ((string)body["workMode"] is "manual" or "api") draft["workMode"] = body["workMode"];
        if (domain == "server") draft["connection"] = snapshot["connection"];
        FilesFor(domain).Write(domain, "draft", draft);
        return Results.Json(new { id, count = edits.Count });
    }
});
app.MapPost("/api/{domain}/translate", async (HttpContext context, string domain) =>
{
    var body = await Body(context);
    var snapshot = RequireRevision(domain, body);
    var mode = Text(body, "mode", 10);
    if (mode != "ai" && mode != "machine") throw new InputError("未知翻译方式。");
    var selected = Select(snapshot, body, 30, needText: false);
    RequireDraftVersion(domain, snapshot, selected.Select(item => (string)item.Row["group"]).Distinct(StringComparer.Ordinal).Skip(1).Any());
    var items = selected.Select((item, i) => WorkItem(item.Row, i.ToString(), domain)).ToArray();
    if (items.Sum(item => (long)item.Source.Length) > 30000)
        throw new InputError("本批原文超过 30,000 字符，请缩小范围。");
    var provider = (mode == "ai" ? aiPresets : machinePresets).FirstOrDefault(item => item.Id == (string)body["provider"]);
    if (provider == null) throw new InputError("只能选择预设供应商。");
    var key = Text(body, "key", 512);
    if (provider.KeyRequired && string.IsNullOrWhiteSpace(key)) throw new InputError("请填写所选供应商的 API 密钥。");
    var timeout = Number(body, "timeout", 40, 5, 90);
    if (!await work.WaitAsync(0, context.RequestAborted)) return Results.Json(new { error = "已有翻译任务，完成或取消后再试。" }, statusCode: 409);
    try
    {
        CheckServerSnapshotConnection(domain, snapshot);
        using var deadline = WebJobDeadline(context, mode == "ai");
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        TranslationResult result;
        string jobId = null;
        var reused = 0;
        var batch = Number(body, "batch", 10, 1, 30);
        if (mode == "ai")
            (result, jobId, reused) = await TranslateWebAi(domain, items, body, provider.Name, provider.Endpoint, provider.Model, key, deadline.Token);
        else
        {
            result = await TranslationClients.TranslateMachineAsync(items, TranslationProviderCatalog.GetMachineProvider(provider.Name),
                provider.Endpoint, key, Text(body, "appId", 150), Text(body, "region", 80), Text(body, "sourceLanguage", 32),
                provider.TargetLanguage, batch, timeout, null, deadline.Token);
        }
        CheckServerSnapshotConnection(domain, snapshot);
        return Results.Json(new { values = selected.Select((item, i) => new { id = (string)item.Row["id"], group = (string)item.Row["group"],
            text = result.Values.GetValueOrDefault(i.ToString()) }).Where(item => item.text != null),
            failed = items.Length - result.Values.Count,
            reasons = result.Failures.Values.GroupBy(reason => reason).OrderByDescending(group => group.Count())
                .Take(3).Select(group => new { reason = group.Key, count = group.Count() }),
            stopped = !string.IsNullOrWhiteSpace(result.StopReason), jobId, reused });
    }
    catch (Exception ex) when (ex is not InputError && !context.RequestAborted.IsCancellationRequested)
    {
        throw new InputError("翻译未完成。请检查供应商、模型、密钥、预算或网络状态。已发出的请求可能计费。");
    }
    finally { work.Release(); }
});

try
{
    await app.StartAsync();
    Console.WriteLine("Local translation workbench: " + origin);
    if (openBrowser) OpenBrowser(port);
    var stopWhenIdle = browserTabs.StopWhenIdleAsync(app.Lifetime);
    await app.WaitForShutdownAsync();
    await stopWhenIdle;
}
catch (Exception ex) when (openBrowser)
{
    ReportStartupError("无法打开本地网页工作台（" + ex.GetType().Name + "）。请检查本机端口和默认浏览器。");
    Environment.ExitCode = 1;
}
finally { await app.DisposeAsync(); }

static void RequireDraftVersion(string domain, JObject snapshot, bool multipleGroups)
{
    if (domain == "f12" && multipleGroups && (int?)snapshot["draftVersion"] != WebWorkbenchFiles.F12DraftVersion)
        throw new InputError("当前 F12 快照只支持单插件草稿。请选择一个插件，或升级 F12 汉化后在游戏里刷新快照。");
}

int InstalledPort()
{
    if (File.Exists(Path.Combine(gameRoot, "GameAssembly.dll"))) return 17833;
    if (Directory.Exists(Path.Combine(gameRoot, "SPT")) && !Directory.Exists(Path.Combine(gameRoot, "SPT_Runtime"))) return 17830;
    return 17832;
}

void ReportStartupError(string message)
{
    Console.Error.WriteLine(message);
    if (openBrowser && OperatingSystem.IsWindows())
        NativeMethods.MessageBox(IntPtr.Zero, message, "网页汉化工作台", 0x10);
}

void OpenBrowser(int browserPort)
{
    using var browser = Process.Start(new ProcessStartInfo(WorkbenchLaunch.Origin(browserPort) + "/") { UseShellExecute = true });
}

DirectServerConnection RequireDirect()
{
    lock (sync) return directServer ?? throw new InputError("请先连接并验证 SPT 服务端。");
}

async Task RunServerOperation(HttpContext context, Func<CancellationToken, Task> action)
{
    if (!await work.WaitAsync(0, context.RequestAborted))
        throw new InputError("工作台正在处理其他任务，请完成或取消后重试。");
    try
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
        await action(limit.Token);
    }
    finally { work.Release(); }
}

WebWorkbenchFiles FilesFor(string domain)
{
    lock (sync) return domain == "server" && directServer != null ? directServer.Files : files;
}

JObject ServerConnection()
{
    lock (sync)
        if (directServer != null)
            return new JObject { ["id"] = directServer.Id, ["locked"] = false, ["direct"] = true, ["address"] = directServer.Address };
    try { return files.ReadServerConnection(); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidCastException or FormatException)
    {
        return null;
    }
}

string OpenServerConnection()
{
    var connection = ServerConnection();
    return (bool?)connection?["locked"] == false ? (string)connection["id"] : null;
}

JObject ReadDraft(string domain)
{
    var draft = FilesFor(domain).Read(domain, "draft");
    if (domain != "server") return draft;
    var connection = OpenServerConnection();
    return connection != null && (string)draft?["connection"] == connection ? draft : null;
}

JObject ReadReceipt(string domain, JObject draft)
{
    if (domain == "server" && draft == null) return null;
    var receipt = FilesFor(domain).Read(domain, "receipt");
    return domain != "server" || (string)receipt?["id"] == (string)draft["id"] ? receipt : null;
}

void CheckServerSnapshotConnection(string domain, JObject snapshot)
{
    if (domain != "server") return;
    lock (sync)
    {
        if (directServer != null)
        {
            if ((bool?)snapshot["direct"] != true || (string)snapshot["revision"] != (string)directServer.Snapshot["revision"] ||
                (string)snapshot["connection"] != directServer.Id)
                throw new InputError("服务端连接或快照已变化，请重新加载。");
            return;
        }
    }
    var connection = OpenServerConnection();
    if (connection == null || (bool?)snapshot["direct"] == true || (string)snapshot["connection"] != connection)
        throw new InputError("服务端连接已切换或需要重新验证，请返回 F12 核对。");
}

JObject ReadSnapshot(string domain)
{
    var name = WebWorkbenchFiles.Name(domain, "snapshot");
    lock (sync)
        if (domain == "server" && directServer != null)
            return directServer.Snapshot;
    var connection = domain == "server" ? OpenServerConnection() : null;
    if (domain == "server" && connection == null) return null;
    lock (sync)
    {
        var path = new FileInfo(Path.Combine(files.DirectoryPath, name));
        if (!path.Exists) { cache.Remove(domain); return null; }
        if (cache.TryGetValue(domain, out var entry) && entry.Stamp == path.LastWriteTimeUtc && entry.Length == path.Length)
            return domain != "server" || (string)entry.Snapshot["connection"] == connection ? entry.Snapshot : null;
        var snapshot = files.Read(domain, "snapshot");
        if (snapshot != null) cache[domain] = (path.LastWriteTimeUtc, path.Length, snapshot);
        return domain != "server" || (string)snapshot?["connection"] == connection ? snapshot : null;
    }
}

JObject RequireRevision(string domain, JObject body)
{
    var snapshot = ReadSnapshot(domain);
    if (snapshot == null || !Guid.TryParseExact((string)body["revision"], "N", out _) || (string)snapshot["revision"] != (string)body["revision"])
        throw new InputError("原文快照已变化，请重新加载后核对。");
    if ((bool?)snapshot["direct"] == true && string.IsNullOrEmpty((string)snapshot["serverRevision"]))
        throw new InputError("请先重新拉取服务端原文。");
    return snapshot;
}

static object Preset(ProviderPreset item) => new { item.Id, item.Name, item.Model, item.AppIdRequired, item.TargetLanguage };
static IResult Json(JObject obj) => Results.Text(obj.ToString(Formatting.None), "application/json; charset=utf-8");
static IEnumerable<JObject> Rows(JObject snap) => ((JArray)snap["rows"]).OfType<JObject>();
static async Task<JObject> Body(HttpContext context)
{
    using var reader = new StreamReader(context.Request.Body, new UTF8Encoding(false, true));
    var text = await reader.ReadToEndAsync(context.RequestAborted);
    using var json = new JsonTextReader(new StringReader(text)) { MaxDepth = 12, DateParseHandling = DateParseHandling.None };
    var result = JObject.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
    if (json.Read()) throw new InputError("请求含多余内容。");
    return result;
}
static bool HasTranslation(JObject row, string layer) => !string.IsNullOrWhiteSpace((string)row[layer]);

TranslationGlossary LoadWebGlossary()
{
    WebWorkbenchFiles.RequireUnlinkedPath(glossaryPath);
    WebWorkbenchFiles.RequireUnlinkedPath(glossaryExample);
    var selected = new GlossaryEditorSession(glossaryPath);
    if (selected.Revision == "missing") selected = new GlossaryEditorSession(glossaryExample);
    if (selected.LoadError.Length != 0) throw new InputError("请先修复 AI 翻译术语表。");
    return selected.Revision == "missing" ? TranslationGlossary.Empty : TranslationGlossary.Parse(selected.OriginalText.TrimStart('\uFEFF'));
}

CancellationTokenSource WebJobDeadline(HttpContext context, bool paid)
{
    lock (aiStopLock)
    {
        if (paid && aiStop.IsCancellationRequested) throw new InputError("API 翻译已暂停。请先恢复 API。");
        return CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping,
            paid ? aiStop.Token : CancellationToken.None);
    }
}

async Task<(TranslationResult Result, string JobId, int Reused)> TranslateWebAi(string domain, IList<TranslationWorkItem> items, JObject body, string provider, string endpoint,
    string defaultModel, string key, CancellationToken token)
{
    var model = Text(body, "model", 150).Trim();
    var reasoning = Text(body, "reasoning", 12);
    if (!new[] { "default", "off", "low", "medium", "high" }.Contains(reasoning)) throw new InputError("推理强度无效。");
    model = model.Length == 0 ? defaultModel : model;
    var prompt = Text(body, "prompt", 1500);
    var glossary = LoadWebGlossary();
    var inputLimit = Number(body, "input", 12000, 1000, 30000);
    var connectionId = domain == "server" ? (string)ServerConnection()?["id"] : null;
    var identity = new JArray(provider, endpoint, model, reasoning, prompt, (string)body["revision"],
        connectionId,
        new JArray(items.OrderBy(item => item.Id, StringComparer.Ordinal).Select(item => new JArray(item.Id,
            TranslationSafety.ContentKey(item), glossary.Reference(new[] { item.Source }, "简体中文", glossary.Budget(inputLimit))))));
    var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString(Formatting.None))));
    var taskFiles = FilesFor(domain);
    var previous = taskFiles.Read(domain, "api-checkpoint");
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var jobId = Guid.NewGuid().ToString("N");
    var budget = new PaidApiBudget();
    if ((string)previous?["fingerprint"] == fingerprint && Guid.TryParseExact((string)previous?["jobId"], "N", out _))
    {
        jobId = (string)previous["jobId"];
        budget = new PaidApiBudget((int?)previous["requests"] ?? 0, (long?)previous["tokens"] ?? 0);
        foreach (var item in items)
            if (previous["values"]?[item.Id]?.Type == JTokenType.String)
                values[item.Id] = TranslationSafety.Validate((string)previous["values"][item.Id], item);
    }
    var reused = values.Count;
    void SaveCheckpoint()
    {
        if (domain == "server" && connectionId != (string)ServerConnection()?["id"])
            throw new InputError("服务端连接已切换，停止写入上一次任务。");
        taskFiles.Write(domain, "api-checkpoint", new JObject { ["jobId"] = jobId, ["fingerprint"] = fingerprint,
            ["values"] = JObject.FromObject(values), ["requests"] = budget.Requests, ["tokens"] = budget.Tokens });
    }
    token.ThrowIfCancellationRequested();
    SaveCheckpoint();
    budget.Changed = SaveCheckpoint;
    var result = await TranslationClients.TranslateAiAsync(items.Where(item => !values.ContainsKey(item.Id)).ToArray(), TranslationProviderCatalog.GetAiProvider(provider),
        endpoint, model, key, "简体中文", prompt,
        Number(body, "batch", 10, 1, 30), inputLimit,
        Number(body, "retries", 0, 0, 2), Number(body, "timeout", 40, 5, 90), null, token, options: new AiExecutionOptions
        {
            Reasoning = reasoning, MaxOutputTokens = Number(body, "output", 4096, 128, 16384),
            Budget = budget,
            MaxRequests = Number(body, "requests", 12, 1, 30),
            MaxReportedTokens = Number(body, "tokens", PaidApiBudget.DefaultTokens, 0, 500000),
            RequestDelayMs = Number(body, "delay", 400, 0, 5000),
            JsonOutput = (bool?)body["json"] != false,
            Glossary = glossary,
            Checkpoint = current =>
            {
                foreach (var pair in current.Values) values[pair.Key] = pair.Value;
                SaveCheckpoint();
            }
        });
    foreach (var pair in result.Values) values[pair.Key] = pair.Value;
    SaveCheckpoint();
    foreach (var pair in values) result.Values[pair.Key] = pair.Value;
    return (result, jobId, reused);
}

static CfgImport ReadCfg(JObject body) => CfgImport.Parse(Text(body, "file", 255),
    Text(body, "cfg", CfgImport.MaxCharacters), Text(body, "guid", 128));

static string SettingUnit(JObject row)
{
    var id = (string)row["id"] ?? "";
    var last = id.LastIndexOf('|');
    return id.StartsWith("setting|", StringComparison.Ordinal) && last > "setting|".Length ? id[..last] : id;
}

static string Text(JObject obj, string key, int max)
{
    var value = obj[key];
    if (value == null) return "";
    if (value.Type != JTokenType.String || ((string)value).Length > max) throw new InputError("文本输入格式或长度不符合要求。");
    return (string)value;
}
static int Number(JObject obj, string key, int fallback, int min, int max)
{
    var value = obj[key] == null ? fallback : obj[key].Type == JTokenType.Integer ? (int)obj[key] : int.MinValue;
    if (value < min || value > max) throw new InputError("数字超出允许范围：" + key);
    return value;
}
static (string Group, string Id) RowKey(JObject row) => ((string)row["group"], (string)row["id"]);
static string SelectionStamp(JObject row)
{
    var fields = new JArray(row["source"], row["context"], row["field"], row["manual"], row["ai"], row["machine"]);
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fields.ToString(Formatting.None))));
}
static JObject SelectableRow(JObject row)
{
    var copy = (JObject)row.DeepClone();
    copy["stamp"] = SelectionStamp(row);
    return copy;
}
static (JObject Edit, JObject Row)[] Select(JObject snap, JObject body, int max, bool needText)
{
    if (!(body["edits"] is JArray list) || list.Count < 1 || list.Count > max)
        throw new InputError("请选择 1 至 " + max + " 条文本。");
    var requested = new Dictionary<(string, string), JObject>();
    foreach (var edit in list)
    {
        if (edit is not JObject obj || !requested.TryAdd(((string)obj["group"], (string)obj["id"]), obj))
            throw new InputError("选中项无效或重复。");
    }
    var selected = new List<(JObject, JObject)>();
    foreach (var row in Rows(snap))
    {
        if (!requested.Remove(((string)row["group"], (string)row["id"]), out var edit)) continue;
        if (needText && edit["text"]?.Type != JTokenType.String) throw new InputError("译文必须为文本。");
        if (!needText && edit["stamp"] != null && (string)edit["stamp"] != SelectionStamp(row))
            throw new InputError("选中条目已变化，请重新核对。");
        selected.Add((edit, row));
    }
    if (requested.Count != 0) throw new InputError("原文条目已失效，请重新加载。");
    return selected.ToArray();
}
static TranslationWorkItem WorkItem(JObject row, string id, string domain)
{
    var field = (string)row["field"];
    // Existing snapshots already encode the enum type and member in the F12 identity.
    var enumIdentity = domain == "f12" ? ((string)row["id"])?.Split('|', 3) : null;
    var isEnum = enumIdentity is { Length: 3 } && enumIdentity[0] == "enum";
    var unit = domain == "f12" ? SettingUnit(row) : "";
    return new TranslationWorkItem
    {
        Id = id, PluginGuid = (string)row["group"], Field = isEnum ? "Enum" : field, Source = (string)row["source"], Context = (string)row["context"],
        Kind = domain == "server" ? "ServerGameText" : isEnum ? "Enum" : field == "PluginName" ? "Plugin" : "Setting",
        EnumType = isEnum ? enumIdentity[1] : null, EnumMember = isEnum ? enumIdentity[2] : null,
        EntryKey = unit.StartsWith("setting|", StringComparison.Ordinal) ? unit["setting|".Length..] : null,
        OriginalSection = (string)row["section"], RelatedDescription = (string)row["descriptionHint"],
        PluginName = (string)row["name"]
    };
}

internal sealed class InputError(string message) : InvalidOperationException(message);
internal static class NativeMethods
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
