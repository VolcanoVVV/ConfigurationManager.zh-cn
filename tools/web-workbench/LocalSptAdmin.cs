using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using ConfigurationManagerZhCn;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class LocalSptAdmin
{
    internal static bool IsLoopback(Uri address) =>
        IPAddress.TryParse(address.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);

    internal static string ReadKey(Uri address)
    {
        if (!OperatingSystem.IsWindows() || !IsLoopback(address)) return null;
        try
        {
            var processId = ListeningProcess(IPAddress.Parse(address.Host.Trim('[', ']')), address.Port);
            if (processId == 0) return null;
            using var process = Process.GetProcessById(processId);
            var image = process.MainModule?.FileName;
            if (!string.Equals(Path.GetFileName(image), "SPT.Server.exe", StringComparison.OrdinalIgnoreCase))
                return null;
            var mods = Path.Combine(Path.GetDirectoryName(image), "user", "mods");
            if (!Directory.Exists(mods)) return null;
            var candidates = Directory.EnumerateDirectories(mods)
                .Where(path => File.Exists(Path.Combine(path, "SimpleTranslatorCS.dll")))
                .Select(path => Path.Combine(path, "workbench-admin.jsonc"))
                .Where(File.Exists).Take(2).ToArray();
            if (candidates.Length != 1) return null;
            var keyPath = candidates[0];
            WebWorkbenchFiles.RequireUnlinkedPath(keyPath);
            using var input = new FileStream(keyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 8192) return null;
            using var reader = new StreamReader(input);
            using var json = new JsonTextReader(reader) { MaxDepth = 8 };
            var key = ((string)JObject.Load(json, new JsonLoadSettings
                { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error })["adminKey"])?.Trim();
            return key is { Length: > 0 and <= 256 } ? key : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or ArgumentException or InvalidOperationException or JsonException)
        {
            return null;
        }
    }

    private static int ListeningProcess(IPAddress address, int port)
    {
        // The owner PID ties the credential to the SPT installation actually listening on this address.
        bool v6 = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        int family = v6 ? 23 : 2; // Windows AF_INET6 / AF_INET
        const int listenersWithPid = 3;
        const int insufficientBuffer = 122;
        uint size = 0;
        if (GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, listenersWithPid, 0) != insufficientBuffer)
            return 0;
        for (var attempt = 0; attempt < 3 && size is >= 4 and <= 8 * 1024 * 1024; attempt++)
        {
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, family, listenersWithPid, 0);
                if (result == insufficientBuffer) continue;
                if (result != 0) return 0;
                var count = Marshal.ReadInt32(buffer);
                var stride = v6 ? 56 : 24;
                if (count < 0 || count > (size - 4) / stride) return 0;
                var owner = 0;
                var bytes = new byte[v6 ? 16 : 4];
                for (var index = 0; index < count; index++)
                {
                    var row = IntPtr.Add(buffer, 4 + index * stride);
                    var offset = v6 ? 20 : 8;
                    if ((Marshal.ReadByte(row, offset) << 8 | Marshal.ReadByte(row, offset + 1)) != port) continue;
                    Marshal.Copy(IntPtr.Add(row, v6 ? 0 : 4), bytes, 0, bytes.Length);
                    var local = new IPAddress(bytes);
                    if (!local.Equals(address) && !local.Equals(v6 ? IPAddress.IPv6Any : IPAddress.Any)) continue;
                    var pid = Marshal.ReadInt32(row, v6 ? 52 : 20);
                    if (owner != 0 && owner != pid) return 0;
                    owner = pid;
                }
                return owner;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return 0;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool ordered, int family, int tableClass, uint reserved);
}
