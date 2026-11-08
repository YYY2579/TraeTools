using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeCheckin;
using TraeTools.Models;
using TraeTools.Services.Avatar;

namespace TraeTools.ViewModels;

/// <summary>
/// 趋势图数据点（X/Y 为画布坐标）。P2-2：从 DashboardViewModel 的嵌套类型提升为顶层类型——
/// Avalonia 编译绑定（x:DataType）无法引用嵌套类型，提升后 XAML 才能用编译绑定，NativeAOT 下才不留反射绑定。
/// </summary>
public record TrendPoint(string DateLabel, double Credits, double X, double Y, string Tooltip)
{
    /// <summary>悬浮命中区宽度（=相邻点间距，用于连续覆盖无缝隙）。</summary>
    public double StepWidth { get; set; }
    /// <summary>是否为当前悬浮命中的点（高亮参考线用）。</summary>
    public bool IsHovered { get; set; }
    /// <summary>
    /// 是否为「插值点」：该日没有真实快照，值由前一个有记录的日期延续而来。
    /// UI 上用半透明点区分，避免把延续值当成真实记录看。
    /// </summary>
    public bool IsInterpolated { get; set; }

    /// <summary>数据点圆环不透明度：真实点 1.0，插值点 0.35（直接绑到 Ellipse.Opacity，避免引入转换器）。</summary>
    public double MarkerOpacity => IsInterpolated ? 0.35 : 1.0;
}

public partial class DashboardViewModel : ViewModelBase
{
    [ObservableProperty]
    private int _remainingCredits = 0;

    [ObservableProperty]
    private string _todayReward = "--";

    [ObservableProperty]
    private string _checkinStatus = "--";

    /// <summary>是否需要重新登录（CheckinStatus 为"需重登"或"未登录"时显示重新登录入口）。</summary>
    [ObservableProperty]
    private bool _needLogin;

    [ObservableProperty]
    private int _streakDays = 0;

    [ObservableProperty]
    private bool _isMember = false;

    /// <summary>批量签到进行中（防重入：避免重复点击叠加 claim）。</summary>
    [ObservableProperty]
    private bool _isBatchRunning;

    /// <summary>是否允许点「一键签到」：进行中、或已勾选账号今日都已签（或没有勾选账号）时禁用。</summary>
    [ObservableProperty]
    private bool _canQuickCheckin = true;

    /// <summary>最近一次一键签到的汇总文案（如「共 3 个账号，成功 3 个，获得 450 积分」）。</summary>
    [ObservableProperty]
    private string _batchSummary = "";

    public bool HasBatchSummary => !string.IsNullOrEmpty(BatchSummary);

    partial void OnBatchSummaryChanged(string value) => OnPropertyChanged(nameof(HasBatchSummary));

    /// <summary>按钮悬浮提示：说明本次会签哪些账号、间隔多少秒（勾选与签到页共用）。</summary>
    [ObservableProperty]
    private string _checkinIntervalHint = "将依次为已勾选的账号签到";

    partial void OnCheckinStatusChanged(string value) => UpdateCanQuickCheckin();
    partial void OnIsBatchRunningChanged(bool value) => UpdateCanQuickCheckin();

    /// <summary>
    /// 重新计算「一键签到」可用性与提示文案。
    /// 判定口径与签到页一致：签的是「已勾选（Enabled）且今日未签」的账号。
    /// </summary>
    private void UpdateCanQuickCheckin()
    {
        int checkedCount = 0, pending = 0, interval = 5;
        try
        {
            (checkedCount, pending, interval) = MainViewModel.Instance?.BatchCheckinScope() ?? (0, 0, 5);
        }
        catch { /* 范围计算失败按不可签处理 */ }

        CanQuickCheckin = !IsBatchRunning && checkedCount > 0 && pending > 0;
        CheckinIntervalHint = checkedCount == 0
            ? "没有勾选任何账号，请先到「每日签到」页勾选要签到的账号"
            : pending == 0
                ? $"已勾选的 {checkedCount} 个账号今日都已签到"
                : $"将依次为 {pending} 个账号签到（共 {checkedCount} 个已勾选），账号间隔 {interval} 秒，可在「每日签到」页调整勾选";
    }

    [ObservableProperty]
    private string _currentAccount = "未添加账号";

    /// <summary>头部展示的当前激活账号（含头像/名称），null=无账号。</summary>
    [ObservableProperty]
    private Models.AccountInfo? _currentAccountInfo;

    [ObservableProperty]
    private bool _hasCurrentAccount;

    [ObservableProperty]
    private string _dateText = DateTime.Today.ToString("yyyy-MM-dd ddd");

    public ObservableCollection<AccountInfo> Accounts { get; } = new();

    private IList<Point> _linePoints = new List<Point>();
    /// <summary>趋势折线坐标（重建时带变更通知，否则切换账号后折线不刷新）。</summary>
    public IList<Point> LinePoints { get => _linePoints; private set => SetProperty(ref _linePoints, value); }

    private Geometry _fillGeometry = new StreamGeometry();
    public Geometry FillGeometry { get => _fillGeometry; private set => SetProperty(ref _fillGeometry, value); }

    private double _todayX;
    public double TodayX { get => _todayX; private set => SetProperty(ref _todayX, value); }

    private double _todayY;
    public double TodayY { get => _todayY; private set => SetProperty(ref _todayY, value); }

    public ObservableCollection<TrendPoint> TrendPoints { get; } = new();
    public ObservableCollection<string> XAxisLabels { get; } = new();

    /// <summary>趋势点横向间距（用于悬浮命中区连续覆盖，避免悬停漏触发）。</summary>
    public double TrendStepX { get; private set; } = 40;

    /// <summary>当前悬浮命中的趋势点（即时展示冒泡，替代有延迟的原生 ToolTip）。</summary>
    [ObservableProperty]
    private TrendPoint? _trendHover;

    [ObservableProperty]
    private bool _trendHoverVisible;

    /// <summary>即时悬浮：命中设置/移开清除。</summary>
    public void SetTrendHover(TrendPoint? p)
    {
        if (TrendHover is { } prev && !ReferenceEquals(prev, p)) prev.IsHovered = false;
        if (p is not null) p.IsHovered = true;
        TrendHover = p;
        TrendHoverVisible = p != null;
    }

    public const double ChartWidth = 520;
    public const double ChartHeight = 200;

    /// <summary>Y 轴刻度（积分）。</summary>
    [ObservableProperty]
    private int _chartYMax = 0;

    [ObservableProperty]
    private int _chartYMid = 0;

    [ObservableProperty]
    private int _chartYMin = 0;

    /// <summary>总积分历史文件（%APPDATA%\TraeTools\data\credits_total_&lt;accId&gt;.txt，逐账号独立）。</summary>
    private static string TotalHistoryPathFor(string accountId)
        => System.IO.Path.Combine(TraeTools.Services.DataPaths.DataDir, $"credits_total_{accountId}.txt");

    /// <summary>读取某账号总积分历史（按日期升序；文件格式 yyyy-MM-dd,total）。</summary>
    private static List<(DateTime Date, double Total)> ReadTotalHistory(string accountId)
    {
        var list = new List<(DateTime Date, double Total)>();
        try
        {
            var path = TotalHistoryPathFor(accountId);
            if (!File.Exists(path)) return list;
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split(',');
                if (parts.Length == 2 && DateTime.TryParse(parts[0], out var d) && double.TryParse(parts[1], out var v))
                    list.Add((d, v));
            }
        }
        catch { /* 读取失败返回空 */ }
        return list.OrderBy(x => x.Date).ToList();
    }

    /// <summary>当天无记录时追加当前总积分（同时写入 SQLite 与旧版文本文件）。</summary>
    private static void AppendTotalToday(TraeCheckin.TraeAccount acc, double total)
    {
        if (total < 0) return;
        // 写入 SQLite（ON CONFLICT 覆盖当天值，同步当日消耗）
        try
        {
            MainViewModel.CheckinDb?.InsertSnapshot(acc.Id, total);
        }
        catch { /* 数据库写入失败不影响 */ }
        // 同时写入旧版文本文件（保持兼容）。当天已有行则**覆盖**而非跳过：
        // 旧实现直接 return，导致当天快照固化在首次刷新那一刻，之后消耗积分不反映到「今日」点。
        try
        {
            var path = TotalHistoryPathFor(acc.Id);
            var dir = System.IO.Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);

            var today = DateTime.Today;
            var kept = new List<string>();
            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(',');
                    if (parts.Length == 2 && DateTime.TryParse(parts[0], out var d) && d.Date == today)
                        continue;   // 丢弃当天旧行，用新值替换
                    kept.Add(line);
                }
            }
            kept.Add($"{today:yyyy-MM-dd},{total:0.##}");
            File.WriteAllLines(path, kept);
        }
        catch { /* 记录失败不影响 */ }
    }

    /// <summary>趋势图窗口天数（固定 14 个日历槽位，日期本身等距，等距绘制才是正确的时间轴）。</summary>
    private const int TrendDays = 14;

    /// <summary>
    /// 从该账号真实总积分历史重建曲线。
    ///
    /// 关键修正：**按真实日期铺 14 个槽位**，不是「有几条记录就均分画布」。
    /// 旧实现把 `TakeLast(14)` 的结果按索引等距排布，缺一天就少一个点然后整条线重新均分，
    /// 导致「10/27 之后直接跳 11/1」看起来像连续 4 天平稳，日期与点位完全对不上。
    /// 缺失日按前值延续，并在数据点上标记 <see cref="TrendPoint.IsInterpolated"/>。
    /// </summary>
    private void BuildChartFromHistory(string accountId)
    {
        // ---- 1) 取数：优先 SQLite，回退旧版文本文件 ----
        List<(DateTime Date, double Value)> history;
        try
        {
            var dbTrend = MainViewModel.CheckinDb?.GetSnapshotTrend(accountId, 60);
            history = dbTrend is { Count: > 0 }
                ? dbTrend.Select(t => (t.Date.Date, t.Remaining)).ToList()
                : ReadTotalHistory(accountId).Select(t => (t.Date.Date, t.Total)).ToList();
        }
        catch
        {
            history = ReadTotalHistory(accountId).Select(t => (t.Date.Date, t.Total)).ToList();
        }

        if (history.Count == 0)
        {
            // 无历史：退化单点平线（不生成 mock 曲线，避免误导）
            double[] single = { RemainingCredits, RemainingCredits };
            LinePoints = ComputeLinePoints(single);
            FillGeometry = ComputeFillGeometry(single);
            TrendPoints.Clear();
            XAxisLabels.Clear();
            XAxisLabels.Add("今日");
            ChartYMax = ChartYMid = ChartYMin = (int)RemainingCredits;
            if (LinePoints.Count > 0)
            {
                TodayX = LinePoints[^1].X - 6;
                TodayY = LinePoints[^1].Y - 6;
            }
            return;
        }

        // ---- 2) 铺满 14 个日历槽位，缺失日按前值延续 ----
        var today = DateTime.Today;
        var slots = Enumerable.Range(0, TrendDays)
                              .Select(i => today.AddDays(i - (TrendDays - 1)))
                              .ToArray();

        var map = new Dictionary<DateTime, double>();
        foreach (var (d, v) in history) map[d] = v;   // 同一天多条取最后一条

        var values = new double[TrendDays];
        var isReal = new bool[TrendDays];
        double? carry = null;
        for (int i = 0; i < TrendDays; i++)
        {
            if (map.TryGetValue(slots[i], out var v))
            {
                carry = v;
                values[i] = v;
                isReal[i] = true;
            }
            else if (carry.HasValue)
            {
                values[i] = carry.Value;   // 延续前值（视觉连续，但标记为非真实点）
            }
            else
            {
                // 轴起点早于该账号第一条记录：用第一条已知值铺平，避免从 0 拉起一根假陡坡
                values[i] = history[0].Value;
            }
        }

        LinePoints = ComputeLinePoints(values);
        FillGeometry = ComputeFillGeometry(values);

        // ---- 3) 点位与悬浮数据 ----
        TrendPoints.Clear();
        const double pad = 12;
        double stepX = (ChartWidth - 2 * pad) / (TrendDays - 1);
        TrendStepX = stepX;
        double min = values.Min();
        double max = values.Max();
        double range = max - min == 0 ? 1 : max - min;

        for (int i = 0; i < TrendDays; i++)
        {
            double x = pad + i * stepX;
            double y = pad + (ChartHeight - 2 * pad) * (1 - (values[i] - min) / range);
            var d = slots[i];
            string note = isReal[i] ? "" : "\n（无当日记录，按前值延续）";
            string label = d.Date == today ? "今日" : d.ToString("M/d");
            TrendPoints.Add(new TrendPoint(label, values[i], x, y,
                $"{d:yyyy-MM-dd}\n积分：{(int)values[i]}{note}")
            {
                StepWidth = TrendStepX,
                IsInterpolated = !isReal[i],
            });
        }

        // ---- 4) X 轴标签：固定 7 个，与槽位一一对应（首/末必显示） ----
        XAxisLabels.Clear();
        int[] labelIdx = { 0, 2, 4, 6, 8, 10, TrendDays - 1 };
        foreach (var idx in labelIdx)
            XAxisLabels.Add(slots[idx].Date == today ? "今日" : slots[idx].ToString("M/d"));

        ChartYMax = (int)Math.Ceiling(max);
        ChartYMid = (int)Math.Ceiling((max + min) / 2);
        ChartYMin = (int)Math.Floor(min);

        if (TrendPoints.Count > 0)
        {
            TodayX = TrendPoints[^1].X - 6;
            TodayY = TrendPoints[^1].Y - 6;
        }
    }

    public DashboardViewModel()
    {
        // 填充账号列表（优先真实账号，否则 mock）
        PopulateAccounts();

        // 从 config 读取剩余积分与签到状态
        var cfg = MainViewModel.AppConfig;
        if (cfg != null)
        {
            try
            {
                // 剩余积分按账号持久化：该账号已有记录则优先，旧数据/未刷新过回退全局 LastRemaining
                var accRem = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)?.RemainingCredits;
                if (accRem is >= 0)
                    RemainingCredits = (int)accRem;
                else if (cfg.LastRemaining >= 0)
                    RemainingCredits = (int)cfg.LastRemaining;

                if (cfg.LastCheckinDate.HasValue)
                {
                    CheckinStatus = cfg.LastCheckinDate.Value.Date == DateTime.Today
                        ? "已完成 ✓"
                        : "未签到";
                }

                var acc = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                          ?? cfg.Accounts.FirstOrDefault();
                if (acc != null)
                {
                    CurrentAccount = string.IsNullOrEmpty(acc.Name) ? $"账号@{(acc.AccountUid ?? acc.Id.Substring(0, 6))}" : acc.Name;
                    IsMember = acc.IsMember;
                }
            }
            catch { /* 保留 mock 默认值 */ }
        }

        // 趋势数据：优先读取激活账号的真实总积分历史，无账号或无历史时不生成模拟曲线
        var app = MainViewModel.AppConfig;
        var active = app?.Accounts.FirstOrDefault(a => a.Id == app.ActiveAccountId) ?? app?.Accounts.FirstOrDefault();
        if (active != null)
        {
            BuildChartFromHistory(active.Id);
            if (TrendPoints.Count == 0)
            {
                var values = GenerateTrendValues();
                LinePoints = ComputeLinePoints(values);
                FillGeometry = ComputeFillGeometry(values);
                TodayX = LinePoints[LinePoints.Count - 1].X - 6;
                TodayY = LinePoints[LinePoints.Count - 1].Y - 6;
                BuildTrendPointsAndLabels(values);
            }
        }
    }

    private void PopulateAccounts()
    {
        try
        {
            // 重载前必须清空，否则追加导致旧账号重复显示（含重复高亮）
            Accounts.Clear();

            var cfg = MainViewModel.AppConfig;
            if (cfg != null && cfg.Accounts.Count > 0)
            {
                var colors = new[] { "#3B82F6", "#10B981", "#F59E0B", "#8B5CF6", "#EC4899" };
                int idx = 0;
                foreach (var acc in cfg.Accounts)
                {
                    var name = string.IsNullOrEmpty(acc.Name)
                        ? $"账号@{(acc.AccountUid ?? acc.Id.Substring(0, 6))}"
                        : acc.Name;
                    var info = new AccountInfo
                    {
                        Id = acc.Id,
                        Name = name,
                        Initial = name.Length > 0 ? name[0].ToString() : "?",
                        Color = colors[idx % colors.Length],
                        // 各账号自己的剩余积分（-1 时界面显示「—」，不伪装成 0 分）
                        Credits = acc.RemainingCredits,
                        Status = acc.LastCheckinDate.HasValue && acc.LastCheckinDate.Value.Date == DateTime.Today ? "已签到" : "待签到",
                        StatusType = acc.LastCheckinDate.HasValue && acc.LastCheckinDate.Value.Date == DateTime.Today ? "ok" : "info",
                        IsCurrent = acc.Id == cfg.ActiveAccountId
                    };
                    // 点击账号概览卡片 → 全局切换账号（仪表盘/用量页跟随）
                    var accountId = acc.Id;
                    info.SelectCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => SelectAccount(accountId));
                    Accounts.Add(info);
                    // 同步头部展示的当前账号（含头像加载）
                    if (info.IsCurrent)
                    {
                        CurrentAccountInfo = info;
                        HasCurrentAccount = true;
                    }
                    idx++;
                }
                LoadAvatars();
                return;
            }

            // 无账号：清空头部当前账号展示
            CurrentAccountInfo = null;
            HasCurrentAccount = false;
        }
        catch { /* 加载失败保持空列表 */ }
    }

    /// <summary>全局切换账号（仪表盘/用量统计/签到等跟随）。</summary>
    private void SelectAccount(string accountId)
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg == null || cfg.ActiveAccountId == accountId) return;
            cfg.ActiveAccountId = accountId;
            try { cfg.Save(); } catch { /* 忽略 */ }
            MainViewModel.NotifyActiveAccountChanged();
        }
        catch { /* 切换失败不影响 */ }
    }

    /// <summary>为账号概览卡片异步加载头像（有 AvatarUrl 且未加载过才拉，内存缓存）。</summary>
    private void LoadAvatars()
    {
        var cfg = MainViewModel.AppConfig;
        if (cfg == null) return;
        foreach (var item in Accounts)
        {
            if (item.HasAvatar) continue;
            var acc = cfg.Accounts.FirstOrDefault(a => a.Id == item.Id);
            if (string.IsNullOrEmpty(acc?.AvatarUrl)) continue;
            _ = AvatarLoader.LoadIntoAsync(acc.AvatarUrl, img =>
            {
                if (img != null) item.AvatarImage = img;
            });
        }
    }

    private double[] GenerateTrendValues()
    {
        // 基于当前剩余积分，向前推 13 天的模拟积分（每天 +50）
        double today = RemainingCredits;
        double[] values = new double[14];
        for (int i = 0; i < 14; i++)
        {
            values[i] = today - (13 - i) * 50;
            if (values[i] < 0) values[i] = 0;
        }
        values[13] = today;
        return values;
    }

    private void BuildTrendPointsAndLabels(double[] values)
    {
        const double pad = 12;
        double stepX = (ChartWidth - 2 * pad) / (values.Length - 1);
        TrendStepX = stepX > 0 ? stepX : ChartWidth;
        double min = values.Min();
        double max = values.Max();

        for (int i = 0; i < values.Length; i++)
        {
            double x = pad + i * stepX;
            double y = pad + (ChartHeight - 2 * pad) * (1 - (values[i] - min) / (max - min));
            var date = DateTime.Today.AddDays(i - (values.Length - 1));
            string dateLabel = date.ToString("M/d");
            string tooltip = $"{date:yyyy-MM-dd}\n积分：{(int)values[i]}";
            TrendPoints.Add(new TrendPoint(dateLabel, values[i], x, y, tooltip) { StepWidth = TrendStepX });
        }

        // X 轴标签：约 8 个均匀分布
        int labelCount = 8;
        for (int i = 0; i < labelCount; i++)
        {
            int idx = (int)Math.Round(i * (values.Length - 1) / (double)(labelCount - 1));
            if (idx == values.Length - 1)
                XAxisLabels.Add("今日");
            else
                XAxisLabels.Add(TrendPoints[idx].DateLabel);
        }
    }

    private static IList<Point> ComputeLinePoints(double[] values)
    {
        const double pad = 12;
        double stepX = (ChartWidth - 2 * pad) / (values.Length - 1);
        double min = values.Min();
        double max = values.Max();
        double range = 1.0 * (max - min == 0 ? 1 : max - min);
        var pts = new List<Point>();
        for (int i = 0; i < values.Length; i++)
        {
            double x = pad + i * stepX;
            double y = pad + (ChartHeight - 2 * pad) * (1 - (values[i] - min) / range);
            pts.Add(new Point(x, y));
        }
        return pts;
    }

    private static Geometry ComputeFillGeometry(double[] values)
    {
        const double pad = 12;
        double stepX = (ChartWidth - 2 * pad) / (values.Length - 1);
        double min = values.Min();
        double max = values.Max();
        double range = 1.0 * (max - min == 0 ? 1 : max - min);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(pad, ChartHeight - pad), true);
            for (int i = 0; i < values.Length; i++)
            {
                double x = pad + i * stepX;
                double y = pad + (ChartHeight - 2 * pad) * (1 - (values[i] - min) / range);
                ctx.LineTo(new Point(x, y));
            }
            ctx.LineTo(new Point(pad + (values.Length - 1) * stepX, ChartHeight - pad));
            ctx.EndFigure(true);
        }
        return geometry;
    }

    public async Task LoadAsync()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg == null) return;

            var acc = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                      ?? cfg.Accounts.FirstOrDefault();
            if (acc == null || string.IsNullOrEmpty(acc.Token)) return;

            // 独立 API 实例：LastError 隔离 + 走当前账号代理
            var api = new TraeApiClient(AccountHelpers.ResolveProxy(acc));

            var status = await api.GetStatusAsync(acc.Token, acc.DeviceId);
            if (status != null && status.code == 0)
            {
                CheckinStatus = status.checked_in ? "已完成 ✓" : "未签到";
                TodayReward = "+" + (status.credits + (acc.IsMember ? status.extra_credits : 0));
            }
            else
            {
                // 状态接口失败（token 失效/风控）：明确提示重登，不显示误导性的"已完成"
                CheckinStatus = "需重登";
            }

            // 归零守卫：接口失败/未授权时 GetRemainingCreditsAsync 可能返回 0，此时不覆盖现有显示
            var credits = await api.GetRemainingCreditsAsync(acc.Token, acc.DeviceId);
            if (credits > 0 || (credits >= 0 && string.IsNullOrEmpty(api.LastError)))
            {
                RemainingCredits = (int)credits;
                acc.RemainingCredits = credits;   // 按账号持久化
                cfg.LastRemaining = credits;
                try { cfg.Save(); } catch { /* 忽略保存失败 */ }
            }
        }
        catch
        {
            // 网络/解析失败时保留 mock 数据，不崩溃
        }
    }

    /// <summary>对齐 TraeCheckin.RefreshAllAsync：按激活账号真实拉取状态/积分/单日奖励。</summary>
    public async Task RefreshAllAsync()
    {
        try
        {
            // 先同步头部当前账号 + 账号概览高亮（重建列表，令 CurrentAccountInfo/IsCurrent 跟随激活账号）
            PopulateAccounts();

            var cfg = MainViewModel.AppConfig;
            var acc = cfg?.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                      ?? cfg?.Accounts.FirstOrDefault();
            if (acc == null)
            {
                CurrentAccount = "未添加账号";
                CheckinStatus = "未登录";
                return;
            }
            // 独立 API 实例：LastError 隔离 + 走当前账号代理
            var api = new TraeApiClient(AccountHelpers.ResolveProxy(acc));
            CurrentAccount = string.IsNullOrEmpty(acc.Name)
                ? $"账号@{(acc.AccountUid ?? acc.Id.Substring(0, 6))}"
                : acc.Name!;
            IsMember = acc.IsMember;
            AccountHelpers.EnsureDeviceId(acc);

            // 账号切换：先从该账号本地积分历史取最近一条，立即同步剩余积分与趋势图，
            // 不必等接口返回（接口慢/失败也不会残留上个账号的旧值）
            var localHis = ReadTotalHistory(acc.Id);
            if (localHis.Count > 0)
            {
                RemainingCredits = (int)localHis[^1].Total;
                acc.RemainingCredits = localHis[^1].Total;   // 按账号持久化
                if (cfg != null) { cfg.LastRemaining = localHis[^1].Total; }
            }
            BuildChartFromHistory(acc.Id);

            double remaining = acc.RemainingCredits >= 0 ? acc.RemainingCredits : RemainingCredits;   // 失败保留旧值，不清零
            TraeCheckin.CheckinStatus? status = null;
            if (api != null && !string.IsNullOrEmpty(acc.Token))
            {
                bool valid = await AccountHelpers.EnsureValidTokenAsync(acc);
                if (valid)
                {
                    var st = await api.GetStatusAsync(acc.Token ?? "", acc.DeviceId);
                    if (st != null && st.code == 0) status = st;
                    var r = await api.GetRemainingCreditsAsync(acc.Token ?? "", acc.DeviceId);
                    if (r >= 0 && string.IsNullOrEmpty(api.LastError))
                    {
                        remaining = r;
                        acc.RemainingCredits = r;   // 按账号持久化，切号再切回仍是新积分
                        if (cfg != null) { cfg.LastRemaining = r; try { cfg.Save(); } catch { /* 忽略 */ } }
                    }
                }
            }

            RemainingCredits = (int)remaining;
            if (status != null)
            {
                CheckinStatus = status.checked_in ? "今日已签到 ✓" : "今日可签到";
                NeedLogin = false;
                TodayReward = "+" + (int)(status.credits + (acc.IsMember ? status.extra_credits : 0));
            }
            else
            {
                CheckinStatus = string.IsNullOrEmpty(acc.Token) ? "未登录" : "需重登";
                NeedLogin = true;
            }

            // 记录今日总积分并重建该账号趋势曲线（账号切换后曲线随之同步）
            if (remaining >= 0)
            {
                AppendTotalToday(acc, remaining);
                BuildChartFromHistory(acc.Id);
            }

            // 连续签到天数：与「每日签到」页同口径（从今天/昨天往前数连签）。
            // 原实现从未给 StreakDays 赋值，卡片永远显示「0 天」，与签到页对不上。
            try
            {
                var signed = MainViewModel.CheckinDb?.GetSignedDates() ?? new HashSet<DateTime>();
                if (acc.LastCheckinDate is DateTime lc) signed.Add(lc.Date);
                int streak = 0;
                var cursor = DateTime.Today;
                if (!signed.Contains(cursor)) cursor = cursor.AddDays(-1);
                while (signed.Contains(cursor)) { streak++; cursor = cursor.AddDays(-1); }
                StreakDays = streak;
            }
            catch { /* 连签计算失败保留旧值 */ }

            // 刷新后重算「一键签到」可用范围与提示（勾选账号数/待签数/间隔秒数会变）
            UpdateCanQuickCheckin();
        }
        catch (Exception ex)
        {
            AccountHelpers.CheckinLog("?", $"[仪表盘刷新] 异常：{ex.Message}");
        }
    }

    /// <summary>重读当前激活账号并刷新展示（账号切换联动用）。</summary>
    public void Reload()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg == null) return;
            var acc = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                      ?? cfg.Accounts.FirstOrDefault();
            if (acc == null) return;
            CurrentAccount = string.IsNullOrEmpty(acc.Name) ? $"账号@{(acc.AccountUid ?? acc.Id.Substring(0, 6))}" : acc.Name;
            IsMember = acc.IsMember;
            Accounts.Clear();
            PopulateAccounts();
        }
        catch { /* 刷新失败保留原值 */ }
    }

    [RelayCommand]
    private void GoToCheckin()
    {
        RaiseNavigateRequested("checkin");
    }

    [RelayCommand]
    private async Task RefreshStatus()
    {
        await LoadAsync();
    }

    /// <summary>重新登录当前激活账号（仪表盘"需重登/未登录"入口）。登录成功后全局刷新。</summary>
    [RelayCommand]
    private async Task Relogin()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            var owner = UiHost.MainWindow;
            var acc = cfg?.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                      ?? cfg?.Accounts.FirstOrDefault();
            if (cfg == null || owner == null || acc == null) return;
            var dlg = new Views.LoginWindow(acc);
            bool ok = await dlg.ShowDialog<bool>(owner);
            if (ok)
            {
                try { cfg.Save(); } catch { /* 忽略保存失败 */ }
                await RefreshAllAsync();
                MainViewModel.NotifyActiveAccountChanged();
            }
        }
        catch { /* 登录窗口异常不影响 */ }
    }

    /// <summary>上次批量签到尝试时间（冷却用：连点会 1 秒内多次打 claim，加重 9074 风控）。</summary>
    private static DateTime _lastBatchAttempt = DateTime.MinValue;
    private static readonly TimeSpan BatchCooldown = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 仪表盘「一键签到」：与「每日签到」页共用同一条批量路径（<see cref="CheckinViewModel.RunBatchCheckinAsync"/>），
    /// 也就自动继承了账号间间隔、勾选范围、未登录先弹窗、9074 换设备号重试、飞书推送。
    /// 旧实现（QuickCheckin）只签当前激活账号，名为「立即签到」实为单账号，语义误导。
    /// </summary>
    [RelayCommand]
    private async Task BatchCheckin()
    {
        if (IsBatchRunning) return;   // 防重入
        var sinceLast = DateTime.Now - _lastBatchAttempt;
        if (sinceLast < BatchCooldown)
        {
            BatchSummary = $"操作太频繁，{(int)(BatchCooldown - sinceLast).TotalSeconds + 1}s 后可重试";
            return;
        }
        _lastBatchAttempt = DateTime.Now;
        IsBatchRunning = true;
        try
        {
            var vm = MainViewModel.Instance;
            if (vm == null)
            {
                BatchSummary = "服务未初始化";
                return;
            }

            var (any, ok, total, gained, summary) = await vm.RunBatchCheckinAsync();
            BatchSummary = summary;
            if (any) CheckinStatus = "今日已签到 ✓";

            // 签到后重读真实状态/积分/趋势，并刷新账号概览（含各账号剩余积分）
            await RefreshAllAsync();
            UpdateCanQuickCheckin();
        }
        catch (Exception ex)
        {
            BatchSummary = $"签到异常：{ex.Message}";
        }
        finally
        {
            IsBatchRunning = false;
            UpdateCanQuickCheckin();
        }
    }
}










