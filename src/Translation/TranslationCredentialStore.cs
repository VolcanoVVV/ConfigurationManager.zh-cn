using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ConfigurationManagerZhCn
{
    internal sealed class TranslationCredentialSnapshot
    {
        internal readonly Dictionary<string, string> AiKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> MachineKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> MachineAppIds = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    internal static class TranslationCredentialStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("volcanofficial.configuration-workbench.credentials.v1");
        private static ManualLogSource _log;
        private static string _path;

        internal static string FilePath { get { return _path; } }
        internal static string LastError { get; private set; }

        internal static void Initialize(ManualLogSource log, string rootOverride = null)
        {
            _log = log;
            _path = Path.Combine(string.IsNullOrWhiteSpace(rootOverride) ? Paths.ConfigPath : rootOverride,
                "ConfigurationManager.zh-cn.credentials.bin");
            LastError = null;
        }

        internal static bool TryLoad(out TranslationCredentialSnapshot snapshot)
        {
            snapshot = new TranslationCredentialSnapshot();
            LastError = null;
            if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path)) return true;
            try
            {
                var encrypted = File.ReadAllBytes(_path);
                var clear = Unprotect(encrypted);
                try
                {
                    var root = JObject.Parse(Encoding.UTF8.GetString(clear));
                    ReadMap(root["AiKeys"] as JObject, snapshot.AiKeys);
                    ReadMap(root["MachineKeys"] as JObject, snapshot.MachineKeys);
                    ReadMap(root["MachineAppIds"] as JObject, snapshot.MachineAppIds);
                }
                finally { Array.Clear(clear, 0, clear.Length); }
                return true;
            }
            catch (Exception ex)
            {
                LastError = "API 凭据无法解密；它可能来自另一个 Windows 账户或文件已经损坏。";
                if (_log != null) _log.LogWarning(LastError + " " + ex.GetType().Name);
                return false;
            }
        }

        internal static bool TrySave(IDictionary<string, string> aiKeys, IDictionary<string, string> machineKeys,
            IDictionary<string, string> machineAppIds)
        {
            LastError = null;
            try
            {
                if (string.IsNullOrWhiteSpace(_path)) throw new InvalidOperationException("凭据存储尚未初始化。");
                var root = new JObject
                {
                    ["SchemaVersion"] = 1,
                    ["AiKeys"] = WriteMap(aiKeys),
                    ["MachineKeys"] = WriteMap(machineKeys),
                    ["MachineAppIds"] = WriteMap(machineAppIds)
                };
                var clear = Encoding.UTF8.GetBytes(root.ToString(Formatting.None));
                byte[] encrypted;
                try { encrypted = Protect(clear); }
                finally { Array.Clear(clear, 0, clear.Length); }
                var directory = Path.GetDirectoryName(_path);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                var temporary = _path + ".tmp";
                File.WriteAllBytes(temporary, encrypted);
                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
                return true;
            }
            catch (Exception ex)
            {
                try { if (!string.IsNullOrWhiteSpace(_path) && File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp"); }
                catch { }
                LastError = "API 凭据加密保存失败，本次输入仍只在内存中有效（" + ex.GetType().Name + "：" + ex.Message + "）。";
                if (_log != null) _log.LogWarning(LastError + " " + ex.GetType().Name);
                return false;
            }
        }

        internal static bool TryClear()
        {
            LastError = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(_path) && File.Exists(_path)) File.Delete(_path);
                return true;
            }
            catch (Exception ex)
            {
                LastError = "无法删除已保存的 API 凭据文件。";
                if (_log != null) _log.LogWarning(LastError + " " + ex.GetType().Name);
                return false;
            }
        }

        private static JObject WriteMap(IDictionary<string, string> values)
        {
            var result = new JObject();
            if (values == null) return result;
            foreach (var pair in values)
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrEmpty(pair.Value)) result[pair.Key] = pair.Value;
            return result;
        }

        private static void ReadMap(JObject source, IDictionary<string, string> target)
        {
            if (source == null) return;
            foreach (var property in source.Properties())
                if (property.Value.Type == JTokenType.String) target[property.Name] = (string)property.Value;
        }

        private static byte[] Protect(byte[] clear)
        {
            return Crypt(clear, true);
        }

        private static byte[] Unprotect(byte[] encrypted)
        {
            return Crypt(encrypted, false);
        }

        private static byte[] Crypt(byte[] input, bool protect)
        {
            var inputBlob = CreateBlob(input);
            var entropyBlob = CreateBlob(Entropy);
            DataBlob outputBlob;
            try
            {
                var succeeded = protect
                    ? CryptProtectData(ref inputBlob, string.Empty, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out outputBlob)
                    : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out outputBlob);
                if (!succeeded) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    var output = new byte[outputBlob.Size];
                    Marshal.Copy(outputBlob.Data, output, 0, output.Length);
                    return output;
                }
                finally { if (outputBlob.Data != IntPtr.Zero) LocalFree(outputBlob.Data); }
            }
            finally
            {
                if (inputBlob.Data != IntPtr.Zero) Marshal.FreeHGlobal(inputBlob.Data);
                if (entropyBlob.Data != IntPtr.Zero) Marshal.FreeHGlobal(entropyBlob.Data);
            }
        }

        private static DataBlob CreateBlob(byte[] value)
        {
            var blob = new DataBlob { Size = value != null ? value.Length : 0, Data = IntPtr.Zero };
            if (blob.Size == 0) return blob;
            blob.Data = Marshal.AllocHGlobal(blob.Size);
            Marshal.Copy(value, 0, blob.Data, blob.Size);
            return blob;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            internal int Size;
            internal IntPtr Data;
        }

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CryptProtectData(ref DataBlob dataIn, string description, ref DataBlob optionalEntropy,
            IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob optionalEntropy,
            IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
