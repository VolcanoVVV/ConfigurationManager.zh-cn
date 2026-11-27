using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ConfigurationManagerZhCn;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal sealed class DirectServerConnection
{
    private const string Prefix = "/simpletranslator/v1/";
    private static readonly HttpClient Http = CreateClient(false);
    private static readonly HttpClient LocalHttps = CreateClient(true);

    private readonly Uri _address;
    private readonly string _adminKey;
    private readonly HttpClient _http;
    internal string Id { get; }
    internal string Address => _address.GetLeftPart(UriPartial.Authority);
    internal WebWorkbenchFiles Files { get; }
    internal JObject Snapshot { get; set; }

    private DirectServerConnection(Uri address, string adminKey, string plugins, bool trustLocalCertificate)
    {
        _address = address;
        _adminKey = adminKey;
        _http = trustLocalCertificate ? LocalHttps : Http;
        Id = Digest(Address)[..32];
        Files = plugins == null ? null : new WebWorkbenchFiles(plugins, Id);
    }

    internal static async Task<(bool Fika, bool NeedsKey)> ProbeAsync(string address, bool trustLocalCertificate, CancellationToken token)
    {
        var uri = ParseAddress(address, trustLocalCertificate);
        var probe = new DirectServerConnection(uri, null, null, trustLocalCertificate);
        var fika = await probe.ReadFikaAsync(token);
        return (fika, fika || !LocalSptAdmin.IsLoopback(uri));
    }

    internal static async Task<DirectServerConnection> OpenAsync(string address, string adminKey, string plugins, bool trustLocalCertificate, CancellationToken token)
    {
        var uri = ParseAddress(address, trustLocalCertificate);
        var probe = new DirectServerConnection(uri, null, null, trustLocalCertificate);
        var fika = await probe.ReadFikaAsync(token);
        if (string.IsNullOrWhiteSpace(adminKey))
        {
            if (fika) throw new InputError("当前服务端已启用 Fika，请填写服务端管理员密钥。");
            if (!LocalSptAdmin.IsLoopback(uri)) throw new InputError("远程服务端需要管理员密钥。");
            adminKey = LocalSptAdmin.ReadKey(uri);
            if (adminKey == null)
                throw new InputError("无法从本机 SPT 进程读取管理员凭据。请确认 SPT 已启动，并用有权限读取其模组目录的 Windows 账户打开工作台。");
        }
        var connection = new DirectServerConnection(uri, adminKey, plugins, trustLocalCertificate);
        var access = await connection.RequestAsync("workbench/access", new JObject(), token);
        if ((bool?)access["ok"] != true || (bool?)access["isAdmin"] != true)
            throw new InputError("服务端管理员密钥未通过验证。");
        connection.Snapshot = await connection.FetchSnapshotAsync(token);
        return connection;
    }

    private static Uri ParseAddress(string address, bool trustLocalCertificate)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            throw new InputError("请输入服务端根地址。本机可用 HTTP，远程连接需要有效证书的 HTTPS。");
        if (trustLocalCertificate && (!LocalSptAdmin.IsLoopback(uri) || uri.Scheme != Uri.UriSchemeHttps))
            throw new InputError("本机证书选项仅适用于 HTTPS 的 127.0.0.1 或 [::1] 地址。");
        return uri;
    }

    private async Task<bool> ReadFikaAsync(CancellationToken token)
    {
        var capabilities = await RequestAsync("capabilities", null, token);
        var features = (capabilities["features"] as JArray)?.Values<string>().ToHashSet(StringComparer.Ordinal);
        if ((string)capabilities["service"] != "SimpleTranslatorCS" || (bool?)capabilities["enabled"] != true ||
            features == null || !new[] { "admin-access", "catalog", "save-batch" }.All(features.Contains))
            throw new InputError("此服务器未提供兼容的 SimpleTranslatorCS 管理接口。");
        return (bool?)capabilities["fikaDetected"] != false;
    }

    internal async Task<JObject> FetchSnapshotAsync(CancellationToken token)
    {
        var catalog = await RequestAsync("workbench/catalog", new JObject(), token);
        var revision = (string)catalog["revision"];
        if (revision == null || revision.Length != 64 || !revision.All(Uri.IsHexDigit) || catalog["entries"] is not JArray entries)
            throw new InputError("服务端目录格式无效，请检查 SimpleTranslatorCS 版本。");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var rows = new JArray();
        foreach (var entry in entries.OfType<JObject>())
        {
            var id = (string)entry["id"];
            var group = (string)entry["sourceGroup"];
            var source = (string)entry["english"];
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(source) ||
                id.Length > 256 || group.Length > 256 || source.Length > 4000 || !keys.Add(id))
                continue;
            rows.Add(new JObject
            {
                ["id"] = id, ["group"] = group, ["source"] = source, ["name"] = (string)entry["sourceGroupName"] ?? group,
                ["context"] = (string)entry["domain"] + " · " + id, ["field"] = (string)entry["field"] ?? "text"
            });
        }
        return new JObject
        {
            ["revision"] = Digest(Id + ":" + revision)[..32], ["serverRevision"] = revision, ["connection"] = Id,
            ["direct"] = true, ["capturedAt"] = (string)catalog["generatedAt"], ["rows"] = rows
        };
    }

    internal async Task<JObject> PublishAsync(string revision, string layer, JArray edits, CancellationToken token)
    {
        var result = await RequestAsync("workbench/save", new JObject
        {
            ["mode"] = layer, ["revision"] = revision, ["combineSources"] = layer == "ai",
            ["entries"] = new JArray(edits.OfType<JObject>().Select(edit => new JObject
            {
                ["id"] = edit["id"], ["source"] = edit["source"], ["translation"] = edit["text"]
            }))
        }, token);
        if ((bool?)result["ok"] != true || (int?)result["saved"] != edits.Count)
            throw new InputError("没有收到服务端的完整保存回执，请先重新拉取原文，确认提交是否成功。");
        return result;
    }

    private async Task<JObject> RequestAsync(string endpoint, JObject body, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, new Uri(_address, Prefix + endpoint));
        if (body != null)
        {
            body["adminKey"] = _adminKey;
            var bytes = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
            using var payload = new MemoryStream();
            using (var compressed = new ZLibStream(payload, CompressionLevel.Fastest, leaveOpen: true))
                compressed.Write(bytes);
            request.Content = new ByteArrayContent(payload.ToArray());
        }
        try
        {
            using var response = await _http.SendAsync(request, limit.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InputError("服务端拒绝管理员访问，请检查密钥和权限。");
            if (!response.IsSuccessStatusCode) throw new InputError("无法访问 SimpleTranslatorCS 管理接口（HTTP " + (int)response.StatusCode + "）。");
            var bytes = await response.Content.ReadAsByteArrayAsync(limit.Token);
            using var buffer = new MemoryStream(bytes);
            // SPT may return zlib without an HTTP Content-Encoding header.
            using Stream source = bytes is [0x78, _, ..] && ((bytes[0] << 8) + bytes[1]) % 31 == 0
                ? new ZLibStream(buffer, CompressionMode.Decompress) : buffer;
            using var output = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await source.ReadAsync(chunk, limit.Token)) != 0)
            {
                if (output.Length + read > WebWorkbenchFiles.MaxSnapshotBytes)
                    throw new InputError("服务端目录超过工作台可读取的大小。");
                output.Write(chunk, 0, read);
            }
            output.Position = 0;
            using var reader = new StreamReader(output, new UTF8Encoding(false, true));
            using var json = new JsonTextReader(reader) { MaxDepth = 24, DateParseHandling = DateParseHandling.None };
            var value = JObject.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (json.Read()) throw new InputError("服务端回复包含多余内容。");
            if ((bool?)value["ok"] == false)
                throw new InputError((bool?)value["conflict"] == true ? "服务端原文或译文已变化，整批未发布。请重新拉取原文。"
                    : (bool?)value["unauthorized"] == true ? "服务端管理员授权失败，请重新连接。"
                    : (bool?)value["rateLimited"] == true ? "服务端请求过于频繁，请稍后重试。"
                    : "服务端拒绝了这次操作，请核对原文、译文格式和服务端日志。");
            return value;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InputError("服务端没有及时回应。若正在发布，请先刷新目录核对结果。");
        }
        catch (HttpRequestException)
        {
            throw new InputError("无法连接服务端，请检查地址、端口和 HTTPS 证书。");
        }
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static HttpClient CreateClient(bool trustLocalCertificate)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        if (trustLocalCertificate)
            handler.ServerCertificateCustomValidationCallback = (request, certificate, _, _) =>
                LocalSptAdmin.IsLoopback(request.RequestUri) && certificate != null;
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = WebWorkbenchFiles.MaxSnapshotBytes };
    }
}
