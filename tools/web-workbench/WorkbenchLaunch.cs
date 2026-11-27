using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ConfigurationManagerZhCn;

internal static class WorkbenchLaunch
{
    internal static int[] Ports(int preferred) => new[] { preferred }.Concat(Enumerable.Range(17840, 8)).Distinct().ToArray();

    internal static string BuildId(string staticRoot)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(typeof(WorkbenchLaunch).Module.ModuleVersionId.ToByteArray());
        foreach (var file in new[] { "index.html", "workbench.css", "workbench.js" })
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(Path.Combine(staticRoot, file))));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static string Origin(int port) => "http://127.0.0.1:" + port;

    internal static FileStream TryLockProfile(string directory)
    {
        var path = Path.Combine(directory, "host.lock");
        WebWorkbenchFiles.RequireUnlinkedPath(path);
        Directory.CreateDirectory(directory);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when (OperatingSystem.IsWindows() && (ex.HResult & 0xffff) is 32 or 33) { return null; }
    }

    internal static async Task<bool> HasRetiredHostAsync(int port, string profile)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
            { Timeout = TimeSpan.FromMilliseconds(550) };
        var probes = new[] { port, 17830, 17832, 17833 }.Distinct().Select(async endpoint =>
        {
            try
            {
                using var response = await client.GetAsync(Origin(endpoint) + "/ready/v6/" + profile, HttpCompletionOption.ResponseHeadersRead);
                return response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.ServiceUnavailable;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { return false; }
        });
        return (await Task.WhenAll(probes)).Any(active => active);
    }

    internal static async Task<int> FindRunningAsync(IEnumerable<int> ports, string readyRoute, string build)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
            { Timeout = TimeSpan.FromMilliseconds(550) };
        var probes = ports.Select(async port =>
        {
            try
            {
                using var response = await client.GetAsync(Origin(port) + readyRoute, HttpCompletionOption.ResponseHeadersRead);
                return response.StatusCode == HttpStatusCode.NoContent &&
                    response.Headers.TryGetValues("X-Workbench-Build", out var values) && values.Contains(build, StringComparer.Ordinal)
                    ? port : 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { return 0; }
        });
        return (await Task.WhenAll(probes)).FirstOrDefault(port => port != 0);
    }

    internal static int FindFree(IEnumerable<int> ports)
    {
        foreach (var port in ports)
        {
            var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            try { listener.Start(); return port; }
            catch (SocketException) { }
            finally { listener.Stop(); }
        }
        return 0;
    }
}
