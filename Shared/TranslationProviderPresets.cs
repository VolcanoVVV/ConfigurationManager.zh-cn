using System;

namespace TranslationFoundation
{
    internal sealed class ProviderPreset
    {
        internal string Id = "", Name = "", Endpoint = "", Model = "", TargetLanguage = "zh-Hans", Documentation = "", Website = "";
        internal bool KeyRequired = true, AppIdRequired, Anthropic, AdminOnly;
    }

    // One catalog shared by the F12 client and server workbench. Custom destinations remain admin-only.
    internal static class TranslationProviderPresets
    {
        internal static readonly ProviderPreset[] Ai = {
            A("doubao", "豆包 / 火山方舟", "https://ark.cn-beijing.volces.com/api/v3/chat/completions", "doubao-seed-2-0-lite-260215", "https://www.volcengine.com/docs/82379/1298454"),
            A("deepseek", "DeepSeek", "https://api.deepseek.com/chat/completions", "deepseek-flash", "https://api-docs.deepseek.com/zh-cn/"),
            A("kimi", "Kimi / Moonshot", "https://api.moonshot.cn/v1/chat/completions", "kimi-k2.6", "https://platform.moonshot.cn/docs/"),
            A("qwen", "通义千问 / 阿里云百炼", "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", "qwen3.8-flash", "https://help.aliyun.com/zh/model-studio/compatibility-of-openai-with-dashscope"),
            A("glm", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4/chat/completions", "glm-4.7-flash", "https://docs.bigmodel.cn/"),
            A("claude", "Claude / Anthropic", "https://api.anthropic.com/v1/chat/completions", "claude-haiku-4-5", "https://docs.anthropic.com/en/api/openai-sdk", true),
            A("openai", "GPT / OpenAI", "https://api.openai.com/v1/chat/completions", "gpt-5.6-luna", "https://platform.openai.com/docs/api-reference/chat"),
            A("gemini", "Gemini / Google", "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", "gemini-3.7-flash", "https://ai.google.dev/gemini-api/docs/openai"),
            new ProviderPreset { Id="ollama", Name="Ollama 本地模型", Endpoint="http://localhost:11434/v1/chat/completions", Model="qwen3:8b", Documentation="https://docs.ollama.com/openai", KeyRequired=false, AdminOnly=true },
            new ProviderPreset { Id="custom", Name="自定义兼容接口", Documentation="https://platform.openai.com/docs/api-reference/chat", KeyRequired=false, AdminOnly=true }
        };
        internal static readonly ProviderPreset[] Machine = {
            M("microsoft", "Microsoft Translator", "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0", "zh-Hans", "https://learn.microsoft.com/azure/ai-services/translator/text-translation/reference/v3/translate"),
            M("deepl", "DeepL", "https://api-free.deepl.com/v2/translate", "ZH-HANS", "https://developers.deepl.com/api-reference/translate"),
            new ProviderPreset { Id="libre", Name="LibreTranslate", Endpoint="http://localhost:5000/translate", TargetLanguage="zh", Documentation="https://docs.libretranslate.com/", KeyRequired=false, AdminOnly=true },
            new ProviderPreset { Id="baidu", Name="百度翻译", Endpoint="https://fanyi-api.baidu.com/api/trans/vip/translate", TargetLanguage="zh", Documentation="https://api.fanyi.baidu.com/doc/23", Website="https://fanyi.baidu.com/", AppIdRequired=true },
            new ProviderPreset { Id="youdao", Name="有道翻译", Endpoint="https://openapi.youdao.com/api", TargetLanguage="zh-CHS", Documentation="https://ai.youdao.com/DOCSIRMA/html/trans/api/wbfy/index.html", Website="https://fanyi.youdao.com/", AppIdRequired=true },
            new ProviderPreset { Id="google", Name="Google Translate", Endpoint="https://translation.googleapis.com/language/translate/v2", TargetLanguage="zh-CN", Documentation="https://docs.cloud.google.com/translate/docs/reference/rest/v2/translate", Website="https://translate.google.com/" }
        };
        private static ProviderPreset A(string id, string name, string endpoint, string model, string docs, bool anthropic=false) =>
            new ProviderPreset { Id=id, Name=name, Endpoint=endpoint, Model=model, Documentation=docs, Anthropic=anthropic };
        private static ProviderPreset M(string id, string name, string endpoint, string language, string docs) =>
            new ProviderPreset { Id=id, Name=name, Endpoint=endpoint, TargetLanguage=language, Documentation=docs };
    }
}
