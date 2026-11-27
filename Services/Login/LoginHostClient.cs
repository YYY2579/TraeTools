using System.Diagnostics;
using System.Text.Json;

namespace TraeTools.Services.Login;

/// <summary>
/// 独立登录宿主（<c>TraeTools.LoginHost.exe</c>）的调用方（P2-1）。
/// 主程序不再引用 WinForms / WebView2（NativeAOT 的两个硬阻塞），而是：
///   找宿主 exe → 起进程 → 等退出 → 读回临时 JSON。宿主缺失时上层回退到"手动粘贴 Token"。
/// </summary>
public static class LoginHostClient
{
    /// <summary>
    /// 登录结果。Token 为空且 Error 非空 = 宿主启动/初始化失败（如 WebView2 不可用）；
    /// 整体返回 null = 用户主动取消 / 登录未完成。
    /// </summary>
    public sealed record LoginResult(string? Token, string? Session, string? Error = null, bool UidConfirmed = false);

    /// <summary>
    /// 宿主可执行文件名。Windows 是 .NET 宿主 TraeTools.LoginHost.exe，
    /// macOS 是 Swift+WKWebView 宿主 TraeTools.LoginHost（无扩展名）。
    /// 两者按同一套命令行参数与 --out 回写 JSON 契约工作，主程序无需区分。
    /// </summary>
    public static string HelperFileName =>
        OperatingSystem.IsWindows() ? "TraeTools.LoginHost.exe" : "TraeTools.LoginHost";

    /// <summary>环境变量覆盖（测试/便携部署用）。</summary>
    public const string HelperPathEnvVar = "TRAETOOLS_LOGIN_HOST";

    /// <summary>宿主 exe 查找顺序：环境变量 → 主程序同目录 → 同目录 loginhost\ 子目录。</summary>
    public static string? ResolveHelperPath()
    {
        var candidates = new List<string>();
        var env = Environment.GetEnvironmentVariable(HelperPathEnvVar);
        if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env!);
        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, HelperFileName));
        candidates.Add(Path.Combine(baseDir, "loginhost", HelperFileName));

        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return Path.GetFullPath(c); } catch { /* 非法路径跳过 */ }
        }
        return null;
    }

    public static bool IsAvailable() => ResolveHelperPath() != null;

    /// <summary>
    /// 启动宿主并等待其结束，返回取得的凭据；用户取消或超时返回 null。
    /// initialToken 用于让宿主忽略 localStorage 里的残留旧 token（只有"新 token"才被接受）。
    /// expectedUid 为账号的平台用户 ID（JWT data.id）：宿主读到归属不符的 token 时不会关闭窗口，
    /// 防止「残留会话自动续期出的他人 token」瞬间顶掉窗口（一闪而过）并造成跨账号凭据污染。
    /// proxy 为账号生效代理（host:port / scheme://…），传入后 webview 经该代理上网；空 = 直连。
    /// </summary>
    public static Task<LoginResult?> RunAsync(
        string userDataDir, string? initialToken, bool debugCapture = false, TimeSpan? timeout = null,
        string? expectedUid = null, string? proxy = null)
    {
        var extra = new List<string>();
        if (expectedUid is not null)
        {
            extra.Add("--expected-uid");
            extra.Add(expectedUid);
        }
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            extra.Add("--proxy-server");
            extra.Add(proxy.Trim());
        }
        return RunCoreAsync(userDataDir, initialToken, debugCapture, timeout, extra);
    }

    /// <summary>
    /// 核心实现。<paramref name="extraArgs"/> 供独立环境验证注入 <c>--selftest</c>（不显示窗口、直接回写结果），
    /// 生产路径始终传 null。
    /// </summary>
    internal static async Task<LoginResult?> RunCoreAsync(
        string userDataDir, string? initialToken, bool debugCapture, TimeSpan? timeout, IEnumerable<string>? extraArgs)
    {
        var exe = ResolveHelperPath()
            ?? throw new FileNotFoundException("未找到登录宿主程序：" + HelperFileName);

        var outFile = Path.Combine(Path.GetTempPath(), "traetools_login_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add("--user-data-dir");
            psi.ArgumentList.Add(userDataDir);
            psi.ArgumentList.Add("--out");
            psi.ArgumentList.Add(outFile);
            if (!string.IsNullOrEmpty(initialToken))
            {
                psi.ArgumentList.Add("--initial-token");
                psi.ArgumentList.Add(initialToken!);
            }
            if (debugCapture) psi.ArgumentList.Add("--debug-capture");
            if (extraArgs != null)
                foreach (var a in extraArgs) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动登录宿主进程：" + exe);

            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(30));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 尽力结束 */ }
                return null;
            }

            if (!File.Exists(outFile)) return null;
            return ParseResult(await File.ReadAllTextAsync(outFile));
        }
        finally
        {
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { /* 清理失败忽略 */ }
        }
    }

    /// <summary>
    /// 解析宿主回传的 JSON（AOT 友好：只用 JsonDocument，不用反射序列化）。
    /// 兼容两种形态：成功 <c>{"token":..,"session":..}</c>；失败 <c>{"error":"原因"}</c>。
    /// </summary>
    public static LoginResult? ParseResult(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            string? error = null;
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                error = e.GetString();

            if (doc.RootElement.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(t.GetString()))
            {
                string? session = null;
                if (doc.RootElement.TryGetProperty("session", out var s) && s.ValueKind == JsonValueKind.String)
                    session = s.GetString();
                bool uidConfirmed = doc.RootElement.TryGetProperty("uidConfirmed", out var uc)
                                    && uc.ValueKind == JsonValueKind.True;
                return new LoginResult(t.GetString(), string.IsNullOrWhiteSpace(session) ? null : session,
                    UidConfirmed: uidConfirmed);
            }

            return error is null ? null : new LoginResult(null, null, error);
        }
        catch { return null; }
    }
}
