using System;
using System.Text;

namespace TranslationFoundation
{
    internal sealed class PaidApiBudget
    {
        internal const int DefaultTokens = 120000;
        internal const int DefaultRequests = 100;
        private const int FailureLimit = 4;
        private int _requests;
        private long _tokens;
        private int _failures;
        internal int Requests => _requests;
        internal long Tokens => _tokens;
        internal Action Changed = delegate { };

        internal PaidApiBudget(int requests = 0, long tokens = 0)
        {
            _requests = Math.Max(0, requests);
            _tokens = Math.Max(0, tokens);
        }

        internal long Reserve(int maxRequests, int maxTokens, int maxOutputTokens, string payload)
        {
            if (_failures >= FailureLimit)
                throw new PaidApiLimitException("连续 4 次接口失败，本任务已停止发送请求。");
            if (_requests >= (maxRequests > 0 ? maxRequests : DefaultRequests))
                throw new PaidApiLimitException("已达到任务请求次数上限，已保存结果保留。");
            var reserved = (long)Encoding.UTF8.GetByteCount(payload) + 256 + Math.Max(256, Math.Min(32768, maxOutputTokens));
            var limit = maxTokens > 0 ? maxTokens : DefaultTokens;
            if (_tokens >= limit || reserved > limit - _tokens)
                throw new PaidApiLimitException("剩余 token 预算不足以发送本批请求，请缩小批次或调整任务额度。");
            _requests++;
            _tokens += reserved;
            Changed();
            return reserved;
        }

        internal void Complete(long reserved, long? input, long? output, bool success)
        {
            if (input.HasValue && output.HasValue)
            {
                var actual = Math.Max(0, Math.Min(int.MaxValue, input.Value)) + Math.Max(0, Math.Min(int.MaxValue, output.Value));
                _tokens += actual - reserved;
            }
            _failures = success ? 0 : _failures + 1;
            Changed();
        }
    }
    internal sealed class PaidApiLimitException : InvalidOperationException
    { internal PaidApiLimitException(string message) : base(message) { } }
}
