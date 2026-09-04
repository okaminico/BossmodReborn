namespace BossMod;

/// <summary>
/// 引導型（channeled）技能期間的移動封鎖。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼既有的機制蓋不到這些技能</b>：<c>ActionManagerEx</c> 原本的封鎖完全掛在
/// <c>MoveMightInterruptCast</c> 上，而那個旗標每幀都會被 <c>&amp;= CastTimeRemaining &gt; 0</c> 清掉，
/// 設定它的兩處也都要求 <c>CastTimeRemaining &gt; 0</c>。引導技全部是<b>瞬發</b>（詠唱時間 0），
/// 所以那條路徑對它們永遠不成立 —— 不是漏判，是機制上完全不相干的兩件事。
/// 引導技是「動了就中斷」而不是「詠唱中」，唯一的進行中訊號是自身身上的那個狀態。
/// </para>
/// <para>
/// 🔑 <b>名單怎麼來的</b>：不是憑印象列的，是從台服 7.20 的 <c>ActionTransient.Description</c>
/// 掃「效果時間內發動技能或進行移動、轉身都會立即解除／中斷／消失」這句話枚舉出來的
/// （錨字串「進行移動」）。全表 46501 筆描述裡命中 13 筆，扣掉 5 筆非玩家技能（NPC／副本技變體）
/// 剩下這 8 支。負對照＝天地人（AID 7403）<b>沒有</b>命中：它現在是「無法發動忍術之外的技能」，
/// 不是移動中斷，照印象列就會多封鎖一支。
/// 另外對「移動 ＋（解除|中斷|消失）但沒有錨字串」再掃一次確認沒有漏掉別的措辭，
/// 那 13 筆全是移動速度增減或完全無法移動，沒有引導技。
/// </para>
/// <para>
/// 🔴 <b>安全設計：最壞情況必須是「封鎖沒生效」，不能是「莫名其妙不能動」。</b>
/// 兩道獨立的閘門都要成立才會封鎖：
/// ①<b>本機玩家自己按過</b>那一支引導技（<see cref="RecordRequest"/> 只在
/// <c>ActionManagerEx.HandleActionRequest</c> 裡被呼叫，那是玩家自己送出的請求）；
/// ②該狀態現在還掛在身上，<b>而且來源是玩家自己</b>（<c>FindStatus(sid, player.InstanceID)</c>）。
/// 少了①，站在別人的武裝戍衛／命運之輪範圍裡的隊員會被一起凍住 —— 那兩支的範圍效果
/// 會讓範圍內的隊員也拿到狀態，純狀態偵測是真的會誤傷旁人的。
/// 這兩道閘門任何一道判錯的方向都是「不封鎖」，不會變成「卡死」。
/// </para>
/// <para>
/// ⚠️ 使用者的「按住某鍵允許移動」逃生鍵照樣有效：封鎖最後是寫進
/// <c>MovementOverride.MovementBlocked</c>，而它的 getter 是 <c>field &amp;&amp; !IsForceUnblocked()</c>。
/// </para>
/// </remarks>
public sealed class ChanneledMovementTweak
{
    /// <summary>
    /// 引導技的技能 id → 「還在引導中」的自身狀態 id。
    /// </summary>
    /// <remarks>
    /// 📌 每一列的狀態 id 都經過兩份互相獨立的來源交叉確認：台服 7.20 的 <c>Status.csv</c> 名稱查表，
    /// 以及本 repo <c>ActionQueue/</c> 底下各職業檔裡本來就有的 <c>SID</c> 列舉
    /// （火焰噴射器／默想／即興表演／玄結界／鬼宿腳 五支兩邊都對得上）。
    /// ⚠️ 命運之輪（848）、武裝戍衛（1175）、默示錄（3644）三支我方樹的 <c>SID</c> 列舉裡沒有，
    /// 只有 EXD 名稱查表這一份來源；其中「命運之輪」在 Status 表裡有三筆同名列（847/848/2283），
    /// 這裡取效果描述吻合的 848。<b>選錯的後果是這一支的封鎖不生效</b>（狀態查不到 ⇒ 立刻解除封鎖），
    /// 不會變成封鎖不放，所以刻意取窄的那一個而不是三個都列。
    /// </remarks>
    private static readonly Dictionary<uint, uint> _channels = new()
    {
        [3613] = 848,   // AST 命運之輪 Collective Unconscious
        [7385] = 1175,  // PLD 武裝戍衛 Passage of Arms
        [7418] = 1205,  // MCH 火焰噴射器 Flamethrower
        [7497] = 1231,  // SAM 默想 Meditate
        [16014] = 1827, // DNC 即興表演 Improvisation
        [23273] = 2496, // BLU 玄結界 Chelonian Gate
        [23288] = 2502, // BLU 鬼宿腳 Phantom Flurry
        [34581] = 3644, // BLU 默示錄 Apokalypsis
    };

    /// <summary>
    /// 從送出請求到狀態真的掛上身之間的寬限秒數（伺服器往返）。
    /// </summary>
    /// <remarks>
    /// 這段期間狀態還查不到，但引導其實已經開始，這時放行移動就等於這個功能對「按下去的頭一瞬間」
    /// 完全沒用 —— 而那正是最容易手滑走掉的時候。
    /// 🔴 上限刻意壓在 1 秒：如果伺服器根本拒絕了這一發（技能沒發出去、狀態永遠不會來），
    /// 最多也只會多封鎖 1 秒就自動放開。
    /// </remarks>
    private const float PendingGraceSeconds = 1f;

    private uint _statusId; // 0 = 目前沒有在追蹤任何引導
    private uint _actionId; // 只給診斷 log 用
    private DateTime _requestedAt;
    private bool _statusSeen;
    private bool _pendingReported;

    /// <summary>
    /// 玩家送出一次技能請求。引導技就開始追蹤，其他技能就停止追蹤。
    /// </summary>
    /// <remarks>
    /// 🔑 「按了別的技能就停止追蹤」不是猜的：這 8 支的技能說明逐字寫著
    /// 「效果時間內<b>發動技能</b>或進行移動、轉身都會立即解除」——
    /// 也就是說任何後續技能請求本身就代表引導已經結束。
    /// </remarks>
    public void RecordRequest(ActionID action)
    {
        if (action.Type == ActionType.Spell && _channels.TryGetValue(action.ID, out var sid))
        {
            _statusId = sid;
            _actionId = action.ID;
            _statusSeen = false;
            _pendingReported = false;
            _requestedAt = default; // 由 Update 用 WorldState 時間填，避免在這裡引進第二個時間來源
        }
        else
        {
            Reset();
        }
    }

    public void Reset()
    {
        _statusId = 0;
        _actionId = 0;
        _statusSeen = false;
        _pendingReported = false;
        _requestedAt = default;
    }

    /// <summary>本幀是否正在引導（＝要不要封鎖移動）。每幀都要呼叫，狀態機才不會留下殘值。</summary>
    /// <param name="player">本機玩家；null 一律停止追蹤。</param>
    /// <param name="now"><c>WorldState</c> 的當前時間（不要傳 <c>DateTime.Now</c>，那和狀態的時間基準不同）。</param>
    public bool Update(Actor? player, DateTime now)
    {
        if (_statusId == 0)
            return false;

        if (player == null || player.IsDeadOrDestroyed)
        {
            Reset();
            return false;
        }

        if (_requestedAt == default)
            _requestedAt = now;

        // 🔴 一定要帶 source：範圍型的引導（武裝戍衛、命運之輪）會把同名狀態也掛到範圍內的隊員身上，
        //    不比對來源的話，站在別人的範圍裡就會被判成「我在引導」。
        if (player.FindStatus(_statusId, player.InstanceID) != null)
        {
            if (!_statusSeen)
            {
                _statusSeen = true;
                // 使用者跑 LogLevel 1，要他看得到才有意義；這一行是「封鎖真的接上了」的唯一離線證據。
                Service.Logger.Information($"[Channel] 引導技 {_actionId} 的狀態 {_statusId} 已確認在身上，移動封鎖生效。");
            }
            return true;
        }

        // 狀態還沒到（伺服器往返）⇒ 短暫寬限
        if (!_statusSeen && now < _requestedAt.AddSeconds(PendingGraceSeconds))
            return true;

        if (!_statusSeen && !_pendingReported)
        {
            _pendingReported = true;
            // 這一行代表「狀態 id 或來源比對其中一項對不上」——功能靜默失效時唯一看得出來的地方。
            Service.Logger.Information($"[Channel] 引導技 {_actionId} 送出後 {PendingGraceSeconds}s 內沒等到自身狀態 {_statusId}，不封鎖移動。");
        }

        Reset();
        return false;
    }
}
