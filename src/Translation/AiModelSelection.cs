using System;
using System.Threading.Tasks;

namespace ConfigurationManagerZhCn
{
    internal sealed class AiModelSelection
    {
        private string _context;
        private DateTime _stableSince, _next;
        internal volatile bool Busy;
        internal volatile string[] Models = new string[0];
        internal volatile string Status = "填写密钥后自动获取模型，始终可手动输入。";
        internal bool Expanded;
        internal string Filter = "";
        internal string[] VisibleModels = new string[0];
        internal int VisibleTotal;

        internal void Ensure(string context, bool ready, bool automatic, Func<bool, Task<string[]>> fetch)
        {
            if (_context != context)
            {
                _context = context; _stableSince = DateTime.UtcNow; _next = DateTime.MinValue;
                Models = new string[0]; Status = "模型列表待获取，手动输入始终可用。";
            }
            if (ready && automatic && !Busy && DateTime.UtcNow >= _next && DateTime.UtcNow.Subtract(_stableSince).TotalSeconds >= 2)
                Fetch(fetch, false);
        }

        internal async void Fetch(Func<bool, Task<string[]>> fetch, bool refresh)
        {
            if (Busy) return;
            var context = _context;
            Busy = true; Status = "正在获取模型列表…";
            try
            {
                var models = await fetch(refresh).ConfigureAwait(false);
                if (_context != context) return;
                Models = models; _next = DateTime.UtcNow.AddMinutes(10);
                Status = "已获取 " + models.Length + " 个模型；模型类型和可用权限请以接口为准。";
            }
            catch (Exception ex)
            {
                if (_context != context) return;
                _next = DateTime.UtcNow.AddMinutes(1);
                Status = ex is InvalidOperationException ? ex.Message : "模型获取失败（" + ex.GetType().Name + "），可手动输入。";
                TranslationDiagnostics.Operation("模型查询", Status, true);
            }
            finally { Busy = false; }
        }
    }
}
