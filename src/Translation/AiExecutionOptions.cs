using TranslationFoundation;
using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ConfigurationManagerZhCn
{
    internal sealed class AiExecutionOptions
    {
        internal string Adapter = "auto", Reasoning = "off";
        internal int MaxOutputTokens = 8192, MaxReportedTokens, MaxRequests = 100, RequestDelayMs = 250;
        internal bool JsonOutput = true;
        internal PaidApiBudget Budget = new PaidApiBudget();
        internal TranslationFoundation.TranslationGlossary Glossary;
        internal int GlossaryBudget;
        internal Action<TranslationResult> Checkpoint;
        internal string ProfileTag => new JArray(TranslationFoundation.TranslationRequestPolicy.Version,
            Adapter, Reasoning, MaxOutputTokens, JsonOutput).ToString(Formatting.None);
    }

}
