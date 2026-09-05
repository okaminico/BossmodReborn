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
