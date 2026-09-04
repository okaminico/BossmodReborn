namespace BossMod;

/// <summary>
/// 「位移技危險攔截」：在 <c>ActionManager::UseAction</c> 這一關把「會把自己送進危險區」的位移技吞掉。
/// </summary>
/// <remarks>
/// 🔑 存在的理由是**既有的位移安全檢查只在「東西進得了 BMR 的佇列」時才生效**：BMR 本來就有一整組
/// 落點判定式（<see cref="ActionDefinitions.DashToTargetCheck"/> 那四條，掛在
/// <c>ActionDefinition.ForbidExecute</c> 上），但它們只在 <c>ActionQueue.FindBest</c> 裡被呼叫。
/// <para>
/// ⚠️ **「外部外掛直接呼叫 <c>ActionManager::UseAction</c> 就整條佇列繞過去了」這句話是錯的**（曾經寫在這裡）。
/// 我們的 <c>ActionManagerEx.UseActionDetour</c> 掛的就是 <c>ActionManager::UseAction</c> 本身，
/// 所以 WrathCombo 這類外掛的呼叫**一樣會經過這一關**；而且當
/// <see cref="ActionTweaksConfig.UseManualQueue"/> 開著時，那一發會被 <c>ManualActionQueueTweak.Push</c>
/// 收進 BMR 自己的佇列 —— 於是它**照樣吃得到** <c>ForbidExecute</c> 的落點判定，不需要這個 tweak。
/// </para>
/// <para>
/// 🔑 因此這個 tweak **不可替代的場合**是「那一發最後沒進 BMR 的佇列、直接落回遊戲原生路徑」的時候，
/// 逐條列出來就是 <c>UseActionDetour</c> 裡 <c>queued</c> 為 false 的那些分支：
/// <list type="bullet">
/// <item><see cref="ActionTweaksConfig.UseManualQueue"/> 關著（預設值就是關著 ⇒ <b>多數使用者的常態</b>）</item>
/// <item><c>mode != UseActionMode.None</c>（地面放置技的預覽/確定那兩段）或動作類型不是 Spell／Item</item>
/// <item><c>Push</c> 自己判掉：<c>ActionDefinitions</c> 沒登記這個動作、冷卻剩餘超過佇列視窗（GCD 1s／oGCD 3s）、
/// 或目標解析失敗 —— 這幾種都是 <c>return false</c> 交還原生佇列</item>
/// <item><c>UseActionDetour</c> 的 <c>try</c> 擲例外走進退化路徑（那裡把 <c>queued</c> 強制設回 false）</item>
/// </list>
/// ⇒ 這個 tweak 補的是**上面這些落回原生路徑的發數**，不是「外部外掛完全繞過 BMR」。
/// </para>
/// <para>
/// 📌 順序上這一關**排在** <c>Push</c> 之前（<c>queued = !dashBlocked &amp;&amp; …</c>），
/// 所以兩條路徑不會重複攔截：被這裡吞掉的那一發根本不會進佇列。
/// </para>
/// <para>
/// 🔴 落點算法**不另建一張表**：直接沿用上面那四條判定式登記的分類與距離
/// （<see cref="ActionDefinitions.TryGetDashGeometry"/>）。兩份表遲早會漂移，而漂移的失敗形式是靜默的。
/// </para>
/// <para>
/// 🔴 執行緒約定：<see cref="Update"/> 只在 Dalamud 的 Draw 回呼（<c>Plugin.DrawUI</c>）裡跑，
/// 把該幀的 <see cref="AIHints.ForbiddenZones"/> 複製成一份**唯讀陣列**後整份換上；
/// <see cref="ShouldBlock"/> 只讀那份快照（開頭一次讀進區域變數），**從頭到尾不碰活的 AIHints**。
/// UseAction 實務上就在主執行緒（遊戲的熱鍵處理、WrathCombo 的 framework tick），
/// 但這樣寫的話「萬一不是」也只會讀到上一幀的**完整**資料，不會撞上 <c>ForbiddenZones.Clear()</c>
/// 進行到一半的中間狀態。⚠️ 這裡不宣稱 shapeDistance 閉包本身跨執行緒安全 ——
/// 它們絕大多數只捕捉數值，少數捕捉 <c>Actor</c> 也只是讀 <c>PosRot</c> 欄位。
/// </para>
/// <para>
/// ⚠️ 這一關**不做**既有 <see cref="ActionDefinitions.IsDashDangerous"/> 的競技場邊界檢查。
/// 那個檢查用的是 <c>hints.PathfindMapBounds</c>，而野外沒有障礙圖時它是「以玩家為中心的 30 碼方形」
/// （<c>AIHintsBuilder.CalculateAutoHints</c> 的 else 分支），任何 20 碼突進都會被判成出界 ——
/// 對「只擋真的危險」這個目的來說那是誤攔。這裡只看 <c>ForbiddenZones</c>。
/// </para>
/// </remarks>
public sealed class DashInterceptTweak
{
    /// <summary>某一幀的唯讀快照。整份物件建好之後不再改，發佈只靠一次欄位指派。</summary>
    private sealed class FrameSnapshot(
        (Func<WPos, float> Shape, DateTime Activation)[] zones, DateTime now, bool aiActive, bool escapeHatchHeld, float threshold)
    {
        public readonly (Func<WPos, float> Shape, DateTime Activation)[] Zones = zones;
        public readonly DateTime Now = now;
        public readonly bool AIActive = aiActive;
        public readonly bool EscapeHatchHeld = escapeHatchHeld;
        public readonly float Threshold = threshold;
    }

    private const float SampleStep = 1f; // 路徑取樣間距（碼）
    private const int MaxSamples = 64; // 最長的位移技也就 25 碼，這個上限只是防呆
    private static readonly TimeSpan BlockLogThrottle = TimeSpan.FromSeconds(3d);
    private static readonly TimeSpan NoticeLogThrottle = TimeSpan.FromSeconds(60d);

    private readonly WorldState _ws;
    private readonly ActionTweaksConfig _config = Service.Config.Get<ActionTweaksConfig>();
    private readonly HashSet<ActionID> _dashActions = [];
    private readonly Dictionary<ActionID, DateTime> _lastBlockLog = [];
    private FrameSnapshot? _snapshot;
    private string? _lastNotice;
    private DateTime _nextNoticeAt;

    public DashInterceptTweak(WorldState ws)
    {
        _ws = ws;

        // 🔴 清單在載入時建一次：熱路徑上只剩一個 HashSet 查詢，不是位移技就零成本直通。
        //    來源是 ActionDefinitions 已經登記好的落點判定式，所以「哪些算位移技」與自動循環那一關完全同源。
        int toTarget = 0, toGround = 0, fixedDist = 0, backdash = 0;
        foreach (var def in ActionDefinitions.Instance.Definitions)
        {
            if (!ActionDefinitions.TryGetDashGeometry(def.ForbidExecute, out var geo))
                continue;
            _dashActions.Add(def.ID);
            // 節流表在這裡就把所有鍵放齊：之後 LogBlock 只會覆寫既有項目的值（8 位元組寫入），
            // 不會發生擴容／rehash —— 萬一 UseAction 真的從別的執行緒進來，最壞也只是多印或少印一行。
            _lastBlockLog[def.ID] = DateTime.MinValue;
            switch (geo.Geometry)
            {
                case ActionDefinitions.DashGeometry.ToTargetHitbox:
                    ++toTarget;
                    break;
                case ActionDefinitions.DashGeometry.ToGroundTarget:
                    ++toGround;
                    break;
                case ActionDefinitions.DashGeometry.FixedFromFacing:
                    ++fixedDist;
                    break;
                case ActionDefinitions.DashGeometry.AwayFromTarget:
                    ++backdash;
                    break;
            }
        }
        // 走 Information：使用者的 LogLevel 是 1，而這行是「涵蓋了幾顆」的唯一憑據
        Service.Logger.Information($"[BMR][位移攔截] 位移技清單建立完成：共 {_dashActions.Count} 顆（衝向目標 {toTarget}、地面指定 {toGround}、定距離 {fixedDist}、以目標為基準後跳 {backdash}）。地面指定型在 UseAction 這一關拿不到座標，一律不攔。");
    }

    /// <summary>
    /// 每幀在繪製執行緒上重建快照。<paramref name="escapeHatchHeld"/> 必須由呼叫端在 Draw 回呼裡取得
    /// （<c>MovementOverride.IsForceUnblocked</c> 會讀 ImGui IO，不能從 detour 裡呼叫）。
    /// </summary>
    public void Update(AIHints hints, bool escapeHatchHeld)
    {
        if (!_config.DashSafety || !_config.DashSafetyBlockExternal)
        {
            _snapshot = null; // 關掉時連快照都不留：熱路徑第一個判斷就出去
            return;
        }

        var now = _ws.CurrentTime;
        var threshold = _config.DashSafetyActivationThreshold;
        var horizon = now.AddSeconds(threshold);
        var src = hints.ForbiddenZones;
        var count = src.Count;

        // 只留「已生效或即將引爆」的區。更遠的區站進去 AI 會自己走出來，攔了只是讓循環白白卡住。
        // 註：沒帶 activation 的區是 default(DateTime)（= MinValue），會被當成「已生效」留下來，這是刻意的。
        var kept = 0;
        for (var i = 0; i < count; ++i)
            if (src[i].activation <= horizon)
                ++kept;

        if (kept == 0)
        {
            _snapshot = null;
            return;
        }

        var zones = new (Func<WPos, float> Shape, DateTime Activation)[kept];
        var j = 0;
        for (var i = 0; i < count; ++i)
        {
            var z = src[i];
            if (z.activation <= horizon)
                zones[j++] = (z.shapeDistance, z.activation);
        }

        _snapshot = new(zones, now, AI.AIManager.Instance?.Beh != null, escapeHatchHeld, threshold);
    }

    /// <summary>
    /// 這一次 <c>UseAction</c> 該不該吞掉。回 true 代表攔下（呼叫端直接回 false 給遊戲，不呼叫 Original）。
    /// </summary>
    /// <remarks>🔴 任何算不出落點的情況一律回 false —— 寧可漏攔，不可誤攔。</remarks>
    public bool ShouldBlock(ActionID action, ulong targetId)
    {
        var snapshot = _snapshot; // 只讀一次，後面全部看這份
        if (snapshot == null || !_dashActions.Contains(action))
            return false; // 熱路徑出口：功能關著／這一幀沒有危險區／不是位移技

        var player = _ws.Party.Player();
        if (player == null)
            return false;

        var def = ActionDefinitions.Instance[action];
        if (def == null || !ActionDefinitions.TryGetDashGeometry(def.ForbidExecute, out var geo))
            return false; // 照理進不來（_dashActions 就是從這裡建的），留著當防禦

        // 與既有的兩個選項語意一致：非「衝向目標」型要另外開 DashSafetyExtra
        if (geo.Geometry != ActionDefinitions.DashGeometry.ToTargetHitbox && !_config.DashSafetyExtra)
            return false;

        // 地面指定型（縮地／魔紋步／回歸／若隱若現）：落點是玩家等一下才會點的地面座標，
        // UseAction 這一關根本還不知道，一律不攔。擺在最前面是為了連「為什麼沒攔」都不要記。
        if (geo.Geometry == ActionDefinitions.DashGeometry.ToGroundTarget)
            return false;

        // 📌 下面兩條放行條件才開始記診斷 —— 排在幾何判定之後，是為了不對「本來就不可能攔的技能」
        //    （例如地面指定型）洗出「為什麼沒攔」的說明。
        if (!snapshot.AIActive)
        {
            // 攔截這一關分不出「誰要放這一發」，所以 AI 沒開時一律不動手 —— 那是純手動玩家的操作。
            Notice(snapshot, "[BMR][位移攔截] 目前不攔：BMR AI 沒有啟用。攔截無法分辨技能是誰要放的，AI 關著＝視為純手動操作，不介入。");
            return false;
        }

        if (snapshot.EscapeHatchHeld)
        {
            Notice(snapshot, "[BMR][位移攔截] 目前不攔：逃生鍵（設定「詠唱期間允許移動需按住的按鍵」）按著。");
            return false;
        }

        var from = player.Position;
        WPos to;
        switch (geo.Geometry)
        {
            case ActionDefinitions.DashGeometry.ToTargetHitbox:
                {
                    var target = ResolveDashTarget(def, player, targetId);
                    if (target == null)
                        return false; // 目標解不出來就算不出落點
                    to = from + player.DirectionTo(target) * MathF.Max(0f, player.DistanceToHitbox(target));
                }
                break;
            case ActionDefinitions.DashGeometry.AwayFromTarget:
                {
                    var target = ResolveDashTarget(def, player, targetId);
                    if (target == null)
                        return false;
                    to = from + target.DirectionTo(player) * geo.Distance;
                }
                break;
            case ActionDefinitions.DashGeometry.FixedFromFacing:
                // 這裡刻意用角色目前的朝向，不用 ActionDefinition.TransformAngle（那些「與攝影機方向一致」
                // 的設定只在 BMR 自己執行時才會先轉向；外部來源送出去時遊戲用的就是角色當下的朝向）。
                to = from + player.Rotation.ToDirection() * geo.Distance;
                break;
            default:
                return false; // 地面指定型：UseAction 這一關還拿不到落點座標
        }

        var zones = snapshot.Zones;
        var nz = zones.Length;

        // 條件三：已經站在（生效中或即將引爆的）危險區裡 —— 這一發位移很可能是在逃命，一律放行。
        for (var i = 0; i < nz; ++i)
        {
            if (zones[i].Shape(from) < 0f)
            {
                Notice(snapshot, "[BMR][位移攔截] 目前不攔：玩家已經站在危險區裡，這一發位移可能是在逃命。");
                return false;
            }
        }

        var delta = to - from;
        var len = delta.Length();
        if (len <= 0f)
            return false; // 零位移（例如已經貼在目標 hitbox 上）不可能把人送進新的危險

        // 取樣整條線段，不是只看落點：突進的中途一樣會吃到傷害。起點不取（上面那道閘門已經涵蓋）。
        var steps = Math.Min(MaxSamples, (int)(len / SampleStep) + 1);
        var invSteps = 1f / steps;
        for (var k = 1; k <= steps; ++k)
        {
            var p = from + delta * (k * invSteps);
            for (var i = 0; i < nz; ++i)
            {
                if (zones[i].Shape(p) < 0f)
                {
                    LogBlock(action, geo, from, to, p, k, steps, len, zones[i].Activation, snapshot);
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 解出「這一發位移技真正會用來算落點的目標」，解不出來就回 null（＝不攔）。
    /// </summary>
    /// <remarks>
    /// 🔴 光是 <c>Actors.Find(targetId)</c> 不夠：<c>targetId</c> 是玩家當下的硬目標，跟這顆技能收不收那種目標無關。
    /// 例如黑魔的乙太步（155）只能指向隊友，但玩家硬目標通常是敵人 —— 直接拿那個敵人算落點，
    /// 會得到一個與實際完全無關的方向，然後**誤攔**。所以這裡拿 <c>AllowedTargets</c> 做一次相容性檢查，
    /// 對不上就回 null（寧可漏攔）。
    /// </remarks>
    private Actor? ResolveDashTarget(ActionDefinition def, Actor player, ulong targetId)
    {
        var target = _ws.Actors.Find(targetId);
        if (target == null || ReferenceEquals(target, player))
            return null;

        var allowed = def.AllowedTargets;
        return target.IsAlly
            ? (allowed & (ActionTargets.Party | ActionTargets.Alliance | ActionTargets.Friendly)) != 0 ? target : null
            : (allowed & ActionTargets.Hostile) != 0 ? target : null;
    }

    private static string GeometryName(ActionDefinitions.DashGeometry g) => g switch
    {
        ActionDefinitions.DashGeometry.ToTargetHitbox => "衝向目標",
        ActionDefinitions.DashGeometry.ToGroundTarget => "地面指定",
        ActionDefinitions.DashGeometry.FixedFromFacing => "定距離",
        ActionDefinitions.DashGeometry.AwayFromTarget => "以目標為基準後跳",
        _ => "未知",
    };

    // 一行判死：互斥成因 + 實際數字。看到這一行就不必再問「是落點還是半路」「是哪個區」「門檻夠不夠」。
    private void LogBlock(ActionID action, ActionDefinitions.DashGeometryInfo geo, WPos from, WPos to, WPos hit, int step, int steps, float len, DateTime activation, FrameSnapshot snapshot)
    {
        var now = snapshot.Now;
        if (_lastBlockLog.TryGetValue(action, out var last) && now - last < BlockLogThrottle)
            return;
        _lastBlockLog[action] = now;

        var eta = activation <= now ? 0d : (activation - now).TotalSeconds;
        var when = eta <= 0d ? "已經生效" : $"{eta:f2} 秒後引爆";
        var where = step == steps ? "落點" : $"路徑上第 {step}/{steps} 個取樣點";
        Service.Logger.Information(
            $"[BMR][位移攔截] 攔下位移技「{action.Name()}」（{action}，{GeometryName(geo.Geometry)}型）：" +
            $"起點 ({from.X:f1}, {from.Z:f1}) → 落點 ({to.X:f1}, {to.Z:f1})，長 {len:f1} 碼；" +
            $"{where} ({hit.X:f1}, {hit.Z:f1}) 落在{when}的危險區內。" +
            $"（本幀快照 {snapshot.Zones.Length} 個生效／即將生效的區、引爆門檻 {snapshot.Threshold:f2} 秒、AI 啟用中、逃生鍵未按）" +
            "這一發已被吞掉，下一幀安全就會自然放行；要強制放行請按住逃生鍵。");
    }

    // 「為什麼沒攔」只在成因改變、或距上次同一成因超過 60 秒時印一次 —— 這幾條在一場戰鬥裡會被踩很多次。
    private void Notice(FrameSnapshot snapshot, string text)
    {
        var now = snapshot.Now;
        if (text == _lastNotice && now < _nextNoticeAt)
            return;
        _lastNotice = text;
        _nextNoticeAt = now + NoticeLogThrottle;
        Service.Logger.Information(text);
    }
}
