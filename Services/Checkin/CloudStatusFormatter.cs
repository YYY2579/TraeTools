namespace TraeCheckin;

/// <summary>
/// 仪表盘「云端签到状态」文案映射。
/// 授权失效应提示重新授权，而非误报「未部署」。
/// </summary>
public static class CloudStatusFormatter
{
    public static (string Text, bool IsError) Describe(bool isAuthorized, WorkflowRunInfo? run)
    {
        if (!isAuthorized) return ("授权失效，请重新授权", true);
        if (run == null) return ("未部署", false);
        if (run.Conclusion == "success") return ("云端签到成功 ✓", false);
        if (run.Conclusion == "failure") return ("最近失败 ✗", true);
        if (run.Status == "completed") return ("已完成", false);
        return ("运行中…", false);
    }
}
