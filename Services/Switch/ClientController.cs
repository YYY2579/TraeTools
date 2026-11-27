using System.Diagnostics;

namespace TraeSwitch.Services;

public interface IClientController
{
    void KillAll();
    void Launch(string? userDataDir = null);
    bool IsRunning();
}

/// <summary>
/// 真实进程控制：按进程名结束全部实例并重新拉起客户端。
/// 关闭采用三级策略（P1-2）：优雅 WM_CLOSE → 等待 → 进程树强杀 → 仍失败则抛错人工介入。
/// 强杀是常态（参考项目实测 TRAE 切换基本都走到强杀），所以"残留 -wal/-shm 的清理"放在恢复侧
/// （见 VaultService.RestoreEntrySet）；但能优雅关就优雅关，让客户端自己 checkpoint 落盘最多数据。
/// </summary>
public sealed class ClientController : IClientController
{
    private readonly string _processName;
    private readonly string _exePath;
    private readonly TimeSpan _gracefulWait;
    private readonly TimeSpan _forceWait;

    public ClientController(string processName, string exePath)
        : this(processName, exePath, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(8))
    {
    }

    /// <summary>超时可注入，便于在独立环境验证三级升级逻辑（不依赖真实客户端）。</summary>
    internal ClientController(string processName, string exePath, TimeSpan gracefulWait, TimeSpan forceWait)
    {
        _processName = processName;
        _exePath = exePath;
        _gracefulWait = gracefulWait;
        _forceWait = forceWait;
    }

    /// <summary>
    /// 枚举属于本客户端的进程。
    /// macOS 上 Trae 的进程名字面就是 "Electron"（Electron 打包未改名），任何 Electron 应用
    /// 都叫这个 —— 只按进程名匹配会把 VS Code、Slack 当成切换目标关掉，所以必须再用
    /// 「主模块路径落在同一个 .app 包内」过滤。读不到主模块的进程一律不计入，
    /// 宁可少判也不能误杀别的应用。
    /// </summary>
    private List<Process> GetClientProcesses()
    {
        var result = new List<Process>();
        Process[] raw;
        try { raw = Process.GetProcessesByName(_processName); }
        catch { return result; }

        var bundle = MacBundleRoot(_exePath);
        foreach (var p in raw)
        {
            if (bundle is null) { result.Add(p); continue; }
            try
            {
                var path = p.MainModule?.FileName ?? "";
                if (path.StartsWith(bundle, StringComparison.OrdinalIgnoreCase)) result.Add(p);
                else p.Dispose();
            }
            catch
            {
                // 主模块读不到（权限/已退出）：跳过，不误伤
                try { p.Dispose(); } catch { /* 忽略 */ }
            }
        }
        return result;
    }

    /// <summary>从客户端主程序路径取出 .app 包根目录；Windows 或非 bundle 路径返回 null。</summary>
    private static string? MacBundleRoot(string exePath)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(exePath)) return null;
        var cut = exePath.LastIndexOf(".app/", StringComparison.OrdinalIgnoreCase);
        return cut > 0 ? exePath[..(cut + 4)] : null;
    }

    public void KillAll()
    {
        if (string.IsNullOrWhiteSpace(_processName))
            throw new InvalidOperationException("未配置客户端进程名（settings.json 的 ProcessName），无法安全关闭客户端");

        List<Process> procs = GetClientProcesses();
        if (procs.Count == 0) return;

        try { CloseAll(procs, _gracefulWait, _forceWait, IsAlive, ForceKill); }
        finally
        {
            // 拿到的 Process 对象必须 Dispose，否则常驻托盘下句柄持续累积（#33）
            foreach (var p in procs) { try { p.Dispose(); } catch { /* 忽略 */ } }
        }
    }

    /// <summary>
    /// 三级关闭核心。isAlive/forceKill 可注入（独立环境验证用），默认真实进程操作。
    /// 两级后仍有存活进程时抛 <see cref="InvalidOperationException"/>：宁可报错让人工处理，
    /// 也不要带着残留进程/未 checkpoint 的 WAL 继续切换（那正是"切了等于没切"的来源）。
    /// </summary>
    internal static void CloseAll(
        IReadOnlyList<Process> procs, TimeSpan gracefulWait, TimeSpan forceWait,
        Func<Process, bool> isAlive, Func<Process, bool> forceKill)
    {
        // 1) 优雅：向主窗口 PostMessage WM_CLOSE，等客户端自行退出（有机会自己 checkpoint WAL）
        foreach (var p in procs)
        {
            try { p.CloseMainWindow(); } catch { /* 无窗口/已退出：进入下一级 */ }
        }
        if (WaitAllExited(procs, gracefulWait, isAlive)) return;

        // 2) 树杀：主进程 + 全部子进程强杀（TRAE 为 Electron 多进程）
        foreach (var p in procs)
        {
            if (!isAlive(p)) continue;
            forceKill(p);
        }
        if (WaitAllExited(procs, forceWait, isAlive)) return;

        // 3) 仍存活：失败上报
        var stuck = string.Join(", ", procs.Where(isAlive).Select(SafeDescribe));
        throw new InvalidOperationException(
            $"无法关闭客户端进程（{stuck}）。请手动结束进程后重试切换——继续操作会因残留文件锁/WAL 导致切换无效。");
    }

    private static bool WaitAllExited(IReadOnlyList<Process> procs, TimeSpan timeout, Func<Process, bool> isAlive)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (!procs.Any(isAlive)) return true;
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(100);
        }
    }

    private static bool IsAlive(Process p)
    {
        try { return !p.HasExited; } catch { return false; }
    }

    private static bool ForceKill(Process p)
    {
        try { p.Kill(entireProcessTree: true); return true; }
        catch { return false; }
    }

    private static string SafeDescribe(Process p)
    {
        try { return $"{p.ProcessName}(PID {p.Id})"; } catch { return "未知进程"; }
    }

    public void Launch(string? userDataDir = null)
    {
        if (string.IsNullOrEmpty(_exePath) || !File.Exists(_exePath))
            throw new FileNotFoundException("找不到客户端：" + _exePath);

        var bundle = MacBundleRoot(_exePath);
        if (bundle is not null)
        {
            // 走 open -a 而不是直接 exec Contents/MacOS/Electron：后者绕过 LaunchServices，
            // 应用拿不到 bundle 身份（图标、应用名、单实例语义都会退化）。
            var psi = new ProcessStartInfo("/usr/bin/open")
            {
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(bundle);
            if (!string.IsNullOrWhiteSpace(userDataDir))
            {
                psi.ArgumentList.Add("--args");
                psi.ArgumentList.Add("--user-data-dir=" + userDataDir);
            }
            Process.Start(psi);
            return;
        }

        var start = new ProcessStartInfo(_exePath) { UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(userDataDir))
            start.ArgumentList.Add("--user-data-dir=" + userDataDir);
        Process.Start(start);
    }

    public bool IsRunning() => GetProcessCount() > 0;

    /// <summary>查询进程数并立即释放全部 Process 句柄（GetProcessesByName 返回的对象必须 Dispose）。</summary>
    private int GetProcessCount()
    {
        var procs = GetClientProcesses();
        foreach (var p in procs) try { p.Dispose(); } catch { /* 忽略 */ }
        return procs.Count;
    }
}
