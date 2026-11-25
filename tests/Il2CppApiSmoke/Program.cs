using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ConfigurationManagerZhCn;
using Newtonsoft.Json.Linq;

typeof(BepInEx.Paths).GetProperty("PluginPath").SetValue(null, Path.Combine(Path.GetTempPath(), "cm-il2cpp-smoke-no-glossary"));
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var endpoint = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/translate";
var requests = new List<string>();
var responder = Task.Run(async () =>
{
    for (var i = 0; i < 3; i++)
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        var header = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one, 0, 1) == 1)
        {
            header.Add(one[0]);
            if (header.Count >= 4 && Encoding.ASCII.GetString(header.Skip(header.Count - 4).ToArray()) == "\r\n\r\n") break;
        }
        var headers = Encoding.ASCII.GetString(header.ToArray());
        var lengthHeader = headers.Split('\n').FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var length = lengthHeader == null ? 0 : int.Parse(lengthHeader.Split(':')[1].Trim());
        var bodyBytes = new byte[length];
        for (var offset = 0; offset < length;)
        {
            var read = await stream.ReadAsync(bodyBytes, offset, length - offset);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
        var body = Encoding.UTF8.GetString(bodyBytes);
        requests.Add(body);
        if (i == 2 && !headers.StartsWith("GET /translate/v1/models ")) throw new Exception("Model discovery must use GET /models");
        var response = i == 0
            ? "{\"choices\":[{\"message\":{\"content\":\"{\\\"t0001\\\":\\\"中文译文\\\"}\"}}]}"
            : i == 1 ? "{\"data\":{\"translations\":[{\"translatedText\":\"机器译文\"}]}}" : "{\"data\":[{\"id\":\"current-model\"}]}";
        var bytes = Encoding.UTF8.GetBytes(response);
        var reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: " + bytes.Length + "\r\n\r\n");
        await stream.WriteAsync(reply, 0, reply.Length);
        await stream.WriteAsync(bytes, 0, bytes.Length);
    }
});

var item = TranslationWorkItem.ForSetting("smoke.plugin", "Setting", "Description", "Travel to GroundZero", "Setting description");
var ai = await TranslationClients.TranslateAiAsync(new[] { item },
    new TranslationProviderDefinition { Name = "smoke", ApiKeyRequired = false },
    endpoint, "test-model", "", "zh", "", 1, 12000, 0, 10, null, CancellationToken.None,
    options:new AiExecutionOptions { Glossary=TranslationFoundation.TranslationGlossary.Parse("GroundZero\t中心区\nhideout\t藏身处"), RequestDelayMs=0 });
if (ai.Values[item.Id] != "中文译文") throw new Exception("IL2CPP AI translation failed");
var machine = await TranslationClients.TranslateMachineAsync(new[] { item },
    TranslationProviderCatalog.GetMachineProvider("Google Translate"), endpoint, "test-key", "", "", "en", "zh-CN", 1, 10, null, CancellationToken.None);
if (machine.Values[item.Id] != "机器译文") throw new Exception("IL2CPP machine translation failed");
var models = await TranslationClients.GetModelsAsync(new TranslationProviderDefinition { Name = "smoke" }, endpoint + "/v1/chat/completions", "", CancellationToken.None);
if (models.Length != 1 || models[0] != "current-model") throw new Exception("IL2CPP model catalog failed");
await responder;
listener.Stop();
if (requests.Count != 3 || requests[2] != "" || (string)JObject.Parse(requests[0])["model"] != "test-model" ||
    (string)JObject.Parse(requests[1])["format"] != "text")
    throw new Exception("Unexpected API request payload");
var glossaryPrompt = (string)JObject.Parse(requests[0])["messages"][0]["content"];
if (!glossaryPrompt.Contains("中心区") || glossaryPrompt.Contains("藏身处")) throw new Exception("IL2CPP glossary reference selection failed");
Console.WriteLine("PASS: IL2CPP-conditional AI, glossary, machine and model discovery requests, JSON parsing and result mapping.");
