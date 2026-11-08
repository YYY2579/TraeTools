using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Input.Platform;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeCheckin;
using TraeTools.Models;
using TraeTools.Views;

namespace TraeTools.ViewModels;

/// <summary>
/// 云端签到：把签到脚本一键部署到用户自己的 GitHub 仓库（TraeCheckin「云端部署」完整流程）。
/// 设备码授权（OAuth Device Flow）→ fork 源仓库 → 写入 Actions Secret → 启用并触发 workflow。
/// </summary>
public partial class CloudViewModel : ViewModelBase
{
    private readonly GitHubApiClient _ghApi = new();

    [ObservableProperty]
    private string _gitHubUser = "";

    [ObservableProperty]
    private string _forkRepo = "TraeTools";

    [ObservableProperty]
    private string _cronExpression = "0 0 * * *";

    [ObservableProperty]
    private string _lastRun = "—";

    [ObservableProperty]
    private string _credentialExpiry = "—";

    [ObservableProperty]
    private string _warningText = "云端签到使用 TRAE_SESSION 作为凭证（Session 约 14 天有效；JWT 仅约 8 小时，由 Session 自动换新）。Session 过期后请重新登录 Trae 并重新部署。";

    /// <summary>提示条是否为「严重」态（红色）：Session 已过期或仅剩 ≤3 天，或云端最近一次运行失败。</summary>
    [ObservableProperty]
    private bool _warningIsCritical;

    /// <summary>云端最近一次运行的结论（success / failure / null），用于和凭证有效期合并告警。</summary>
    private string? _lastRunConclusion;

    // ===== 「上次云端运行结果」面板（不实时轮询，只在打开页面/部署后按上一次结果提醒）=====

    /// <summary>是否展示上次运行结果面板（有过运行记录才显示）。</summary>
    [ObservableProperty]
    private bool _hasLastRun;

    /// <summary>结论徽章文案：成功 / 失败 / 运行中。</summary>
    [ObservableProperty]
    private string _lastRunBadge = "";

    /// <summary>是否为失败（驱动红色样式与「如何修复」区的显示）。</summary>
    [ObservableProperty]
    private bool _lastRunFailed;

    /// <summary>时间行，如「2026-11-07 08:24（第 12 次运行）」。</summary>
    [ObservableProperty]
    private string _lastRunWhen = "";

    /// <summary>失败原因（由运行日志归类）。成功时为空。</summary>
    [ObservableProperty]
    private string _lastRunReason = "";

    /// <summary>修复建议。</summary>
    [ObservableProperty]
    private string _lastRunSuggestion = "";

    /// <summary>数据来自本地缓存（本次没能联网确认），文案需标注，避免用户误以为是刚查到的。</summary>
    [ObservableProperty]
    private bool _lastRunFromCache;

    /// <summary>诊断中（拉取日志需要几秒，给个提示避免用户以为卡住）。</summary>
    [ObservableProperty]
    private bool _isDiagnosing;

    /// <summary>本次会话是否已经诊断过该 runId（避免每次进页面都下载日志）。</summary>
    private long _diagnosedRunId;

    /// <summary>部署操作日志（底部深色日志区）。</summary>
    [ObservableProperty]
    private string _deployLog = "[云端部署] 已就绪\n点击「授权 / 部署」开始发布签到任务到 GitHub Actions";

    /// <summary>主状态文案（已授权/未授权/部署状态）。</summary>
    [ObservableProperty]
    private string _stateText = "尚未授权 GitHub";

    /// <summary>主按钮文案：授权 GitHub / 一键部署到云端 / 重新部署 / 检测中…</summary>
    [ObservableProperty]
    private string _actionButtonText = "授权 GitHub";

    /// <summary>设备码授权中：显示授权码提示。</summary>
    [ObservableProperty]
    private string _deviceCodeText = "";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>云端签到每日运行时间（北京时间 HH:mm）。改写 fork 中 checkin.yml 的 cron。</summary>
    [ObservableProperty]
    private string _cloudCheckinTime = "08:00";

    /// <summary>待轮询的设备码（用于「重新打开授权网页」）。</summary>
    private GitHubDeviceCode? _pendingCode;

    /// <summary>云端最多部署的账号数（与 checkin.yml 显式枚举一致）。</summary>
    private const int CloudAccountLimit = 4;

    public System.Collections.ObjectModel.ObservableCollection<CloudStep> Steps { get; } = new();

    public CloudViewModel()
    {
        Steps.Add(new CloudStep { Index = 1, Title = "GitHub 授权", Description = "自动打开授权页面完成登录", Status = "pending" });
        Steps.Add(new CloudStep { Index = 2, Title = "写入 Secret", Description = "写入账号会话与设备号凭证", Status = "pending" });
        Steps.Add(new CloudStep { Index = 3, Title = "启用定时任务", Description = "一键部署时自动启用 · 每日北京时间 08:00", Status = "pending" });

        RefreshCloudState(isDeployFlow: false);
        _ = RefreshDeploymentStateAsync();
    }

    private void AppendLog(string line)
    {
        DeployLog += $"\n[{DateTime.Now:HH:mm:ss}] {line}";
        AccountHelpers.AppLog("cloud", "", line);
    }

    /// <summary>从本地配置读取授权信息并刷新按钮/状态文案。</summary>
    private void RefreshCloudState(bool isDeployFlow)
    {
        var cfg = MainViewModel.AppConfig;
        bool hasToken = cfg != null && !string.IsNullOrEmpty(cfg.GitHubToken);
        if (hasToken)
        {
            StateText = (cfg?.GitHubLogin is { Length: > 0 } login ? "已授权：" + login : "已授权");
            ActionButtonText = isDeployFlow ? "检测部署状态…" : "一键部署到云端";
        }
        else
        {
            StateText = "尚未授权 GitHub";
            ActionButtonText = "授权 GitHub";
        }
    }

    /// <summary>进入页面/显示后自动检测云端部署状态（授权有效性与部署完成度）。</summary>
    private async Task RefreshDeploymentStateAsync()
    {
        var cfg = MainViewModel.AppConfig;
        if (cfg == null || string.IsNullOrEmpty(cfg.GitHubToken)) return;
        string token = cfg.GitHubToken;
        string login = cfg.GitHubLogin ?? "";
        try
        {
            if (string.IsNullOrEmpty(login))
            {
                login = await _ghApi.GetLoginAsync(token) ?? "";
                if (string.IsNullOrEmpty(login)) { ClearCloudAuth("GitHub 授权已失效，请重新授权"); return; }
                cfg.GitHubLogin = login;
                cfg.Save();
            }

            DeploymentStatus status;
            try { status = await _ghApi.GetDeploymentStatusAsync(token, login); }
            catch (Exception ex)
            {
                AccountHelpers.AppLog("cloud", "", $"检测部署状态异常：{ex.Message}");
                // 拿不到最新状态时，至少把上次已知的运行结果摆出来（并标注是缓存）
                ShowCachedLastRun();
                return;
            }

            if (!status.IsAuthorized) { ClearCloudAuth("GitHub 授权已失效，请重新授权"); return; }

            GitHubUser = login;
            // 同步「云端签到时间」设置到界面（默认 08:00；用户可改后单独应用或随部署生效）
            if (!string.IsNullOrWhiteSpace(cfg.CloudCheckinTime)) CloudCheckinTime = cfg.CloudCheckinTime;
            if (status.IsDeployed)
            {
                StateText = status.DeployedAccountCount >= 2
                    ? $"已部署完成（{status.DeployedAccountCount} 个账号），云端每天北京时间 {CloudCheckinTime} 自动签到"
                    : $"已部署完成（1 个账号），云端每天北京时间 {CloudCheckinTime} 自动签到";
                ActionButtonText = "重新部署";
                LastRun = await TryGetLastRunAsync(token, login);
                // 已部署：三步回填为已完成（冷启动进页面也能看到真实状态，而不是永远灰点）
                MarkAllSteps("ok");
                // 账号重新登录/增删后，把最新 Session 自动同步到云端 Secret，避免云端一直用旧值直到过期
                await TrySyncCloudSecretsAsync(cfg, token, login);
            }
            else
            {
                StateText = "已授权：" + login + "（尚未部署）";
                ActionButtonText = "一键部署到云端";
                MarkAllSteps("pending");
            }

            // 凭证有效期（原先 CredentialExpiry 从未赋值，界面恒显示「—」）
            UpdateCredentialExpiry();
        }
        catch
        {
            StateText = "检测部署状态失败，仍可手动部署";
            ActionButtonText = "一键部署到云端";
            ShowCachedLastRun();   // 断网/代理故障时也别让用户对着空白区发呆
        }
    }

    /// <summary>把部署流程三步统一置为某状态（pending/ok/warn）。</summary>
    private void MarkAllSteps(string status)
    {
        foreach (var s in Steps) s.Status = status;
    }

    /// <summary>
    /// 取「最老的那个启用账号 Session」的剩余天数：最老的一过期，云端就会有一个账号签不上。
    /// 返回 (剩余天数, 是否估算)。无账号或无任何时间戳时返回 (int.MaxValue, false)。
    /// </summary>
    private (int LeftDays, bool Estimated) SessionLeftDays()
    {
        const int SessionLifeDays = 14;
        var cfg = MainViewModel.AppConfig;
        var enabled = cfg?.Accounts.Where(a => a.Enabled).ToList() ?? new();
        if (enabled.Count == 0) return (int.MaxValue, false);

        DateTime oldest = DateTime.MaxValue;
        bool estimated = false;
        foreach (var acc in enabled)
        {
            if (acc.SessionUpdatedAt is { } s) { if (s < oldest) oldest = s; }
            else if (acc.TokenUpdatedAt is { } t) { if (t < oldest) oldest = t; estimated = true; }
        }
        if (oldest == DateTime.MaxValue) return (int.MaxValue, false);

        int usedDays = Math.Max(0, (int)(DateTime.Now - oldest).TotalDays);
        return (SessionLifeDays - usedDays, estimated);
    }

    /// <summary>
    /// 计算并展示云端凭据（Session）剩余寿命。
    /// Session 约 14 天有效，且**不会**因换取新 JWT 而续期，所以基准取
    /// <see cref="TraeCheckin.TraeAccount.SessionUpdatedAt"/>（登录/换新 Session 时写入）；
    /// 旧配置没有该字段时回退用 TokenUpdatedAt 估算，并在文案里标注。
    /// </summary>
    private void UpdateCredentialExpiry()
    {
        var cfg = MainViewModel.AppConfig;
        var enabled = cfg?.Accounts.Where(a => a.Enabled).ToList() ?? new();

        if (enabled.Count == 0)
        {
            CredentialExpiry = "—";
            WarningIsCritical = false;
            WarningText = "尚未配置启用账号，部署后云端将按启用账号的 Session 运行签到。";
            return;
        }

        var (leftDays, estimated) = SessionLeftDays();
        if (leftDays == int.MaxValue)
        {
            CredentialExpiry = "未知（建议重新登录一次）";
            WarningIsCritical = true;
            WarningText = "无法确定云端 Session 的签发时间。若云端签到失败，请在本程序重新登录 Trae 后重新部署。";
            return;
        }

        string est = estimated ? "（按 Token 更新时间估算）" : "";

        // 卡片里只放短文案：四列 Grid 宽度有限，长句子会被右侧裁掉；
        // 「估算」口径放到下方提示条（那里有换行空间）。
        CredentialExpiry = leftDays <= 0
            ? "已过期，需重新登录"
            : $"约 {leftDays} 天后过期";

        bool lastRunFailed = _lastRunConclusion == "failure";
        WarningIsCritical = leftDays <= 3 || lastRunFailed;

        WarningText = leftDays switch
        {
            <= 0 => "云端 Session 已过期，云端签到会失败。请在本程序重新登录 Trae，然后点「重新部署」刷新 Secret。",
            <= 3 => $"云端 Session 仅剩约 {leftDays} 天，建议尽快重新登录并「重新部署」，避免云端签到中断。"
                    + (lastRunFailed ? "（最近一次云端运行已失败，很可能就是这个原因）" : ""),
            _ => lastRunFailed
                    ? "云端最近一次签到运行失败，具体原因与修复方式见下方「上次云端运行结果」。"
                    : $"云端签到使用 TRAE_SESSION 作为凭证（约 14 天有效，与换取 JWT 无关），剩余约 {leftDays} 天{est}。"
        };
    }

    private static string FormatRunTime(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return "—";
        return DateTime.TryParse(iso, out var dt) ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : iso.Replace("T", " ").TrimEnd('Z');
    }

    private static DateTime? ParseRunTime(string? iso)
        => DateTime.TryParse(iso, out var dt) ? dt.ToLocalTime() : null;

    private async Task<string> TryGetLastRunAsync(string token, string login)
    {
        try
        {
            var run = await _ghApi.GetLatestRunAsync(token, login);
            if (run == null)
            {
                _lastRunConclusion = null;
                ShowCachedLastRun();
                return "—";
            }

            _lastRunConclusion = run.Conclusion;
            await ApplyLastRunAsync(run, token, login);

            var conclusionText = run.Conclusion switch
            {
                "success" => "成功",
                "failure" => "失败",
                _ => run.Status ?? "运行中"
            };
            return $"{FormatRunTime(run.CreatedAt)}（{conclusionText}）";
        }
        catch
        {
            _lastRunConclusion = null;
            ShowCachedLastRun();
            return "—";
        }
    }

    /// <summary>
    /// 把一次运行的结果落到面板上，并在失败时给出具体原因与修复方式。
    ///
    /// 设计口径（按需求）：**不做实时轮询**，只在打开页面/部署结束时读「上一次」结果；
    /// 原因是拉日志有开销，而云端签到每天只跑一次，实时探测没有收益。
    /// 失败原因按 run 日志归类，并按 runId 缓存，同一次运行不会反复下载日志。
    /// </summary>
    private async Task ApplyLastRunAsync(WorkflowRunInfo run, string token, string login)
    {
        var cfg = MainViewModel.AppConfig;
        HasLastRun = true;
        LastRunFromCache = false;
        LastRunFailed = run.Conclusion == "failure";
        LastRunBadge = run.Conclusion switch
        {
            "success" => "成功",
            "failure" => "失败",
            _ => run.Status ?? "运行中"
        };
        LastRunWhen = $"{FormatRunTime(run.CreatedAt)}" + (run.RunNumber > 0 ? $"（第 {run.RunNumber} 次运行）" : "");

        void Persist(string reason, string suggestion)
        {
            if (cfg == null) return;
            cfg.CloudLastRunId = run.Id;
            cfg.CloudLastRunConclusion = run.Conclusion ?? "";
            cfg.CloudLastRunAt = ParseRunTime(run.CreatedAt);
            cfg.CloudLastRunReason = reason;
            cfg.CloudLastRunSuggestion = suggestion;
            try { cfg.Save(); } catch { /* 落盘失败不影响展示 */ }
        }

        if (!LastRunFailed)
        {
            LastRunReason = "";
            LastRunSuggestion = "";
            // 成功即清掉历史失败原因，避免下次离线时误报旧故障
            if (cfg == null || cfg.CloudLastRunId != run.Id || !string.IsNullOrEmpty(cfg.CloudLastRunReason))
                Persist("", "");
            return;
        }

        // 1) 同一次 run 已有缓存结论 → 直接用，不重复下载日志
        if (cfg != null && cfg.CloudLastRunId == run.Id && !string.IsNullOrEmpty(cfg.CloudLastRunReason))
        {
            LastRunReason = cfg.CloudLastRunReason;
            LastRunSuggestion = cfg.CloudLastRunSuggestion;
            return;
        }

        // 2) 本次会话已诊断过同一 run → 不重复诊断
        if (_diagnosedRunId == run.Id && !string.IsNullOrEmpty(LastRunReason)) return;

        // 3) 拉日志诊断
        IsDiagnosing = true;
        try
        {
            var d = await _ghApi.DiagnoseRunFailureAsync(token, login, run.Id);
            _diagnosedRunId = run.Id;

            // 日志拿不到时的兜底：Session 已过期本身就是最可能的原因，直接点明
            var (leftDays, _) = SessionLeftDays();
            if (d.Key == "unknown" && leftDays <= 0)
            {
                d.Key = "session";
                d.Reason = "云端登录态已过期（Session 超过 14 天），运行日志已无法获取";
                d.Suggestion = "在本程序重新登录 Trae，然后点「重新部署」把新 Session 写回云端。";
            }

            LastRunReason = d.Reason;
            LastRunSuggestion = d.Suggestion;
            Persist(d.Reason, d.Suggestion);
            AppendLog($"上次云端运行失败：{d.Reason}");
        }
        catch (Exception ex)
        {
            LastRunReason = "运行失败，原因识别时出错：" + ex.Message;
            LastRunSuggestion = "点「打开 Actions」查看日志。";
        }
        finally
        {
            IsDiagnosing = false;
        }
    }

    /// <summary>拿不到最新运行记录时，退回展示本地上次已知结果（并标注可能已过期）。</summary>
    private void ShowCachedLastRun()
    {
        var cfg = MainViewModel.AppConfig;
        if (cfg == null || string.IsNullOrEmpty(cfg.CloudLastRunConclusion))
        {
            HasLastRun = false;
            return;
        }

        HasLastRun = true;
        LastRunFromCache = true;
        LastRunFailed = cfg.CloudLastRunConclusion == "failure";
        LastRunBadge = cfg.CloudLastRunConclusion == "failure" ? "失败" : "成功";
        LastRunWhen = (cfg.CloudLastRunAt?.ToString("yyyy-MM-dd HH:mm") ?? "—") + "（本地缓存）";
        LastRunReason = cfg.CloudLastRunReason;
        LastRunSuggestion = cfg.CloudLastRunSuggestion;
    }

    private void ClearCloudAuth(string reason)
    {
        var cfg = MainViewModel.AppConfig;
        if (cfg != null)
        {
            cfg.GitHubToken = null;
            cfg.GitHubLogin = null;
            cfg.Save();
        }
        _pendingCode = null;
        DeviceCodeText = "";
        MarkAllSteps("pending");
        CredentialExpiry = "—";
        WarningIsCritical = false;
        AppendLog(reason);
        RefreshCloudState(isDeployFlow: false);
    }

    /// <summary>主按钮统一入口：未授权→设备码授权；已授权→部署/重新部署。</summary>
    [RelayCommand]
    private async Task OneClickDeploy()
    {
        if (IsBusy) return;
        try
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg == null) { AppendLog("配置未初始化"); return; }

            if (string.IsNullOrEmpty(cfg.GitHubToken))
            {
                if (_pendingCode != null) { ReopenAuthPage(); return; }
                await AuthAsync();
                return;
            }
            await DeployAsync();
        }
        catch (Exception ex)
        {
            AppendLog("操作异常：" + ex.Message);
        }
    }

    /// <summary>GitHub 设备码授权：申请设备码 → 打开浏览器 → 轮询直到拿到 access_token。</summary>
    private async Task AuthAsync()
    {
        IsBusy = true;
        ActionButtonText = "重新打开授权网页";
        try
        {
            var code = await _ghApi.RequestDeviceCodeAsync();
            if (code == null)
            {
                AppendLog("申请设备码失败：" + (_ghApi.LastError ?? "未知错误"));
                RefreshCloudState(isDeployFlow: false);
                return;
            }
            _pendingCode = code;
            DeviceCodeText = "授权码：" + code.UserCode + "（请在打开的网页中粘贴）";
            StateText = "请在打开的 GitHub 页面中完成授权…";
            OpenAuthPage(code);

            int interval = code.Interval >= 1 ? code.Interval : 5;
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(interval * 1000);
                var (state, token) = await _ghApi.PollForAccessTokenAsync(code.DeviceCode);
                if (state == DeviceAuthState.Success && !string.IsNullOrEmpty(token))
                {
                    var login = await _ghApi.GetLoginAsync(token);
                    var cfg = MainViewModel.AppConfig;
                    if (cfg != null)
                    {
                        cfg.GitHubToken = token;
                        cfg.GitHubLogin = login;
                        cfg.Save();
                    }
                    _pendingCode = null;
                    DeviceCodeText = "";
                    GitHubUser = login ?? "";
                    AppendLog("授权成功：" + (login ?? ""));
                    RefreshCloudState(isDeployFlow: false);
                    await RefreshDeploymentStateAsync();
                    return;
                }
                if (state == DeviceAuthState.Failed)
                {
                    _pendingCode = null;
                    DeviceCodeText = "";
                    AppendLog("授权失败：" + (_ghApi.LastError ?? "未知错误"));
                    RefreshCloudState(isDeployFlow: false);
                    return;
                }
            }
            _pendingCode = null;
            DeviceCodeText = "";
            AppendLog("授权超时，请重新授权");
            RefreshCloudState(isDeployFlow: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async void OpenAuthPage(GitHubDeviceCode code)
    {
        // 自动复制授权码到剪贴板（与原 TraeCheckin 行为一致）
        try
        {
            var top = UiHost.MainWindow;
            if (top?.Clipboard is { } cb)
            {
                await cb.SetTextAsync(code.UserCode);
                AppendLog("授权码 " + code.UserCode + " 已复制到剪贴板，粘贴到网页即可");
            }
        }
        catch { /* 复制失败不阻断 */ }

        try { Process.Start(new ProcessStartInfo(code.VerificationUri) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            AppendLog("自动打开浏览器失败，请手动访问：" + code.VerificationUri + "（" + ex.Message + "）");
        }
    }

    private void ReopenAuthPage()
    {
        if (_pendingCode == null) return;
        AppendLog("已重新打开授权网页，授权码：" + _pendingCode.UserCode);
        OpenAuthPage(_pendingCode);
    }

    /// <summary>
    /// 一键部署（TraeCheckin 完整流程）：fork → 每个账号写 Session/DeviceId Secret →
    /// 清理多余 Secret → 写飞书 Webhook → 启用 workflow → 触发验证运行 → 轮询结论。
    /// </summary>
    private async Task DeployAsync()
    {
        var cfg = MainViewModel.AppConfig;
        if (cfg == null) return;

        var enabled = cfg.Accounts.Where(a => a.Enabled).ToList();
        if (enabled.Count == 0) { AppendLog("没有启用的 Trae 账号，请先到「设置」页添加/登录账号后再部署"); return; }
        if (enabled.Any(a => string.IsNullOrEmpty(a.Session)))
        {
            AppendLog("存在未登录的账号（缺少会话 Cookie），请先在设置页登录后再部署");
            return;
        }
        if (enabled.Count > CloudAccountLimit)
        {
            AppendLog($"当前启用了 {enabled.Count} 个账号，云端部署上限为 {CloudAccountLimit} 个（受 checkin.yml 映射限制）");
            return;
        }

        IsBusy = true;
        ActionButtonText = "部署中…";
        try
        {
            string token = cfg.GitHubToken!;
            string login = await _ghApi.GetLoginAsync(token) ?? "";
            if (string.IsNullOrEmpty(login)) { ClearCloudAuth("GitHub 授权已失效，请重新授权"); return; }
            if (cfg.GitHubLogin != login) { cfg.GitHubLogin = login; cfg.Save(); }
            GitHubUser = login;

            AppendLog("开始部署…");

                        Steps[0].Status = "ok"; Steps[1].Status = "progress";

            // 优化①：探测是否已有旧 fork。已存在且用户同意 → 在 UI 里一步「删除并重建」
            //（消灭「需去 GitHub 网页手动删 fork」这一小白最大障碍：#8/#13/#15 的根因之一）
            // 注意：源仓库 owner 本人部署时不走 fork（ForkAsync/DeleteForkAsync 均跳过），无需探测，
            // 否则 /repos/{login}/{repo} 返回 200 会误判成「存在旧 fork」并弹出误导性删除确认。
            bool forkKept = false;   // 用户选择「保留现有 fork」→ 后面跳过 ForkAsync，避免重复建 fork（#26）
            if (!GitHubApiClient.ShouldSkipStar(login))
            {
                int forkStatus = await _ghApi.CheckForkAsync(token, login);
                if (forkStatus == (int)System.Net.HttpStatusCode.Unauthorized ||
                    forkStatus == (int)System.Net.HttpStatusCode.Forbidden)
                {
                    HandleDeployFailure();
                    return;
                }
                if (forkStatus == (int)System.Net.HttpStatusCode.OK)
                {
                    var ownerW = UiHost.MainWindow;
                    var ask = new PromptWindow(
                        "检测到已部署过一次",
                        "您已 fork 过 TraeTools。若之前部署异常（「未找到 workflow」「HTTP 401」等），" +
                        "通常需要删除旧 fork 后重建。\n\n是否删除旧 fork 并重新部署？\n" +
                        "（选择「保留」则继续使用现有仓库，不会重复创建）",
                        "删除并重建", "保留现有");
                    bool rebuild = ownerW != null ? await ask.ShowDialog<bool>(ownerW) : true;
                    if (rebuild)
                    {
                        AppendLog("正在删除旧 fork 仓库…");
                        if (!await _ghApi.DeleteForkAsync(token, login)) { HandleDeployFailure(); return; }
                        AppendLog("旧 fork 已删除，重新创建中…");
                    }
                    else
                    {
                        forkKept = true;   // 保留现有：不复建 fork，兑现「不会重复创建」承诺（#26）
                        AppendLog("沿用现有 fork 仓库，跳过新建 fork…");
                    }
                }
            }

            if (!forkKept)
            {
                AppendLog("正在 fork 仓库…");
                if (!await _ghApi.ForkAsync(token, login)) { HandleDeployFailure(); return; }
                AppendLog("fork 完成");
            }

            // 写凭证（含清理多余 + 飞书 webhook）；重建 fork 后会自动再次调用
            if (!await WriteSecretsToCloudAsync(token, login, enabled)) { HandleDeployFailure(); return; }

                        Steps[1].Status = "ok"; Steps[2].Status = "progress";
            AppendLog("正在查找 checkin workflow…");
            long wfId = await _ghApi.GetWorkflowIdAsync(token, login);
            if (wfId < 0)
            {
                AppendLog("fork 中未找到 checkin workflow，正在开启该 fork 的 GitHub Actions…");
                if (!await _ghApi.EnsureActionsEnabledAsync(token, login)) { HandleDeployFailure(); return; }
                for (int i = 0; i < 12 && wfId < 0; i++)
                {
                    await Task.Delay(5000);
                    wfId = await _ghApi.GetWorkflowIdAsync(token, login);
                }
                if (wfId < 0)
                {
                    // 优化⑥：自动自愈 —— 开启 Actions 后仍无 workflow 时，询问一次「删 fork 重建」，
                    // 不再要求小白手动去 GitHub 网页删除旧 fork
                    AppendLog("已开启 Actions 但仍未登记 workflow（旧 fork 早于 checkin.yml、或 fork 状态异常所致）。");
                    var ownerW = UiHost.MainWindow;
                    var ask = new PromptWindow(
                        "workflow 未登记",
                        "已开启 GitHub Actions 但仍找不到 checkin 定时任务。\n" +
                        "通常是旧 fork 早于源仓库添加 checkin.yml、或 fork 状态异常导致。\n\n" +
                        "是否自动删除旧 fork 并重建？（重建后会自动重新写入凭证）",
                        "删除并重建", "保留现状");
                    bool rebuild = ownerW != null ? await ask.ShowDialog<bool>(ownerW) : false;
                    if (!rebuild)
                    {
                        AppendLog("已跳过重建：可手动删除 fork 后重试，或到源仓库确认 checkin.yml 是否存在。");
                        HandleDeployFailure();
                        return;
                    }

                    AppendLog("正在删除旧 fork 并重建…");
                    if (!await _ghApi.DeleteForkAsync(token, login)) { HandleDeployFailure(); return; }
                    await Task.Delay(3000);   // 等 GitHub 完成删除，避免立即 re-fork 报冲突
                    if (!await _ghApi.ForkAsync(token, login)) { HandleDeployFailure(); return; }
                    AppendLog("fork 已重建，重新写入凭证…");
                    if (!await WriteSecretsToCloudAsync(token, login, enabled)) { HandleDeployFailure(); return; }

                    AppendLog("等待重建后 workflow 登记…");
                    for (int i = 0; i < 12 && wfId < 0; i++)
                    {
                        await Task.Delay(5000);
                        wfId = await _ghApi.GetWorkflowIdAsync(token, login);
                    }
                    if (wfId < 0)
                    {
                        AppendLog("重建后仍未登记 workflow，请到「打开 Actions」或源仓库确认，稍后可重新部署。");
                        HandleDeployFailure();
                        return;
                    }
                    AppendLog("重建完成，checkin workflow 已登记");
                }
                AppendLog("checkin workflow 可用");
            }
            if (!await _ghApi.EnableWorkflowAsync(token, login, wfId)) { HandleDeployFailure(); return; }
            Steps[2].Status = "ok";
            AppendLog("workflow 已启用");

            // 应用自定义签到时间（北京时间 → UTC cron，改写 fork 的 checkin.yml）
            bool cronApplied = await ApplyCronInternalAsync(token, login);

            AppendLog("正在触发一次验证运行…");
            // 先记下当前最新一次运行的编号，后面只认「本次 dispatch 之后新产生的 run」，
            // 否则可能把上一次（甚至前几天的）运行结论当成这次部署的结果。
            var previousRun = await _ghApi.GetLatestRunAsync(token, login);
            long previousRunNumber = previousRun?.RunNumber ?? 0;
            if (!await _ghApi.DispatchWorkflowAsync(token, login, wfId)) { HandleDeployFailure(); return; }

            AppendLog("等待运行结果（最长约 100 秒）…");
            string? conclusion = null;
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(5000);
                var latestRun = await _ghApi.GetLatestRunAsync(token, login);
                if (latestRun == null || latestRun.RunNumber <= previousRunNumber) continue;
                conclusion = latestRun.Conclusion;
                if (!string.IsNullOrEmpty(conclusion)) break;
            }

            if (conclusion == "success")
            {
                var timeNote = cronApplied
                    ? $"GitHub 将每天北京时间 {CloudCheckinTime} 自动签到。"
                    : "但云端定时时间未更新（仍为仓库原有时间），请稍后重试「应用到云端」。";
                AppendLog($"部署成功！{enabled.Count} 个账号已就绪，{timeNote}");
                StateText = cronApplied
                    ? $"已部署完成（{enabled.Count} 个账号），云端每天 {CloudCheckinTime} 自动签到"
                    : $"已部署完成（{enabled.Count} 个账号），云端定时未更新";
                ActionButtonText = "重新部署";
            }
            else if (string.IsNullOrEmpty(conclusion))
            {
                AppendLog("已触发，但未在超时时间内拿到结果，请点击「打开 Actions」查看运行状态。");
            }
            else
            {
                // 优化④：failure 时给出最常见的两类根因（Trae 风控 / Actions 环境），避免小白无从下手
                AppendLog(conclusion == "failure"
                    ? "运行结论：failure。常见原因：① Trae 风控拦截——日志出现「当前参与用户太多/参与用户太多」时，"
                      + "建议错开 08:00 高峰、减少云端账号数；② GitHub Actions 环境异常。请到 Actions 日志确认后再判断。"
                    : $"运行结论：{conclusion}，请到 GitHub Actions 页面查看日志。");
            }
            UpdateCredentialExpiry();
            await RefreshDeploymentStateAsync();
        }
        catch (Exception ex)
        {
            Steps[0].Status = Steps[0].Status == "pending" ? "warn" : Steps[0].Status;
            Steps[1].Status = Steps[1].Status == "progress" ? "warn" : Steps[1].Status;
            Steps[2].Status = Steps[2].Status == "progress" ? "warn" : Steps[2].Status;
            AppendLog("部署异常：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void HandleDeployFailure()
    {
        var err = _ghApi.LastError ?? "";
        if (err.Contains("授权已失效", StringComparison.Ordinal))
        {
            ClearCloudAuth(err);
        }
        else if (err.StartsWith("fork ", StringComparison.Ordinal))
        {
            // fork 专用诊断（BuildForkError 文案）原样输出，避免被下方 401/403 分支误判
            // 覆盖成「补 Actions/Secrets/Contents 权限」的旧方向提示（#26）。
            AppendLog("部署失败：" + err);
        }
        else if (err.Contains("401", StringComparison.Ordinal))
        {
            // 优化②：401/403 给出可执行的小白文案，而不是裸报 HTTP 码
            AppendLog("部署失败：" + GitHubApiClient.BuildAuthFailureHint(401));
        }
        else if (err.Contains("403", StringComparison.Ordinal))
        {
            AppendLog("部署失败：" + GitHubApiClient.BuildAuthFailureHint(403));
        }
        else
        {
            AppendLog(string.IsNullOrEmpty(err) ? "部署失败，请重试" : err);
        }
        RefreshCloudState(isDeployFlow: false);
    }

    /// <summary>
    /// 把启用账号的 Session/DeviceId（含清理多余）与飞书 Webhook 写入 fork 的 Actions Secret。
    /// 部署流程与「凭据自动同步」共用；成功后记录凭据指纹，避免重复同步。
    /// </summary>
    private async Task<bool> WriteSecretsToCloudAsync(string token, string login, IReadOnlyList<TraeAccount> enabled)
    {
        var cfg = MainViewModel.AppConfig;
        if (cfg == null) return false;

        for (int i = 0; i < enabled.Count; i++)
        {
            var acc = enabled[i];
            int idx = i + 1;
            var sName = GitHubApiClient.SessionSecretNameFor(idx);
            var dName = GitHubApiClient.DeviceSecretNameFor(idx);

            AppendLog($"正在写入 {sName} secret…");
            if (!await _ghApi.SetSecretAsync(token, login, sName, acc.Session ?? "")) return false;
            AppendLog($"{sName} 写入成功");

            AppendLog($"正在写入 {dName} secret…");
            if (!await _ghApi.SetSecretAsync(token, login, dName, acc.DeviceId ?? "")) return false;
            AppendLog($"{dName} 写入成功");
        }

        // 清理本次减少后遗留的多余 secret，避免云端误签已停用账号
        for (int n = enabled.Count + 1; n <= CloudAccountLimit; n++)
        {
            var stale = GitHubApiClient.SessionSecretNameFor(n);
            if (!await _ghApi.DeleteSecretAsync(token, login, stale)) return false;
            var staleD = GitHubApiClient.DeviceSecretNameFor(n);
            if (!await _ghApi.DeleteSecretAsync(token, login, staleD)) return false;
            AppendLog($"已清理多余 {stale} secret");
        }

        if (!string.IsNullOrEmpty(cfg.FeishuWebhook))
        {
            AppendLog("正在写入 FEISHU_WEBHOOK secret…");
            if (!await _ghApi.SetSecretAsync(token, login, GitHubApiClient.FeishuWebhookSecretName, cfg.FeishuWebhook)) return false;
            AppendLog("FEISHU_WEBHOOK 写入成功");
        }

        cfg.CloudSecretsHash = ComputeSecretsHash(enabled);
        try { cfg.Save(); } catch { /* 忽略 */ }
        return true;
    }

    /// <summary>启用账号凭据（Id/Session/DeviceId）的 SHA-256 指纹，用于判断是否需要重新同步 Secret。</summary>
    private static string ComputeSecretsHash(IReadOnlyList<TraeAccount> enabled)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var a in enabled)
            sb.Append(a.Id).Append('|').Append(a.Session).Append('|').Append(a.DeviceId).Append('\n');
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Session 更新或账号增删后，自动把新凭据同步到云端 Secret，避免云端长期沿用旧 Session 直到过期失败。</summary>
    private async Task TrySyncCloudSecretsAsync(AppConfig cfg, string token, string login)
    {
        // 与部署保持一致：若某个启用账号还没登录（缺少 Session），整体跳过同步，
        // 否则会把后面的账号顶到前面的序号，导致云端签到串号。
        var enabled = cfg.Accounts.Where(a => a.Enabled).ToList();
        if (enabled.Count == 0 || enabled.Count > CloudAccountLimit) return;
        if (enabled.Any(a => string.IsNullOrEmpty(a.Session))) return;
        if (ComputeSecretsHash(enabled) == cfg.CloudSecretsHash) return;

        AppendLog("检测到账号凭据与云端不一致，正在自动同步 Secret…");
        if (await WriteSecretsToCloudAsync(token, login, enabled))
            AppendLog("账号凭据已自动同步到云端");
        else
            AppendLog("自动同步云端凭据失败：" + _ghApi.LastError + "（可稍后重新部署）");
    }

    /// <summary>
    /// 应用自定义签到时间（北京时间 HH:mm → UTC cron，改写 fork 的 checkin.yml）。
    /// 部署流程与「应用到云端」按钮共用；失败不阻断部署（云端继续沿用仓库原有定时）。
    /// </summary>
    private async Task<bool> ApplyCronInternalAsync(string token, string login)
    {
        var cron = GitHubApiClient.CronFromBeijingTime(CloudCheckinTime);
        if (cron == null)
        {
            AppendLog($"签到时间「{CloudCheckinTime}」格式无效（应为 HH:mm，如 08:30），沿用默认 08:00");
            CloudCheckinTime = "08:00";
            cron = "0 0 * * *";
        }
        AppendLog($"正在设置云端签到时间为北京时间 {CloudCheckinTime}（UTC cron: {cron}）…");
        if (await _ghApi.UpdateWorkflowCronAsync(token, login, cron))
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg != null && cfg.CloudCheckinTime != CloudCheckinTime)
            {
                cfg.CloudCheckinTime = CloudCheckinTime;
                try { cfg.Save(); } catch { /* 忽略 */ }
            }
            AppendLog($"云端签到时间已生效：每天北京时间 {CloudCheckinTime}（GitHub 定时调度通常有 ±15 分钟左右延迟，属正常现象）");
            return true;
        }
        AppendLog("设置签到时间失败：" + _ghApi.LastError + "（部署继续，云端定时沿用仓库原有设置）");
        return false;
    }

    /// <summary>「应用到云端」按钮：只更新云端签到时间，无需重新部署。</summary>
    [RelayCommand]
    private async Task ApplyCloudTime()
    {
        if (IsBusy) return;
        var cfg = MainViewModel.AppConfig;
        if (cfg == null || string.IsNullOrEmpty(cfg.GitHubToken))
        {
            AppendLog("尚未授权 GitHub，请先授权后再设置签到时间");
            return;
        }
        if (GitHubApiClient.CronFromBeijingTime(CloudCheckinTime) == null)
        {
            AppendLog($"时间「{CloudCheckinTime}」格式无效，请输入 HH:mm（如 08:30）");
            return;
        }
        IsBusy = true;
        try
        {
            string token = cfg.GitHubToken;
            string login = cfg.GitHubLogin ?? GitHubUser;
            if (string.IsNullOrEmpty(login))
            {
                login = await _ghApi.GetLoginAsync(token) ?? "";
                if (string.IsNullOrEmpty(login)) { ClearCloudAuth("GitHub 授权已失效，请重新授权"); return; }
            }
            await ApplyCronInternalAsync(token, login);
            await RefreshDeploymentStateAsync();
        }
        catch (Exception ex)
        {
            AppendLog("设置签到时间异常：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 授权方式 2（PAT 粘贴）：弹出输入框 → ValidatePatAsync 校验 → 保存授权。
    /// 与源 TraeCheckin UsePatAsync 对齐；设备码授权仍为主方式。
    /// </summary>
    [RelayCommand]
    private async Task UsePat()
    {
        try
        {
            var owner = UiHost.MainWindow;
            if (owner == null) { AppendLog("无法打开输入窗口（主窗口未就绪）"); return; }
            var dlg = new Views.PatInputWindow();
            bool ok = await dlg.ShowDialog<bool>(owner);
            if (!ok || string.IsNullOrWhiteSpace(dlg.PatText)) { AppendLog("已取消 PAT 授权"); return; }

            AppendLog("正在校验 PAT…");
            var v = await _ghApi.ValidatePatAsync(dlg.PatText);
            if (!v.IsValid)
            {
                AppendLog("PAT 校验失败：" + (v.Error ?? "未知错误"));
                return;
            }
            var cfg = MainViewModel.AppConfig;
            if (cfg != null)
            {
                cfg.GitHubToken = dlg.PatText;
                cfg.GitHubLogin = v.Login;
                cfg.Save();
            }
            GitHubUser = v.Login ?? "";
            AppendLog("已使用 PAT 授权：" + (v.Login ?? "") +
                      (v.CanWrite ? "" : "（尚未检测到 fork 仓库，部署时会自动创建）"));
            RefreshCloudState(isDeployFlow: false);
            await RefreshDeploymentStateAsync();
        }
        catch (Exception ex)
        {
            AppendLog("PAT 授权失败：" + ex.Message);
        }
    }

    /// <summary>打开 GitHub Actions 页面（默认浏览器）。</summary>
    [RelayCommand]
    private void OpenActions()
    {
        try
        {
            var owner = string.IsNullOrEmpty(GitHubUser) ? "YYY2579" : GitHubUser;
            var url = $"https://github.com/{owner}/{ForkRepo}/actions";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            AppendLog($"已在浏览器打开：{url}");
        }
        catch (Exception ex)
        {
            AppendLog("打开 Actions 失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 重新登录当前激活账号：云端失败最常见的原因是 Session 过期，而修复只能靠重新登录。
    /// 登录成功后 SessionUpdatedAt 会刷新，随后 RefreshDeploymentStateAsync 里的
    /// TrySyncCloudSecretsAsync 会把新 Session 自动写回云端 Secret。
    /// </summary>
    [RelayCommand]
    private async Task Relogin()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            var owner = UiHost.MainWindow;
            var acc = cfg?.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                      ?? cfg?.Accounts.FirstOrDefault();
            if (cfg == null || owner == null || acc == null)
            {
                AppendLog("没有可重新登录的账号，请先到「设置 → 账号管理」添加");
                return;
            }

            var dlg = new Views.LoginWindow(acc);
            bool ok = await dlg.ShowDialog<bool>(owner);
            if (!ok)
            {
                AppendLog("已取消重新登录");
                return;
            }

            try { cfg.Save(); } catch { /* 忽略 */ }
            AppendLog("本地登录态已更新，正在把新的 Session 同步到云端…");
            UpdateCredentialExpiry();
            await RefreshDeploymentStateAsync();   // 内含 TrySyncCloudSecretsAsync
        }
        catch (Exception ex)
        {
            AppendLog("重新登录异常：" + ex.Message);
        }
    }
}








