using System.IO;
using System.Text.Json;

namespace BossMod.Autorotation;

// note: presets in the database are immutable (otherwise eg. manager won't see the changes in active preset)
public sealed class PresetDatabase
{
    // 🔑 preset 名稱同一性的唯一比較器。preset 名是 7 個 IPC 端點的主鍵（跨外掛契約），過去三處
    //    各用不同比較：FindPresetByName 查找用 CurrentCultureIgnoreCase、UIPresetEditor.CheckNameConflict
    //    與 IPCProvider 的 Presets.Create/Delete 查重用 ordinal ==。同一個名字在不同端點得到不一致的
    //    答案（Get 命中、Delete 卻不命中）。統一為 OrdinalIgnoreCase：大小寫不敏感（符合使用者對「同名」
    //    的直覺）、且不隨系統地區改變比對結果（CurrentCulture 會）。
    //    ⚠️ 既有行為變更：大小寫僅差的兩個既有 preset，統一後查找/查重只會命中先出現的那個。
    //    這裡不改變任何**存進去**的字串，只統一「查找/查重時算不算同一個」。
    public const StringComparison NameComparison = StringComparison.OrdinalIgnoreCase;

    // 📌 新預設的預設名稱。這是會被寫進資料庫、並被 FindPresetByName／深層迷宮設定以字串比對的
    //    **資料**，不是介面文字 —— 絕對不要包成 Loc.T，換語言會讓既有設定對不上而靜默失效。
    //    放在 PresetDatabase 而非 UIPresetEditor，是因為載入時的空名自動改名（NormalizeBlankUserPresetNames）
    //    與 UI 新增預設要沿用同一個常數，不能各寫各的。
    public const string DefaultPresetName = "New";

    private readonly AutorotationConfig _cfg = Service.Config.Get<AutorotationConfig>();

    public readonly List<Preset> DefaultPresets; // default presets, distributed as part of the plugin
    public readonly List<Preset> UserPresets; // user-defined presets, stored in user's preset db
    public Event<Preset?, Preset?> PresetModified = new(); // (old, new); old == null if preset is added, new == null if preset is removed

    private readonly FileInfo _dbPath;

    public List<Preset> AllPresets
    {
        get
        {
            var countD = DefaultPresets.Count;
            var countU = UserPresets.Count;
            List<Preset> presets = new(countD + countU);
            for (var i = 0; i < countD; ++i)
            {
                var def = DefaultPresets[i];
                if (def.HiddenByDefault == _cfg.HideDefaultPreset)
                {
                    presets.Add(def);
                }
            }
            for (var i = 0; i < countU; ++i)
            {
                presets.Add(UserPresets[i]);
            }
            return presets;
        }
    }

    public PresetDatabase(string rootPath, FileInfo defaultPresets)
    {
        _dbPath = new(rootPath + ".db.json");
        BackupUserPresetsOnce();
        DefaultPresets = LoadPresetsFromFile(defaultPresets);
        UserPresets = LoadPresetsFromFile(_dbPath);
        // 🔴 在 BackupUserPresetsOnce 之後才動:改名會 Save() 覆寫 db，原檔已先備份成 .v1-backup.json。
        NormalizeBlankUserPresetNames();
    }

    /// <summary>
    /// 載入後把「名稱為空白」的既有使用者 preset 自動改成不重複的預設名並存回。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>只動空白名的那些</b>（<c>IsNullOrWhiteSpace</c>）——非空一律不碰。空名 preset 是本次改動
    /// 之前就存下來的：UI 端（CheckNameConflict）現在會擋新的空名，但擋不了舊資料，所以在載入處收口。
    /// 空名會讓清單/下拉/深層迷宮設定的字串比對退化（見 <see cref="UIPresetEditor.CheckNameConflict"/> 註解）。
    /// <para>
    /// 改名沿用 UI 新增預設的同一個 <see cref="DefaultPresetName"/> 與 " (N)" 湊唯一規則，
    /// 唯一性用 <see cref="NameComparison"/> 對 DefaultPresets＋UserPresets 一起判（與 CheckNameConflict 一致）。
    /// </para>
    /// <para>
    /// 📌 冪等：改完 Save() 後檔內不再有空名，下次載入偵測不到、不會再動。
    /// </para>
    /// </remarks>
    private void NormalizeBlankUserPresetNames()
    {
        var renamed = 0;
        for (var i = 0; i < UserPresets.Count; ++i)
        {
            var p = UserPresets[i];
            if (!string.IsNullOrWhiteSpace(p.Name))
                continue;

            // 逐號湊唯一：base 名先套 DefaultPresetName，被占用就往上加 " (N)"。
            var newName = DefaultPresetName;
            var n = 1;
            while (NameTakenExcept(newName, i))
                newName = $"{DefaultPresetName} ({n++})";

            // 載入當下沒有任何 manager 持有這些物件（建構期），就地改 Name 是安全的。
            p.Name = newName;
            ++renamed;
            // 使用者跑 LogLevel 1，Information 才看得到
            Service.Logger.Information($"[BMR] 偵測到空名循環預設，已自動改名為「{newName}」。");
        }
        if (renamed > 0)
            Save();
    }

    // 名字是否已被 DefaultPresets 或 UserPresets（排除 selfIndex 這一筆）占用 —— 與 CheckNameConflict 同語意、同比較器。
    private bool NameTakenExcept(string name, int selfIndex)
    {
        for (var i = 0; i < DefaultPresets.Count; ++i)
            if (string.Equals(DefaultPresets[i].Name, name, NameComparison))
                return true;
        for (var i = 0; i < UserPresets.Count; ++i)
            if (i != selfIndex && string.Equals(UserPresets[i].Name, name, NameComparison))
                return true;
        return false;
    }

    /// <summary>
    /// 第一次載入時，把使用者的 preset 資料庫逐位元組複製一份備份。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>為什麼需要</b>：策略軌的<b>序列化鍵</b>（track／option 的 InternalName）
    /// 一旦改變，舊存檔裡的字串就對不上，載入時 <c>FindIndex</c> 回 -1，
    /// 那一條設定<b>靜默消失</b>，而且存檔一被覆寫就再也救不回來。
    /// 循環框架改版必然會動到這些鍵，所以在任何載入發生之前先留一份原檔。
    /// <para>
    /// 📌 <b>只做一次</b>：備份檔已存在就不再覆寫 —— 否則第二次啟動會拿「已經失效的新檔」
    /// 蓋掉「還完好的舊備份」，那比沒有備份更糟。
    /// </para>
    /// <para>
    /// ⚠️ 逐位元組複製，不解析也不重新序列化：這份備份的用途是讓使用者手動還原，
    /// 經過我們的序列化器就不再是原檔了。
    /// </para>
    /// </remarks>
    private void BackupUserPresetsOnce()
    {
        try
        {
            if (!_dbPath.Exists)
                return;

            var backup = new FileInfo(Path.Combine(
                _dbPath.DirectoryName ?? "",
                Path.GetFileNameWithoutExtension(_dbPath.Name) + ".v1-backup.json"));
            if (backup.Exists)
                return;

            File.Copy(_dbPath.FullName, backup.FullName);
            // 使用者跑 LogLevel 1，要他看得到才有意義
            Service.Logger.Information(
                $"[Autorotation] 循環預設資料庫已備份到 {backup.FullName}。" +
                "循環框架改版會讓舊的預設內容失效、需要重新建立；這份是改版前的原檔，只會產生一次。");
        }
        catch (Exception ex)
        {
            // 🔴 備份失敗不可以擋住外掛載入，但一定要說出來 —— 否則使用者會以為自己有備份
            Service.Logger.Information($"[Autorotation] 備份循環預設資料庫失敗（將繼續載入，但沒有備份）: {ex}");
        }
    }

    private List<Preset> LoadPresetsFromFile(FileInfo file)
    {
        if (!file.Exists)
            return [];

        try
        {
            var data = PlanPresetConverter.PresetSchema.Load(file);
            using var json = data.document;
            // 逐筆反序列化：JsonPresetConverter.Read 對缺鍵/壞值會擲例外，整份一起反序列化時
            // 任何一筆壞 preset 會讓所有 preset 靜默消失 —— 壞的略過、好的保留。
            var opts = Serialization.BuildSerializationOptions();
            List<Preset> res = [];
            var index = 0;
            foreach (var jp in data.payload.EnumerateArray())
            {
                ++index;
                try
                {
                    var p = jp.Deserialize<Preset>(opts);
                    if (p != null)
                        res.Add(p);
                }
                catch (Exception ex)
                {
                    var name = jp.ValueKind == JsonValueKind.Object && jp.TryGetProperty(nameof(Preset.Name), out var jn) ? jn.ToString() : $"第 {index} 筆";
                    // 使用者跑 LogLevel 1，Information 才看得到
                    Service.Logger.Information($"[Autorotation] 循環預設「{name}」損毀，已略過（{file.Name} 裡其餘預設不受影響）: {ex.Message}");
                }
            }
            return res;
        }
        catch (Exception ex)
        {
            Service.Logger.Information($"[Autorotation] 循環預設資料庫 '{file.FullName}' 無法解析，本次以空清單載入（原檔與備份未動）: {ex.Message}");
            return [];
        }
    }

    // if index >= 0: replace or delete
    // if index == -1: add (if replacement is non-null) or notify about reordering (otherwise)
    public void Modify(int index, Preset? replacement)
    {
        var previous = index >= 0 ? UserPresets[index] : null;

        if (index < 0 && replacement != null)
            UserPresets.Add(replacement);
        else if (index >= 0 && replacement == null)
            UserPresets.RemoveAt(index);
        else if (index >= 0 && replacement != null)
            UserPresets[index] = replacement;

        if (previous != null || replacement != null)
            PresetModified.Fire(previous, replacement);

        Save();
    }

    public void Save()
    {
        try
        {
            PlanPresetConverter.PresetSchema.Save(_dbPath, jwriter => JsonSerializer.Serialize(jwriter, UserPresets, Serialization.BuildSerializationOptions()));
            Service.Log($"Database saved successfully to '{_dbPath.FullName}'");
        }
        catch (Exception ex)
        {
            Service.Log($"Failed to write database to '{_dbPath.FullName}': {ex}");
        }
    }

    public List<Preset> PresetsForClass(Class c)
    {
        var visible = AllPresets;
        var count = visible.Count;
        List<Preset> presets = new(count);
        for (var i = 0; i < count; ++i)
        {
            var vis = visible[i];
            var pm = vis.Modules;
            var countM = pm.Count;
            for (var j = 0; j < countM; ++j)
            {
                var pmj = pm[j];
                if (pmj.Definition.Classes[(int)c])
                {
                    presets.Add(vis);
                    break;
                }
            }
        }
        return presets;
    }

    public Preset? FindPresetByName(ReadOnlySpan<char> name, StringComparison cmp = NameComparison)
    {
        foreach (var p in AllPresets)
            if (name.Equals(p.Name, cmp))
                return p;
        return null;
    }
}
