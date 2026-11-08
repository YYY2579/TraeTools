using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeCheckin;
using TraeTools.Models;
using TraeTools.Services.Notify;
using TraeTools.Views;

namespace TraeTools.ViewModels;

public partial class CheckinViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _todayReward = "--";

    /// <summary>今日是否已签到（驱动"今日已签到"强调与一键签到按钮禁用联动）。</summary>
    [ObservableProperty]
    private bool _checkedInToday;

    /// <summary>一键签到按钮文案：今日已签则显示"今日已签到"。</summary>
    public string CheckinButtonText => CheckedInToday ? "今日已签到 ✓" : "一键签到";

    partial void OnCheckedInTodayChanged(bool value) => OnPropertyChanged(nameof(CheckinButtonText));

    [ObservableProperty]
    private int _streakDays = 0;

    [ObservableProperty]
    private string _memberMultiplier = "×1.0";

    [ObservableProperty]
    private bool _isMember;

    [ObservableProperty]
    private string _memberHint = "非会员：基础签到 150 积分";

    [ObservableProperty]
    private double _progressRatio = 0;

    [ObservableProperty]
    private string _progressText = $"0/{DateTime.DaysInMonth(DateTime.Today.Year, DateTime.Today.Month)}";

    /// <summary>连签圆环弧线（按本月签到进度绘制，分母 = 当月实际天数）。</summary>
    [ObservableProperty]
    private Geometry _streakRingGeometry = new StreamGeometry();

    /// <summary>圆环下方小字（本月 N/当月天数）。</summary>
    [ObservableProperty]
    private string _streakRingCaption = $"本月 0/{DateTime.DaysInMonth(DateTime.Today.Year, DateTime.Today.Month)}";

    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>是否有状态消息需要显示（空则隐藏反馈区）。</summary>
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));

    /// <summary>标题副标题（动态日期）。</summary>
    [ObservableProperty]
    private string _pageSubtitle = "";

    /// <summary>本月标题（“2026 年 9 月”），随月份动态显示。</summary>
    [ObservableProperty]
    private string _monthTitle = $"{DateTime.Now.Year} 年 {DateTime.Now.Month} 月";

    public ObservableCollection<CalendarDay> CalendarDays { get; } = new();
    public ObservableCollection<CheckinRecord> Records { get; } = new();

    public CheckinViewModel()
    {
        PageSubtitle = DateTime.Today.ToString("yyyy-MM-dd · dddd");

        var cfg = MainViewModel.AppConfig;
        try
        {
            if (cfg != null)
            {
                var acc = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                          ?? cfg.Accounts.FirstOrDefault();
                if (acc != null)
                {
                    IsMember = acc.IsMember;
                    MemberHint = acc.IsMember ? "会员：基础 150 + 连签 50" : "非会员：基础签到 150 积分";
                    MemberMultiplier = acc.IsMember ? "×1.33" : "×1.0";
                }
            }
        }
        catch { /* 保留默认 */ }

        // 真实接入：日历按当月实际签到日期构建，连签天数从历史计算
        AccountHelpers.CleanupLegacyHistoryFiles();   // 顺手清理旧版每账号历史文件（幂等）
        RefreshTodayState();
        ReloadCalendar();
        LoadHistory();
    }

    /// <summary>历史记录目录（唯一真源在 AccountHelpers，这里仅转发）。</summary>
    private static string HistoryDir => AccountHelpers.HistoryDir;

    /// <summary>读取全部历史文件，解析出「日期集合」（用于日历与连签）与「记录行」（用于记录列表）。</summary>
    private List<(DateTime Date, string Line)> ReadAllHistory()
    {
        var result = new List<(DateTime Date, string Line)>();
        try
        {
            lock (HistoryIoLock)   // 与 TryAppendHistory 同锁，避免并发读到半截行
            {
                if (!Directory.Exists(HistoryDir)) return result;
                foreach (var file in Directory.GetFiles(HistoryDir, "history_*.txt"))
                {
                    try
                    {
                        foreach (var line in File.ReadAllLines(file))
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            var parts = line.Split('|', StringSplitOptions.TrimEntries);
                            if (parts.Length < 1) continue;
                            if (TryParseHistoryDate(line, out var dt))
                                result.Add((dt.Date, line));
                            else
                                result.Add((DateTime.MinValue, line));
                        }
                    }
                    catch { /* 跳过损坏文件 */ }
                }
            }
        }
        catch { /* 目录不可读 */ }
        return result.OrderByDescending(r => r.Date).ToList();
    }

    private HashSet<DateTime> CollectSignedDates()
    {
        var signed = new HashSet<DateTime>();
        var cfg = MainViewModel.AppConfig;
        var acc = cfg?.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                  ?? cfg?.Accounts.FirstOrDefault();
        if (acc?.LastCheckinDate is DateTime lc) signed.Add(lc.Date);
        if (cfg?.LastCheckinDate is DateTime clc) signed.Add(clc.Date);

        // 优先从 SQLite 读取
        try
        {
            var dbDates = MainViewModel.CheckinDb?.GetSignedDates();
            if (dbDates != null && dbDates.Count > 0)
            {
                foreach (var d in dbDates) signed.Add(d);
                return signed;
            }
        }
        catch { /* 数据库读取失败回退到文本文件 */ }

        foreach (var (date, line) in ReadAllHistory())
        {
            if (date == DateTime.MinValue) continue;
            if (TryParseRecord(line, out var rec) && rec.Type == "签到失败") continue;
            signed.Add(date);
        }
        return signed;
    }

    /// <summary>本月日历是按哪一年哪一月构建的（用于跨月检测，程序常驻托盘时自动重建）。</summary>
    private (int Year, int Month) _builtFor;

    /// <summary>
    /// 重建本月签到日历：今天=today，历史/配置中有记录的=checked，
    /// 本月内早于今天且未签到、且已过「统计起点」的=missed，其余=empty。
    /// </summary>
    private void ReloadCalendar()
    {
        CalendarDays.Clear();
        try
        {
            var today = DateTime.Today;
            _builtFor = (today.Year, today.Month);
            MonthTitle = $"{today.Year} 年 {today.Month} 月";

            // 月首工作日偏移补位（周标头：日一...六；Sunday=0..Saturday=6），保证每月1号对齐正确星期列
            var first = new DateTime(today.Year, today.Month, 1);
            for (int i = 0; i < (int)first.DayOfWeek; i++)
                CalendarDays.Add(new CalendarDay { Day = 0, State = "blank" });

            var signed = CollectSignedDates();

            // 当月签到记录按日期分组（用于悬浮提示：当日签到账号及积分）
            var byDate = new Dictionary<DateTime, List<string>>();
            try
            {
                var records = MainViewModel.CheckinDb?.GetRecords(DateTime.Today.Year, DateTime.Today.Month, 500) ?? new();
                foreach (var r in records)
                {
                    if (DateTime.TryParse(r.Date, out var rd))
                    {
                        if (!byDate.TryGetValue(rd.Date, out var lst)) byDate[rd.Date] = lst = new();
                        var name = string.IsNullOrEmpty(r.AccountName) ? "账号" : r.AccountName;
                        lst.Add($"{name} +{(int)r.Credits}");
                    }
                }
            }
            catch { /* 悬浮数据失败不影响日历 */ }

            int days = DateTime.DaysInMonth(today.Year, today.Month);

            // 「漏签」统计起点：本月 1 号与「最早一次签到」取较晚者。
            // 一条记录都没有时退到 7 天前，避免首次使用就满屏飘红（那不是漏签，是还没开始用）。
            var earliestSigned = signed.Count > 0 ? signed.Min() : today.AddDays(-7);
            var statStart = earliestSigned > first ? earliestSigned : first;
            if (statStart > today) statStart = today;   // 防御：未来日期不参与判定

            for (int day = 1; day <= days; day++)
            {
                var d = new DateTime(today.Year, today.Month, day);
                string state;
                if (signed.Contains(d.Date)) state = "checked";                 // 已签（含今日已签）→ 蓝色
                else if (d.Date == today) state = "today";                       // 今天未签 → 今日高亮
                else if (d.Date < today && d.Date >= statStart) state = "missed"; // 本月应签未签 → 红色
                else state = "empty";                                            // 未开始统计/未来日期 → 灰白
                var item = new CalendarDay { Day = day, State = state, Date = d };
                if (byDate.TryGetValue(d.Date, out var list) && list.Count > 0)
                    item.ToolTipText = $"{d:yyyy-MM-dd}\n" + string.Join("\n", list);
                else
                    item.ToolTipText = state switch
                    {
                        "checked" => $"{d:yyyy-MM-dd}\n已签到",
                        "missed" => $"{d:yyyy-MM-dd}\n漏签（未签到）",
                        "today" => $"{d:yyyy-MM-dd}\n今日尚未签到",
                        _ => $"{d:yyyy-MM-dd}\n未签到",
                    };
                CalendarDays.Add(item);
            }

            // 连签天数：从今天（或昨天）向前连续计数
            var signedDates = signed;
            int streak = 0;
            var cursor = today;
            if (!signedDates.Contains(cursor.Date)) cursor = cursor.AddDays(-1);
            while (signedDates.Contains(cursor.Date))
            {
                streak++;
                cursor = cursor.AddDays(-1);
            }
            StreakDays = streak;

            // 月进度（分母 = 当月实际天数 28/29/30/31）
            int signedCount = signedDates.Count(d => d.Year == today.Year && d.Month == today.Month);
            ProgressText = $"{signedCount}/{days}";
            ProgressRatio = days > 0 ? (double)signedCount / days : 0;
            RebuildRing();   // 圆环跟着本月进度走
        }
        catch { /* 失败保持空日历 */ }
    }

    /// <summary>
    /// 重建连签圆环：弧长 = 本月签到进度，分母是**当月实际天数**（28/29/30/31，闰年 2 月 29）。
    /// 原实现是写死的静态弧（M 48,12 A 36,36 …），连签 1 天和 20 天长同一个样。
    /// </summary>
    private void RebuildRing()
    {
        const double cx = 48, cy = 48, r = 36;
        const double startAngle = -90;   // 12 点方向起画，顺时针
        var geo = new StreamGeometry();
        try
        {
            double ratio = Math.Clamp(ProgressRatio, 0, 1);
            double sweep = 360 * ratio;
            if (sweep > 0.5)   // 0 天时连一丝弧都不留
            {
                using var ctx = geo.Open();
                ctx.BeginFigure(PointOnCircle(cx, cy, r, startAngle), false);
                ctx.ArcTo(PointOnCircle(cx, cy, r, startAngle + sweep),
                          new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise);
                ctx.EndFigure(false);
            }
        }
        catch { /* 几何构建失败不影响页面 */ }
        StreakRingGeometry = geo;
        StreakRingCaption = $"本月 {ProgressText}";
    }

    private static Point PointOnCircle(double cx, double cy, double r, double deg)
    {
        double rad = deg * Math.PI / 180.0;
        return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
    }

    /// <summary>
    /// 跨月检查：月份变了就重建日历与圆环。
    /// 常驻托盘的程序跨月后若不重建，日历会停在旧月份、进度分母还是旧月天数（28/30/31 各不相同）。
    /// </summary>
    public void EnsureCurrentMonth()
    {
        var today = DateTime.Today;
        if (_builtFor == (today.Year, today.Month)) return;
        ReloadCalendar();
    }

    partial void OnProgressRatioChanged(double value) => RebuildRing();

    /// <summary>读取签到历史列表（优先 SQLite，回退文本文件；无数据时用示例）。</summary>
    private void LoadHistory()
    {
        Records.Clear();

        // 优先从 SQLite 读取
        try
        {
            var dbRecords = MainViewModel.CheckinDb?.GetRecords(limit: 50);
            if (dbRecords != null && dbRecords.Count > 0)
            {
                foreach (var r in dbRecords)
                {
                    Records.Add(new CheckinRecord
                    {
                        Date = $"{r.Date} {r.Time}",
                        Account = r.AccountName,
                        Type = "每日签到",
                        Result = $"+{(int)r.Credits}",
                    });
                }
                return;
            }
        }
        catch { /* 数据库读取失败回退到文本文件 */ }

        var all = ReadAllHistory().Where(r => r.Date != DateTime.MinValue).ToList();
        if (all.Count == 0) return;
        foreach (var (_, line) in all.Take(50))
        {
            if (!TryParseRecord(line, out var rec)) continue;
            Records.Add(rec);
        }
    }

    /// <summary>解析历史日期：兼容新版管道格式（首段即日期）与旧版空格格式（yyyy-MM-dd HH:mm 开头）。</summary>
    private static bool TryParseHistoryDate(string line, out DateTime date)
    {
        var pipe = line.Split('|', StringSplitOptions.TrimEntries);
        if (pipe.Length >= 1 && DateTime.TryParse(pipe[0], out date)) return true;
        var m = System.Text.RegularExpressions.Regex.Match(line,
            @"^\s*(\d{4}-\d{2}-\d{2})\s+(\d{1,2}:\d{2})");
        if (m.Success && DateTime.TryParse(m.Groups[1].Value + " " + m.Groups[2].Value, out date)) return true;
        date = DateTime.MinValue;
        return false;
    }

    /// <summary>
    /// 解析一条历史记录为展示模型。兼容：
    /// 新版管道格式 "yyyy-MM-dd HH:mm | 账号 | 类型 | +N"；
    /// 旧版空格格式 "2026-09-08 16:38  [账号 8D21]  签到成功  +150 积分"。
    /// </summary>
    private static bool TryParseRecord(string line, out CheckinRecord rec)
    {
        rec = new CheckinRecord();
        var parts = line.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length >= 4)
        {
            // 过滤早期版本遗留的 +0 空记录
            if (string.Equals(parts[3], "+0", StringComparison.OrdinalIgnoreCase)) return false;
            rec.Date = parts[0];
            rec.Account = parts[1];
            rec.Type = parts[2];
            rec.Result = parts[3];
            return !string.IsNullOrEmpty(parts[0]);
        }
        // 旧版空格格式
        var m = System.Text.RegularExpressions.Regex.Match(line,
            @"^\s*(\d{4}-\d{2}-\d{2})\s+(\d{1,2}:\d{2})\s*\[([^\]]+)\].*?([+-]?\d+(?:\.\d+)?)\s*积分");
        if (!m.Success) return false;
        rec.Date = $"{m.Groups[1].Value} {m.Groups[2].Value}";
        rec.Account = m.Groups[3].Value.Trim();
        rec.Type = "每日签到";
        rec.Result = m.Groups[4].Value.StartsWith("+") || m.Groups[4].Value.StartsWith("-")
            ? m.Groups[4].Value
            : "+" + m.Groups[4].Value;
        return true;
    }

    /// <summary>
    /// 自动签到检查：到点、启用且当天未签到时，对每个 Enabled 账号逐一执行签到。
    /// </summary>
    public async Task<bool> TryAutoCheckinAsync()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg == null || !cfg.AutoCheckinEnabled) return false;
            if (string.IsNullOrWhiteSpace(cfg.AutoCheckinTime)) return false;
            if (!TimeSpan.TryParse(cfg.AutoCheckinTime, out var due)) return false;
            if (DateTime.Now.TimeOfDay < due) return false;
            if (_lastAutoCheckDate == DateTime.Today) return false;
            if (MainViewModel.CheckinApi == null) return false;
            _lastAutoCheckDate = DateTime.Today;  // 到点时置位，本日不再重复触发

            RebuildCheckList();   // 勾选与账号保持同步（新增账号也能进入自动签到）
            var (any, results) = await CheckinAllAccountsAsync();
            if (any) { StatusMessage = "自动签到完成 ✓"; RefreshTodayState(); ReloadCalendar(); LoadHistory(); }
            if (results.Any(r => r.Ok)) await NotifyFeishuBatchAsync(results);   // 全失败不打扰
            return any;
        }
        catch
        {
            return false;
        }
    }



    /// <summary>自动签到结束后，把本轮多账号结果批量推送到飞书（对齐 TraeCheckin.NotifyFeishuBatchAsync）。</summary>
    private async Task NotifyFeishuBatchAsync(List<(string Name, bool Ok, double Gained, string Reason)> results)
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.FeishuWebhook) || results.Count == 0) return;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Trae 多账号签到结果");
            foreach (var r in results)
            {
                sb.AppendLine((r.Ok ? "✅ " : "⚠️ ") + r.Name + (r.Ok ? $"  获得 {r.Gained:0} 积分" : $"  失败：{r.Reason}"));
            }
            await TraeCheckin.FeishuNotifier.SendTextAsync(cfg.FeishuWebhook, sb.ToString());
        }
        catch { /* 推送失败不影响签到 */ }
    }

    /// <summary>
    /// 对齐 TraeCheckin.DoCheckinAsync：遍历全部 Enabled 账号签到，单账号失败不阻断。
    /// 返回是否至少成功一个 + 各账号结果（供汇总文案/飞书推送）。
    /// </summary>
    private async Task<(bool Any, List<(string Name, bool Ok, double Gained, string Reason)> Results)> CheckinAllAccountsAsync()
    {
        var cfg = MainViewModel.AppConfig;
        var results = new List<(string Name, bool Ok, double Gained, string Reason)>();
        bool any = false;
        if (cfg == null) return (false, results);
        // 每账号独立客户端：LastError 隔离 + 各自走账号代理（多账号出口 IP 隔离）

        AccountHelpers.CheckinLog("", $"===== 批量签到开始，共 {cfg.Accounts.Count(a => a.Enabled)} 个启用账号 =====");
        int idx = 0;
        foreach (var acc in cfg.Accounts.Where(a => a.Enabled))
        {
            var display = string.IsNullOrEmpty(acc.Name)
                ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id)
                : acc.Name!;

            // 账号间间隔：从配置读取基准秒数，附加 ±2 秒随机抖动
            if (idx++ > 0)
            {
                int baseSec = cfg.CheckinIntervalSeconds > 0 ? cfg.CheckinIntervalSeconds : 5;
                int jitter = Random.Shared.Next(-2000, 2001);
                int delay = Math.Max(1000, baseSec * 1000 + jitter);
                AccountHelpers.CheckinLog(display, $"等待 {delay}ms 后签到下一个账号");
                await Task.Delay(delay);
            }

            AccountHelpers.EnsureDeviceId(acc);
            AccountHelpers.CheckinLog(display, $"开始签到，DeviceId={acc.DeviceId}，Token={(string.IsNullOrEmpty(acc.Token) ? "无" : "有")}，IsMember={acc.IsMember}");

            // 未登录：计入失败汇总并给出原因（失败详情已由 checkin_log 记录）
            if (string.IsNullOrEmpty(acc.Token))
            {
                AccountHelpers.CheckinLog(display, "跳过：未登录（Token 为空）");
                results.Add((display, false, 0, "未登录"));
                continue;
            }
            // 已签（本地记录）：计入成功、不计本日新增积分（历史已有当日记录）
            if (acc.LastCheckinDate.HasValue && acc.LastCheckinDate.Value.Date == DateTime.Today)
            {
                AccountHelpers.CheckinLog(display, $"跳过：本地记录今日已签到（{acc.LastCheckinDate:HH:mm}）");
                results.Add((display, true, 0, "今日已签"));
                continue;
            }
            try
            {
                // token 校验/换新（内部会做 status 探测）
                bool valid = await AccountHelpers.EnsureValidTokenAsync(acc);
                if (!valid)
                {
                    AccountHelpers.CheckinLog(display, "Token 校验失败且 Session 换新失败，登录态彻底失效");
                    results.Add((display, false, 0, "会话失效，请重新登录"));
                    continue;
                }
                AccountHelpers.CheckinLog(display, "Token 校验通过");
                // 本账号的签到客户端（走账号代理，LastError 与其他账号隔离）
                var accApi = new TraeApiClient(AccountHelpers.ResolveProxy(acc));
                // 真实状态判定：已真签（本地漏记）→ 补记并计入成功；未签才执行 claim
                var st = await accApi.GetStatusAsync(acc.Token ?? "", acc.DeviceId);
                if (st is { } s && s.code == 0 && s.checked_in)
                {
                    acc.LastCheckinDate = DateTime.Now;
                    double already = TraeCheckin.CheckinEvaluator.ResolveGainedCredits(s, acc.IsMember);
                    try { cfg.Save(); } catch { /* 忽略 */ }
                    AccountHelpers.CheckinLog(display, $"服务器返回已签到，补记历史，积分={already}");
                    if (already > 0) TryAppendHistory(acc, already);   // 服务器已签而本地漏记 → 补写历史
                    results.Add((display, true, already, "今日已签"));
                    continue;
                }
                if (st != null)
                    AccountHelpers.CheckinLog(display, $"Status 查询返回：code={st.code}, checked_in={st.checked_in}, message={st.message}");
                else
                    AccountHelpers.CheckinLog(display, "Status 查询返回 null（网络异常）");

                var (g, reason) = await CheckinOneAccountAsync(acc, accApi);
                if (g > 0) any = true;
                results.Add((display, g > 0, g, reason));
            }
            catch (Exception ex)
            {
                AccountHelpers.CheckinLog(display, $"签到异常：{ex.Message}");
                results.Add((display, false, 0, "网络或接口异常")); // 单账号失败继续下一个
            }
        }
        AccountHelpers.CheckinLog("", $"===== 批量签到结束，成功 {results.Count(r => r.Ok)} / {results.Count} =====");
        return (any, results);
    }

    /// <summary>对指定账号执行一次签到；返回 (本次获得的积分, 失败原因)。失败原因用于界面/推送明确提示。
    /// 调用前须已完成 EnsureValidTokenAsync 校验（CheckinAllAccountsAsync 已做，这里不再重复探测）。</summary>
    private async Task<(double Gained, string Reason)> CheckinOneAccountAsync(TraeCheckin.TraeAccount acc, TraeCheckin.TraeApiClient? api = null)
    {
        var cfg = MainViewModel.AppConfig;
        api ??= MainViewModel.CheckinApi;
        var displayName = string.IsNullOrEmpty(acc.Name) ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id) : acc.Name!;
        if (cfg == null || api == null) return (0, "服务未初始化");
        if (string.IsNullOrEmpty(acc.Token))
        {
            AccountHelpers.CheckinLog(displayName, "Claim 前 Token 为空");
            return (0, "会话失效，请重新登录");
        }

        AccountHelpers.CheckinLog(displayName, $"调用 ClaimAsync，DeviceId={acc.DeviceId}");
        // 9074「当前参与用户太多」= 设备号被风控标记：自动换新设备号重试（对齐 checkin.py，最多 5 次尝试）
        var result = await AccountHelpers.ClaimWithRiskRetryAsync(api, acc, displayName);
        if (result == null || result.code != 0)
        {
            var code = result?.code ?? -1;
            var msg = result?.message ?? "null response";
            var reason = api.LastError;
            if (string.IsNullOrEmpty(reason))
                reason = $"签到失败：{msg}（code={code}）";
            AccountHelpers.CheckinLog(displayName, $"Claim 失败：code={code}, message={msg}, LastError={api.LastError ?? "null"}");
            return (0, reason);
        }
        AccountHelpers.CheckinLog(displayName, $"Claim 成功：code={result.code}, checked_in={result.checked_in}");

        // claim 响应不含本次所得积分（源注释明确），签到成功后再查 status 解析（对齐源 CheckinOneAsync）
        double gained = 0;
        try
        {
            var after = await api.GetStatusAsync(acc.Token, acc.DeviceId);
            gained = TraeCheckin.CheckinEvaluator.ResolveGainedCredits(after ?? result, acc.IsMember);
            if (after != null)
                AccountHelpers.CheckinLog(displayName, $"签到后 Status 查询：code={after.code}, credits={after.credits}, extra_credits={after.extra_credits}, checked_in={after.checked_in}，解析 gained={gained}");
        }
        catch (Exception ex) { AccountHelpers.CheckinLog(displayName, $"签到后 Status 查询异常：{ex.Message}"); }

        if (gained <= 0)
            AccountHelpers.CheckinLog(displayName, "签到成功但本次积分解析为 0（如实记录，不虚构）");

        acc.LastCheckinDate = DateTime.Now;
        if (acc.Id == cfg.ActiveAccountId)
        {
            cfg.LastCheckinDate = DateTime.Now;
            TodayReward = "+" + (int)gained;
        }
        try
        {
            var credits = await api.GetRemainingCreditsAsync(acc.Token, acc.DeviceId);
            if (credits >= 0 && string.IsNullOrEmpty(api.LastError))
            {
                acc.RemainingCredits = credits;   // 按账号持久化
                cfg.LastRemaining = credits;
            }
        }
        catch { /* 积分刷新失败不影响 */ }
        try { cfg.Save(); } catch { /* 忽略 */ }
        TryAppendHistory(acc, gained);
        AccountHelpers.CheckinLog(displayName, $"签到完成，获得 {gained} 积分，剩余 {cfg.LastRemaining}");
        return (gained, "");
    }

    /// <summary>历史读写锁（唯一真源在 AccountHelpers，这里仅转发）。</summary>
    private static object HistoryIoLock => AccountHelpers.HistoryIoLock;

    /// <summary>签到页账号勾选列表（与 TraeAccount.Enabled 双向同步，勾谁签谁）。</summary>
    public ObservableCollection<AccountCheckItem> CheckAccounts { get; } = new();

    /// <summary>按当前账号重建勾选列表：新增的补上、删除的移除、显示名刷新，勾选状态保留。</summary>
    private void RebuildCheckList()
    {
        if (MainViewModel.AppConfig?.Accounts is not { } accounts) return;
        var ids = accounts.Select(a => a.Id).ToHashSet();
        foreach (var item in CheckAccounts.Where(i => !ids.Contains(i.Id)).ToList())
            CheckAccounts.Remove(item);
        foreach (var acc in accounts)
        {
            var display = string.IsNullOrEmpty(acc.Name) ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id) : acc.Name!;
            var existing = CheckAccounts.FirstOrDefault(i => i.Id == acc.Id);
            if (existing == null)
            {
                var item = new AccountCheckItem { Id = acc.Id, Display = display, IsChecked = acc.Enabled };
                item.OnChanged = c =>
                {
                    var a = MainViewModel.AppConfig?.Accounts.FirstOrDefault(x => x.Id == c.Id);
                    if (a == null || a.Enabled == c.IsChecked) return;
                    a.Enabled = c.IsChecked;
                    try { MainViewModel.AppConfig?.Save(); } catch { /* 忽略 */ }
                };
                CheckAccounts.Add(item);
            }
            else existing.Display = display;
        }
    }

    /// <summary>
    /// 到点告警：过了「自动签到时间 + 10 分钟」还有已勾选账号没签上，就认为这次签到失败并告警。
    ///
    /// 为什么用这个判据而不是去查 GitHub Actions：云端签到成功与否，最终都体现在
    /// 「本地账号今天有没有签到记录」上；而查 Actions 需要网络+日志下载，还要求用户配了 GitHub。
    /// 时间窗 + 签到状态是唯一同时覆盖「本地没跑」「云端定时任务失败」两种情况的判据。
    /// 每天只告警一次（日期落盘，重启程序也不重复）。
    /// </summary>
    public async Task<bool> CheckMissedCheckinAsync()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg == null || !cfg.AutoCheckinEnabled) return false;
            if (!TimeSpan.TryParse(cfg.AutoCheckinTime, out var due)) return false;

            // 宽限期：到点后再等 10 分钟，给自动签到本身留出执行时间
            if (DateTime.Now.TimeOfDay < due + TimeSpan.FromMinutes(MissedGraceMinutes)) return false;
            if (cfg.LastMissedAlertDate?.Date == DateTime.Today) return false;

            var checkedAccs = cfg.Accounts.Where(a => a.Enabled).ToList();
            if (checkedAccs.Count == 0) return false;

            static bool Signed(TraeCheckin.TraeAccount a)
                => a.LastCheckinDate.HasValue && a.LastCheckinDate.Value.Date == DateTime.Today;

            var pending = checkedAccs.Where(a => !Signed(a)).ToList();
            if (pending.Count == 0) return false;

            cfg.LastMissedAlertDate = DateTime.Now;
            try { cfg.Save(); } catch { /* 落盘失败不阻断告警 */ }

            static string Name(TraeCheckin.TraeAccount a)
                => string.IsNullOrEmpty(a.Name) ? (a.Id.Length > 6 ? a.Id[..6] : a.Id) : a.Name!;

            bool allMissed = pending.Count == checkedAccs.Count;
            string title = allMissed
                ? $"Trae 今日签到未完成（{checkedAccs.Count} 个账号全部未签）"
                : $"Trae 今日签到部分失败（{pending.Count}/{checkedAccs.Count} 个账号未签）";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"自动签到时间：{cfg.AutoCheckinTime}，已过 {MissedGraceMinutes} 分钟仍未完成。");
            sb.AppendLine($"当前进度：{checkedAccs.Count - pending.Count}/{checkedAccs.Count} 已签");
            foreach (var a in checkedAccs)
                sb.AppendLine((Signed(a) ? "✅ " : "⚠️ ") + Name(a) + (Signed(a) ? "  已签到" : "  未签到"));
            sb.AppendLine();
            sb.AppendLine("可能原因与处理：");
            sb.AppendLine("· 云端定时任务失败（最常见是 Session 过期）→ 「云端签到」页重新登录 Trae 后重新部署");
            sb.AppendLine("· 本机当时没开机 / 程序没运行 → 打开程序点「一键签到」补签");
            sb.AppendLine("· 命中风控 9074（参与用户太多）→ 错开整点时段，或换发设备号后重试");
            var content = sb.ToString().TrimEnd();

            AccountHelpers.CheckinLog("", $"[到点未签告警] {title}");
            StatusMessage = title;
            await NotifyDispatcher.SendAlertAsync(cfg, title, content);

            // 界面兜底：即使没配任何推送渠道，本机也要弹一条，并写入日志页
            MainViewModel.RaiseMissedAlert(title, content);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>到点未签的宽限期（分钟）。</summary>
    private const int MissedGraceMinutes = 10;

    /// <summary>自动签到上次已触发日期（每日仅触发一次）。</summary>
    private static DateTime _lastAutoCheckDate = DateTime.MinValue;

    /// <summary>把签到结果写入本地历史文件（统一走 AccountHelpers，格式 date | name | type | 结果）。</summary>
    private void TryAppendHistory(TraeCheckin.TraeAccount acc, double gained)
        => AccountHelpers.AppendHistory(acc, gained);

    /// <summary>今日奖励 + 今日已签状态：跨所有账号统计——任一账号未签即可一键签到，今日奖励为所有账号今日实际获得总积分。</summary>
    private void RefreshTodayState()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            var accounts = cfg?.Accounts;
            if (accounts == null || accounts.Count == 0)
            {
                TodayReward = "--";
                CheckedInToday = false;
                return;
            }

            // 一键签到 vs 今日已签：任一账号未签则允许点击（CheckedInToday=false）
            bool allChecked = accounts.All(a => a.LastCheckinDate.HasValue && a.LastCheckinDate.Value.Date == DateTime.Today);
            CheckedInToday = allChecked;

            // 今日奖励：统计所有账号今日实际获得的总积分
            double total = MainViewModel.CheckinDb?.GetTodayTotalCredits() ?? 0;
            TodayReward = total > 0 ? "+" + (int)total : "--";
        }
        catch
        {
            TodayReward = "--";
            CheckedInToday = false;
        }
    }

    /// <summary>账号切换联动：按新激活账号刷新会员/奖励/日历/记录。</summary>
    public void Reload()
    {
        RebuildCheckList();
        try
        {
            var cfg = MainViewModel.AppConfig;
            var acc = cfg?.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                      ?? cfg?.Accounts.FirstOrDefault();
            if (acc != null)
            {
                IsMember = acc.IsMember;
                MemberHint = acc.IsMember ? "会员：基础 150 + 连签 50" : "非会员：基础签到 150 积分";
                MemberMultiplier = acc.IsMember ? "×1.33" : "×1.0";
                StatusMessage = acc.LastCheckinDate.HasValue && acc.LastCheckinDate.Value.Date == DateTime.Today
                    ? "今日已签到"
                    : "";
            }
            RefreshTodayState();
            ReloadCalendar();
            LoadHistory();
        }
        catch { /* 保留原值 */ }
    }

    /// <summary>弹出登录窗口；登录成功返回 true。未登录时一键签到、手动登录共用。</summary>
    private async Task<bool> PromptLoginAsync(TraeCheckin.TraeAccount? acc)
    {
        try
        {
            var owner = UiHost.MainWindow;
            if (owner == null) return false;
            var dlg = new LoginWindow(acc);
            return await dlg.ShowDialog<bool>(owner);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 一键签到（对齐 TraeCheckin.DoCheckinAsync）：为全部 Enabled（= 勾选）账号签到。
    /// 存在未登录账号时先弹登录窗；完成后汇总文案与飞书推送，并同步仪表盘/账号状态。
    /// </summary>
    [RelayCommand]
    private Task DoCheckin() => RunBatchCheckinAsync();

    /// <summary>
    /// 批量签到的可复用入口（签到页「一键签到」与仪表盘「一键签到」共用同一条路径、同一份勾选）。
    /// 勾选真源是 <see cref="TraeAccount.Enabled"/>（签到页勾选框双向同步），因此两处行为必然一致。
    /// 返回 (是否有成功, 成功数, 总数, 总积分, 摘要文案)。
    /// </summary>
    public async Task<(bool Any, int Ok, int Total, double Gained, string Summary)> RunBatchCheckinAsync()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg == null || MainViewModel.CheckinApi == null)
            {
                StatusMessage = "服务未初始化";
                return (false, 0, 0, 0, StatusMessage);
            }
            RebuildCheckList();   // 勾选与账号保持同步（含刚添加的账号）
            if (cfg.Accounts.Count == 0)
            {
                StatusMessage = "没有可用账号，请先在「设置」中添加";
                return (false, 0, 0, 0, StatusMessage);
            }

            // 存在未登录账号：先弹登录窗（对齐源：登录取消则中止，登录成功继续）
            var firstNoToken = cfg.Accounts.FirstOrDefault(a => a.Enabled && string.IsNullOrEmpty(a.Token));
            if (firstNoToken != null)
            {
                StatusMessage = "检测到未登录账号，正在打开登录窗口…";
                bool logged = await PromptLoginAsync(firstNoToken);
                if (!logged)
                {
                    StatusMessage = "已取消登录";
                    return (false, 0, 0, 0, StatusMessage);
                }
                StatusMessage = "登录成功，正在为所有启用账号签到…";
            }

            var (any, results) = await CheckinAllAccountsAsync();
            int ok = results.Count(r => r.Ok);
            double total = results.Sum(r => r.Gained);
            string summary = results.Count == 0
                ? "今日所有账号均已签到"
                : $"共 {results.Count} 个账号，成功 {ok} 个，获得 {total:0} 积分";
            var fails = results.Where(r => !r.Ok).ToList();
            if (fails.Count > 0)
                summary += "；失败：" + string.Join("、", fails.Select(f => $"{f.Name}：{f.Reason}"));
            StatusMessage = summary;

            RefreshTodayState();
            ReloadCalendar();
            LoadHistory();
            if (results.Count > 0) await NotifyFeishuBatchAsync(results);
            if (any) await MainViewModel.NotifyAsyncRefresh();
            return (any, ok, results.Count, total, summary);
        }
        catch (Exception ex)
        {
            StatusMessage = $"签到异常：{ex.Message}";
            return (false, 0, 0, 0, StatusMessage);
        }
    }
}
















