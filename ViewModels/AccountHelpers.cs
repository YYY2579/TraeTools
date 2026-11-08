using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraeCheckin;

namespace TraeTools.ViewModels;

/// <summary>账号相关跨 VM 共享工具（DeviceId 补发 / Token 会话换新 / 签到风控重试）。</summary>
public static class AccountHelpers
{
    // ==================== Token 校验/换新的并发治理 ====================
    // 背景：仪表盘、设置页、签到批处理、用量页会并发对同一账号调 EnsureValidTokenAsync，
    // 每次 = 1 次 status 探测 + 可能的 GetUserToken 换新。曾经同一账号在一秒内被并发
    // 探测/换新 5+ 次，触发 Trae 接口限流（1001 / GetUserToken 返回异常），日志里
    // "Session 换新 Token 失败（返回为空）" 刷屏。此处做三层收敛：
    //   1) 校验结果缓存：短时间内的重复校验直接复用结果；
    //   2) 换新限流：同一账号换新失败后 45 秒内不再重试；
    //   3) 并发闸门：同一账号的换新同一时刻只允许一个在跑，后来者等待并复用结果。

    /// <summary>校验成功结果缓存时长（期间不再打 status 探测）。</summary>
    private static readonly TimeSpan StatusCacheTtl = TimeSpan.FromSeconds(90);
    /// <summary>换新失败后的冷却时长（期间快速失败，不 hammer 接口）。</summary>
    private static readonly TimeSpan RenewalCooldown = TimeSpan.FromSeconds(45);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> TokenGates = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DateTime> LastRenewalAttempt = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DateTime> ValidUntil = new(StringComparer.Ordinal);

    /// <summary>标记账号 token 在此刻验证通过（缓存 StatusCacheTtl）。</summary>
    private static void MarkTokenValid(string accountId) => ValidUntil[accountId] = DateTime.Now.Add(StatusCacheTtl);

    /// <summary>供外部（如快签成功）确认 token 有效，随后一小段时间内不再重复探测。</summary>
    public static void MarkTokenValidPublic(string accountId) => MarkTokenValid(accountId);

    /// <summary>登录态变化（重新登录/换新失败）后调用，避免缓存掩盖真实状态。</summary>
    public static void InvalidateTokenCache(string accountId) => ValidUntil.TryRemove(accountId, out _);

    /// <summary>解析账号生效代理：账号自有优先，其次全局；null = 直连。</summary>
    public static string? ResolveProxy(TraeAccount acc)
        => TraeTools.Services.NetProxy.Resolve(acc?.Proxy);

    /// <summary>缺号则生成不与其它账号重复的 16 位数字设备号（风控要求，多账号共用会触发 9074）。</summary>
    public static void EnsureDeviceId(TraeAccount acc)
    {
        var used = new System.Collections.Generic.HashSet<string>(
            MainViewModel.AppConfig?.Accounts.Where(a => a.Id != acc.Id).Select(a => a.DeviceId)
               .Where(d => !string.IsNullOrWhiteSpace(d)) ?? Array.Empty<string>(),
            StringComparer.Ordinal);
        // 已有且不与其它账号重复 → 保持不动（幂等）；为空或与别人相同（手工填错）→ 换发独立新号
        if (!string.IsNullOrWhiteSpace(acc.DeviceId) && !used.Contains(acc.DeviceId)) return;
        acc.DeviceId = NewDeviceId(used);
    }

    /// <summary>生成一个不在 used 集合内的 16 位数字设备号。</summary>
    private static string NewDeviceId(System.Collections.Generic.HashSet<string> used)
    {
        string id;
        do { id = Random.Shared.NextInt64(1_000_000_000_000_000L, 10_000_000_000_000_000L).ToString(); }
        while (used.Contains(id));
        return id;
    }

    /// <summary>校验/换新 Token：失效时用 X-Cloudide-Session 静默换新并保存；返回是否持有可用 token。</summary>
    public static async Task<bool> EnsureValidTokenAsync(TraeAccount acc)
    {
        var cfg = MainViewModel.AppConfig;
        var api = MainViewModel.CheckinApi;
        var name = string.IsNullOrEmpty(acc.Name) ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id) : acc.Name!;
        if (cfg == null || api == null) return false;

        // 1) 校验结果缓存：最近刚验证过就直接放行，省掉一次 status 探测
        if (ValidUntil.TryGetValue(acc.Id, out var until) && until > DateTime.Now)
            return true;

        // 2) 并发闸门：同一账号同一时刻只走一个"探测+换新"流程，其余等待后复用缓存
        var gate = TokenGates.GetOrAdd(acc.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            // 双检：排队期间可能已被前一个等待者验证通过
            if (ValidUntil.TryGetValue(acc.Id, out until) && until > DateTime.Now)
                return true;

            // 换新专用客户端：隔离 LastError，且走账号自己的代理
            var renewApi = new TraeApiClient(ResolveProxy(acc));

            if (!string.IsNullOrEmpty(acc.Token))
            {
                var st = await renewApi.GetStatusAsync(acc.Token, acc.DeviceId);
                if (st != null && st.code == 0)
                {
                    MarkTokenValid(acc.Id);
                    return true;
                }
                CheckinLog(name, $"Token 已失效（status code={st?.code ?? -1}），尝试 Session 换新");
            }

            // token 失效：用会话 Cookie 静默换新（无需重新登录）
            if (!string.IsNullOrEmpty(acc.Session))
            {
                // 3) 换新限流：刚失败过就别再 hammer 接口（失败原因多为限流，越打越失败）
                if (LastRenewalAttempt.TryGetValue(acc.Id, out var last)
                    && DateTime.Now - last < RenewalCooldown)
                {
                    CheckinLog(name, $"距上次 Session 换新仅 {(int)(DateTime.Now - last).TotalSeconds}s（此前失败：{renewApi.LastError ?? "原因未知"}），暂缓重试");
                    return false;
                }
                LastRenewalAttempt[acc.Id] = DateTime.Now;

                var renewed = await renewApi.GetUserTokenAsync(acc.Session);
                if (!string.IsNullOrEmpty(renewed))
                {
                    var uid = TokenUtils.ParseAccountUid(renewed);
                    // 防污染：换新出来的 token 归属与账号记录的 AccountUid 不符时拒绝采用
                    if (!string.IsNullOrEmpty(acc.AccountUid) && !string.IsNullOrEmpty(uid) && uid != acc.AccountUid)
                    {
                        CheckinLog(name, $"Session 换新出的 Token 属于其他用户（uid={uid}，期望 {acc.AccountUid}），已拒绝，Session 可能已串号");
                        return false;
                    }
                    acc.Token = renewed;
                    acc.AccountUid = uid ?? acc.AccountUid;
                    acc.TokenUpdatedAt = DateTime.Now;
                    try { cfg.Save(); } catch { /* 忽略 */ }
                    MarkTokenValid(acc.Id);
                    CheckinLog(name, $"Session 换新 Token 成功，新 AccountUid={acc.AccountUid}");
                    return true;
                }
                CheckinLog(name, $"Session 换新 Token 失败（{renewApi.LastError ?? "返回为空"}）");
            }
            else
            {
                CheckinLog(name, "Token 失效且无 Session，无法自动换新（请重新登录；重新登录成功后会同时保存 Session）");
            }
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    // ==================== 签到 9074 风控重试（对齐 checkin.py 策略） ====================
    // checkin.py：命中 9074「当前参与用户太多」= 设备号被风控标记 → 换全新设备号重试（最多 5 次）。
    // C# 版此前只会失败一次就放弃，这是"没法签到"的直接原因之一。

    /// <summary>9074 重试上限（与 checkin.py 一致：首次 + 4 次换号重试）。</summary>
    private const int ClaimRiskRetries = 4;

    /// <summary>
    /// 带风控重试的签到：命中 9074 时自动换新设备号重试，成功后把可用设备号持久化。
    /// 返回与 ClaimAsync 相同的结果对象；全部重试后仍失败则返回最后一次的结果。
    /// </summary>
    public static async Task<TraeCheckin.CheckinStatus?> ClaimWithRiskRetryAsync(
        TraeCheckin.TraeApiClient api, TraeAccount acc, string displayName)
    {
        EnsureDeviceId(acc);
        var result = await api.ClaimAsync(acc.Token ?? "", acc.DeviceId);
        int attempt = 1;
        while (result != null && result.code == 9074 && attempt <= ClaimRiskRetries)
        {
            var used = new System.Collections.Generic.HashSet<string>(
                MainViewModel.AppConfig?.Accounts.Where(a => a.Id != acc.Id).Select(a => a.DeviceId)
                   .Where(d => !string.IsNullOrWhiteSpace(d)) ?? Array.Empty<string>(),
                StringComparer.Ordinal);
            acc.DeviceId = NewDeviceId(used);
            attempt++;
            CheckinLog(displayName, $"命中风控 9074，换新设备号重试（第 {attempt - 1}/{ClaimRiskRetries} 次）DeviceId={acc.DeviceId}");
            await Task.Delay(Random.Shared.Next(800, 1500));
            result = await api.ClaimAsync(acc.Token ?? "", acc.DeviceId);
        }
        // 换出来的设备号能签到 → 持久化（下次启动继续用；若以后再被标记，重试机制会再换）
        if (result is { code: 0 } && !string.IsNullOrEmpty(acc.DeviceId))
        {
            try { MainViewModel.AppConfig?.Save(); } catch { /* 忽略 */ }
        }
        return result;
    }

    /// <summary>
    /// 拉取账号资料（昵称/脱敏手机号/头像）+ 学生认证状态并回填 TraeAccount；返回是否成功。
    /// 结果按「账号·当天」内存缓存：设置页每次刷新只会联网拉一次，避免每账号每次 3 个请求。
    /// force=true 时跳过缓存（用于刚登录的账号，立即拿到最新资料）。
    /// </summary>
    public static async Task<bool> RefreshProfileAsync(TraeAccount acc, bool force = false)
    {
        var cfg = MainViewModel.AppConfig;
        var name = string.IsNullOrEmpty(acc.Name) ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id) : acc.Name!;
        if (cfg == null || string.IsNullOrEmpty(acc.Token)) return false;
        // 资料接口走账号自己的代理（独立实例隔离 LastError）
        var api = new TraeApiClient(ResolveProxy(acc));

        // 当日已有缓存：直接回填，不再打接口
        lock (ProfileCacheLock)
        {
            if (!force && ProfileCache.TryGetValue(acc.Id, out var cached) && cached.Date == DateTime.Today)
            {
                acc.ScreenName = cached.ScreenName;
                acc.MobileMasked = cached.MobileMasked;
                acc.AvatarUrl = cached.AvatarUrl;
                acc.IsStudent = cached.IsStudent;
                if (string.IsNullOrWhiteSpace(acc.Name) && !string.IsNullOrEmpty(cached.ScreenName))
                    acc.Name = cached.ScreenName;
                return true;
            }
        }

        bool valid = await EnsureValidTokenAsync(acc);   // 先保证 token 可用
        if (!valid)
        {
            CheckinLog(name, "拉取账号资料失败：Token 无效且换新失败");
            return false;
        }
        var token = acc.Token ?? "";

        try
        {
            var profile = await api.GetUserProfileAsync(token, acc.Session);
            if (profile != null)
            {
                acc.ScreenName = profile.ScreenName;
                acc.MobileMasked = profile.MobileMasked;
                acc.AvatarUrl = profile.AvatarUrl;
                if (string.IsNullOrEmpty(acc.AccountUid) && !string.IsNullOrEmpty(profile.UserId))
                    acc.AccountUid = profile.UserId;
                // 未手动备注时用平台昵称充当展示名（Name 非空则保留用户备注）
                if (string.IsNullOrWhiteSpace(acc.Name) && !string.IsNullOrEmpty(profile.ScreenName))
                    acc.Name = profile.ScreenName;
                CheckinLog(name, $"资料拉取成功：昵称={profile.ScreenName}，手机={profile.MobileMasked}");
            }
            else
            {
                CheckinLog(name, "资料拉取返回 null");
            }
            int student = await api.GetStudentStatusAsync(token, acc.Session);
            acc.IsStudent = student == 1;
            try { cfg.Save(); } catch { /* 忽略 */ }

            lock (ProfileCacheLock)
            {
                ProfileCache[acc.Id] = new ProfileCacheEntry
                {
                    Date = DateTime.Today,
                    ScreenName = acc.ScreenName,
                    MobileMasked = acc.MobileMasked,
                    AvatarUrl = acc.AvatarUrl,
                    IsStudent = acc.IsStudent,
                };
            }
            return true;
        }
        catch (Exception ex)
        {
            CheckinLog(name, $"资料拉取异常：{ex.Message}");
            return false;
        }
    }

    // ---- 账号资料按天缓存（避免每次进设置页都打接口） ----

    private sealed class ProfileCacheEntry
    {
        public DateTime Date { get; init; }
        public string? ScreenName { get; init; }
        public string? MobileMasked { get; init; }
        public string? AvatarUrl { get; init; }
        public bool IsStudent { get; init; }
    }

    private static readonly object ProfileCacheLock = new();
    private static readonly Dictionary<string, ProfileCacheEntry> ProfileCache = new(StringComparer.Ordinal);

    // ==================== 数据存储目录（统一走 DataPaths）====================

    internal static readonly object HistoryIoLock = new();

    /// <summary>数据目录（数据库、签到历史、用量等），统一走 DataPaths.DataDir。</summary>
    internal static string DataDir => TraeTools.Services.DataPaths.DataDir;

    /// <summary>日志目录（各模块调试日志），统一走 DataPaths.LogsDir。</summary>
    internal static string LogsDir => TraeTools.Services.DataPaths.LogsDir;

    /// <summary>兼容旧名，指向 DataDir。</summary>
    internal static string HistoryDir => DataDir;

    private static bool _legacyHistoryCleaned;

    /// <summary>
    /// 清理旧版「每账号一份」的历史文件 history_{32位GUID}.txt（新版本统一写 history_yyyyMM.txt）。
    /// 幂等：整个进程只执行一次；读取期有锁保护。
    /// </summary>
    public static void CleanupLegacyHistoryFiles()
    {
        if (_legacyHistoryCleaned) return;
        _legacyHistoryCleaned = true;
        try
        {
            lock (HistoryIoLock)
            {
                if (!Directory.Exists(HistoryDir)) return;
                foreach (var file in Directory.GetFiles(HistoryDir, "history_*.txt"))
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(
                            Path.GetFileName(file), @"^history_[0-9a-fA-F]{32}\.txt$"))
                    {
                        try { File.Delete(file); } catch { /* 单个删除失败跳过 */ }
                    }
                }
            }
        }
        catch { /* 清理失败不影响 */ }
    }

    /// <summary>
    /// 记录一次签到成功（同时写入 SQLite 数据库与旧版 history 文本文件保持兼容）。
    /// </summary>
    public static void AppendHistory(TraeCheckin.TraeAccount acc, double gained)
    {
        var name = string.IsNullOrEmpty(acc.Name) ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id) : acc.Name;

        // 写入 SQLite
        try
        {
            MainViewModel.CheckinDb?.InsertCheckin(DateTime.Now, acc.Id, name, gained, acc.IsMember);
        }
        catch { /* 数据库写入失败不影响签到 */ }

        // 同时写入旧版文本文件（保持兼容，后续版本可移除）
        try
        {
            lock (HistoryIoLock)
            {
                Directory.CreateDirectory(HistoryDir);
                var historyFile = Path.Combine(HistoryDir, $"history_{DateTime.Now:yyyyMM}.txt");
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm} | {name} | 每日签到 | +{(int)gained}";
                File.AppendAllText(historyFile, line + Environment.NewLine);
            }
        }
        catch { /* 历史写入失败不影响签到 */ }
    }

    // ==================== 通用分类日志（排查问题用） ====================

    /// <summary>
    /// 写入一条分类调试日志。**统一走 <see cref="TraeTools.Services.AppLog"/>**：
    /// 该出口同时维护内存环形缓冲（日志页「最近」实时可见）、触发 EntryAdded 事件，
    /// 并按 <c>{category}_log_yyyyMM.txt</c> 落盘（与历史文件名一致，旧日志仍可读）。
    ///
    /// 历史坑：本方法原先是直接 File.AppendAllText 自己落盘，绕开了内存缓冲，
    /// 导致日志页「最近」永远为空（只有仪表盘刷新等少数走 Services.AppLog 的路径才有记录）。
    /// 各模块按 category 归档：checkin / account / cloud / switch / usage。
    /// </summary>
    public static void AppLog(string category, string accountName, string message)
        => TraeTools.Services.AppLog.Info(category, accountName, message);

    /// <summary>签到日志快捷方法（兼容已有调用）。</summary>
    public static void CheckinLog(string accountName, string message)
        => AppLog("checkin", accountName, message);
}
