using BossMod.AI;
using BossMod.Autorotation;
using System.Text.Json;

namespace BossMod;

sealed class IPCProvider : IDisposable
{
    private Action? _disposeActions;

    public IPCProvider(BossModuleManager bossmod, AIHints hints, RotationModuleManager autorotation, ActionManagerEx amex, MovementOverride movement, AIManager ai)
    {
        Register("HasModuleByDataId", (uint dataId) => BossModuleRegistry.FindByOID(dataId) != null);

        #region 機制感知（回移自上游；端點名稱逐字對齊 upstream/main:BossMod/Framework/IPCProvider.cs）

        // 🔑 這一整區都是**唯讀**端點，給外部循環外掛（RotationSolverReborn 之類）問
        //    「接下來會發生什麼機制」。BMR 是唯一持有 boss 模組時間軸的一方，外部外掛看不到
        //    StateMachine 與 AIHints，所以不開這些端點的話它們只能瞎猜。
        //
        // 🔴 失敗語意一律照上游：**沒有 active module／沒有資料 ⇒ float.MaxValue**（＝「沒有機制」），
        //    絕不擲例外。IPC 端點擲出去的例外會在呼叫端變成 InvalidOperationException，
        //    而呼叫端多半沒有 try/catch —— 等於我們把別人的外掛弄崩。
        //
        // ⚠️ 時間基準刻意沿用上游的 DateTime.Now（而不是 WorldState.CurrentTime），
        //    因為那是既有呼叫端所依據的線上契約。兩者在正常遊玩時只差不到一幀
        //    （WorldState.CurrentTime 就是該幀開始時取的 DateTime.Now）；差異只在重播回放時才顯著，
        //    而重播時本來就沒有外部外掛在問這些端點。

        Register("HasActiveModule", () => bossmod.ActiveModule?.StateMachine.ActiveState != null);
        Register("ActiveModuleName", () => bossmod.ActiveModule?.PrimaryActor.Name.ToString());

        // 走一遍狀態機並回報看到什麼；純診斷字串，沒有機器可讀契約。
        Register("Debug.TimelineWalk", () =>
        {
            var module = bossmod.ActiveModule;
            if (module == null)
            {
                return "No active module";
            }

            var sm = module.StateMachine;
            if (sm.ActiveState == null)
            {
                return "ActiveState is null";
            }

            var sb = new StringBuilder();
            sb.Append($"Phase={sm.ActivePhaseIndex} State={sm.ActiveState.ID:X}({sm.ActiveState.Name}) Dur={sm.ActiveState.Duration:F1}s Hint={sm.ActiveState.EndHint}");
            var count = 0;
            var next = sm.ActiveState;
            var foundRW = false;
            var foundTB = false;
            while (next != null && count < 20)
            {
                if (!foundRW && next.EndHint.HasFlag(StateMachine.StateHint.Raidwide))
                {
                    foundRW = true;
                    sb.Append($" | RW@{next.ID:X}({next.Name})");
                }
                if (!foundTB && next.EndHint.HasFlag(StateMachine.StateHint.Tankbuster))
                {
                    foundTB = true;
                    sb.Append($" | TB@{next.ID:X}({next.Name})");
                }
                next = next.NextStates?.Length == 1 ? next.NextStates[0] : null;
                count++;
            }
            if (!foundRW)
            {
                sb.Append(" | RW=NONE");
            }

            if (!foundTB)
            {
                sb.Append(" | TB=NONE");
            }

            if (next == null && count < 20)
            {
                sb.Append($" | Chain ended at {count} states");
            }

            if (count >= 20)
            {
                sb.Append(" | Walked 20+ states");
            }

            return sb.ToString();
        });

        // 時間軸類：沿著狀態機往後找第一個帶指定旗標的轉換。
        // 📌 StateMachine.NextTransitionWithFlag 沒有命中時回 DateTime.MaxValue，這裡折成 float.MaxValue。
        float nextTransitionIn(StateMachine.StateHint flag)
        {
            var module = bossmod.ActiveModule;
            if (module?.StateMachine.ActiveState == null)
            {
                return float.MaxValue;
            }

            var next = module.StateMachine.NextTransitionWithFlag(flag);
            return next == DateTime.MaxValue ? float.MaxValue : (float)(next - DateTime.Now).TotalSeconds;
        }

        Register("Timeline.NextRaidwideIn", () => nextTransitionIn(StateMachine.StateHint.Raidwide));
        Register("Timeline.NextTankbusterIn", () => nextTransitionIn(StateMachine.StateHint.Tankbuster));
        Register("Timeline.NextKnockbackIn", () => nextTransitionIn(StateMachine.StateHint.Knockback));
        Register("Timeline.NextDowntimeIn", () => nextTransitionIn(StateMachine.StateHint.DowntimeStart));
        Register("Timeline.NextDowntimeEndIn", () => nextTransitionIn(StateMachine.StateHint.DowntimeEnd));
        Register("Timeline.NextVulnerableIn", () => nextTransitionIn(StateMachine.StateHint.VulnerableStart));
        Register("Timeline.NextVulnerableEndIn", () => nextTransitionIn(StateMachine.StateHint.VulnerableEnd));

        // 預測傷害類：AIHints.PredictedDamage 由 boss 模組每幀重建。
        Register("Hints.NextDamageIn", () =>
        {
            var predicted = hints.PredictedDamage;
            return predicted.Count == 0 ? float.MaxValue : (float)(predicted[0].Activation - DateTime.Now).TotalSeconds;
        });

        Register("Hints.NextDamageType", () =>
        {
            var predicted = hints.PredictedDamage;
            return predicted.Count == 0 ? 0 : (int)predicted[0].Type;
        });

        // 分屬性的版本：掃**全部**條目找第一筆吻合的類型（不是只看 [0]）。
        float nextDamageOfType(AIHints.PredictedDamageType type)
        {
            var predicted = hints.PredictedDamage;
            var now = DateTime.Now;
            var count = predicted.Count;
            for (var i = 0; i < count; ++i)
            {
                if (predicted[i].Type == type)
                {
                    return (float)(predicted[i].Activation - now).TotalSeconds;
                }
            }
            return float.MaxValue;
        }

        Register("Hints.NextRaidwideDamageIn", () => nextDamageOfType(AIHints.PredictedDamageType.Raidwide));
        Register("Hints.NextTankbusterDamageIn", () => nextDamageOfType(AIHints.PredictedDamageType.Tankbuster));
        Register("Hints.PredictedDamagePlayers", () => hints.PredictedDamage.Count == 0 ? 0ul : hints.PredictedDamage[0].Players.Raw);

        Register("Hints.MaxCastTime", () => hints.MaxCastTime);

        // ⚠️ 上游把「取消詠唱」的成因拆成 Mechanic／Other 兩個欄位，**我方樹仍是單一
        //    AIHints.ForceCancelCast**（AIController.ForceCancelCast 同理，見 AIController.cs:15）。
        //    這裡把上游的四個名字全部註冊，Mechanic 與 Other 回同一個 bool ——
        //    語意上等於「兩種成因都算在內」，對呼叫端只會偏保守（該取消時一定為 true），不會漏報。
        //    🔑 上游若哪天真的把欄位拆進來，這裡只要改 lambda 內容，端點名不必動、呼叫端不必改。
        //    另外保留不帶後綴的舊名，讓既有呼叫端不必跟著改（兩者指向同一個 bool）。
        Register("Hints.ForceCancelCast", () => hints.ForceCancelCast);
        Register("Hints.ForceCancelCastMechanic", () => hints.ForceCancelCast);
        Register("Hints.ForceCancelCastOther", () => hints.ForceCancelCast);
        Register("Hints.ForceCancelCastAI", () => ai.Controller.ForceCancelCast);
        Register("Hints.ForceCancelCastMechanicAI", () => ai.Controller.ForceCancelCast);
        Register("Hints.ForceCancelCastOtherAI", () => ai.Controller.ForceCancelCast);

        Register("Movement.IsMoving", () => hints.ForcedMovement != null);
        Register("Movement.IsMoveRequested", movement.IsMoveRequested);

        Register("Hints.ForbiddenZonesCount", () => hints.ForbiddenZones.Count);
        Register("Hints.ForbiddenZonesNextActivation", () => hints.ForbiddenZones.Count == 0 ? float.MaxValue : (float)(hints.ForbiddenZones[0].activation - DateTime.Now).TotalSeconds);
        Register("Hints.ForbiddenDirectionsCount", () => hints.ForbiddenDirections.Count);
        Register("Hints.ArenaCenter", () => new Vector2(hints.PathfindMapCenter.X, hints.PathfindMapCenter.Z));
        Register("Hints.ArenaRadius", () => hints.PathfindMapBounds.Radius);
        Register("Hints.ShouldCleansePlayers", () => hints.ShouldCleanse.Raw);
        Register("Hints.InteractWithTargetOID", () => hints.InteractWithTarget?.InstanceID ?? 0ul);
        Register("Hints.RecommendedPositional", () => (int)hints.RecommendedPositional.Pos);

        // 打斷／暈眩目標：把 boss 模組與深宮樓層模組每幀寫進 AIHints.Enemy 的兩個旗標曝光給外部循環外掛。
        //
        // 🔑 資料源刻意選 hints.PotentialTargets 而不是 hints.Enemies[]，但兩者裝的是**同一批物件參考**：
        //    AIHintsBuilder.cs 裡 `hints.Enemies[index] = new(...)` 的下一行就是 `PotentialTargets.Add(enemy)`，
        //    而那是全樹唯一「產生 Enemy 物件」的地方（Clear() 的 Array.Fill 只把 Enemies[] 清成 null）；
        //    旗標的寫入端（CastHint.AddAIHints、DeepDungeon/AutoClear 等）
        //    走的 hints.FindEnemy() 就是在索引 Enemies[]。選 List 的那份是因為它沒有 null 洞，
        //    而且 BMR 自己的 AI 消費這兩個旗標時讀的正是它（xan/AI/Ranged.cs、Tank.cs、Melee.cs）。
        //
        // 🔴 這裡**不做**「敵人在不在戰鬥中」「詠唱可不可打斷」「距離夠不夠」的過濾 ——
        //    那些是呼叫端的職責，BMR 自己的 AI 也是拿到旗標後才各自加條件（見 AIBase.ShouldInterrupt：
        //    旗標之外還要 `Actor.InCombat`）。端點語意就是逐字的「模組說這隻該被打斷／該被暈」；
        //    在這裡多濾一層會把呼叫端要的判斷材料吃掉，而且濾掉的理由它看不見。
        //
        // 📌 沒有 active module 不必特別處理：旗標的唯一寫入端就是模組，沒模組就全是 false ⇒ 自然回空陣列。
        // ⚠️ 執行緒語意沿用同檔其他讀 hints 的端點（Hints.NextDamageIn、Hints.ForbiddenZonesNextActivation
        //    都是直接讀、不加鎖），假設呼叫端在 framework 執行緒上問。
        static ulong[] flaggedEnemies(List<AIHints.Enemy> targets, Func<AIHints.Enemy, bool> flagged)
        {
            // 先數再填的兩趟掃描是為了讓「一個都沒有」這個絕大多數的情況完全不配置
            // （`[]` 對陣列會被降階成 Array.Empty<ulong>()）。上限是敵人數，≤100。
            var count = targets.Count;
            var n = 0;
            for (var i = 0; i < count; ++i)
            {
                if (flagged(targets[i]))
                {
                    ++n;
                }
            }
            if (n == 0)
            {
                return [];
            }

            var res = new ulong[n];
            var j = 0;
            for (var i = 0; i < count && j < n; ++i)
            {
                var e = targets[i];
                if (flagged(e))
                {
                    res[j++] = e.Actor.InstanceID;
                }
            }
            return j == n ? res : res[..j];
        }

        Register("Hints.ShouldInterruptTargets", () => flaggedEnemies(hints.PotentialTargets, static e => e.ShouldBeInterrupted));
        Register("Hints.ShouldStunTargets", () => flaggedEnemies(hints.PotentialTargets, static e => e.ShouldBeStunned));

        Register("Hints.SpecialModeIn", () => hints.ImminentSpecialMode == default
            ? float.MaxValue
            : (float)(hints.ImminentSpecialMode.activation - DateTime.Now).TotalSeconds);
        Register("Hints.SpecialModeType", () => hints.ImminentSpecialMode == default ? 0 : (int)hints.ImminentSpecialMode.mode);

        // 位移安全性：上游是 ActionPredicate.IsDashSafe，我方樹的同一份幾何檢查叫
        // ActionDefinitions.IsDashDangerous（**回傳值相反**，所以這裡一律取反）。
        // 🔑 刻意用 IsDashDangerous 這支「純幾何」的，不是 DashFixedDistanceCheck 那些條件委派 ——
        //    後者還會看 DashSafety/DashSafetyExtra 設定與 PendingKnockbacks，
        //    那是「我方要不要攔這一發」的策略，不是呼叫端問的「這個落點安不安全」。
        Register("Hints.IsPositionSafe", (Vector3 to) =>
        {
            var player = bossmod.WorldState.Party.Player();
            return player != null && !ActionDefinitions.IsDashDangerous(player.Position, new WPos(to.X, to.Z), hints);
        });

        Register("Hints.IsDashSafe", (Vector3 from, Vector3 to) =>
            !ActionDefinitions.IsDashDangerous(new WPos(from.X, from.Z), new WPos(to.X, to.Z), hints));

        // 對齊 DashFixedDistanceCheck 的落點算法：dest = playerPos + playerRotation * range（backwards 時取負）。
        Register("Hints.IsFixedDashSafe", (float range, bool backwards) =>
        {
            var player = bossmod.WorldState.Party.Player();
            if (player == null)
            {
                return false;
            }

            var dest = player.Position + player.Rotation.ToDirection() * range * (backwards ? -1f : 1f);
            return !ActionDefinitions.IsDashDangerous(player.Position, dest, hints);
        });

        // 對齊 BackdashCheck：dir = normalize(playerPos - enemyPos)，dest = playerPos + dir * range。
        Register("Hints.IsBackdashSafe", (Vector3 enemyPos, float range) =>
        {
            var player = bossmod.WorldState.Party.Player();
            if (player == null)
            {
                return false;
            }

            var dir = (player.Position - new WPos(enemyPos.X, enemyPos.Z)).Normalized();
            var dest = player.Position + dir * range;
            return !ActionDefinitions.IsDashDangerous(player.Position, dest, hints);
        });

        Register("AI.IsNavigating", () => ai.Controller.NaviTargetPos != null);
        Register("AI.NaviTargetPos", () =>
        {
            var pos = ai.Controller.NaviTargetPos;
            return pos.HasValue ? new Vector3(pos.Value.X, 0, pos.Value.Z) : (Vector3?)null;
        });
        Register("AI.PlayerSpeed", () => ai.WorldState.Client.MoveSpeed);

        // 冷卻計畫（cooldown planner）：回傳沿著**當前生效分支**解析出來的預定動作。
        // 沒有計畫在跑時回空陣列的 JSON，不是 null —— 呼叫端可以無條件丟給 JSON 解析器。
        Register("Plan.GetUpcomingActions", (float lookAheadSeconds) =>
        {
            var planner = autorotation.Planner;
            if (planner == null)
                return "[]";

            var actions = planner.GetUpcomingPlannedActions(bossmod.WorldState, autorotation.PlayerSlot, lookAheadSeconds);
            return JsonSerializer.Serialize(actions);
        });

        // 推播：生效中的計畫換掉時發一次訊號，讓呼叫端知道該重新問 Plan.GetUpcomingActions。
        // 🔴 這個是唯一在本類別裡「訂閱別人事件」的端點，所以退訂必須掛進 _disposeActions ——
        //    漏掉的話 RotationModuleManager 會一直握著已 Dispose 的 IPCProvider 的委派。
        var plannedActionsChangedProvider = Service.PluginInterface.GetIpcProvider<object>("BossMod.Plan.ActionsChanged");
        void OnPlannedActionsChanged() => plannedActionsChangedProvider.SendMessage();
        autorotation.PlannedActionsChanged += OnPlannedActionsChanged;
        _disposeActions += () => autorotation.PlannedActionsChanged -= OnPlannedActionsChanged;

        #endregion

        Register("Configuration", (List<string> args, bool save) => Service.Config.ConsoleCommand(args.AsSpan(), save));

        DateTime lastModified = DateTime.Now;
        Service.Config.Modified.Subscribe(() => lastModified = DateTime.Now);
        Register("Configuration.LastModified", () => lastModified);

        Register("Rotation.ActionQueue.HasEntries", () =>
        {
            var entries = CollectionsMarshal.AsSpan(autorotation.Hints.ActionsToExecute.Entries);
            var len = entries.Length;
            for (var i = 0; i < len; ++i)
            {
                ref readonly var e = ref entries[i];
                if (!e.Manual)
                {
                    return true;
                }
            }
            return false;
        });

        // 完整 IPC 名稱:BossMod.ActionQueue.UseManualQueueEnabled
        // 回「手動佇列接管是否啟用」。唯讀、無副作用,每次呼叫讀設定現值(使用者中途改也拿得到新值)。
        // 🔑 給別的外掛判斷「我直接呼叫 ActionManager::UseAction 會不會被 BMR 收進它自己的佇列」——
        //    這個開關為 true 時會,為 false 時不會(見 ActionManagerEx.UseActionDetour)。
        Register("ActionQueue.UseManualQueueEnabled", () => Service.Config.Get<ActionTweaksConfig>().UseManualQueue);

        Register("Presets.Get", (string name) =>
        {
            var preset = autorotation.Database.Presets.FindPresetByName(name);
            return preset != null ? JsonSerializer.Serialize(preset, Serialization.BuildSerializationOptions()) : null;
        });
        Register("Presets.Create", (string presetSerialized, bool overwrite) =>
        {
            var p = JsonSerializer.Deserialize<Preset>(presetSerialized, Serialization.BuildSerializationOptions());
            if (p == null)
                return false;
            // 🔴 外部 IPC 直接反序列化外部字串就寫入,繞過 UI 的空名閘門(CheckNameConflict 把空白名判為衝突)。
            //    空名 preset 會讓清單/下拉/深層迷宮設定的字串比對全部退化(見 CheckNameConflict 註解),
            //    必須在寫入前擋下。回傳 false 即失敗訊號(本端點回傳型別是 bool,呼叫端已在處理)。
            if (string.IsNullOrWhiteSpace(p.Name))
            {
                // 使用者跑 LogLevel 1,要他看得到才有意義;具名說明是哪個 IPC 端點拒絕了什麼。
                Service.Logger.Information("[BMR] IPC Presets.Create 被拒:preset 名稱為空白。");
                return false;
            }
            // 查重用與 FindPresetByName/CheckNameConflict 同一個比較器（見 PresetDatabase.NameComparison）。
            var index = autorotation.Database.Presets.UserPresets.FindIndex(x => string.Equals(x.Name, p.Name, PresetDatabase.NameComparison));
            if (index >= 0 && !overwrite)
                return false;
            autorotation.Database.Presets.Modify(index, p);
            return true;
        });
        Register("Presets.Delete", (string name) =>
        {
            var index = autorotation.Database.Presets.UserPresets.FindIndex(x => string.Equals(x.Name, name, PresetDatabase.NameComparison));
            if (index < 0)
                return false;
            autorotation.Database.Presets.Modify(index, null);
            return true;
        });

        Register("Presets.GetActive", () => autorotation.Preset?.Name);
        Register("Presets.SetActive", (string name) =>
        {
            var preset = autorotation.Database.Presets.FindPresetByName(name);
            if (preset == null)
                return false;
            autorotation.Preset = preset;
            return true;
        });
        Register("Presets.ClearActive", () =>
        {
            if (autorotation.Preset == null)
                return false;
            autorotation.Preset = null;
            return true;
        });
        Register("Presets.GetForceDisabled", () => autorotation.Preset == RotationModuleManager.ForceDisable);
        Register("Presets.SetForceDisabled", () =>
        {
            if (autorotation.Preset == RotationModuleManager.ForceDisable)
                return false;
            autorotation.Preset = RotationModuleManager.ForceDisable;
            return true;
        });

        bool addTransientStrategy(string presetName, string moduleTypeName, string trackName, string value, StrategyTarget target = StrategyTarget.Automatic, int targetParam = 0)
        {
            var mt = Type.GetType(moduleTypeName);
            if (mt == null || !RotationModuleRegistry.Modules.TryGetValue(mt, out var md))
                return false;
            var iTrack = md.Definition.Configs.FindIndex(td => td.InternalName == trackName);
            if (iTrack < 0)
                return false;
            StrategyValue tempValue;
            switch (md.Definition.Configs[iTrack])
            {
                case StrategyConfigTrack tr:
                    var iOpt = tr.Options.FindIndex(od => od.InternalName == value);
                    if (iOpt < 0)
                        return false;
                    tempValue = new StrategyValueTrack() { Option = iOpt, Target = target, TargetParam = targetParam };
                    break;
                case StrategyConfigFloat sc:
                    if (!float.TryParse(value, out var fv))
                        return false;
                    tempValue = new StrategyValueFloat() { Value = Math.Clamp(fv, sc.MinValue, sc.MaxValue) };
                    break;
                case StrategyConfigInt si:
                    if (!long.TryParse(value, out var lv))
                        return false;
                    tempValue = new StrategyValueInt() { Value = Math.Clamp(lv, si.MinValue, si.MaxValue) };
                    break;
                default:
                    return false;
            }
            var ms = autorotation.Database.Presets.FindPresetByName(presetName)?.Modules.Find(m => m.Type == mt);
            if (ms == null)
                return false;
            var setting = new Preset.ModuleSetting(default, iTrack, tempValue);
            var index = ms.TransientSettings.FindIndex(s => s.Track == iTrack);
            if (index < 0)
                ms.TransientSettings.Add(setting);
            else
                ms.TransientSettings[index] = setting;
            return true;
        }
        Register("Presets.AddTransientStrategy", (string presetName, string moduleTypeName, string trackName, string value) => addTransientStrategy(presetName, moduleTypeName, trackName, value));
        Register("Presets.AddTransientStrategyTargetEnemyOID", (string presetName, string moduleTypeName, string trackName, string value, int oid) => addTransientStrategy(presetName, moduleTypeName, trackName, value, StrategyTarget.EnemyByOID, oid));

        Register("Presets.ClearTransientStrategy", (string presetName, string moduleTypeName, string trackName) =>
        {
            var mt = Type.GetType(moduleTypeName);
            if (mt == null || !RotationModuleRegistry.Modules.TryGetValue(mt, out var md))
                return false;
            var iTrack = md.Definition.Configs.FindIndex(td => td.InternalName == trackName);
            if (iTrack < 0)
                return false;
            var ms = autorotation.Database.Presets.FindPresetByName(presetName)?.Modules.Find(m => m.Type == mt);
            if (ms == null)
                return false;
            var index = ms.TransientSettings.FindIndex(s => s.Track == iTrack);
            if (index < 0)
                return false;
            ms.TransientSettings.RemoveAt(index);
            return true;
        });
        Register("Presets.ClearTransientModuleStrategies", (string presetName, string moduleTypeName) =>
        {
            var mt = Type.GetType(moduleTypeName);
            if (mt == null || !RotationModuleRegistry.Modules.TryGetValue(mt, out var md))
                return false;
            var ms = autorotation.Database.Presets.FindPresetByName(presetName)?.Modules.Find(m => m.Type == mt);
            if (ms == null)
                return false;
            ms.TransientSettings.Clear();
            return true;
        });
        Register("Presets.ClearTransientPresetStrategies", (string presetName) =>
        {
            var preset = autorotation.Database.Presets.FindPresetByName(presetName);
            if (preset == null)
                return false;
            foreach (var ms in preset.Modules)
                ms.TransientSettings.Clear();
            return true;
        });

        // 🔑 preset 名查找統一走 PresetDatabase.NameComparison（見該常數，跨外掛 IPC 契約的唯一比較器）。
        //    這裡兩側都 .Trim() 是 SetPreset 這個呼叫點的區域輸入正規化（去掉呼叫方傳來的 preset 名頭尾空白），
        //    與大小寫敏感度是兩件事——刻意保留在此呼叫點，不提進 canonical 比較器：那會回頭讓已出貨的
        //    Presets.Create/Delete/FindPresetByName 也開始 Trim，可能把本來區分得開的名字折在一起。
        Register("AI.SetPreset", (string name) => ai.SetAIPreset(autorotation.Database.Presets.AllPresets.FirstOrDefault(x => x.Name.Trim().Equals(name.Trim(), PresetDatabase.NameComparison))));
        Register("AI.GetPreset", () => ai.GetAIPreset);
    }

    public void Dispose() => _disposeActions?.Invoke();

    private void Register<TRet>(string name, Func<TRet> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<TRet>("BossMod." + name);
        p.RegisterFunc(func);
        _disposeActions += p.UnregisterFunc;
    }

    private void Register<T1, TRet>(string name, Func<T1, TRet> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<T1, TRet>("BossMod." + name);
        p.RegisterFunc(func);
        _disposeActions += p.UnregisterFunc;
    }

    private void Register<T1, T2, TRet>(string name, Func<T1, T2, TRet> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<T1, T2, TRet>("BossMod." + name);
        p.RegisterFunc(func);
        _disposeActions += p.UnregisterFunc;
    }

    private void Register<T1, T2, T3, TRet>(string name, Func<T1, T2, T3, TRet> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<T1, T2, T3, TRet>("BossMod." + name);
        p.RegisterFunc(func);
        _disposeActions += p.UnregisterFunc;
    }

    private void Register<T1, T2, T3, T4, TRet>(string name, Func<T1, T2, T3, T4, TRet> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<T1, T2, T3, T4, TRet>("BossMod." + name);
        p.RegisterFunc(func);
        _disposeActions += p.UnregisterFunc;
    }

    private void Register<T1, T2, T3, T4, T5, TRet>(string name, Func<T1, T2, T3, T4, T5, TRet> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<T1, T2, T3, T4, T5, TRet>("BossMod." + name);
        p.RegisterFunc(func);
        _disposeActions += p.UnregisterFunc;
    }

    //private void Register(string name, Action func)
    //{
    //    var p = Service.PluginInterface.GetIpcProvider<object>("BossMod." + name);
    //    p.RegisterAction(func);
    //    _disposeActions += p.UnregisterAction;
    //}

    private void Register<T1>(string name, Action<T1> func)
    {
        var p = Service.PluginInterface.GetIpcProvider<T1, object>("BossMod." + name);
        p.RegisterAction(func);
        _disposeActions += p.UnregisterAction;
    }
}
