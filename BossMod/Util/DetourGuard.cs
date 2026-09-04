using System.Threading;

namespace BossMod;

// detour 是「原生程式碼直接呼叫的受管理函式」。受管理例外從 detour 逸出時會穿過沒有 SEH handler 的
// 原生呼叫框架，行程極可能直接被終止（而且不會留下有用的堆疊）。
// 所以每一支 detour 的自訂邏輯都要包在 try 裡，catch 之後**照樣呼叫 Original 並回傳它的結果**，
// 讓遊戲原本的行為完全不受我們的失敗影響（fail-closed）。
//
// ⚠️ 這**攔不到 AccessViolationException** —— 在 .NET Core 那是 corrupted-state exception，
//    try/catch 與任何受管理層的包裝都無效。這裡防的是受管理例外：
//    NullReference、IndexOutOfRange、KeyNotFound、Overflow、InvalidOperation 等。
//
// 📌 try/catch 在不擲例外時近乎零成本（只多一份 EH 表，不產生執行期指令），
//    所以封包這類熱路徑也可以放心包。真正要避免的是在 catch **以外**做額外工作。
public static class DetourGuard
{
    // 同一個 detour 最多每 60 秒印一行。封包 detour 一旦開始持續擲例外，
    // 不節流會在幾秒內把 log 灌爆、反而讓使用者回報不出有用的東西。
    private const long ThrottleMillis = 60_000;

    private sealed class SiteState
    {
        public long LastReportMs;
        public int Total;
    }

    private static readonly ConcurrentDictionary<string, SiteState> _sites = [];

    /// <summary>
    /// 在 detour 的 catch 區塊裡呼叫。節流地記一行 Information 級診斷（使用者跑 LogLevel 1，
    /// 盲區只有 Verbose,Debug 收得到但單檔數十萬行會淹沒），然後靜靜返回 —— 呼叫端必須繼續呼叫 Original。
    /// </summary>
    /// <param name="site">出事的 detour 名稱，用 nameof() 傳。</param>
    /// <param name="ex">攔下來的受管理例外。</param>
    // NoInlining：把字串格式化與字典操作留在這裡，detour 本體的程式碼才不會被撐大。
    // 格式化只有真的 catch 到時才發生。
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Report(string site, Exception ex)
    {
        try
        {
            var state = _sites.GetOrAdd(site, static _ => new SiteState());
            var total = Interlocked.Increment(ref state.Total);

            var now = Environment.TickCount64;
            var last = Volatile.Read(ref state.LastReportMs);
            if (last != 0L && now - last < ThrottleMillis)
                return; // 節流窗內：只累計次數，不印
            Volatile.Write(ref state.LastReportMs, now);

            Service.Logger.Information($"[BMR][detour-guard] {site} 擲出未處理的受管理例外（累計 {total} 次，已吞下，Original 照常執行）: {ex}");
        }
        catch
        {
            // 記錄本身絕不能把例外送回原生框架 —— 這個 catch 是刻意留白的。
        }
    }
}
