using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TranslationFoundation
{
    internal static class BoundedHttpContent
    {
        internal static async Task<string> ReadAsync(HttpContent content, CancellationToken token)
        {
            const int max = 4 * 1024 * 1024;
            if (content.Headers.ContentLength > max) throw new InvalidDataException("翻译接口响应超过 4 MiB，已停止读取。");
            using (var input = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192];
                int count;
                while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + count > max) throw new InvalidDataException("翻译接口响应超过 4 MiB，已停止读取。");
                    output.Write(buffer, 0, count);
                }
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }
    }
}
