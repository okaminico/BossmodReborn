using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace BossMod.Global.DeepDungeon;

/// <summary>
/// 對 vnavmesh 的唯讀 IPC 包裝，供深牢小地圖的「走到目標房間」使用。
/// </summary>
/// <remarks>
/// <para>
/// vnavmesh 可能沒安裝、沒啟用，或在執行期間被停用，所以每次呼叫都即時探測、
/// 失敗一律回傳「不可用」，不擲例外、<b>也不快取「可用」狀態</b>
/// （避免使用者中途切換外掛後我們還沿用舊判定）。
/// </para>
/// <para>
/// 🔴 <b>紅線</b>：這裡只走 vnavmesh 的伺服器認可走路，不碰記憶體、不碰封包，
/// 也不會自動接手任何後續互動。
/// </para>
/// <para>
/// 📌 <b>刻意不用 <c>SimpleMove.PathfindAndMoveTo</c>。</b>那個端點把路徑計算丟到背景工作，
/// 算完之後在自己的 Update 裡直接開走——呼叫端<b>沒有機會檢查那條路徑</b>，
/// 而檢查路徑正是這個功能的安全核心。改成自己叫 <c>Nav.Pathfind</c> 拿路徑點、
/// 驗過了才 <c>Path.MoveTo</c>。副作用是「按了停止幾秒後角色自己走起來」那個經典問題
/// 在結構上就不存在了（那個背景工作是我們自己的，停止時直接作廢即可）。
/// </para>
/// </remarks>
static class DeepDungeonNav
{
    // ── 例外處理的分工 ────────────────────────────────────────────────
    // `IpcError`（含 NotReady／TypeMismatch／LengthMismatch／ValueNull，全部繼承自它）
    // ＝「對方不在或介面對不上」，是預期中的狀況，安靜地回報不可用即可。
    //
    // 🔴 但 **vnavmesh 自己的處理常式擲出來的例外不是 IpcError** —— Dalamud 的 CallGate
    //    是直接呼叫對方註冊的委派，對方內部炸掉會原樣往上冒。這些呼叫點在
    //    `Update()` 與 ImGui 繪製途中，讓它冒出去會打斷 BMR 整個 frame。
    //    所以額外接一層 Exception，並用 Information 記下來（使用者跑 LogLevel 1，
    //    要他回報得到的等級才有意義）。
    private static void LogUnexpected(string endpoint, Exception ex)
        => Service.Logger.Information($"[DD nav] vnavmesh.{endpoint} 擲出非 IPC 例外（已忽略，不影響 BMR）: {ex}");

    // ICallGateSubscriber 建立時不探測對方在不在（純本地物件、零成本），
    // 真正的探測發生在 InvokeFunc()：對方沒註冊同名端點就丟 IpcNotReadyError。
    private static ICallGateSubscriber<T>? Gate<T>(string name)
        => Service.PluginInterface?.GetIpcSubscriber<T>("vnavmesh." + name);

    private static readonly Lazy<ICallGateSubscriber<float>?> BuildProgress = new(() => Gate<float>("Nav.BuildProgress"));
    private static readonly Lazy<ICallGateSubscriber<bool>?> NavIsReady = new(() => Gate<bool>("Nav.IsReady"));
    private static readonly Lazy<ICallGateSubscriber<bool>?> PathIsRunning = new(() => Gate<bool>("Path.IsRunning"));
    private static readonly Lazy<ICallGateSubscriber<bool>?> SimpleMoveInProgress = new(() => Gate<bool>("SimpleMove.PathfindInProgress"));

    // 📌 vnavmesh 端 Path.Stop 是 RegisterAction（無參數無回傳）→ 訂閱型別是
    //    ICallGateSubscriber<object> 且必須用 InvokeAction()。寫成 InvokeFunc() 會在執行期炸，
    //    編譯期完全看不出來。（vnavmesh/IPCProvider.cs 的 RegisterAction 多載直證。）
    private static readonly Lazy<ICallGateSubscriber<object>?> PathStop = new(() => Gate<object>("Path.Stop"));

    private static readonly Lazy<ICallGateSubscriber<List<Vector3>, bool, object>?> PathMoveTo =
        new(() => Service.PluginInterface?.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo"));

    // 📌 `Path.GetMovementAllowed` 是 RegisterFunc（回 bool），`Path.SetMovementAllowed` 是
    //    RegisterAction（吃 bool、無回傳）→ 訂閱型別分別是 <bool> 與 <bool, object>，
    //    而且後者必須用 InvokeAction()。寫錯只會在執行期炸，編譯期看不出來。
    private static readonly Lazy<ICallGateSubscriber<bool>?> PathGetMovementAllowed = new(() => Gate<bool>("Path.GetMovementAllowed"));

    private static readonly Lazy<ICallGateSubscriber<bool, object>?> PathSetMovementAllowed =
        new(() => Service.PluginInterface?.GetIpcSubscriber<bool, object>("vnavmesh.Path.SetMovementAllowed"));

    // ── 移動租約（vnavmesh v7.20.0.37 起才有的一組端點）─────────────
    // 📌 提供端逐字對照（vnavmesh/IPCProvider.cs）：
    //      RegisterFunc("Path.AcquireSuppression",       (string owner)             => Guid)
    //      RegisterFunc("Path.ReleaseSuppression",       (Guid lease)               => bool)
    //      RegisterFunc("Path.RenewSuppression",         (Guid lease)               => bool)
    //      RegisterFunc("Path.SetLeasedMovementAllowed", (Guid lease, bool allowed) => bool)
    //    ⚠️ vnavmesh 的 RegisterFunc<TRet, T1, …> 多載展開成 GetIpcProvider<T1, …, TRet>，
    //       所以訂閱端型別參數的**最後一個**才是回傳型別，前面的是參數。
    // 🔴 這四個端點的回傳全是**不可為 null 的值型別**，而且提供端保證永不回 null
    //    （失敗回 Guid.Empty／false）。宣告成別的型別不會在編譯期被擋下來：
    //    型別不同時 CallGate 會做 JSON 來回轉換而**靜默成功**，只有真的回 null 的那一次
    //    才擲一個看起來與 IPC 完全無關的 NullReferenceException。這裡兩邊逐字相同。
    private static readonly Lazy<ICallGateSubscriber<string, Guid>?> PathAcquireSuppression =
        new(() => Service.PluginInterface?.GetIpcSubscriber<string, Guid>("vnavmesh.Path.AcquireSuppression"));

    private static readonly Lazy<ICallGateSubscriber<Guid, bool>?> PathReleaseSuppression =
        new(() => Service.PluginInterface?.GetIpcSubscriber<Guid, bool>("vnavmesh.Path.ReleaseSuppression"));

    private static readonly Lazy<ICallGateSubscriber<Guid, bool>?> PathRenewSuppression =
        new(() => Service.PluginInterface?.GetIpcSubscriber<Guid, bool>("vnavmesh.Path.RenewSuppression"));

    private static readonly Lazy<ICallGateSubscriber<Guid, bool, bool>?> PathSetLeasedMovementAllowed =
        new(() => Service.PluginInterface?.GetIpcSubscriber<Guid, bool, bool>("vnavmesh.Path.SetLeasedMovementAllowed"));

    private static readonly Lazy<ICallGateSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>?>?> NavPathfind =
        new(() => Service.PluginInterface?.GetIpcSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>?>("vnavmesh.Nav.Pathfind"));

    // 📌 vnavmesh 端註冊成 (Vector3 p, bool allowUnlandable, float halfExtentXZ) => Vector3?
    //    ⚠️ 查不到落點時回傳的是 **null，不是 Vector3.Zero**——
    //    拿 Zero 當「查不到」會把地圖原點附近的合法落點誤判成失敗。
    private static readonly Lazy<ICallGateSubscriber<Vector3, bool, float, Vector3?>?> PointOnFloor =
        new(() => Service.PluginInterface?.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor"));

    /// <summary>
    /// vnavmesh 這個外掛在不在（<b>與導航網格就緒與否無關</b>）。
    /// </summary>
    /// <remarks>
    /// 存在的意義只有一個：把「不能走」拆成「要去裝外掛」與「只要等一下」兩種原因。
    /// 兩者的處置完全不同，合併成一句話等於沒說。
    /// 挑 <c>Nav.BuildProgress</c> 是因為它唯讀、零副作用，而且與網格狀態無關——
    /// 它一定註冊得起來，所以擲例外＝真的沒這個外掛。
    /// </remarks>
    public static bool IsInstalled()
    {
        try
        {
            if (BuildProgress.Value is not { } g)
                return false;
            g.InvokeFunc();
            return true;
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("Nav.BuildProgress", ex);
            return false;
        }
    }

    /// <summary>導航網格是否已經就緒。</summary>
    public static bool IsMeshReady()
    {
        try
        {
            return NavIsReady.Value?.InvokeFunc() ?? false;
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("Nav.IsReady", ex);
            return false;
        }
    }

    /// <summary>vnavmesh 目前是不是正在沿路徑移動。</summary>
    /// <remarks>
    /// 🔴 為真<b>不代表那是我們發起的移動</b>——Lifestream／Questionable 之類的也會用 vnavmesh。
    /// 拿它當「我們在移動中」顯示會說謊。呼叫端要自己記住是不是自己叫的。
    /// </remarks>
    public static bool IsPathRunning()
    {
        try
        {
            return PathIsRunning.Value?.InvokeFunc() ?? false;
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("Path.IsRunning", ex);
            return false;
        }
    }

    /// <summary>vnavmesh 的 SimpleMove 是不是正在背景算路徑（別的外掛叫的）。</summary>
    public static bool IsSimpleMovePathfinding()
    {
        try
        {
            return SimpleMoveInProgress.Value?.InvokeFunc() ?? false;
        }
        catch (IpcError)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("SimpleMove.PathfindInProgress", ex);
            return false;
        }
    }

    /// <summary>要求 vnavmesh 立刻停止移動（清空路徑點）。</summary>
    /// <remarks>
    /// ⚠️ 回傳 true 只代表「指令送出去了」，vnavmesh 端沒有回傳值可以確認真的停了。
    /// 📌 對「本來就沒在移動」是安全的無操作，呼叫端不必先查 IsRunning。
    /// </remarks>
    public static bool Stop()
    {
        try
        {
            if (PathStop.Value is not { } g)
                return false;
            g.InvokeAction();
            return true;
        }
        catch (IpcError ex)
        {
            Service.Log($"[DD nav] vnavmesh.Path.Stop 失敗: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("Path.Stop", ex);
            return false;
        }
    }

    /// <summary>
    /// vnavmesh 目前允不允許沿路徑移動；<c>null</c>＝問不到（沒安裝／端點不存在／擲例外）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>回 <c>null</c> 與回 <c>false</c> 是兩件完全不同的事</b>，不可以合併成「不允許」：
    /// 前者是「不知道」，後者是「有人（可能是別的外掛）刻意關掉了」。
    /// 呼叫端要靠這個差別決定「該不該接手」——把 null 當 false 會讓我們在問不到的情況下
    /// 誤以為別人握著開關而永遠不接手，失敗形式是<b>暫停鍵靜默沒反應</b>。
    /// </remarks>
    public static bool? GetMovementAllowed()
    {
        try
        {
            if (PathGetMovementAllowed.Value is not { } g)
                return null;
            return g.InvokeFunc();
        }
        catch (IpcError)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogUnexpected("Path.GetMovementAllowed", ex);
            return null;
        }
    }

    /// <summary>
    /// 開關 vnavmesh 的「允許沿路徑移動」。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>這是 vnavmesh 的全域開關，不是只影響我們這條路徑。</b>關掉之後 <b>任何</b>外掛
    /// （AutoDuty、Lifestream、BOCCHI…）的 vnavmesh 移動都會一起停住。所以只准在
    /// 「確實是我們發起的移動正在跑」時動它，而且<b>一定要還原</b>——留在 <c>false</c> 的話
    /// 使用者的 vnavmesh 從此不會動，而且完全沒有錯誤訊息。
    /// <para>
    /// 📌 關掉<b>不會清掉路徑點</b>（vnavmesh 的 <c>FollowPath.Update</c> 只是不再寫入移動輸入），
    /// 所以這是真正的「暫停／繼續」而不是「停止／重走」：還原成 <c>true</c> 的<b>下一幀</b>
    /// 角色就從當下所在位置沿原路徑續走，不必重算路徑，也不會倒回去補走已經過掉的路徑點。
    /// </para>
    /// </remarks>
    /// <returns>指令有沒有送出去（false＝沒安裝、端點不存在，或對方擲了例外）。</returns>
    public static bool SetMovementAllowed(bool value)
    {
        try
        {
            if (PathSetMovementAllowed.Value is not { } g)
                return false;
            g.InvokeAction(value);
            return true;
        }
        catch (IpcError ex)
        {
            Service.Log($"[DD nav] vnavmesh.Path.SetMovementAllowed 失敗: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("Path.SetMovementAllowed", ex);
            return false;
        }
    }

    /// <summary>向 vnavmesh 借移動租約的結果。</summary>
    public enum SuppressionResult
    {
        /// <summary>
        /// 這個 vnavmesh 沒有租約端點（版本比 v7.20.0.37 舊，或根本沒安裝）。
        /// ⇒ 呼叫端要退回 <see cref="SetMovementAllowed"/> 那條舊路徑。
        /// </summary>
        NotSupported,

        /// <summary>
        /// 端點在，但這一次沒發租約給我們（多半是別的外掛只借不還，把租約數撐到上限）。
        /// 🔴 <b>這種情況不可以退回舊路徑</b>：舊路徑寫的是全域開關，正是租約要消滅的東西。
        /// </summary>
        Refused,

        /// <summary>借到了。</summary>
        Acquired
    }

    /// <summary>租用者名字（＝我們的 InternalName）。vnavmesh 會把它寫進使用者的 log。</summary>
    private const string LeaseOwner = "BossModReborn";

    /// <summary>
    /// 跟 vnavmesh 借一把「這段期間請你別動」的租約。
    /// </summary>
    /// <remarks>
    /// 🔑 <b>租約是惰性的</b>：借到之後<b>還沒有壓住任何東西</b>（新租約對受控值「沒有意見」），
    /// 要真的暫停必須接著呼叫 <see cref="SetLeasedMovementAllowed"/> 傳 <see langword="false"/>。
    /// <para>
    /// 🔑 <b>不需要記得還</b>：租期上限 5 分鐘，借用者當掉／被卸載／忘了放開，逾時 vnavmesh
    /// 就自動還原成<b>使用者自己的值</b>（不是寫死的 <see langword="true"/>）並寫一行 Information
    /// 指名是誰壓著。長工作要自己每 30 秒 <see cref="RenewSuppression"/> 一次。
    /// </para>
    /// </remarks>
    public static SuppressionResult TryAcquireSuppression(out Guid lease)
    {
        lease = Guid.Empty;
        try
        {
            if (PathAcquireSuppression.Value is not { } g)
                return SuppressionResult.NotSupported;
            var id = g.InvokeFunc(LeaseOwner);
            if (id == Guid.Empty)
                return SuppressionResult.Refused;
            lease = id;
            return SuppressionResult.Acquired;
        }
        catch (IpcError)
        {
            // 端點不存在（vnavmesh 太舊或沒安裝）——這是預期中的狀況，安靜地讓呼叫端走舊路徑。
            return SuppressionResult.NotSupported;
        }
        catch (Exception ex)
        {
            // 🔑 這裡刻意也回 NotSupported 而不是 Refused：退回舊路徑＝「加租約之前的行為」，
            //    而 Refused 會讓呼叫端一直重試、暫停鍵整段沒反應。壞掉時要落回能動的那一邊。
            LogUnexpected("Path.AcquireSuppression", ex);
            return SuppressionResult.NotSupported;
        }
    }

    /// <summary>
    /// 用租約押住移動開關。<paramref name="allowed"/> 傳 <see langword="false"/>＝
    /// 「我這把要求別動」；傳 <see langword="true"/>＝「我這把不再要求別動」，
    /// <b>不是</b>「我要求放行」——使用者自己在 vnavmesh 裡取消勾選的「Allow movement」蓋不掉。
    /// </summary>
    /// <returns><see langword="false"/>＝這把租約已經不在了（逾時被掃掉／vnavmesh 重載過）。</returns>
    public static bool SetLeasedMovementAllowed(Guid lease, bool allowed)
    {
        try
        {
            return PathSetLeasedMovementAllowed.Value?.InvokeFunc(lease, allowed) ?? false;
        }
        catch (IpcError ex)
        {
            Service.Log($"[DD nav] vnavmesh.Path.SetLeasedMovementAllowed 失敗: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("Path.SetLeasedMovementAllowed", ex);
            return false;
        }
    }

    /// <summary>續約（心跳）。</summary>
    /// <remarks>
    /// ⚠️ 續約間隔不可以接近租期：vnavmesh 的 Renew 第一件事是掃除過期租約，
    /// 間隔接近租期時第一次心跳<b>必定</b>失敗（那不是競態，是每次都會發生）。
    /// </remarks>
    /// <returns>
    /// <see langword="false"/>＝<b>這把租約已經不在了</b>，呼叫端必須重新
    /// <see cref="TryAcquireSuppression"/>，<b>不可以當成續約成功</b>。
    /// </returns>
    public static bool RenewSuppression(Guid lease)
    {
        try
        {
            return PathRenewSuppression.Value?.InvokeFunc(lease) ?? false;
        }
        catch (IpcError ex)
        {
            Service.Log($"[DD nav] vnavmesh.Path.RenewSuppression 失敗: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("Path.RenewSuppression", ex);
            return false;
        }
    }

    /// <summary>交回租約。</summary>
    /// <remarks>
    /// 🔑 交回失敗<b>不需要重試到成功</b>：租約最多再壓 5 分鐘就逾時，vnavmesh 會自己
    /// 還原成使用者的值。這正是租約與舊路徑最大的差別——舊路徑還原失敗就是永久災情。
    /// </remarks>
    /// <returns><see langword="false"/>＝這把已經不在了（放開過或逾時），沒有事情要做。</returns>
    public static bool ReleaseSuppression(Guid lease)
    {
        try
        {
            return PathReleaseSuppression.Value?.InvokeFunc(lease) ?? false;
        }
        catch (IpcError ex)
        {
            Service.Log($"[DD nav] vnavmesh.Path.ReleaseSuppression 失敗: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("Path.ReleaseSuppression", ex);
            return false;
        }
    }

    /// <summary>把一條<b>已經驗過</b>的路徑交給 vnavmesh 走。</summary>
    /// <remarks>🔴 呼叫這個之前必須先做路徑點驗證，這裡不做任何檢查。</remarks>
    public static bool MoveAlong(List<Vector3> waypoints)
    {
        try
        {
            if (PathMoveTo.Value is not { } g)
                return false;
            // 第二個參數是 fly；深牢一律走路
            g.InvokeAction(waypoints, false);
            return true;
        }
        catch (IpcError ex)
        {
            Service.Log($"[DD nav] vnavmesh.Path.MoveTo 失敗: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            LogUnexpected("Path.MoveTo", ex);
            return false;
        }
    }

    /// <summary>叫 vnavmesh 算一條路徑；回傳 null＝叫不動（沒安裝／網格沒好）。</summary>
    public static Task<List<Vector3>>? Pathfind(Vector3 from, Vector3 to)
    {
        try
        {
            return NavPathfind.Value?.InvokeFunc(from, to, false);
        }
        catch (IpcError ex)
        {
            Service.Log($"[DD nav] vnavmesh.Nav.Pathfind 失敗: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            LogUnexpected("Nav.Pathfind", ex);
            return null;
        }
    }

    /// <summary>
    /// 從指定位置<b>垂直往下</b>找地板，用來把只有 X／Z 的座標補成完整三維座標。
    /// </summary>
    /// <param name="probe">探測起點，Y 要<b>高於</b>地形，否則會從地板底下往下找而落空。</param>
    /// <param name="point">找到的落點。</param>
    /// <returns>是否找到（false＝沒安裝、網格沒好，或這個位置下面沒有地板）。</returns>
    public static bool TryPointOnFloor(Vector3 probe, out Vector3 point)
    {
        try
        {
            // ⚠️ 回傳是 Vector3?，查不到是 null 不是 Zero
            if (PointOnFloor.Value?.InvokeFunc(probe, false, 5f) is { } p)
            {
                point = p;
                return true;
            }
        }
        catch (IpcError ex)
        {
            Service.Log($"[DD nav] vnavmesh.Query.Mesh.PointOnFloor 失敗: {ex.Message}");
        }
        catch (Exception ex)
        {
            LogUnexpected("Query.Mesh.PointOnFloor", ex);
        }
        point = default;
        return false;
    }
}
