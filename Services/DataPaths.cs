using System.IO;

namespace TraeTools.Services;

/// <summary>
/// 统一数据根目录：Windows = %APPDATA%\TraeTools；macOS = ~/Library/Application Support/TraeTools。
/// 收敛历史遗留的 TraeCheckin / TraeSwitch 三个目录。
/// WebView 缓存与抓包输出保持在 %LOCALAPPDATA%\TraeTools（缓存不进 APPDATA）。
/// 启动时调用 Migrate() 把旧目录数据自动搬入新位置（幂等、失败不阻塞）。
/// </summary>
public static class DataPaths
{
    /// <summary>
    /// 根目录：Windows = %APPDATA%\TraeTools；macOS = ~/Library/Application Support/TraeTools。
    /// .NET 在 macOS 上把 SpecialFolder.ApplicationData 映射到 ~/.config，直接用它得到的
    /// 路径不符合 mac 惯例（卸载残留难找、Time Machine 备份遗漏），所以 mac 显式走惯例路径。
    /// 两种芯片（Intel / Apple Silicon）的用户目录布局一致，同一条实现通吃，无需按架构分派。
    /// 首次访问会触发 MacLegacyRootMigration：把旧版落在 ~/.config/TraeTools 的数据整体搬过来
    /// （含 machine.key，保证已加密凭据仍可解密），幂等、失败不阻塞。
    /// </summary>
    public static string Root
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TraeTools");
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                    "Library", "Application Support", "TraeTools");
            MigrateMacLegacyRoot(root);
            return root;
        }
    }

    private static int _macLegacyMigrated;

    /// <summary>
    /// 一次性把旧根（~/.config/TraeTools）搬到新根（~/Library/Application Support/TraeTools）。
    /// 逐项搬、目标已存在则跳过（保留新数据不覆盖）；搬空后删掉旧壳，搬不空则保留旧目录
    /// （宁可残留也不丢数据）。整个流程吞异常：迁移失败最多退回"新路径从零开始"，不影响启动。
    /// </summary>
    private static void MigrateMacLegacyRoot(string newRoot)
    {
        if (Interlocked.Exchange(ref _macLegacyMigrated, 1) == 1) return;
        try
        {
            // SpecialFolder.ApplicationData 在 mac 上就是 ~/.config，旧版根目录在这里
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TraeTools");
            if (!Directory.Exists(legacy)) return;
            MoveTree(legacy, newRoot);
            // 旧目录已搬空（无任何残留条目）才删壳
            if (!Directory.EnumerateFileSystemEntries(legacy).Any())
                Directory.Delete(legacy);
        }
        catch { /* 迁移失败不阻塞启动 */ }
    }

    /// <summary>把 src 的全部条目逐个移入 dst；dst 已有同名条目时跳过（不覆盖）。逐项失败继续下一项。</summary>
    private static void MoveTree(string src, string dst)
    {
        try { Directory.CreateDirectory(dst); } catch { return; }
        foreach (var f in Directory.GetFiles(src))
            MoveFile(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src))
        {
            var target = Path.Combine(dst, Path.GetFileName(d));
            if (Directory.Exists(target))
                MoveTree(d, target);   // 同名目录：递归合并子内容
            else
            {
                try { Directory.Move(d, target); }
                catch { /* 单目录失败跳过 */ }
            }
        }
    }

    /// <summary>签到配置 config.json。</summary>
    public static string ConfigPath => Path.Combine(Root, "config.json");

    /// <summary>
    /// 数据目录：%APPDATA%\TraeTools\data
    /// 存放 checkin.db、history_*.txt、credits_total_*.txt、usage_*.jsonl。
    /// </summary>
    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(Root, "data");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// 日志目录：%APPDATA%\TraeTools\logs
    /// 存放各模块调试日志（checkin_log / account_log / cloud_log / switch_log / usage_log）。
    /// </summary>
    public static string LogsDir
    {
        get
        {
            var dir = Path.Combine(Root, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>账号切换配置（settings.json）。</summary>
    public static string SwitchDir => Path.Combine(Root, "switch");

    /// <summary>登录态备份 vault（原 %LOCALAPPDATA%\TraeSwitch\vault）。</summary>
    public static string VaultDir => Path.Combine(Root, "vault");

    /// <summary>确保所有子目录存在（启动时调用一次）。</summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(SwitchDir);
        Directory.CreateDirectory(VaultDir);
    }

    /// <summary>
    /// 把旧版目录（TraeCheckin / TraeSwitch）数据迁移到统一根，保留旧目录；幂等。
    /// 迁移来源：
    ///   %APPDATA%\TraeCheckin\config.json          → config.json
    ///   %APPDATA%\TraeCheckin\history_*.txt        → data\
    ///   %APPDATA%\TraeCheckin\checkin_log_*.txt    → logs\
    ///   %APPDATA%\TraeCheckin\usage_*.jsonl        → data\
    ///   %APPDATA%\TraeCheckin\credits_total_*.txt  → data\
    ///   %APPDATA%\TraeCheckin\data\*               → data\（中间版本）
    ///   %APPDATA%\TraeCheckin\logs\*               → logs\（中间版本）
    ///   %APPDATA%\TraeCheckin\checkin.db           → data\
    ///   %APPDATA%\TraeSwitch\settings.json         → switch\
    ///   %LOCALAPPDATA%\TraeSwitch\vault\           → vault\
    /// </summary>
    public static void Migrate()
    {
        try
        {
            EnsureDirectories();
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // ---- 1) 旧版 TraeCheckin 根目录 ----
            var oldCheckin = Path.Combine(appData, "TraeCheckin");
            if (Directory.Exists(oldCheckin))
            {
                // 配置合并
                MergeOrMoveConfigFile(Path.Combine(oldCheckin, "config.json"), ConfigPath);
                // 根目录散落文件
                foreach (var f in Directory.GetFiles(oldCheckin, "history_*.txt"))
                    MoveFile(f, Path.Combine(DataDir, Path.GetFileName(f)));
                foreach (var f in Directory.GetFiles(oldCheckin, "checkin_log_*.txt"))
                    MoveFile(f, Path.Combine(LogsDir, Path.GetFileName(f)));
                foreach (var f in Directory.GetFiles(oldCheckin, "usage_*.jsonl"))
                    MoveFile(f, Path.Combine(DataDir, Path.GetFileName(f)));
                foreach (var f in Directory.GetFiles(oldCheckin, "credits_total_*.txt"))
                    MoveFile(f, Path.Combine(DataDir, Path.GetFileName(f)));
                // 数据库（旧版直接在根目录）
                MoveFile(Path.Combine(oldCheckin, "checkin.db"), Path.Combine(DataDir, "checkin.db"));
            }

            // ---- 2) 中间版本 TraeCheckin\data\ 和 TraeCheckin\logs\ ----
            var oldData = Path.Combine(oldCheckin, "data");
            if (Directory.Exists(oldData))
            {
                foreach (var f in Directory.GetFiles(oldData))
                    MoveFile(f, Path.Combine(DataDir, Path.GetFileName(f)));
            }
            var oldLogs = Path.Combine(oldCheckin, "logs");
            if (Directory.Exists(oldLogs))
            {
                foreach (var f in Directory.GetFiles(oldLogs))
                    MoveFile(f, Path.Combine(LogsDir, Path.GetFileName(f)));
            }

            // ---- 3) 旧版 TraeSwitch 配置 ----
            var oldSwitch = Path.Combine(appData, "TraeSwitch");
            if (Directory.Exists(oldSwitch))
                MoveFile(Path.Combine(oldSwitch, "settings.json"), Path.Combine(SwitchDir, "settings.json"));

            // ---- 4) 旧版 vault（LocalAppData\TraeSwitch\vault → AppData\TraeTools\vault）----
            var oldVault = Path.Combine(localAppData, "TraeSwitch", "vault");
            if (Directory.Exists(oldVault) && !Directory.Exists(VaultDir))
            {
                Directory.CreateDirectory(VaultDir);
                // 逐个移动子目录（Directory.Move 在目标非空时会失败）
                foreach (var sub in Directory.GetDirectories(oldVault))
                {
                    var dest = Path.Combine(VaultDir, Path.GetFileName(sub));
                    if (!Directory.Exists(dest))
                        Directory.Move(sub, dest);
                }
            }
        }
        catch { /* 迁移失败不阻塞启动 */ }
    }

    private static void MoveFile(string src, string dst)
    {
        try
        {
            if (!File.Exists(src) || File.Exists(dst)) return;
            var dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Move(src, dst);
        }
        catch { /* 单文件失败跳过 */ }
    }

    /// <summary>
    /// 旧 config.json 迁移：目标不存在则直接搬；目标已存在则按账号 Id 执行并集合并
    /// （旧号带登录态但目标缺该号 → 补进目标），保证老账号不因「跳过」而丢失（需重新登录）。
    /// </summary>
    private static void MergeOrMoveConfigFile(string src, string dst)
    {
        try
        {
            if (!File.Exists(src)) return;
            var dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (!File.Exists(dst))
            {
                File.Move(src, dst);
                return;
            }
            var srcNode = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(src)) as System.Text.Json.Nodes.JsonObject;
            var dstNode = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(dst)) as System.Text.Json.Nodes.JsonObject;
            if (srcNode?["Accounts"] is System.Text.Json.Nodes.JsonArray srcAcc
                && dstNode?["Accounts"] is System.Text.Json.Nodes.JsonArray dstAcc)
            {
                var ids = dstAcc
                    .Select(a => (string?)a?["Id"])
                    .Where(id => !string.IsNullOrEmpty(id))
                    .ToHashSet(StringComparer.Ordinal);
                var added = false;
                foreach (var acc in srcAcc)
                {
                    var id = (string?)acc?["Id"];
                    if (id != null && !ids.Contains(id))
                    {
                        dstAcc.Add(acc?.DeepClone());
                        ids.Add(id);
                        added = true;
                    }
                }
                // 仅当「实际新增了账号」才写回：旧目录每次启动都存在，
                // 无条件重写会以旧序列化结构覆盖新配置（如 DPAPI 加密标记 SecretsVaultVersion 等被冲掉）。
                if (added)
                    File.WriteAllText(dst, dstNode.ToJsonString(TraeTools.Services.Json.AppJsonIndented.Options));
            }
        }
        catch { /* 合并失败保留原状 */ }
    }
}
