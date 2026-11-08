using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeCheckin;
using TraeSwitch.Services;
using TraeTools.Services;
using TraeTools.Services.Usage;

namespace TraeTools.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly DashboardViewModel _dashboard;
    private readonly CheckinViewModel _checkin;
    private readonly SwitchViewModel _switch;
    private readonly CloudViewModel _cloud;
    private readonly SettingsViewModel _settings;
    private readonly UsageViewModel _usage;
    private readonly LogViewModel _log;

    [ObservableProperty]
    private ViewModelBase? _currentPage;

    [ObservableProperty]
    private string _currentPageKey = "dashboard";

    // 全局服务容器：页面 VM 通过静态属性访问
    public static AppConfig? AppConfig;
    public static TraeApiClient? CheckinApi;
    public static TraeUsageClient? UsageApi;
    public static SettingsStore? SwitchSettings;
    public static VaultService? Vault;
    public static GitHubApiClient? GitHubApi;
    public static CheckinDatabase? CheckinDb;

    /// <summary>界面展示的应用版本号，与"关于/更新检查"（TraeTools.Services.AppVersion）同源。</summary>
    public string AppVersion => Services.AppVersion.Display;

    public MainViewModel()
    {
        // 初始化真实服务（try-catch，失败则为 null，页面 VM 用 mock 兜底）
        try
        {
            AppConfig = AppConfig.Load();
            // 全局代理注入（签到/用量/GitHub 接口 + 登录浏览器统一走此代理；账号可覆盖）
            NetProxy.SetGlobal(NetProxy.ProxyAddress(AppConfig.ProxyEnabled, AppConfig.ProxyServer));
        }
        catch
        {
            AppConfig = new AppConfig();
        }

        try
        {
            CheckinApi = new TraeApiClient();
        }
        catch
        {
            CheckinApi = null;
        }

        try
        {
            UsageApi = new TraeUsageClient();
        }
        catch
        {
            UsageApi = null;
        }

        try
        {
            SwitchSettings = new SettingsStore(CarrierDefaults.SettingsDir);
        }
        catch
        {
            SwitchSettings = null;
        }

        try
        {
            if (SwitchSettings != null)
            {
                var vaultRoot = TraeTools.Services.DataPaths.VaultDir;
                Vault = new VaultService(SwitchSettings.Data.RootDir, vaultRoot);
            }
        }
        catch
        {
            Vault = null;
        }

        try
        {
            GitHubApi = new GitHubApiClient();
        }
        catch
        {
            GitHubApi = null;
        }

        try
        {
            CheckinDb = new CheckinDatabase();
            // 首次启动时从旧版文本文件迁移数据到数据库
            // DataPaths.Migrate() 已在 Program.cs 中先行调用，文件已搬入新目录
            // 这里扫描新目录（DataDir）即可；同时兼容扫描旧目录以防迁移未执行
            int migrated = 0;
            var newDataDir = TraeTools.Services.DataPaths.DataDir;
            var oldBaseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TraeCheckin");
            var oldDataDir = Path.Combine(oldBaseDir, "data");
            foreach (var dir in new[] { newDataDir, oldBaseDir, oldDataDir })
            {
                if (Directory.Exists(dir))
                    migrated += CheckinDb.MigrateFromHistoryFiles(dir)
                              + CheckinDb.MigrateFromSnapshotFiles(dir);
            }
            if (migrated > 0)
                AccountHelpers.AppLog("account", "", $"从旧版文本文件导入 {migrated} 条新记录到数据库（幂等：已有记录不会重复计入）");
        }
        catch
        {
            CheckinDb = null;
        }

        Instance = this;
        _dashboard = new DashboardViewModel();
        _checkin = new CheckinViewModel();
        _switch = new SwitchViewModel();
        _cloud = new CloudViewModel();
        _settings = new SettingsViewModel();
        _settings.LoadExtendedSettings();   // 载入本 PR 新增的推送/日志/主题/设备号配置
        // 设置页下拉改主题时，标题栏 ☾/☀ 图标同步刷新
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.ThemeMode))
                OnPropertyChanged(nameof(IsDarkMode));
        };
        _usage = new UsageViewModel();
        _log = new LogViewModel();

        _dashboard.NavigateRequested += p => Navigate(p);
        _checkin.NavigateRequested += p => Navigate(p);
        _switch.NavigateRequested += p => Navigate(p);
        _cloud.NavigateRequested += p => Navigate(p);
        _settings.NavigateRequested += p => Navigate(p);
        _usage.NavigateRequested += p => Navigate(p);
        _log.NavigateRequested += p => Navigate(p);

        // 顶栏账号下拉：默认选中当前激活账号（账号列表由 SwitchViewModel 在构造时载入）
        try
        {
            var cfg0 = AppConfig;
            var active = cfg0?.Accounts.FirstOrDefault(a => a.Id == cfg0.ActiveAccountId)
                         ?? cfg0?.Accounts.FirstOrDefault();
            if (active != null)
                _switch.SelectedAccount = _switch.Accounts.FirstOrDefault(a => a.Id == active.Id);
            RefreshTopBar();
        }
        catch { /* 顶栏初始化失败不影响主流程 */ }

        CurrentPage = _dashboard;
        CurrentPageKey = "dashboard";

        // 首次启动即按激活账号刷新仪表盘状态/积分/奖励（否则启动时只显示旧缓存，状态不更新）
        _ = _dashboard.RefreshAllAsync();

        // 启动日志：记录账号概况，便于排查问题
        try
        {
            if (AppConfig != null)
            {
                var accs = AppConfig.Accounts;
                AccountHelpers.AppLog("account", "", $"===== 程序启动，共 {accs.Count} 个账号 =====");
                foreach (var a in accs)
                {
                    var n = string.IsNullOrEmpty(a.Name) ? (a.Id.Length > 6 ? a.Id[..6] : a.Id) : a.Name!;
                    AccountHelpers.AppLog("account", n, $"DeviceId={a.DeviceId}，Token={(string.IsNullOrEmpty(a.Token) ? "无" : $"有({a.Token!.Length})")}，Enabled={a.Enabled}，LastCheckin={a.LastCheckinDate?.ToString("MM-dd HH:mm") ?? "无"}");
                }
            }
        }
        catch { /* 启动日志失败不影响 */ }
    }

    public bool IsDashboardActive => CurrentPageKey == "dashboard";
    public bool IsCheckinActive => CurrentPageKey == "checkin";
    public bool IsSwitchActive => CurrentPageKey == "switch";
    public bool IsCloudActive => CurrentPageKey == "cloud";
    public bool IsSettingsActive => CurrentPageKey == "settings";
    public bool IsUsageActive => CurrentPageKey == "usage";
    public bool IsLogActive => CurrentPageKey == "log";

    /// <summary>标题栏显示的当前页面名。</summary>
    public string CurrentPageTitle => CurrentPageKey switch
    {
        "usage" => "用量统计",
        "checkin" => "每日签到",
        "switch" => "账号切换",
        "cloud" => "云端签到",
        "log" => "运行日志",
        "settings" => "设置",
        _ => "仪表盘",
    };

    /// <summary>标题栏 ☾/☀ 图标状态：跟随当前生效主题实时变化（设置页下拉改动也会同步）。</summary>
    public bool IsDarkMode => ThemeService.IsDark;

    /// <summary>标题栏快捷开关：在明亮 / 深色之间切换；「跟随系统」仍可在设置页选择。</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        _settings.ThemeMode = ThemeService.IsDark ? ThemeService.Light : ThemeService.Dark;
        OnPropertyChanged(nameof(IsDarkMode));
    }

    partial void OnCurrentPageKeyChanged(string value)
    {
        OnPropertyChanged(nameof(IsDashboardActive));
        OnPropertyChanged(nameof(IsCheckinActive));
        OnPropertyChanged(nameof(IsSwitchActive));
        OnPropertyChanged(nameof(IsCloudActive));
        OnPropertyChanged(nameof(IsSettingsActive));
        OnPropertyChanged(nameof(IsUsageActive));
        OnPropertyChanged(nameof(IsLogActive));
        OnPropertyChanged(nameof(CurrentPageTitle));
    }

    /// <summary>账号管理切换激活账号后：仪表盘完整刷新 + 签到页 + 账号状态同步 + 用量页联动。</summary>
    public static async void NotifyActiveAccountChanged()
    {
        if (Instance is not { } vm) return;
        try
        {
            await vm._dashboard.RefreshAllAsync();
            vm._checkin.Reload();
            vm._usage.InvalidateExpiryCache();   // 切换账号后强制重扫积分过期
            vm._usage.Reload();
            await vm._settings.RefreshAccountStatesAsync();
            vm.RefreshTopBar();   // 顶栏「当前生效」文案与下拉选中项随之更新
        }
        catch { /* 异步刷新失败不应导致崩溃 */ }
    }

    /// <summary>签到完成后同步仪表盘与账号状态。</summary>
    public static async Task NotifyAsyncRefresh()
    {
        if (Instance is not { } vm) return;
        await vm._dashboard.RefreshAllAsync();
        await vm._settings.RefreshAccountStatesAsync();
    }

    /// <summary>当前 VM 实例（供静态通知使用）。</summary>
    public static MainViewModel? Instance { get; private set; }

    /// <summary>由 MainWindow 定时器定时调用：到点自动签到（TraeCheckin「每日自动签到」能力）。</summary>
    public Task<bool> AutoCheckinIfDue() => _checkin.TryAutoCheckinAsync();

    /// <summary>
    /// 由 MainWindow 定时器与启动流程调用：过了「签到时间 + 10 分钟」仍有账号未签 → 告警。
    /// 云端定时任务失败、本机没开机、自动签到被风控拦下，最终都会落到这个判据上。
    /// </summary>
    public Task<bool> CheckMissedCheckinIfDue() => _checkin.CheckMissedCheckinAsync();

    /// <summary>到点未签告警事件（MainWindow 订阅后弹托盘通知 + 写界面日志，兜底无推送渠道的情况）。</summary>
    public static event Action<string, string>? MissedAlertRaised;

    /// <summary>触发告警事件（异常不影响告警主流程，推送已经先行发出）。</summary>
    public static void RaiseMissedAlert(string title, string content)
    {
        try { MissedAlertRaised?.Invoke(title, content); }
        catch { /* 界面提示失败不影响已发出的推送 */ }
    }

    /// <summary>
    /// 仪表盘「一键签到」入口：与「每日签到」页共用同一条批量路径与同一份账号勾选
    /// （勾选真源是 TraeAccount.Enabled），不做第二套选择逻辑。
    /// </summary>
    public Task<(bool Any, int Ok, int Total, double Gained, string Summary)> RunBatchCheckinAsync()
        => _checkin.RunBatchCheckinAsync();

    /// <summary>勾选（Enabled）账号数 / 今日未签的已勾选账号数，供仪表盘按钮文案与可用性判断。</summary>
    public (int Checked, int Pending, int IntervalSeconds) BatchCheckinScope()
    {
        var cfg = AppConfig;
        if (cfg == null) return (0, 0, 5);
        var checkedAccs = cfg.Accounts.Where(a => a.Enabled).ToList();
        int pending = checkedAccs.Count(a => !(a.LastCheckinDate.HasValue && a.LastCheckinDate.Value.Date == DateTime.Today));
        int interval = cfg.CheckinIntervalSeconds > 0 ? cfg.CheckinIntervalSeconds : 5;
        return (checkedAccs.Count, pending, interval);
    }

    /// <summary>由 MainWindow 定时器定时调用：客户端已退出时静默刷新当前账号的 vault 备份（本次运行只刷一次）。</summary>
    public Task RefreshSnapshotIfNeeded() => _switch.TryAutoRefreshSnapshotAsync();

    /// <summary>托盘「立即签到」：对当前账号执行一次签到（不弹窗，由调用方提示结果）。</summary>
    public async Task<bool> QuickCheckinAsync()
    {
        try
        {
            var cfg = AppConfig;
            if (cfg == null) return false;
            var acc = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                      ?? cfg.Accounts.FirstOrDefault();
            if (acc == null || string.IsNullOrEmpty(acc.Token)) return false;
            var name = string.IsNullOrEmpty(acc.Name) ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id) : acc.Name!;

            // token 失效先静默换新（账号级闸门防重复探测）
            if (!await AccountHelpers.EnsureValidTokenAsync(acc) || string.IsNullOrEmpty(acc.Token))
            {
                AccountHelpers.CheckinLog(name, "[托盘快签] Token 校验/换新失败，请重新登录");
                return false;
            }

            // 独立 API 实例：隔离 LastError + 走账号代理；9074 风控自动换设备号重试
            var api = new TraeApiClient(AccountHelpers.ResolveProxy(acc));
            AccountHelpers.CheckinLog(name, $"[托盘快签] 开始签到，DeviceId={acc.DeviceId}");
            var result = await AccountHelpers.ClaimWithRiskRetryAsync(api, acc, name);
            if (result == null || result.code != 0)
            {
                AccountHelpers.CheckinLog(name, $"[托盘快签] Claim 失败：code={result?.code ?? -1}, message={result?.message ?? "null"}");
                return false;
            }
            acc.LastCheckinDate = DateTime.Now;
            cfg.LastCheckinDate = DateTime.Now;
            try
            {
                // 解析本次所得并写入签到历史（与签到页/自动签到同口径）
                var after = await api.GetStatusAsync(acc.Token, acc.DeviceId);
                double gained = TraeCheckin.CheckinEvaluator.ResolveGainedCredits(after ?? result, acc.IsMember);
                AccountHelpers.AppendHistory(acc, gained);
                AccountHelpers.CheckinLog(name, $"[托盘快签] 签到成功，获得 {gained} 积分");
            }
            catch { /* 历史写入失败不影响 */ }
            try
            {
                var credits = await api.GetRemainingCreditsAsync(acc.Token, acc.DeviceId);
                if (credits >= 0)
                {
                    acc.RemainingCredits = credits;   // 按账号持久化
                    cfg.LastRemaining = credits;
                }
            }
            catch { /* 积分刷新失败不影响签到结果 */ }
            try { cfg.Save(); } catch { /* 忽略 */ }
            return true;
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        switch (page)
        {
            case "dashboard":
                _ = _dashboard.RefreshAllAsync();   // 每次进入仪表盘都按当前激活账号真实验证刷新
                CurrentPage = _dashboard;
                CurrentPageKey = "dashboard";
                break;
            case "checkin":
                _checkin.Reload();     // 同上，签到页跟随激活账号
                CurrentPage = _checkin;
                CurrentPageKey = "checkin";
                break;
            case "switch":
                CurrentPage = _switch;
                CurrentPageKey = "switch";
                break;
            case "cloud":
                CurrentPage = _cloud;
                CurrentPageKey = "cloud";
                break;
            case "settings":
                _settings.LoadExtendedSettings();   // 每次进入都从配置重读，避免多页面改配置后界面过期
                _ = _settings.RefreshAccountStatesAsync();
                CurrentPage = _settings;
                CurrentPageKey = "settings";
                break;
            case "usage":
                    _usage.Reload();        // 先展示本地记录，再后台增量同步近 7 天
                    _ = _usage.RefreshCommand.ExecuteAsync(null);
                    CurrentPage = _usage;
                    CurrentPageKey = "usage";
                    break;
            case "log":
                // 进页面时重建「最近」内存缓冲并重扫磁盘文件（日志文件可能被其它进程追加）
                _log.ReloadRecent();
                _log.RefreshFiles();
                CurrentPage = _log;
                CurrentPageKey = "log";
                break;
        }
    }
}