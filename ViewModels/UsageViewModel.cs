using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeCheckin;
using TraeTools.Models;
using TraeTools.Services.Usage;

namespace TraeTools.ViewModels;

/// <summary>
/// 用量统计页：按激活账号展示逐会话用量（模型 / token / 缓存命中 / 积分）。
/// 数据来源 = 本地持久化（usage_&lt;accId&gt;.jsonl） + 逆向 usage 接口增量拉取。
/// </summary>
public partial class UsageViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _currentAccount = "未添加账号";

    [ObservableProperty]
    private string _dataRangeText = "暂无本地记录";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isLoading;

    // ---- 积分过期日志（全部账号，可按账号筛选） ----

    private const string ExpiryFilterAll = "全部账号";

    /// <summary>筛选后的展示行（由 _allExpiryRows 按账号过滤得出）。</summary>
    public ObservableCollection<ExpiryLogRow> ExpiryLogRows { get; } = new();

    /// <summary>扫描全量数据（筛选的数据源）。</summary>
    private List<ExpiryLogRow> _allExpiryRows = new();

    [ObservableProperty]
    private string _expirySummaryText = "";

    [ObservableProperty]
    private bool _isExpiryLoading;

    /// <summary>保活是否进行中（防止重复发送测试消息）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeepAliveButtonText))]
    private bool _isKeepAliveRunning;

    /// <summary>保活结果反馈文字（独立于拉取用量的 StatusMessage，不会被其它动作覆盖）。</summary>
    [ObservableProperty]
    private string _keepAliveStatusText = "";

    /// <summary>保活状态：idle / running / ok / fail（驱动状态条颜色与可见性）。</summary>
    [ObservableProperty]
    private string _keepAliveState = "idle";

    public bool KeepAliveIsRunning => KeepAliveState == "running";
    public bool KeepAliveIsOk => KeepAliveState == "ok";
    public bool KeepAliveIsFail => KeepAliveState == "fail";
    /// <summary>按钮文字：进行中显示「保活中…」防重复点击。</summary>
    public string KeepAliveButtonText => IsKeepAliveRunning ? "保活中…" : "保活";

    partial void OnKeepAliveStateChanged(string value)
    {
        OnPropertyChanged(nameof(KeepAliveIsRunning));
        OnPropertyChanged(nameof(KeepAliveIsOk));
        OnPropertyChanged(nameof(KeepAliveIsFail));
    }

    /// <summary>账号筛选当前值："全部账号" 或具体账号名。</summary>
    [ObservableProperty]
    private string _expiryFilterAccount = ExpiryFilterAll;

    public ObservableCollection<string> ExpiryFilterOptions { get; } = new();

    private bool _suppressFilterApply;   // 重建选项时抑制联动（选项变化不算筛选动作）

    partial void OnExpiryFilterAccountChanged(string value)
    {
        if (!_suppressFilterApply) ApplyExpiryFilter();
    }

    private void ApplyExpiryFilter()
    {
        var rows = ExpiryFilterAccount == ExpiryFilterAll || ExpiryFilterAccount == ""
            ? _allExpiryRows
            : _allExpiryRows.Where(r => r.Account == ExpiryFilterAccount).ToList();

        ExpiryLogRows.Clear();
        foreach (var r in rows) ExpiryLogRows.Add(r);

        double urgent = rows.Where(r => r.Level != "ok").Sum(r => r.Credits);
        double totalAll = rows.Sum(r => r.Credits);
        var scope = ExpiryFilterAccount == ExpiryFilterAll || ExpiryFilterAccount == ""
            ? $"{_allExpiryRows.Select(r => r.Account).Distinct().Count()} 个账号"
            : ExpiryFilterAccount;
        ExpirySummaryText = rows.Count == 0
            ? "没有待过期的积分包（扫描一次可刷新）"
            : $"{scope} 共 {totalAll:0} 分待过期" + (urgent > 0 ? $"（7 天内 {urgent:0} 分）" : "");
    }

    private static DateTime? _lastExpiryScan;  // 上次扫描时间（3 分钟内不重复扫，手动按钮/切账号强制刷新）
    private static DateTime? _expiryLogDate;   // 每天自动写一条日志（手动刷新不限）

    /// <summary>扫描全部启用账号的积分包过期时间并刷新展示 + 落日志。</summary>
    [RelayCommand]
    private async Task RefreshExpiryLog()
    {
        var cfg = MainViewModel.AppConfig;
        if (cfg == null || IsExpiryLoading) return;
        var targets = cfg.Accounts.Where(a => a.Enabled && !string.IsNullOrEmpty(a.Token)).ToList();
        if (targets.Count == 0) { ExpirySummaryText = "没有可用账号"; return; }

        IsExpiryLoading = true;
        ExpirySummaryText = "扫描中…";
        try
        {
            var rows = new List<ExpiryLogRow>();
            var summaries = new List<string>();

            foreach (var acc in targets)
            {
                var accName = string.IsNullOrEmpty(acc.Name) ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id) : acc.Name!;
                try
                {
                    if (!await AccountHelpers.EnsureValidTokenAsync(acc) || string.IsNullOrEmpty(acc.Token))
                    {
                        summaries.Add($"{accName}：登录态失效");
                        continue;
                    }
                    // 该账号自己的代理
                    var usageApi = new TraeTools.Services.Usage.TraeUsageClient(AccountHelpers.ResolveProxy(acc));
                    var packs = await usageApi.GetCurrentEntitlementsAsync(acc.Token, acc.Session);
                    var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    double accTotal = 0;
                    var accLines = new List<string>();
                    foreach (var p in packs.Where(p => p.Remaining > 0 && p.EndTimeSec > nowSec && p.EndTimeSec < 4102444800)
                                           .OrderBy(p => p.EndTimeSec))
                    {
                        var expireLocal = DateTimeOffset.FromUnixTimeSeconds(p.EndTimeSec).ToLocalTime();
                        var days = (int)Math.Ceiling((p.EndTimeSec - nowSec) / 86400.0);
                        rows.Add(new ExpiryLogRow
                        {
                            Account = accName,
                            Name = string.IsNullOrEmpty(p.Name) ? "积分包" : p.Name,
                            Credits = p.Remaining,
                            ExpireText = expireLocal.ToString("yyyy-MM-dd"),
                            DaysText = days <= 0 ? "今天内" : $"{days} 天",
                            Level = days <= 3 ? "urgent" : days <= 7 ? "warn" : "ok",
                            DaysLeftSort = p.EndTimeSec,
                        });
                        accTotal += p.Remaining;
                        accLines.Add($"{p.Name} {p.Remaining:0}分→{expireLocal:MM-dd}");
                    }
                    summaries.Add($"{accName}：{(accTotal > 0 ? $"待过期 {accTotal:0} 分（{rows.Count(r => r.Account == accName)} 个包）" : "暂无待过期积分")}");

                    // 每日一条日志落盘（usage_log），手动刷新不重复写
                    if (accLines.Count > 0 && _expiryLogDate != DateTime.Today)
                        AccountHelpers.AppLog("usage", accName, $"[积分过期日志] {string.Join("；", accLines)}");
                }
                catch (Exception ex)
                {
                    summaries.Add($"{accName}：查询异常 {ex.Message}");
                }
            }

            if (_expiryLogDate != DateTime.Today) _expiryLogDate = DateTime.Today;

            _allExpiryRows = rows.OrderBy(r => r.DaysLeftSort).ThenBy(r => r.Account).ToList();

            // 重建筛选项：全部账号 + 各账号（尽量保留当前选择）
            var keep = ExpiryFilterAccount;
            ExpiryFilterOptions.Clear();
            ExpiryFilterOptions.Add(ExpiryFilterAll);
            foreach (var name in _allExpiryRows.Select(r => r.Account).Distinct())
                ExpiryFilterOptions.Add(name);
            _suppressFilterApply = true;
            ExpiryFilterAccount = ExpiryFilterOptions.Contains(keep) ? keep : ExpiryFilterAll;
            _suppressFilterApply = false;

            ApplyExpiryFilter();   // 显式重建展示（值未变时 OnChanged 不会触发，必须显式调）
            AccountHelpers.AppLog("usage", "", $"[积分过期日志] 扫描完成：{ExpirySummaryText}（{rows.Count} 个待过期包）");
        }
        catch (Exception ex)
        {
            ExpirySummaryText = "扫描失败：" + ex.Message;
        }
        finally
        {
            IsExpiryLoading = false;
        }
    }

    /// <summary>页面进入 / 切换账号时刷新（3 分钟缓存内不重复扫；手动按钮不限）。</summary>
    public void RefreshExpiryLogIfDue()
    {
        if (_lastExpiryScan == null || DateTime.Now - _lastExpiryScan.Value > TimeSpan.FromMinutes(3))
            _ = RefreshExpiryLogCommand.ExecuteAsync(null);
    }

    /// <summary>切换账号等场景强制下一次查看时重新扫描。</summary>
    public void InvalidateExpiryCache() => _lastExpiryScan = null;


    // ---- 汇总卡片 ----
    [ObservableProperty]
    private int _totalSessions;

    [ObservableProperty]
    private string _totalCreditsText = "0";

    [ObservableProperty]
    private string _totalTokensText = "0";

    [ObservableProperty]
    private string _totalTokensDetail = "";

    [ObservableProperty]
    private string _cacheHitText = "0";

    [ObservableProperty]
    private double _cacheHitRate;

    // ---- 积分快过期提醒 ----
    [ObservableProperty]
    private bool _hasExpiring;

    [ObservableProperty]
    private string _expiringTotalText = "";

    public ObservableCollection<ExpiringEntItem> ExpiringItems { get; } = new();
    public ObservableCollection<UsageSessionRecord> Sessions { get; } = new();
    public ObservableCollection<ModelUsageStat> ModelStats { get; } = new();

    private const int MaxSessionsShown = 500;   // 列表展示上限（汇总仍按全量）
    /// <summary>可选的拉取时间范围（天）。实测 usage_type=[7] 时近 7 天常为空，默认放宽到 30 天。</summary>
    public int[] FetchDayOptions { get; } = { 7, 30, 90 };

    [ObservableProperty]
    private int _fetchDays = 30;

    private const int ExpireWarnDays = 7;       // 提前多少天提醒积分过期

    public UsageViewModel()
    {
        Reload();
    }

    /// <summary>账号切换/进入页面时同步展示（读本地，不主动拉接口）。只跟随全局激活账号。</summary>
    public void Reload()
    {
        var cfg = MainViewModel.AppConfig;
        var acc = cfg?.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                  ?? cfg?.Accounts.FirstOrDefault();
        if (acc == null)
        {
            CurrentAccount = "未添加账号";
            Sessions.Clear();
            ModelStats.Clear();
            TotalSessions = 0;
            return;
        }
        RefreshExpiryLogIfDue();   // 页面进入时每天自动扫一次积分过期（手动按钮不限）
        _ = RefreshExpiringForCurrentAccountAsync(acc);   // 切账号后顶部提醒立即更新，无需点「拉取用量」
        CurrentAccount = string.IsNullOrEmpty(acc.Name)
            ? $"账号@{(acc.AccountUid ?? acc.Id.Substring(0, 6))}"
            : acc.Name;
        LoadFromStore(acc.Id);
    }

    /// <summary>真实拉取：近 7 天分页 → 增量落盘 → 刷新展示。</summary>
    [RelayCommand]
    private async Task Refresh()
    {
        var cfg = MainViewModel.AppConfig;
        var acc = cfg?.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                  ?? cfg?.Accounts.FirstOrDefault();
        if (acc == null)
        {
            StatusMessage = "未添加账号";
            return;
        }
        // 用量接口走当前账号的代理
        var usageApi = new TraeTools.Services.Usage.TraeUsageClient(AccountHelpers.ResolveProxy(acc));

        if (IsLoading) return;
        IsLoading = true;
        StatusMessage = "正在拉取用量数据…";
        var accName = string.IsNullOrEmpty(acc.Name) ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id) : acc.Name!;
        AccountHelpers.AppLog("usage", accName, $"开始拉取用量数据（近 {FetchDays} 天）");
        try
        {
            bool valid = await AccountHelpers.EnsureValidTokenAsync(acc);
            if (!valid)
            {
                AccountHelpers.AppLog("usage", accName, "Token 已失效且无法换新");
                StatusMessage = "Token 已失效且无法换新，请重新登录";
                return;
            }

            var start = DateTime.Now.AddDays(-FetchDays);
            var end = DateTime.Now;
            var fetched = await usageApi.FetchAllAsync(acc.Token ?? "", acc.Session, start, end);
            if (fetched.Count == 0 && !string.IsNullOrEmpty(usageApi.LastError))
            {
                AccountHelpers.AppLog("usage", accName, $"拉取失败：{usageApi.LastError}");
                StatusMessage = $"拉取失败：{usageApi.LastError}";
                return;
            }

            UsageStore.Merge(acc.Id, fetched);
            LoadFromStore(acc.Id);

            // 积分快过期提醒（当前持有包的剩余/到期；expired_ents 是已过期历史，语义不同）
            await RefreshExpiringForCurrentAccountAsync(acc);

            AccountHelpers.AppLog("usage", accName, $"拉取完成：{fetched.Count} 条新会话");
            StatusMessage = fetched.Count > 0
                ? $"已同步 {fetched.Count} 条新会话（近 {FetchDays} 天）"
                : $"近 {FetchDays} 天没有用量记录（接口连接正常；可在上方把范围调大到 90 天再试）";
        }
        catch (Exception ex)
        {
            AccountHelpers.AppLog("usage", accName, $"拉取异常：{ex.Message}");
            StatusMessage = "拉取异常：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 手动保活：向当前激活账号发送一条测试消息。用户主动点击才执行，不自动定时。
    /// 日志只记成败，不记消息内容与凭证。
    /// </summary>
    [RelayCommand]
    private async Task KeepAlive()
    {
        if (IsKeepAliveRunning) { KeepAliveStatusText = "保活进行中，请稍候"; return; }

        var cfg = MainViewModel.AppConfig;
        var acc = cfg?.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                  ?? cfg?.Accounts.FirstOrDefault();
        if (acc == null || string.IsNullOrEmpty(acc.Token))
        {
            KeepAliveState = "fail";
            KeepAliveStatusText = "✗ 未找到带登录凭证的账号";
            return;
        }

        var accName = string.IsNullOrEmpty(acc.Name) ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id) : acc.Name!;
        IsKeepAliveRunning = true;
        KeepAliveState = "running";
        KeepAliveStatusText = "正在发送测试消息（你好）… 请稍候";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!await AccountHelpers.EnsureValidTokenAsync(acc) || string.IsNullOrEmpty(acc.Token))
            {
                KeepAliveState = "fail";
                KeepAliveStatusText = "✗ Token 已失效且无法换新，请重新登录";
                AccountHelpers.AppLog("usage", accName, "保活失败：Token 不可用");
                return;
            }

            var client = new KeepAliveClient(AccountHelpers.ResolveProxy(acc));
            var (ok, detail) = await client.SendTestMessageAsync(acc.Token);
            sw.Stop();
            if (ok)
            {
                KeepAliveState = "ok";
                KeepAliveStatusText = $"✓ 保活成功：{detail}，耗时 {sw.Elapsed.TotalSeconds:0.0}s，账号活跃度已刷新";
                AccountHelpers.AppLog("usage", accName, "保活完成（消息内容不入日志）");
            }
            else
            {
                KeepAliveState = "fail";
                KeepAliveStatusText = "✗ 保活失败：" + detail;
                AccountHelpers.AppLog("usage", accName, "保活失败：" + detail);
            }
        }
        catch (Exception ex)
        {
            KeepAliveState = "fail";
            KeepAliveStatusText = "✗ 保活异常：" + ex.Message;
            AccountHelpers.AppLog("usage", accName, "保活异常：" + ex.Message);
        }
        finally
        {
            IsKeepAliveRunning = false;
        }
    }

    /// <summary>
    /// 刷新「积分快过期提醒」（当前生效账号）：切账号/进页面/拉取用量后都会调用，
    /// 只发 1 个请求，切换账号后无需点「拉取用量」也能更新。
    /// </summary>
    public async Task RefreshExpiringForCurrentAccountAsync(TraeCheckin.TraeAccount? acc = null)
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            acc ??= cfg?.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                    ?? cfg?.Accounts.FirstOrDefault();
            if (acc == null || string.IsNullOrEmpty(acc.Token)) { HasExpiring = false; return; }
            var usageApi = new TraeTools.Services.Usage.TraeUsageClient(AccountHelpers.ResolveProxy(acc));
            if (!await AccountHelpers.EnsureValidTokenAsync(acc) || string.IsNullOrEmpty(acc.Token)) { HasExpiring = false; return; }
            var packs = await usageApi.GetCurrentEntitlementsAsync(acc.Token, acc.Session);
            UpdateExpiring(packs
                .Where(p => p.Remaining > 0)
                .Select(p => new ExpiredEnt
                {
                    Name = p.Name,
                    CreditsLimit = p.Remaining,
                    ExpireTimeMs = p.EndTimeSec * 1000,
                }).ToList());
        }
        catch { HasExpiring = false; /* 提醒失败不影响主流程 */ }
    }

    /// <summary>从本地读取该账号记录并重建列表 + 汇总 + 模型分布。</summary>
    private void LoadFromStore(string accountId)
    {
        var all = UsageStore.LoadAll(accountId);

        // ---- 汇总 ----
        TotalSessions = all.Count;
        double credits = all.Sum(r => r.CreditsFloat);
        TotalCreditsText = credits.ToString("0.#");
        long input = all.Sum(r => r.InputToken);
        long output = all.Sum(r => r.OutputToken);
        long cache = all.Sum(r => r.CacheReadToken);
        TotalTokensText = FormatTokens(input + output);
        TotalTokensDetail = $"入 {FormatTokens(input)} / 出 {FormatTokens(output)}";
        CacheHitText = FormatTokens(cache);
        CacheHitRate = input > 0 ? cache * 100.0 / input : (cache > 0 ? 100 : 0);

        if (all.Count == 0)
        {
            DataRangeText = "暂无本地记录（点「拉取用量」同步）";
            Sessions.Clear();
            ModelStats.Clear();
            return;
        }

        var min = DateTimeOffset.FromUnixTimeSeconds(all.Min(r => r.UsageTime)).ToLocalTime();
        var max = DateTimeOffset.FromUnixTimeSeconds(all.Max(r => r.UsageTime)).ToLocalTime();
        DataRangeText = $"本地记录 {min:MM-dd} ~ {max:MM-dd}，共 {all.Count} 次会话";

        // ---- 会话列表（最新 N 条） ----
        Sessions.Clear();
        foreach (var r in all.Take(MaxSessionsShown))
            Sessions.Add(r);

        // ---- 模型分布 ----
        ModelStats.Clear();
        foreach (var g in all.GroupBy(r => r.ModelText).OrderByDescending(g => g.Sum(r => r.TotalToken)))
        {
            ModelStats.Add(new ModelUsageStat
            {
                ModelName = g.Key,
                SessionCount = g.Count(),
                TotalInput = g.Sum(r => r.InputToken),
                TotalOutput = g.Sum(r => r.OutputToken),
                TotalCacheRead = g.Sum(r => r.CacheReadToken),
                TotalCredits = g.Sum(r => r.CreditsFloat),
            });
        }
    }

    private static string FormatTokens(long n)
    {
        return n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M"
            : n >= 1_000 ? $"{n / 1_000.0:0.#}K"
            : n.ToString();
    }

    /// <summary>从过期资格包列表里筛出「近 N 天即将过期」的，刷新提醒卡片。</summary>
    private void UpdateExpiring(List<ExpiredEnt> expired)
    {
        ExpiringItems.Clear();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        double total = 0;
        foreach (var e in expired.OrderBy(e => e.ExpireTimeMs))
        {
            if (e.ExpireTimeMs <= nowMs) continue;          // 已过期的不提醒
            if (e.CreditsLimit <= 0) continue;
            var item = new ExpiringEntItem
            {
                Name = e.Name,
                CreditsLimit = e.CreditsLimit,
                ExpireTimeMs = e.ExpireTimeMs,
            };
            if (item.DaysLeft > ExpireWarnDays) continue;   // 提前量之外的不提醒
            ExpiringItems.Add(item);
            total += e.CreditsLimit;
        }
        HasExpiring = ExpiringItems.Count > 0;
        ExpiringTotalText = HasExpiring ? $"共 {total:0} 积分即将过期" : "";
    }
}