using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace ConfigurationManagerZhCn
{
    // Keep all errors and other RequestHandler traffic. Do not change global log levels.
    [HarmonyPatch]
    internal static class LiveSyncLogFilter
    {
        private const string Route = "/simpletranslator/v1/workbench/live";
        private static MethodBase TargetMethod() => typeof(ManualLogSource).GetMethod("Log",
            new[] { typeof(LogLevel), typeof(object) });
        internal static bool ShouldHide(string source, LogLevel level, object data, bool enabled)
        {
            if (!enabled || source != "RequestHandler" || level != LogLevel.Info) return false;
            var text = data as string;
            return text == "[REQUEST]: " + Route || text == "[REQUEST SUCCESSFUL] " + Route;
        }
        private static bool Prefix(ManualLogSource __instance, LogLevel __0, object __1) =>
            !ShouldHide(__instance.SourceName, __0, __1,
                ConfigurationManagerZhCnPlugin.QuietServerLiveSyncLogs != null &&
                ConfigurationManagerZhCnPlugin.QuietServerLiveSyncLogs.Value);
    }
}
