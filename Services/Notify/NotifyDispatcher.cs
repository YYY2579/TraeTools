using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TraeCheckin;
using TraeTools.Services;

namespace TraeTools.Services.Notify;

/// <summary>单个渠道的推送结果（用于界面逐渠道显示成败）。</summary>
public sealed class ChannelOutcome
{
    public required string Channel { get; init; }
    public required bool Ok { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// 推送总调度：把一条消息扇出到全部已启用的渠道。
///
/// 设计原则：
///   · **互不影响**——各渠道并行发送，单个渠道失败不阻断其它渠道，也不影响签到主流程；
///   · **不静默**——每个渠道的成败都会被记录到日志，并在「测试推送」时逐条回显；
///   · **可开关**——总开关 NotifyOnCheckin 之外，每个渠道还有独立开关与配置。
/// </summary>
public static class NotifyDispatcher
{
    /// <summary>渠道标识（界面与日志共用同一套名称）。</summary>
    public const string ChFeishu = "飞书";
    public const string ChEmail = "邮箱";
    public const string ChWebhook = "通用 Webhook";
    public const string ChDingTalk = "钉钉";
    public const string ChWeCom = "企业微信";
    public const string ChTelegram = "Telegram";
    public const string ChSystem = "系统通知";

    /// <summary>
    /// 把所有渠道扇出执行；返回每个渠道的结果。
    /// 传入的 cfg 若为 null 则直接返回空列表。
    /// </summary>
    public static async Task<IReadOnlyList<ChannelOutcome>> SendAsync(
        AppConfig? cfg, string title, string content, bool includeSystem = true, CancellationToken ct = default)
    {
        var outcomes = new List<ChannelOutcome>();
        if (cfg == null) return outcomes;

        var tasks = new List<Task<ChannelOutcome>>();

        if (!string.IsNullOrWhiteSpace(cfg.FeishuWebhook))
            tasks.Add(WrapAsync(ChFeishu, async () =>
                await FeishuNotifier.SendTextAsync(cfg.FeishuWebhook, title + "\n" + content)
                    ? NotifyResult.Ok0("已发送") : NotifyResult.Fail("发送失败（请检查 Webhook 地址）")));

        if (cfg.EmailEnabled)
            tasks.Add(WrapAsync(ChEmail, async () => await EmailNotifier.SendAsync(
                cfg.SmtpHost, cfg.SmtpPort, cfg.SmtpSecurity, cfg.SmtpUser, cfg.SmtpPassword,
                cfg.SmtpFrom, cfg.SmtpTo, title, content, ct)));

        if (cfg.WebhookEnabled)
            tasks.Add(WrapAsync(ChWebhook, async () => await RobotNotifiers.SendWebhookAsync(
                cfg.WebhookUrl, cfg.WebhookBodyTemplate, cfg.WebhookContentType, title, content, ct)));

        if (cfg.DingTalkEnabled)
            tasks.Add(WrapAsync(ChDingTalk, async () => await RobotNotifiers.SendDingTalkAsync(
                cfg.DingTalkWebhook, cfg.DingTalkSecret, title, content, ct)));

        if (cfg.WeComEnabled)
            tasks.Add(WrapAsync(ChWeCom, async () => await RobotNotifiers.SendWeComAsync(
                cfg.WeComWebhook, title, content, ct)));

        if (cfg.TelegramEnabled)
            tasks.Add(WrapAsync(ChTelegram, async () => await RobotNotifiers.SendTelegramAsync(
                cfg.TelegramBotToken, cfg.TelegramChatId, title, content, ct)));

        if (includeSystem && cfg.NotifySystemEnabled)
            tasks.Add(Task.FromResult(SendSystem(title, content)));

        if (tasks.Count == 0) return outcomes;

        try
        {
            var done = await Task.WhenAll(tasks);
            outcomes.AddRange(done);
        }
        catch { /* WrapAsync 已兜住异常；此处仅防御 */ }

        // 统一落日志：推送是否成功对用户排查很关键
        foreach (var o in outcomes)
            AppLog.Write(o.Ok ? LogLevel.Info : LogLevel.Warn, "notify", "",
                $"{o.Channel}：{(o.Ok ? "成功" : "失败")} {o.Message}");

        return outcomes;
    }

    /// <summary>系统通知单独包一层（Send 是同步调用，且只应执行一次）。</summary>
    private static ChannelOutcome SendSystem(string title, string content)
    {
        var r = SystemNotifier.Send(title, content);
        return new ChannelOutcome { Channel = ChSystem, Ok = r.Ok, Message = r.Message };
    }

    /// <summary>把渠道调用包成「永不抛异常 + 带渠道名」的任务。</summary>
    private static async Task<ChannelOutcome> WrapAsync(string channel, Func<Task<NotifyResult>> body)
    {
        try
        {
            var r = await body();
            return new ChannelOutcome { Channel = channel, Ok = r.Ok, Message = r.Message };
        }
        catch (Exception ex)
        {
            return new ChannelOutcome { Channel = channel, Ok = false, Message = "异常：" + ex.Message };
        }
    }

    /// <summary>
    /// 签到结束后的批量结果推送。
    /// 全部成功 → 受「签到完成后推送」总开关约束（成功摘要属于可选的日常播报）。
    /// 有失败 → **不受总开关约束**：失败才是真正需要让人知道的事，否则用户永远只会收到「一切正常」。
    /// </summary>
    public static async Task<IReadOnlyList<ChannelOutcome>> SendCheckinBatchAsync(
        AppConfig? cfg, IReadOnlyList<(string Name, bool Ok, double Gained, string Reason)> results)
    {
        if (cfg == null || results.Count == 0) return Array.Empty<ChannelOutcome>();

        int okCount = results.Count(r => r.Ok);
        bool anyFailure = okCount < results.Count;

        if (!anyFailure && !cfg.NotifyOnCheckin) return Array.Empty<ChannelOutcome>();

        var title = anyFailure
            ? $"Trae 签到异常（{okCount}/{results.Count} 成功）"
            : $"Trae 签到结果（{okCount}/{results.Count} 成功）";

        var sb = new StringBuilder();
        sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm}");
        foreach (var r in results)
            sb.AppendLine((r.Ok ? "✅ " : "⚠️ ") + r.Name + (r.Ok ? $"  获得 {r.Gained:0} 积分" : $"  失败：{r.Reason}"));

        if (anyFailure)
        {
            sb.AppendLine();
            sb.AppendLine("如何处理：");
            sb.AppendLine("· 「会话失效」→ 在本程序重新登录该账号，再点「重新部署」刷新云端 Secret");
            sb.AppendLine("· 「9074 / 参与用户太多」→ 错开整点时段，或为该账号换发设备号后重新部署");
            sb.AppendLine("· 云端定时任务未运行 → 到 GitHub Actions 查看该次运行日志");
        }

        return await SendAsync(cfg, title, sb.ToString().TrimEnd());
    }

    /// <summary>
    /// 告警推送（过了签到时间仍未签 / 部分失败）：不受「签到完成后推送」总开关约束，
    /// 只受各渠道自己的开关约束。没有任何渠道启用时返回空列表，由调用方用界面/托盘兜底。
    /// </summary>
    public static Task<IReadOnlyList<ChannelOutcome>> SendAlertAsync(AppConfig? cfg, string title, string content)
        => cfg == null ? Task.FromResult<IReadOnlyList<ChannelOutcome>>(Array.Empty<ChannelOutcome>())
                       : SendAsync(cfg, title, content);

    /// <summary>把渠道结果汇总成一行人类可读文案（用于设置页状态条）。</summary>
    public static string Summarize(IReadOnlyList<ChannelOutcome> outcomes)
    {
        if (outcomes.Count == 0) return "没有启用任何推送渠道";
        var okCount = outcomes.Count(o => o.Ok);
        var head = $"推送完成：成功 {okCount}/{outcomes.Count}";
        var failed = outcomes.Where(o => !o.Ok).ToList();
        if (failed.Count == 0) return head + " ✓";
        return head + "；失败：" + string.Join("、", failed.Select(f => f.Channel));
    }
}
