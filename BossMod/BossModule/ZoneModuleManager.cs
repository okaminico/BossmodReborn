namespace BossMod;

public sealed class ZoneModuleManager : IDisposable
{
    public readonly WorldState WorldState;
    public static readonly ZoneModuleConfig Config = Service.Config.Get<ZoneModuleConfig>();
    private readonly EventSubscriptions _subsciptions;

    public ZoneModule? ActiveModule;
    public Event<ZoneModule> ModuleLoaded = new();
    public Event<ZoneModule> ModuleUnloaded = new();

    public ZoneModuleManager(WorldState ws)
    {
        WorldState = ws;
        _subsciptions = new
        (
            WorldState.CurrentZoneChanged.Subscribe(op => OnZoneChanged(op.CFCID))
        );
        OnZoneChanged(ws.CurrentCFCID);
    }

    public void Dispose()
    {
        _subsciptions.Dispose();
        ActiveModule?.Dispose();
    }

    // 事件通知與釋放的失敗只記一行 Error 就繼續 —— 這幾條路徑上已經沒有別的地方可以回報了,
    // 所以連「寫 log 本身失敗」也必須吞掉,否則又會中斷呼叫端正在做的釋放/分發。
    // 🔴 這裡處理的只有受管理例外;AVE 在 .NET Core 是 corrupted-state exception,catch 攔不到。
    private static void LogIsolatedFailure(Exception ex, string message)
    {
        try
        {
            Service.Logger.Error(ex, message);
        }
        catch
        {
        }
    }

    private void OnZoneChanged(uint cfcid)
    {
        if (ActiveModule != null)
        {
            Service.Log($"[ZMM] Unloading zone module '{ActiveModule.GetType()}'");
            // 🔴 訂閱者擲出的受管理例外不可以害死下面的釋放。Event<T>.Fire 就是一個裸的多播委派
            //    Invoke(Util/Event.cs 的 _ev?.Invoke(a1)),沒有任何防護 ⇒ 不隔離的話,擲一次例外
            //    就會讓 Dispose() 與 ActiveModule = null 整段不執行:舊的區域模組連同它自己的訂閱
            //    一起留著,而 Plugin.DrawUI 的 _zonemod.ActiveModule?.Update() 下一幀還會繼續跑它,
            //    外面看起來只像「換區時報了一個錯」。
            // 📌 今天這兩顆事件全 repo 零訂閱者(唯一訂 ModuleLoaded/ModuleUnloaded 的是
            //    ReplayBuilder.cs:61-62,而它訂的是 BossModuleManager 的那一對,不是這一對)⇒
            //    Fire 這段是純保險;真正今天就會擲例外的是下面的 Dispose()。
            // 🔴 順序刻意不動:先 Fire、讓訂閱者拿到「還沒被釋放」的模組是既有語意,
            //    對調是行為改變,要改請連訂閱者的期待一起裁決。
            try
            {
                ModuleUnloaded.Fire(ActiveModule);
            }
            catch (Exception ex)
            {
                LogIsolatedFailure(ex, $"[ZMM] 「{ActiveModule?.GetType().Name}」的 ModuleUnloaded 訂閱者擲出例外，已略過通知、照常釋放這個區域模組。");
            }
            // 🔴 ZoneModule.Dispose(bool) 是 virtual 而且真的有覆寫者會做外部工作 ——
            //    AutoClear.cs:301 的覆寫會還 vnavmesh 租約、退 WrathCombo 租約、解訂閱、釋放障礙圖。
            //    擲例外的話 ActiveModule 會留著指向一個半釋放的模組,而 Plugin.DrawUI 每一幀都會對它
            //    呼叫 Update() ⇒ 這裡記一行 Error 之後照樣把 ActiveModule 清成 null。
            try
            {
                // ! 是編譯期註記(不產生 IL):外層 if (ActiveModule != null) 已經保證非 null,
                //   但上面的 try/catch 讓編譯器對這個欄位的 null 狀態重新保守估計 ⇒ CS8602。
                //   這裡刻意保留「讀欄位」而不是先存進區域變數 —— 存區域變數會改變語意
                //   (訂閱者若在 Fire 裡換掉 ActiveModule,原本的碼會釋放換上去的那一顆)。
                ActiveModule!.Dispose();
            }
            catch (Exception ex)
            {
                LogIsolatedFailure(ex, $"[ZMM] 「{ActiveModule?.GetType().Name}」的 Dispose() 擲出例外,區域模組可能只釋放了一半;照樣把 ActiveModule 清成 null,避免下一幀又對它呼叫 Update()。");
            }
            ActiveModule = null;
        }

        var m = ZoneModuleRegistry.CreateModule(WorldState, cfcid, Config.MinMaturity);
        if (m != null)
        {
            Service.Log($"[ZMM] Loading module '{m.GetType()}' for zone {cfcid}");
            ActiveModule = m;
            // 🔴 Fire 排在 ActiveModule = m 之後,所以訂閱者擲例外時模組已經掛上去了(不會漏掉);
            //    但例外會一路傳出 OnZoneChanged -> WorldState.CurrentZoneChanged 的 Fire,
            //    而那顆事件在實機上是多方共用的:ZoneModuleManager 自己(:18)、
            //    ReplayManagementWindow.OnZoneChange(:47),以及每一個 BossModule 與 AIHintsBuilder
            //    各自持有的 ObstacleMapManager.LoadMaps(:32)。
            //    ⇒ 訂閱者數量隨載入的模組變動、順序也不固定(Plugin.cs:145 建 _bossmod 時就可能
            //      先掛上一批,早於 :146 的 _zonemod),但無論順序如何,擲一次例外都會讓排在自己
            //      後面的那些換區處理整批不執行,而且是靜默的。
            try
            {
                ModuleLoaded.Fire(m);
            }
            catch (Exception ex)
            {
                LogIsolatedFailure(ex, $"[ZMM] 「{m.GetType().Name}」的 ModuleLoaded 訂閱者擲出例外，已略過通知、區域模組照常留在 ActiveModule 上。");
            }
        }
    }
}
