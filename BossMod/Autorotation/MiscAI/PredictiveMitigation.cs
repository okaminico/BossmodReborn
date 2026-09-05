using BossMod.Autorotation.xan;

namespace BossMod.Autorotation.MiscAI;

/// <summary>
/// 依 <see cref="AIHints.PredictedDamage"/> 預先按下減傷／自保 oGCD 的模組。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：使用者把「出招」交給外部循環外掛（WrathCombo 之類），BMR 只負責走位。
/// 但「知道有傷害要來了」這件事只有 BMR 知道——它是唯一有 boss 模組時間軸的一方，
/// 而外部循環外掛看不到 <c>PredictedDamage</c>。於是常見的結果是減傷技能整場沒開過。
/// 這個模組把兩邊接起來：<b>只按減傷／自保，不碰輸出、不碰目標</b>。
/// </para>
/// <para>
/// 🔑 <b>與外部循環外掛並行的機理</b>：<c>Hints.ActionsToExecute</c> 由
/// <c>ActionManagerEx.FinishActionGather()</c> → <c>ActionManagerEx.Update</c> 每幀無條件消費
/// （<c>Plugin.DrawUI</c> 固定呼叫，不看 AI 開關、也不看外部外掛在做什麼）。
/// 因此不需要走 WrathCombo 的 ActionRequest IPC——直推佇列較簡單也較不會壞：
/// IPC 路線多一個版本相依、對方改簽名就靜默失效，而這條路只依賴 BMR 自己的程式碼。
/// </para>
/// <para>
/// 🔴 <b>已知閘門（不是這個模組能解的）</b>：BMR 的 AI 模式在
/// <c>AIBehaviour.Execute</c> 是 <c>autorot.Preset = target.Target != null ? preset : null</c>——
/// <b>沒有目標時整個 preset 管線關掉</b>，本模組也就不會執行。對「團刀／死刑」而言這通常不成立
/// （有 boss 就有目標），但趕路、目標死亡的空窗期確實不會有減傷。
/// 手動掛 preset（<c>/bmr ar set</c>）的路徑沒有這個閘門。
/// </para>
/// <para>
/// 🔴 <b>安全設計：最壞情況必須是「不按技能」，不能是「亂按技能」。</b>每一次推送都同時滿足：
/// ①<c>ActionDefinition.IsUnlocked</c>（職業對、等級夠）②冷卻真的好了（不推 CD 中的技能，
/// 否則會在 <c>ActionQueue.FindBest</c> 裡變成 deadline 卡住別的動作）③同一格減傷的效果
/// 沒有正在罩著這次傷害 ④優先級一律 &lt; <c>High</c>，永遠不會延遲 GCD ⑤只在戰鬥中。
/// 完全不寫 <c>Hints.ForcedTarget</c>、不推任何 GCD。
/// </para>
/// <para>
/// 🔑 <b>傷害屬性配對</b>：<c>DamagePrediction</c> 沒有屬性欄位，所以屬性是靠
/// 「活化時間對上哪一個敵方詠唱的完成時間」反推、再查 <c>Action.AttackType</c>
/// （見 <see cref="ClassifyIncoming"/>）。判出物理就跳過魔法專用的減傷、判出魔法就跳過物理專用的，
/// 通用減傷照推。<b>判不出時預設整個不推</b>，留給使用者手動 —— 要退回舊行為請把
/// <c>UnknownSchool</c> 軌道改成「照樣推減傷」。
/// <para>
/// 📌 實務上這個過濾影響的技能很少：台服 7.20 的減傷描述絕大多數是無屬性限定的
/// 「所受到的傷害減輕 N%」，或同時列出物理與魔法兩個數字（暗黑佈道、光之心、棄明投暗、
/// 牽制、昏亂皆屬此類）。整個模組裡真正分屬性的只有<b>抗死</b>（魔法專用）與
/// <b>壁壘</b>（格擋只對物理生效）兩支。真正會改變行為的是「判不出就不推」那條規則。
/// </para>
/// </para>
/// </remarks>
public sealed class PredictiveMitigation(RotationModuleManager manager, Actor player) : AIBase(manager, player)
{
    public enum Track { PartyMit, SelfMit, Emergency, RaidwideLead, TankbusterLead, EmergencyHP, UnknownSchool }

    public enum SelfMitStrategy
    {
        TankbusterOnly,
        Raidwides,
        Disabled
    }

    /// <summary>判不出這次傷害是物理還是魔法時要怎麼辦。</summary>
    public enum UnknownSchoolStrategy
    {
        /// <summary>不推，把這一次留給使用者手動處理（預設）。</summary>
        Skip,
        /// <summary>照推，等同於加上屬性配對之前的行為。</summary>
        Mitigate
    }

    /// <summary>
    /// 一格減傷對哪一種傷害有效。
    /// </summary>
    /// <remarks>
    /// 🔑 <b>依據是台服 7.20 的 <c>ActionTransient.Description</c> 逐條查證，不是憑印象。</b>
    /// 現行版本裡幾乎所有減傷都寫「所受到的傷害減輕 N%」（無屬性限定）或同時列出物理與魔法兩個數字，
    /// 因此 <see cref="All"/> 是壓倒性多數 —— 這是刻意的結論而不是偷懶的預設值：
    /// <list type="bullet">
    /// <item>暗黑佈道／光之心：「物理傷害減輕5%、魔法傷害減輕10%」⇒ 兩種都減，算 <see cref="All"/>。
    /// （⚠️ 這兩支在舊版是魔法專用，靠印象分類會錯。）</item>
    /// <item>棄明投暗（暗黑心眼）：「物理傷害減輕10%、魔法傷害減輕20%」⇒ 同上，<see cref="All"/>。</item>
    /// <item>牽制／昏亂：主屬性10%、副屬性5%，兩種都減 ⇒ <see cref="All"/>，永遠不會整個白放。</item>
    /// <item>復仇／戮罪：減傷本身無屬性限定，只有「反擊」那段限物理 ⇒ <see cref="All"/>。</item>
    /// <item>迷彩：招架率那段只對物理有意義，但「所受的傷害減輕10%」無限定 ⇒ <see cref="All"/>。</item>
    /// </list>
    /// <para>🔴 不確定一律標 <see cref="All"/>：失敗方向必須是「照常推得出來」而不是「該推的沒推」。</para>
    /// </remarks>
    private enum School
    {
        /// <summary>通用減傷，對物理與魔法都有效。</summary>
        All,
        /// <summary>只對物理傷害有效。</summary>
        Physical,
        /// <summary>只對魔法傷害有效。</summary>
        Magical
    }

    /// <summary>
    /// 一次推送所需的「這次傷害是什麼」context。
    /// </summary>
    /// <remarks>刻意打包成一個值：這三項在同一輪推送裡是固定的，分開傳會讓每個呼叫點都要重複三個參數。</remarks>
    private readonly record struct MitContext(float TimeToHit, ActionDamageType.Kind Incoming, float Priority);

    // 推送優先級：全部低於 ActionQueue.Priority.High(4000)，所以永遠不會為了插入而延遲 GCD。
    private const float PrioParty = ActionQueue.Priority.Medium;   // 3000：團減值得搶第一個 ogcd 空檔
    private const float PrioSelf = ActionQueue.Priority.Low;       // 2000：自身減傷不必搶
    private const float PrioEmergency = ActionQueue.Priority.Medium;

    // 冷卻視為「好了」的容差；FindBest 自己用 0.05f，這裡放寬一點避免每幀邊界抖動。
    private const float ReadyEpsilon = 0.1f;

    // 太舊的預測不理會（正常情況下 PredictedDamage 每幀由 boss 模組重建，不會有殘留，
    // 但「活化時間已過」的項目若照推會變成傷害結算後才開減傷）。
    private const float StaleCutoff = -1f;

    // 詠唱與預測傷害的配對窗。AIHints.DamagePrediction 沒有屬性欄位（也沒有來源技能），
    // 所以屬性只能靠「這次預測的活化時間對上哪一個敵方詠唱的完成時間」反推。
    // 🔑 窗口刻意不對稱：多數元件的 activation 就是 BossModule.CastFinishAt(cast)，也就是
    //    WorldState.FutureTime(cast.NPCRemainingTime) —— 與這裡算法完全相同，理論上差 0。
    //    有些元件會再加上快照／飛行時間的延遲，讓 activation 落在詠唱完成「之後」，
    //    所以往後開得比較寬；落在「之前」則沒有合理成因，只留浮點與換幀的抖動餘裕。
    // ⚠️ 這兩個數字是推估值，無法離線證明。估錯的方向是安全的：配不到＝判不出＝依軌道設定處理，
    //    不會造成「推錯一格減傷」。
    private const float CastMatchLead = 0.5f;  // activation 可以早於詠唱完成多少秒
    private const float CastMatchLag = 1.5f;   // activation 可以晚於詠唱完成多少秒

    public static RotationModuleDefinition Definition()
    {
        var def = new RotationModuleDefinition(
            "Predictive mitigation",
            "Presses mitigation and self-preservation oGCDs ahead of predicted raidwides and tankbusters. Never presses GCDs and never changes your target, so it can run alongside an external rotation plugin.",
            "AI",
            "ffxiv-tc-port",
            RotationModuleQuality.Basic,
            new BitMask(~0ul),
            1000);

        def.AbilityTrack(Track.PartyMit, "PartyMit", "Party mitigation", 100)
            .AddAssociatedActions(ClassShared.AID.Reprisal, ClassShared.AID.Feint, ClassShared.AID.Addle);

        def.Define(Track.SelfMit).As<SelfMitStrategy>("SelfMit", "Personal mitigation", 90)
            .AddOption(SelfMitStrategy.TankbusterOnly, "Only when a tankbuster targets me")
            .AddOption(SelfMitStrategy.Raidwides, "Also before raidwides")
            .AddOption(SelfMitStrategy.Disabled, "Do not use")
            .AddAssociatedActions(ClassShared.AID.Rampart);

        def.AbilityTrack(Track.Emergency, "Emergency", "Emergency self-preservation", 80)
            .AddAssociatedActions(ClassShared.AID.SecondWind);

        // 📌 這三條以前受限於「CreateEmpty() 回 MinValue ⇒ 預設值只能等於下限」，所以下限是被當成
        //    預設值在用的、範圍只能往單邊開。DefineFloat 現在收 defaultValue，兩者已經解耦。
        // 🔴 defaultValue 一律明寫成解耦前的舊 MinValue（5 / 4 / 30）：沒設過這幾條軌的使用者拿到的
        //    值必須與解耦前逐位元組相同，這是這次改動的回歸錨，不要「順手」跟著範圍一起改。
        //    要調整可選範圍就只動 minValue / maxValue。
        // ⚠️ 提前秒數往下開到 1 秒。刻意不開到 0：0 秒＝命中的那一瞬間才按，動畫鎖加伺服器往返一定
        //    來不及，減傷會落在傷害之後＝等於沒開。
        def.DefineFloat(Track.RaidwideLead, "Raidwide lead time (s)", 1, 20, 10, defaultValue: 5);
        def.DefineFloat(Track.TankbusterLead, "Tankbuster lead time (s)", 1, 15, 9, defaultValue: 4);
        // 血量門檻沒有「要更低」的訴求，範圍維持不動；仍然明寫 defaultValue，免得下次有人調了下限
        // 卻沒注意到預設值會跟著跑掉。
        def.DefineFloat(Track.EmergencyHP, "Emergency HP threshold (%)", 30, 90, 8, defaultValue: 30);

        // ⚠️ 這條軌道刻意加在最後面：Define/AddOption 都會斷言「索引 == 列舉值」，而軌道的
        //    InternalName 是 preset 的序列化鍵 —— 插在中間會把既有 preset 的軌道對應整個錯開。
        // 📌 選項 0 是 StrategyValueTrack 的預設值（CreateEmpty 給 Option = 0），
        //    所以 Skip 放第一個＝「判不出就不推」是預設，符合使用者要的「沒分的手動」。
        def.Define(Track.UnknownSchool).As<UnknownSchoolStrategy>("UnknownSchool", "Unknown damage type", 70)
            .AddOption(UnknownSchoolStrategy.Skip, "Do not mitigate, leave it to me")
            .AddOption(UnknownSchoolStrategy.Mitigate, "Mitigate anyway");

        return def;
    }

    // 🔴 每個技能各自節流。舊版只記「上一個推過的技能」一個格子，戰慄／泰然自若／戮罪三支輪流推時
    //    每一幀都會換人，10 秒節流因此每幀都失效（實機一分鐘 1521 行）。字典的鍵只會是本模組推過的
    //    減傷技，正常是個位數；換職業會慢慢累積，所以在視窗滾動時順手清一次過期項目。
    private readonly Dictionary<ActionID, DateTime> _lastLogTimes = [];

    // 第二層閘門：就算 per-action 節流被某種沒想到的組合打穿，同一秒也最多印 MaxLogsPerSecond 行，
    //    其餘只留一行計數摘要 —— 診斷訊息本身絕不該變成效能問題，而「有東西沒印出來」要看得見。
    private const int MaxLogsPerSecond = 4;
    private DateTime _logWindowStart;
    private int _logWindowCount;
    private int _logWindowSuppressed;

    // 判不出屬性時的處置；每幀由 Execute 更新，供 TryMit 讀取（同一輪推送裡是固定值，不必層層傳）。
    private UnknownSchoolStrategy _unknownSchool;

    // 最近一次 Execute 判出來的屬性，只給 DescribeState 顯示用。
    private ActionDamageType.Kind _lastRaidwideKind;
    private ActionDamageType.Kind _lastTankbusterKind;

    /// <remarks>
    /// 📌 屬性判不出來時顯示 <c>?</c> 而不是留白 —— 使用者要能在列上看見「這一次不知道是什麼傷害」，
    /// 因為那正是模組預設不出手的原因；藏起來會讓「沒反應」看起來像故障。
    /// </remarks>
    public override string DescribeState()
    {
        var (rw, _, tb, _) = Imminent();
        var rwText = rw < float.MaxValue ? $"{rw:f1}{KindTag(_lastRaidwideKind)}" : "-";
        var tbText = tb < float.MaxValue ? $"{tb:f1}{KindTag(_lastTankbusterKind)}" : "-";
        return $"RW {rwText} / TB {tbText}";
    }

    private static string KindTag(ActionDamageType.Kind kind) => kind switch
    {
        ActionDamageType.Kind.Physical => "物",
        ActionDamageType.Kind.Magical => "魔",
        ActionDamageType.Kind.Unknown => "?",
        _ => ""
    };

    public override void Execute(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        if (Player.IsDeadOrDestroyed || !Player.InCombat)
            return;

        var (raidwideIn, raidwideAt, tankbusterIn, tankbusterAt) = Imminent();

        var raidwideLead = strategy.GetFloat(Track.RaidwideLead);
        var tankbusterLead = strategy.GetFloat(Track.TankbusterLead);

        var raidwideImminent = raidwideIn <= raidwideLead;
        var tankbusterImminent = tankbusterIn <= tankbusterLead;

        _unknownSchool = strategy.Option(Track.UnknownSchool).As<UnknownSchoolStrategy>();

        // 只在真的要用到時才去掃詠唱（每次都要走一遍 actor 表）。
        _lastRaidwideKind = raidwideImminent ? ClassifyIncoming(raidwideAt) : ActionDamageType.Kind.None;
        _lastTankbusterKind = tankbusterImminent ? ClassifyIncoming(tankbusterAt) : ActionDamageType.Kind.None;

        if (raidwideImminent && strategy.Enabled(Track.PartyMit))
            ExecutePartyMit(new(raidwideIn, _lastRaidwideKind, PrioParty), primaryTarget);

        var selfStrategy = strategy.Option(Track.SelfMit).As<SelfMitStrategy>();
        if (selfStrategy != SelfMitStrategy.Disabled)
        {
            if (tankbusterImminent)
                ExecuteSelfMit(new(tankbusterIn, _lastTankbusterKind, PrioSelf));
            else if (selfStrategy == SelfMitStrategy.Raidwides && raidwideImminent)
                ExecuteSelfMit(new(raidwideIn, _lastRaidwideKind, PrioSelf));
        }

        if (strategy.Enabled(Track.Emergency) && Player.PendingHPRatio < strategy.GetFloat(Track.EmergencyHP) * 0.01f)
        {
            // 🔑 低血自保刻意<b>不</b>做屬性過濾：這條路徑談的是「血快沒了」而不是「某一次特定傷害」，
            //    傳 Kind.None 讓 ShouldMitigate 一律放行，維持加上屬性配對之前的行為。
            ExecuteSelfHeal();
            ExecuteSelfMit(new(0, ActionDamageType.Kind.None, PrioEmergency));
        }
    }

    /// <summary>
    /// 本幀最近的團刀與指向自己的死刑：剩餘秒數與活化時刻；沒有就是 <c>float.MaxValue</c>／<c>default</c>。
    /// </summary>
    private (float Raidwide, DateTime RaidwideAt, float Tankbuster, DateTime TankbusterAt) Imminent()
    {
        var now = World.CurrentTime;
        var rw = float.MaxValue;
        var tb = float.MaxValue;
        var rwAt = default(DateTime);
        var tbAt = default(DateTime);

        foreach (var t in Raidwides)
        {
            var dt = (float)(t - now).TotalSeconds;
            if (dt > StaleCutoff && dt < rw)
            {
                rw = dt;
                rwAt = t;
            }
        }

        foreach (var (actor, t) in Tankbusters)
        {
            if (actor != Player)
                continue;
            var dt = (float)(t - now).TotalSeconds;
            if (dt > StaleCutoff && dt < tb)
            {
                tb = dt;
                tbAt = t;
            }
        }

        return (rw, rwAt, tb, tbAt);
    }

    #region 傷害屬性判定

    /// <summary>
    /// 判斷某次預測傷害是物理還是魔法。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <c>AIHints.DamagePrediction</c> 只有「誰會被打到、什麼時候、哪一類」，<b>沒有來源技能也沒有屬性</b>。
    /// 所以這裡走反推：預測的活化時間 ≈ 某個敵方詠唱的完成時間 ⇒ 那一筆詠唱的技能就是傷害來源，
    /// 再查 <c>Action</c> 表的 <c>AttackType</c>（<see cref="ActionDamageType"/> 已經做好查表與快取，
    /// 也就是遊戲自己用來決定掛「物理受傷加重」還是「魔法受傷加重」的同一個欄位）。
    /// </para>
    /// <para>
    /// 🔑 <b>保守規則</b>：配不到任何詠唱、或同時配到屬性不一致的多筆詠唱，一律回
    /// <c>Unknown</c> 而不是挑一個。誤判的代價是「開錯減傷」，比「沒開」更糟。
    /// </para>
    /// <para>
    /// ⚠️ 已知配不到的情形：以標記／連線（icon、tether）觸發而<b>沒有詠唱</b>的分攤與散開，
    /// 以及在詠唱開始前就先預測出來的傷害。這些都會落在 <c>Unknown</c>，由 UnknownSchool 軌道決定怎麼辦。
    /// </para>
    /// </remarks>
    private ActionDamageType.Kind ClassifyIncoming(DateTime activation)
    {
        var res = ActionDamageType.Kind.None;

        foreach (var actor in World.Actors)
        {
            if (actor.IsAlly || actor.IsDeadOrDestroyed)
                continue;

            var cast = actor.CastInfo;
            if (cast == null || !cast.IsSpell())
                continue;

            // 與 BossModule.CastFinishAt 同一條算式，所以由詠唱推出來的 activation 理論上完全對得上。
            var finishAt = World.FutureTime(cast.NPCRemainingTime);
            var dt = (float)(activation - finishAt).TotalSeconds;
            if (dt < -CastMatchLead || dt > CastMatchLag)
                continue;

            var kind = ActionDamageType.Classify(cast.Action.ID);
            if (kind == ActionDamageType.Kind.None)
                continue;

            if (res == ActionDamageType.Kind.None)
                res = kind;
            else if (res != kind)
                return ActionDamageType.Kind.Unknown; // 同時吻合的詠唱屬性不一致 ⇒ 不猜
        }

        // 完全沒有吻合的詠唱也是「判不出」，不是「沒有傷害」——呼叫端只在確定有預測傷害時才會問。
        return res == ActionDamageType.Kind.None ? ActionDamageType.Kind.Unknown : res;
    }

    /// <summary>這一格減傷對這次傷害到底有沒有用。</summary>
    /// <remarks>
    /// 🔴 <b>失敗方向鐵律：任何不確定都往「不推」倒。</b>
    /// 判不出屬性時預設整個不推（使用者原話「沒分的手動」），要退回舊行為得自己把
    /// UnknownSchool 軌道改成「照推」。
    /// </remarks>
    private bool ShouldMitigate(School school, ActionDamageType.Kind incoming) => incoming switch
    {
        // 沒有「這一次傷害」可談（低血自保）⇒ 不做屬性過濾。
        ActionDamageType.Kind.None => true,
        ActionDamageType.Kind.Physical => school != School.Magical,
        ActionDamageType.Kind.Magical => school != School.Physical,
        _ => _unknownSchool == UnknownSchoolStrategy.Mitigate
    };

    #endregion

    #region 推送與判定

    /// <summary>
    /// 嘗試推一格減傷。
    /// </summary>
    /// <param name="action">技能。</param>
    /// <param name="school">這一格減傷對哪一種傷害有效；與 <c>ctx.Incoming</c> 對不上就直接跳過。</param>
    /// <param name="duration">效果持續時間（秒）。⚠️ 這是<b>寫死的估計值</b>，遊戲資料表沒有可靠來源
    /// （TankAI 同樣是寫死的，見 <c>xan/AI/Tank.cs</c> 的註解）。估錯只會影響「這格是不是已經罩著了」
    /// 的判斷——太短＝可能提早補第二格、太長＝可能少開一格，<b>不會造成亂放技能</b>。</param>
    /// <param name="ctx">這次傷害的 context（剩餘秒數／屬性／優先級）。</param>
    /// <param name="target">目標；預設是自己。null 一律不推。</param>
    /// <returns>true＝這一格已經解決（推出去了，或效果還罩著），呼叫端不要再往下找備援。</returns>
    private bool TryMit(ActionID action, School school, float duration, in MitContext ctx, Actor? target = null)
    {
        // 🔴 屬性不合就當這一格不存在（回 false 而不是 true）——讓自身減傷鏈繼續往下找還能用的那一格。
        if (!ShouldMitigate(school, ctx.Incoming))
            return false;

        var def = ActionDefinitions.Instance[action];
        if (def == null || !def.IsUnlocked(World, Player))
            return false;

        var cd = def.ReadyIn(World.Client.Cooldowns, World.Client.DutyActions);

        // 效果殘留 = 持續時間 -（已經過的冷卻）= duration -(maxCD - cd)。沒用過時 cd=0 ⇒ 必為 <= 0。
        // 🔑 這個推法不需要狀態 id，也就不會因為狀態 id 在台服對不上而靜默失效（沿用 TankAI 的做法）。
        var effectRemaining = duration + cd - def.Cooldown;
        if (effectRemaining > ctx.TimeToHit)
            return true;

        if (cd > ReadyEpsilon)
            return false;

        var tgt = target ?? Player;
        Hints.ActionsToExecute.Push(action, tgt, ctx.Priority);
        LogPush(action, ctx.Incoming);
        return true;
    }

    private bool TryMit<AID>(AID aid, School school, float duration, in MitContext ctx, Actor? target = null) where AID : Enum
        => TryMit(ActionID.MakeSpell(aid), school, duration, in ctx, target);

    /// <summary>
    /// 升級技的挑選：解鎖了就用升級版，否則用原版。
    /// ⚠️ 刻意不用 <c>BestActionUnlocked</c>——那支的參數是 <c>params AID[]</c>，
    /// 每次呼叫都會配一個陣列，而這裡位於每幀都可能走到的路徑上。
    /// </summary>
    private AID Upgrade<AID>(AID upgraded, AID basic) where AID : struct, Enum
        => ActionUnlocked(upgraded) ? upgraded : basic;

    /// <summary>
    /// 要使用者回報時看得到的診斷。刻意寫 <c>Information</c>（使用者跑 LogLevel 1，盲區只有 Verbose,Debug 收得到但單檔數十萬行會淹沒），
    /// 並且對「每一個技能各自」節流（外加每秒行數上限），否則每幀一行會把 log 灌爆。
    /// </summary>
    private void LogPush(ActionID action, ActionDamageType.Kind incoming)
    {
        var now = World.CurrentTime;
        if (_lastLogTimes.TryGetValue(action, out var lastForAction) && (now - lastForAction).TotalSeconds < 10)
            return;
        _lastLogTimes[action] = now;

        if ((now - _logWindowStart).TotalSeconds >= 1)
        {
            // 先把上一個視窗被壓下來的次數補一行摘要，再開新視窗。
            if (_logWindowSuppressed > 0)
                Service.Logger.Information($"[PredMit] 前一秒另有 {_logWindowSuppressed} 次推送未列出（每秒上限 {MaxLogsPerSecond} 行）。");
            _logWindowStart = now;
            _logWindowCount = 0;
            _logWindowSuppressed = 0;
            PruneLogTimes(now);
        }

        if (_logWindowCount >= MaxLogsPerSecond)
        {
            ++_logWindowSuppressed;
            return;
        }
        ++_logWindowCount;
        // 📌 把判出來的屬性一起記下來：使用者回報「該開的沒開」時，這一行是唯一能分辨
        //    「屬性判不出所以沒推」與「冷卻沒好／職業不對」的離線證據。
        var kind = incoming switch
        {
            ActionDamageType.Kind.Physical => "物理",
            ActionDamageType.Kind.Magical => "魔法",
            ActionDamageType.Kind.Unknown => "判不出",
            _ => "不適用"
        };
        Service.Logger.Information($"[PredMit] 推送減傷 {action}（傷害屬性：{kind}，HP {Player.PendingHPRatio:P0}）。");
    }

    /// <summary>清掉已經超過節流窗的字典項目；只在視窗滾動且字典明顯偏大時呼叫，不在每幀路徑上。</summary>
    private void PruneLogTimes(DateTime now)
    {
        if (_lastLogTimes.Count <= 32)
            return;
        List<ActionID> stale = [];
        foreach (var kv in _lastLogTimes)
            if ((now - kv.Value).TotalSeconds >= 10)
                stale.Add(kv.Key);
        foreach (var k in stale)
            _lastLogTimes.Remove(k);
    }

    #endregion

    #region 團體減傷

    private void ExecutePartyMit(in MitContext ctx, Actor? primaryTarget)
    {
        switch (Player.Class)
        {
            // 聖光幕簾＝防護罩（抵消最大HP10%），描述無屬性限定 ⇒ All
            case Class.GLA:
            case Class.PLD:
                TryMit(BossMod.PLD.AID.DivineVeil, School.All, 30, in ctx);
                TryReprisal(in ctx, primaryTarget);
                break;
            // 擺脫＝防護罩（抵消最大HP15%），無屬性限定 ⇒ All
            case Class.MRD:
            case Class.WAR:
                TryMit(BossMod.WAR.AID.ShakeItOff, School.All, 15, in ctx);
                TryReprisal(in ctx, primaryTarget);
                break;
            // ⚠️ 暗黑佈道在台服 7.20 是「物理減5%、魔法減10%」——兩種都減，不是舊版的魔法專用 ⇒ All
            case Class.DRK:
                TryMit(BossMod.DRK.AID.DarkMissionary, School.All, 15, in ctx);
                TryReprisal(in ctx, primaryTarget);
                break;
            // ⚠️ 光之心同上，「物理減5%、魔法減10%」⇒ All
            case Class.GNB:
                TryMit(BossMod.GNB.AID.HeartOfLight, School.All, 15, in ctx);
                TryReprisal(in ctx, primaryTarget);
                break;

            // 近戰：牽制（對敵單體，射程 10）。「物理降10%、魔法降5%」⇒ 兩種都降，All
            case Class.PGL:
            case Class.MNK:
            case Class.LNC:
            case Class.DRG:
            case Class.ROG:
            case Class.NIN:
            case Class.SAM:
            case Class.RPR:
            case Class.VPR:
                TryEnemyDebuff(ClassShared.AID.Feint, School.All, 10, in ctx, primaryTarget);
                break;

            // 遠敏：各自的團減（皆為對自身施放的 30m 範圍增益），描述都是「所受到的傷害減輕15%」⇒ All
            case Class.ARC:
            case Class.BRD:
                TryMit(BossMod.BRD.AID.Troubadour, School.All, 15, in ctx);
                break;
            case Class.MCH:
                TryMit(BossMod.MCH.AID.Tactician, School.All, 15, in ctx);
                break;
            case Class.DNC:
                TryMit(BossMod.DNC.AID.ShieldSamba, School.All, 15, in ctx);
                break;

            // 法系：昏亂（對敵單體，射程 25）。「物理降5%、魔法降10%」⇒ 兩種都降，All
            case Class.THM:
            case Class.BLM:
            case Class.ACN:
            case Class.SMN:
            case Class.BLU:
            case Class.PCT:
                TryEnemyDebuff(ClassShared.AID.Addle, School.All, 10, in ctx, primaryTarget);
                break;
            // 🔑 抗死是整個模組裡唯一的魔法專用團減：「所受到的魔法傷害減輕10%」⇒ Magical。
            //    物理團刀時會被跳過，昏亂仍然照推（它兩種都降）。
            case Class.RDM:
                TryMit(BossMod.RDM.AID.MagickBarrier, School.Magical, 10, in ctx);
                TryEnemyDebuff(ClassShared.AID.Addle, School.All, 10, in ctx, primaryTarget);
                break;

            // 治療：只挑「按下去就生效、不打斷走位、不需要寵物」的那一個。
            // ⚠️ 刻意不碰占星的命運之輪（詠唱式、會把人釘在原地，與走位模組直接打架），
            //    也不碰學者的異想的幻光（需要仙女在場，沒仙女時是靜默失效）。
            // 三支的描述都是無屬性限定的「受到的傷害減輕10%」⇒ All
            case Class.CNJ:
            case Class.WHM:
                TryMit(BossMod.WHM.AID.Temperance, School.All, 20, in ctx);
                break;
            case Class.SCH:
                TryMit(BossMod.SCH.AID.Expedient, School.All, 20, in ctx);
                break;
            case Class.SGE:
                TryMit(BossMod.SGE.AID.Kerachole, School.All, 15, in ctx);
                break;
        }
    }

    /// <summary>
    /// 雪仇：以自己為中心的 5m 範圍減益，射程欄位是 0 ⇒ <c>ActionQueue</c> 不會幫我們做距離檢查，
    /// 沒有敵人在範圍內照推就是白白丟掉一次 60 秒 CD。所以這裡自己檢查距離（做法同 TankAI）。
    /// </summary>
    private void TryReprisal(in MitContext ctx, Actor? primaryTarget)
    {
        var enemy = primaryTarget ?? Bossmods.ActiveModule?.PrimaryActor;
        if (enemy == null || enemy.IsAlly || enemy.IsDeadOrDestroyed || Player.DistanceToHitbox(enemy) > 5)
            return;
        // 雪仇＝「使自身周圍的敵人攻擊傷害降低10%」，無屬性限定 ⇒ All
        TryMit(ClassShared.AID.Reprisal, School.All, 10, in ctx);
    }

    /// <summary>
    /// 牽制／昏亂這類「掛在敵人身上」的團減。
    /// 🔴 <b>不做目標選取</b>：只用呼叫端已經解析好的主要目標（<c>Hints.ForcedTarget</c> 或玩家自己選的），
    /// 沒有目標就不推。射程由 <c>ActionQueue.CanExecute</c> 依技能定義檢查（這兩招的 Range &gt; 0）。
    /// </summary>
    private void TryEnemyDebuff(ClassShared.AID aid, School school, float duration, in MitContext ctx, Actor? primaryTarget)
    {
        if (primaryTarget == null || primaryTarget.IsAlly || primaryTarget.IsDeadOrDestroyed)
            return;
        TryMit(aid, school, duration, in ctx, primaryTarget);
    }

    #endregion

    #region 自身減傷

    /// <summary>
    /// 自身減傷鏈：由「效果最強／最長」往下找，<b>只會成立一格</b>——
    /// <see cref="TryMit"/> 回 true 就中斷，所以同一次傷害不會連開三個減傷。
    /// </summary>
    private void ExecuteSelfMit(in MitContext ctx)
    {
        switch (Player.Class)
        {
            // 預警／極致防禦、鐵壁：描述都是無屬性限定的「所受的傷害減輕 N%」⇒ All
            // 🔑 壁壘＝「受到攻擊必定發動格擋」。格擋在 FFXIV 只對物理攻擊生效（機制事實，
            //    描述本身沒寫），所以標 Physical：魔法團刀時跳過它，不會白丟一次 90 秒 CD。
            case Class.GLA:
            case Class.PLD:
                _ = TryMit(Upgrade(BossMod.PLD.AID.Guardian, BossMod.PLD.AID.Sentinel), School.All, 15, in ctx)
                    || TryMit(ClassShared.AID.Rampart, School.All, 20, in ctx)
                    || TryMit(BossMod.PLD.AID.Bulwark, School.Physical, 10, in ctx);
                break;
            // 復仇／戮罪：減傷段無屬性限定（只有「反擊」限物理）⇒ All；原初的直覺／血氣同樣無限定
            case Class.MRD:
            case Class.WAR:
                _ = TryMit(Upgrade(BossMod.WAR.AID.Damnation, BossMod.WAR.AID.Vengeance), School.All, 15, in ctx)
                    || TryMit(ClassShared.AID.Rampart, School.All, 20, in ctx)
                    || TryMit(Upgrade(BossMod.WAR.AID.Bloodwhetting, BossMod.WAR.AID.RawIntuition), School.All, 8, in ctx);
                break;
            // ⚠️ 棄明投暗（暗黑心眼）在台服 7.20 是「物理減10%、魔法減20%」——兩種都減 ⇒ All，
            //    不是舊版的魔法專用。憑印象標 Magical 會讓物理死刑少一格減傷。
            case Class.DRK:
                _ = TryMit(Upgrade(BossMod.DRK.AID.ShadowedVigil, BossMod.DRK.AID.ShadowWall), School.All, 15, in ctx)
                    || TryMit(ClassShared.AID.Rampart, School.All, 20, in ctx)
                    || TryMit(BossMod.DRK.AID.DarkMind, School.All, 10, in ctx);
                break;
            // 迷彩：招架率那段只對物理有意義，但「所受的傷害減輕10%」無屬性限定 ⇒ All
            case Class.GNB:
                _ = TryMit(Upgrade(BossMod.GNB.AID.GreatNebula, BossMod.GNB.AID.Nebula), School.All, 15, in ctx)
                    || TryMit(ClassShared.AID.Rampart, School.All, 20, in ctx)
                    || TryMit(BossMod.GNB.AID.Camouflage, School.All, 20, in ctx)
                    || TryMit(Upgrade(BossMod.GNB.AID.HeartOfCorundum, BossMod.GNB.AID.HeartOfStone), School.All, 8, in ctx);
                break;

            // 以下皆為「受到的傷害減輕 N%」或「抵消 N 傷害量的防護罩」，描述均無屬性限定 ⇒ All
            case Class.PGL:
            case Class.MNK:
                TryMit(BossMod.MNK.AID.RiddleOfEarth, School.All, 10, in ctx);
                break;
            case Class.ROG:
            case Class.NIN:
                TryMit(BossMod.NIN.AID.ShadeShift, School.All, 20, in ctx);
                break;
            case Class.SAM:
                TryMit(Upgrade(BossMod.SAM.AID.Tengentsu, BossMod.SAM.AID.ThirdEye), School.All, 4, in ctx);
                break;
            case Class.RPR:
                TryMit(BossMod.RPR.AID.ArcaneCrest, School.All, 5, in ctx);
                break;
            // ⚠️ 魔法護盾雖然叫「魔法」，描述是「抵消相當於最大HP30%的傷害量」的無屬性吸收盾 ⇒ All
            case Class.THM:
            case Class.BLM:
                TryMit(BossMod.BLM.AID.Manaward, School.All, 20, in ctx);
                break;
            case Class.PCT:
                TryMit(BossMod.PCT.AID.TemperaCoat, School.All, 10, in ctx);
                break;

            // 其餘職業（龍騎、毒蛇、詩人、機工、舞者、召喚、赤魔、治療四職）在目前版本
            // 沒有「單體、無資源消耗、無副作用」的自身減傷可推 ⇒ 什麼都不做（fail-safe）。
            default:
                break;
        }
    }

    /// <summary>
    /// 低血自保：只推「按下去就回血、不占 GCD、不需要目標」的技能。
    /// </summary>
    /// <remarks>
    /// 這些是<b>治療</b>不是減傷，與傷害屬性無關，所以一律 <see cref="School.All"/>，
    /// 而且 context 帶的是 <c>Kind.None</c>（呼叫端刻意不做屬性過濾）。
    /// </remarks>
    private void ExecuteSelfHeal()
    {
        var ctx = new MitContext(0, ActionDamageType.Kind.None, PrioEmergency);
        switch (Player.Class)
        {
            case Class.MRD:
            case Class.WAR:
                TryMit(BossMod.WAR.AID.ThrillOfBattle, School.All, 10, in ctx);
                TryMit(BossMod.WAR.AID.Equilibrium, School.All, 15, in ctx);
                break;
            case Class.GNB:
                TryMit(BossMod.GNB.AID.Aurora, School.All, 18, in ctx);
                break;
            default:
                // 內丹是物理職的共通技（坦克沒有；ActionDefinition.IsUnlocked 會擋掉不該有的職業，
                // 所以這裡不需要再列一次職業清單）。
                TryMit(ClassShared.AID.SecondWind, School.All, 0, in ctx);
                break;
        }
    }

    #endregion
}
