// Portable C# 7.3 source. Vendored in both independently buildable mods; Verify-Matrix checks parity.
using System;
using System.Collections.Generic;

namespace TranslationFoundation
{
    internal static class TranslationRequestPolicy
    {
        internal const string Version = "translation-policy-v1";
        internal static string Adapter(string endpoint, string configured = "auto")
        {
            if (!string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase)) return configured;
            #pragma warning disable CS8600
            Uri uri;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out uri)) return "generic";
#pragma warning restore CS8600
            if (uri.Host == "api.deepseek.com") return "deepseek";
            if (uri.Host == "api.openai.com") return "openai";
            if (uri.Host == "dashscope.aliyuncs.com" || uri.Host == "dashscope-intl.aliyuncs.com") return "qwen";
            if (uri.Host == "generativelanguage.googleapis.com") return "gemini";
            return "generic";
        }

        internal static Dictionary<string, object> Parameters(string endpoint, string model, string adapter,
            string reasoning, int maxOutputTokens, bool jsonOutput)
        {
            var result = new Dictionary<string, object>();
            adapter = Adapter(endpoint, adapter);
            var mode = (reasoning ?? "default").ToLowerInvariant();
            var modernOpenAi = adapter == "openai" && (model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase) || model.StartsWith("o", StringComparison.OrdinalIgnoreCase));
            if (maxOutputTokens > 0) result[modernOpenAi ? "max_completion_tokens" : "max_tokens"] = Math.Max(256, Math.Min(32768, maxOutputTokens));
            if (jsonOutput) result["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" };
            if (mode == "default") return result;
            if (mode != "off" && mode != "low" && mode != "medium" && mode != "high")
                throw new ArgumentException("思考强度必须是 default、off、low、medium 或 high。");
            if (adapter == "deepseek")
            {
                result["thinking"] = new Dictionary<string, object> { ["type"] = mode == "off" ? "disabled" : "enabled" };
                if (mode != "off") result["reasoning_effort"] = mode == "medium" ? "high" : mode;
            }
            else if (adapter == "qwen") result["enable_thinking"] = mode != "off";
            else if (adapter == "gemini") result["reasoning_effort"] = mode == "off" ? "low" : mode;
            else if (modernOpenAi) result["reasoning_effort"] = mode == "off"
                ? (model.StartsWith("o", StringComparison.OrdinalIgnoreCase) || model == "gpt-5" || model.StartsWith("gpt-5-", StringComparison.OrdinalIgnoreCase) ? "low" : "none") : mode;
            // Unknown compatible gateways receive no guessed vendor-specific reasoning parameters.
            return result;
        }

        internal static bool IsEffective(string source, string result)
        {
            return !string.IsNullOrWhiteSpace(result) && !string.Equals((source ?? "").Trim(), result.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}

