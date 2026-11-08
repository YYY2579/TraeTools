using Avalonia;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;

namespace TraeTools;

sealed class Program
{
    /// <summary>单实例互斥名（与项目名一致，避免多实例各自签到/切换冲突）。</summary>
    private const string MutexName = @"Local\TraeTools.SingleInstance";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // 无界面自检：给 CI 冒烟用。macOS 上跑不到登录流程，就验证不了凭据加密，
        // 而这个环节恰恰是 DPAPI → AES-GCM 换机制后最容易静默坏掉的地方。
        if (Array.Exists(args, a => a == "--crypto-selftest"))
        {
            Environment.Exit(RunCryptoSelftest());
            return;
        }
        if (Array.Exists(args, a => a == "--autostart-selftest"))
        {
            Environment.Exit(RunAutoStartSelftest());
            return;
        }
        if (Array.Exists(args, a => a == "--switch-path-selftest"))
        {
            Environment.Exit(RunSwitchPathSelftest());
            return;
        }
        if (Array.Exists(args, a => a == "--net-selftest"))
        {
            Environment.Exit(RunNetSelftest());
            return;
        }
        if (Array.Exists(args, a => a == "--log-selftest"))
        {
            Environment.Exit(RunLogSelftest());
            return;
        }
        if (Array.Exists(args, a => a == "--diagnose-selftest"))
        {
            Environment.Exit(RunDiagnoseSelftest());
            return;
        }
        if (Array.Exists(args, a => a == "--notify-selftest"))
        {
            Environment.Exit(RunNotifySelftest());
            return;
        }
        if (Array.Exists(args, a => a == "--update-swap-selftest"))
        {
            Environment.Exit(RunUpdateSwapSelftest());
            return;
        }

        // CI 冒烟需要知道启动到底走到哪一步：Rosetta 转译下 /usr/bin/sample 的栈回溯是坏的，
        // 只能靠程序自己打点。stderr 会并进 app.log。
        Boot("main-entered");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Console.Error.WriteLine($"[boot] UNHANDLED {e.ExceptionObject}");

        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[boot] FATAL {ex}");
            throw;
        }
    }

    private static void Run(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // 已有实例在运行，直接退出本实例（TraeTools 为单实例工具）
            Boot("another-instance-exits");
            return;
        }
        Boot("mutex-acquired");
        // 旧版 TraeCheckin / TraeSwitch 数据迁移到统一根 %APPDATA%\TraeTools（幂等，保留旧目录）
        TraeTools.Services.DataPaths.Migrate();
        Boot("paths-migrated");
        var app = BuildAvaloniaApp();
        Boot("builder-configured");
        app.StartWithClassicDesktopLifetime(args);
        Boot("lifetime-returned");
    }

    private static void Boot(string stage) => Console.Error.WriteLine($"[boot] {stage}");

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// 凭据加密往返自检，返回进程退出码。只探平台加密层，不碰 AppConfig 的迁移逻辑。
    /// </summary>
    private static int RunCryptoSelftest()
    {
        const string sample = "traetools-crypto-selftest-2026";
        bool ok = true;

        Console.WriteLine($"mechanism: {TraeTools.Services.Security.SecretProtector.MechanismName}");

        string? enc;
        try
        {
            enc = TraeTools.Services.Security.SecretProtector.Protect(sample);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: Protect 抛异常 {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        if (string.IsNullOrEmpty(enc)) { Console.WriteLine("FAIL: Protect 返回空"); return 1; }
        if (enc == sample) { Console.WriteLine("FAIL: 密文与明文相同，未真正加密"); ok = false; }

        var back = TraeTools.Services.Security.SecretProtector.Unprotect(enc);
        if (!string.Equals(back, sample, StringComparison.Ordinal))
        {
            Console.WriteLine("FAIL: 解密未能还原原文");
            ok = false;
        }

        // 坏密文必须收敛成 null（触发重新登录），不能把异常抛到启动路径上。
        if (TraeTools.Services.Security.SecretProtector.Unprotect("not-base64-at-all") != null)
        {
            Console.WriteLine("FAIL: 非法密文未返回 null");
            ok = false;
        }

        Console.WriteLine(ok ? "CRYPTO SELFTEST: PASS" : "CRYPTO SELFTEST: FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 开机自启往返自检：真实写一次、读回校验、再复原。
    /// macOS 上这条探的是 LaunchAgent plist 能否被系统认账（plutil 校验），
    /// 光看文件存在证明不了 plist 格式合法。
    /// </summary>
    private static int RunAutoStartSelftest()
    {
        Console.Error.WriteLine($"[autostart] mechanism: {TraeCheckin.AutoStartManager.MechanismName}");
        var before = TraeCheckin.AutoStartManager.IsEnabled();

        var err = TraeCheckin.AutoStartManager.SetEnabled(!before);
        if (err != null)
        {
            Console.Error.WriteLine($"FAIL: SetEnabled({!before}) 返回错误：{err}");
            return 1;
        }
        var after = TraeCheckin.AutoStartManager.IsEnabled();

        // 开关处于打开态时把落点内容原样打出来，让 CI 能用 plutil 校验 plist 合法性——
        // 文件存在不等于格式合法，格式错了 launchd 会静默忽略，用户端就是「勾了没反应」。
        var loc = TraeCheckin.AutoStartManager.ConfigLocation;
        Console.Error.WriteLine($"[autostart] location: {loc}");
        if (after && File.Exists(loc))
        {
            Console.Error.WriteLine("[autostart] begin-content");
            Console.Error.Write(File.ReadAllText(loc));
            Console.Error.WriteLine("[autostart] end-content");
        }

        // 复原，别把 CI 机器的自启状态改掉。
        TraeCheckin.AutoStartManager.SetEnabled(before);
        var restored = TraeCheckin.AutoStartManager.IsEnabled();

        bool ok = true;
        if (after != !before) { Console.Error.WriteLine($"FAIL: 开关未生效（期望 {!before}，实际 {after}）"); ok = false; }
        if (restored != before) { Console.Error.WriteLine($"FAIL: 复原失败（期望 {before}，实际 {restored}）"); ok = false; }

        Console.WriteLine(ok ? "AUTOSTART SELFTEST: PASS" : "AUTOSTART SELFTEST: FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// TLS 自检。签到/用量/云端全走 https://api.trae.cn，而自包含 macOS 包的证书校验
    /// 走 Security.framework，这一步坏掉时表现是「签到失败：SSL/信任」，只在真机暴露。
    ///
    /// 判据是**真实握手**，不是 X509Store(Root, CurrentUser) 的证书数：macOS 上根证书
    /// 由 SecTrustEvaluate 在握手时取用，不会填进 .NET 的 X509Store，那个库恒为空 ——
    /// 拿它当门禁会把正常的 macOS 运行时报成"根证书库为空"。
    ///
    /// 分两档：github API 是硬门禁（CI 一定到得了，用来证明 TLS 信任链机制本身可用）；
    /// api.trae.cn 只记录结果，因为 CI 机器在境外，连不上不代表包有问题。
    /// </summary>
    private static int RunNetSelftest()
    {
        bool ok = true;

        var gh = Probe("https://api.github.com");
        if (gh == NetProbeResult.TlsFailure)
        {
            Console.Error.WriteLine("FAIL: 到 api.github.com 的 TLS 握手失败 —— 证书校验机制在本平台不可用，签到必然也失败");
            ok = false;
        }
        else if (gh == NetProbeResult.Ok)
        {
            Console.Error.WriteLine("[net] api.github.com TLS 正常");
        }
        else
        {
            Console.Error.WriteLine("FAIL: 连 api.github.com 都不可达，无法判定 TLS（CI 网络异常）");
            ok = false;
        }

        var trae = Probe(TraeCheckin.TraeApiClient.BaseUrl);
        Console.Error.WriteLine(trae switch
        {
            NetProbeResult.Ok => $"[net] {TraeCheckin.TraeApiClient.BaseUrl} 可达且 TLS 正常",
            NetProbeResult.TlsFailure => $"[net] {TraeCheckin.TraeApiClient.BaseUrl} TLS/证书异常",
            _ => $"[net] {TraeCheckin.TraeApiClient.BaseUrl} 不可达（CI 在境外属正常，不计失败）",
        });
        if (trae == NetProbeResult.TlsFailure) ok = false;

        Console.WriteLine(ok ? "NET SELFTEST: PASS" : "NET SELFTEST: FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 云端失败原因归类自检：用 checkin.py 真实会打印的几种日志片段，验证归类映射正确。
    /// 纯离线，不访问网络。
    /// </summary>
    private static int RunDiagnoseSelftest()
    {
        (string Sample, string Expect)[] cases =
        [
            ("Traceback...\nRuntimeError: 账号会话已失效(HTTP 401)：X-Cloudide-Session 可能已过期(~14天有效期)。", "session"),
            ("错误：缺少环境变量 TRAE_SESSION", "secret"),
            ("[用户A] 命中风控 9074，换新设备号重试（第 2 次）", "risk"),
            ("github request failed: HTTP 403 rate limit exceeded", "github"),
            ("[用户B] 签到失败：code=500 服务异常", "account"),
            ("something completely unknown happened", "unknown"),
        ];

        bool ok = true;
        foreach (var (sample, expect) in cases)
        {
            var d = TraeCheckin.GitHubApiClient.Classify(sample);
            bool pass = d.Key == expect;
            ok &= pass;
            Console.WriteLine($"{(pass ? "OK  " : "FAIL")} [{d.Key}] 期望 {expect}");
            Console.WriteLine($"      原因：{d.Reason}");
            Console.WriteLine($"      建议：{d.Suggestion}");
        }

        Console.WriteLine(ok ? "DIAGNOSE SELFTEST: PASS" : "DIAGNOSE SELFTEST: FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 推送渠道自检：用环境变量里的 SMTP 配置真实发一封告警邮件，并把「实际发出的内容」打印出来。
    ///
    /// 凭据只从环境变量读取，**不落盘、不进仓库**（日志里也会做邮箱打码）。
    /// 需要：TTSMTP_HOST / TTSMTP_PORT / TTSMTP_SEC / TTSMTP_USER / TTSMTP_PASS / TTSMTP_TO
    /// </summary>
    private static int RunNotifySelftest()
    {
        string host = Environment.GetEnvironmentVariable("TTSMTP_HOST") ?? "";
        string portRaw = Environment.GetEnvironmentVariable("TTSMTP_PORT") ?? "465";
        string sec = Environment.GetEnvironmentVariable("TTSMTP_SEC") ?? "ssl";
        string user = Environment.GetEnvironmentVariable("TTSMTP_USER") ?? "";
        string pass = Environment.GetEnvironmentVariable("TTSMTP_PASS") ?? "";
        string to = Environment.GetEnvironmentVariable("TTSMTP_TO") ?? "";

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass) || string.IsNullOrEmpty(to))
        {
            Console.WriteLine("FAIL: 缺少环境变量 TTSMTP_HOST / TTSMTP_USER / TTSMTP_PASS / TTSMTP_TO");
            return 1;
        }
        int port = int.TryParse(portRaw, out var p) ? p : 465;

        // 与线上真实告警同格式的内容（签到时间已过 10 分钟仍未签）
        string title = "Trae 今日签到部分失败（1/3 个账号未签）";
        string body = string.Join(Environment.NewLine, new[]
        {
            $"自动签到时间：08:00，已过 10 分钟仍未完成。",
            $"当前进度：2/3 已签",
            "✅ 用户38167781120  已签到",
            "✅ 用户3782480143  已签到",
            "⚠️ 用户8528184254  未签到",
            "",
            "可能原因与处理：",
            "· 云端定时任务失败（最常见是 Session 过期）→ 「云端签到」页重新登录 Trae 后重新部署",
            "· 本机当时没开机 / 程序没运行 → 打开程序点「一键签到」补签",
            "· 命中风控 9074（参与用户太多）→ 错开整点时段，或换发设备号后重试",
            "",
            "（这是 TraeTools 的推送自检邮件，收到即说明邮件渠道可用）",
        });

        Console.WriteLine("=== 通知标题 ===");
        Console.WriteLine(title);
        Console.WriteLine("=== 通知正文（邮件正文原样） ===");
        Console.WriteLine(body);
        Console.WriteLine("=== 发送 ===");

        // 用内存配置，不写 config.json —— 凭据不落盘
        var cfg = new TraeCheckin.AppConfig
        {
            EmailEnabled = true,
            SmtpHost = host,
            SmtpPort = port,
            SmtpSecurity = sec,
            SmtpUser = user,
            SmtpPassword = pass,
            SmtpFrom = user,
            SmtpTo = to,
            NotifySystemEnabled = false,
        };

        var outcomes = TraeTools.Services.Notify.NotifyDispatcher
            .SendAsync(cfg, title, body).GetAwaiter().GetResult();

        bool ok = false;
        foreach (var o in outcomes)
        {
            Console.WriteLine($"[{o.Channel}] {(o.Ok ? "OK" : "FAIL")} - {o.Message}");
            if (o.Ok) ok = true;
        }
        if (outcomes.Count == 0) Console.WriteLine("FAIL: 没有任何启用的渠道");

        Console.WriteLine(ok ? "NOTIFY SELFTEST: PASS" : "NOTIFY SELFTEST: FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 日志链路自检：验证「业务写日志 → 内存缓冲（日志页「最近」）→ 分类文件落盘」三者一致，
    /// 且当天日志回填不会产生重复行。这三个性质坏掉时表现就是「日志页永远空白」。
    /// </summary>
    private static int RunLogSelftest()
    {
        bool ok = true;
        string marker = "log-selftest-" + Guid.NewGuid().ToString("N")[..8];
        var logsDir = TraeTools.Services.DataPaths.LogsDir;
        var file = Path.Combine(logsDir, $"selftest_log_{DateTime.Now:yyyyMM}.txt");

        // 1) 业务侧入口（AccountHelpers.AppLog）必须进入内存缓冲
        TraeTools.ViewModels.AccountHelpers.AppLog("selftest", "lab", marker);
        var recent = TraeTools.Services.AppLog.Recent();
        if (!recent.Any(e => e.Message == marker))
        {
            Console.WriteLine("FAIL 1: AccountHelpers.AppLog 未进入内存缓冲（日志页会永远空白）");
            ok = false;
        }
        else Console.WriteLine($"OK 1: 内存缓冲可见（当前 {recent.Count} 条）");

        // 2) 同一条日志必须落到分类文件，且带级别标签（与旧格式兼容解析）
        if (!File.Exists(file) || !File.ReadAllText(file).Contains(marker))
        {
            Console.WriteLine($"FAIL 2: 未写入分类日志 {file}");
            ok = false;
        }
        else
        {
            var line = File.ReadAllLines(file).FirstOrDefault(l => l.Contains(marker)) ?? "";
            if (!line.Contains("[INFO]"))
            {
                Console.WriteLine($"FAIL 2b: 落盘行缺少级别标签：{line}");
                ok = false;
            }
            else Console.WriteLine("OK 2: 分类日志已落盘且带级别标签");
        }

        // 3) 回填幂等：已写入的日志行不能被回填重复加入
        TraeTools.Services.AppLog.SeedFromTodayFiles();
        TraeTools.Services.AppLog.SeedFromTodayFiles();
        int dup = TraeTools.Services.AppLog.Recent().Count(e => e.Message == marker);
        if (dup != 1)
        {
            Console.WriteLine($"FAIL 3: 回填后本测试行出现 {dup} 次（应为 1）");
            ok = false;
        }
        else Console.WriteLine($"OK 3: 回填幂等，无重复行（共 {TraeTools.Services.AppLog.Recent().Count} 条）");

        // 清理本测试产生的行，避免污染真实日志
        try
        {
            if (File.Exists(file))
            {
                var kept = File.ReadAllLines(file).Where(l => !l.Contains(marker)).ToArray();
                File.WriteAllLines(file, kept);
                if (kept.Length == 0) File.Delete(file);
            }
        }
        catch { /* 清理失败无害 */ }

        Console.WriteLine(ok ? "LOG SELFTEST: PASS" : "LOG SELFTEST: FAIL");
        return ok ? 0 : 1;
    }

    private enum NetProbeResult { Ok, TlsFailure, Unreachable }

    private static NetProbeResult Probe(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var resp = http.GetAsync(baseUrl).GetAwaiter().GetResult();
            Console.Error.WriteLine($"[net] {baseUrl} -> HTTP {(int)resp.StatusCode}");
            return NetProbeResult.Ok;   // 任何 HTTP 状态码都说明握手与证书链已通过
        }
        catch (HttpRequestException ex)
        {
            var inner = ex.InnerException?.GetType().Name ?? "none";
            bool tlsish = inner.Contains("Authentication", StringComparison.OrdinalIgnoreCase)
                          || inner.Contains("Certificate", StringComparison.OrdinalIgnoreCase)
                          || inner.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                          || ex.Message.Contains("ssl", StringComparison.OrdinalIgnoreCase)
                          || ex.Message.Contains("trust", StringComparison.OrdinalIgnoreCase);
            Console.Error.WriteLine($"[net] {baseUrl} 异常 inner={inner}: {ex.Message}");
            return tlsish ? NetProbeResult.TlsFailure : NetProbeResult.Unreachable;
        }
        catch (TaskCanceledException)
        {
            return NetProbeResult.Unreachable;
        }
    }

    /// <summary>
    /// 应用内更新的换包自检。更新器是唯一能把用户现有安装直接搞坏的模块，
    /// 不能只靠读代码验收：这里用真实的替换脚本和真实的 hdiutil/ditto 跑三条路径，
    /// 其中两条是失败路径 —— 失败时能不能回滚，比成功时能不能装上是更关键的性质。
    /// </summary>
    private static int RunUpdateSwapSelftest()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("[swap] 非 macOS，跳过");
            Console.WriteLine("UPDATE SWAP SELFTEST: PASS");
            return 0;
        }

        var work = Path.Combine(Path.GetTempPath(), "trae_swap_selftest_" + Guid.NewGuid().ToString("N"));
        bool ok = true;
        try
        {
            Directory.CreateDirectory(work);

            // ---- 1) 正常换包：新版就位、旧版与暂存清干净、不留 .~old ----
            var dst1 = Path.Combine(work, "dst1");
            var stage1 = Path.Combine(work, "stage1");
            WriteBundle(Path.Combine(dst1, "TraeTools.app"), "old");
            WriteBundle(Path.Combine(stage1, "TraeTools.app"), "new");
            var rc1 = RunSwapScript(stage1, dst1, DeadPid());
            if (rc1 != 0 || ReadMarker(dst1) != "new")
            {
                Console.Error.WriteLine($"FAIL 1: 正常换包失败 rc={rc1} marker={ReadMarker(dst1) ?? "<无>"}");
                ok = false;
            }
            else if (Directory.Exists(Path.Combine(dst1, ".TraeTools.app.old"))
                     || Directory.Exists(Path.Combine(dst1, ".TraeTools.app.new")))
            {
                Console.Error.WriteLine("FAIL 1: 换包成功但残留 .old/.new 中间目录");
                ok = false;
            }
            else Console.Error.WriteLine("OK 1: 正常换包，新版就位且无残留");

            // ---- 2) 失败路径：暂存里没有新版 → 必须回滚，绝不允许两个都没有 ----
            var dst2 = Path.Combine(work, "dst2");
            var stage2 = Path.Combine(work, "stage2");
            WriteBundle(Path.Combine(dst2, "TraeTools.app"), "old");
            Directory.CreateDirectory(stage2);   // 故意不放 TraeTools.app
            var rc2 = RunSwapScript(stage2, dst2, DeadPid());
            var marker2 = ReadMarker(dst2);
            if (rc2 == 0 || marker2 != "old")
            {
                Console.Error.WriteLine($"FAIL 2: 换包失败时未回滚 rc={rc2} marker={marker2 ?? "<旧版丢了>"}");
                ok = false;
            }
            else Console.Error.WriteLine("OK 2: 放入失败 → 旧版已回滚，用户仍可用");

            // ---- 2b) 残局：TARGET 本来就不存在（上一次换包失败的后果）→ 必须能装好，不能砖 ----
            var dst2b = Path.Combine(work, "dst2b");
            var stage2b = Path.Combine(work, "stage2b");
            Directory.CreateDirectory(dst2b);   // 没有 TraeTools.app
            WriteBundle(Path.Combine(stage2b, "TraeTools.app"), "new");
            var rc2b = RunSwapScript(stage2b, dst2b, DeadPid());
            if (rc2b != 0 || ReadMarker(dst2b) != "new")
            {
                Console.Error.WriteLine($"FAIL 2b: 无旧版时换包失败 rc={rc2b} marker={ReadMarker(dst2b) ?? "<无>"}");
                ok = false;
            }
            else Console.Error.WriteLine("OK 2b: TARGET 缺失的残局也能装好，不会变砖");

            // ---- 2c) 目标目录不可写 → 旧版必须原样还在 ----
            var dst2c = Path.Combine(work, "dst2c");
            var stage2c = Path.Combine(work, "stage2c");
            WriteBundle(Path.Combine(dst2c, "TraeTools.app"), "old");
            WriteBundle(Path.Combine(stage2c, "TraeTools.app"), "new");
            File.SetUnixFileMode(dst2c, UnixFileMode.UserRead | UnixFileMode.UserExecute);  // 去掉写权限
            var rc2c = RunSwapScript(stage2c, dst2c, DeadPid());
            var restored = ReadMarker(dst2c);
            File.SetUnixFileMode(dst2c, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (rc2c == 0 || restored != "old")
            {
                Console.Error.WriteLine($"FAIL 2c: 目录不可写时旧版丢了 rc={rc2c} marker={restored ?? "<旧版丢了>"}");
                ok = false;
            }
            else Console.Error.WriteLine("OK 2c: 目录不可写 → 旧版完好无损，脚本放弃");

            // ---- 2d) 旧进程没退出 → 必须放弃且一个文件都不动 ----
            // 这条正是把我自己的守卫逼出来的场景：脚本原先按进程名等，
            // 而自检就跑在叫 TraeTools 的进程里，永远等不到。没有这条测试，
            // 那个设计错误会一路带到发版。
            var dst2d = Path.Combine(work, "dst2d");
            var stage2d = Path.Combine(work, "stage2d");
            WriteBundle(Path.Combine(dst2d, "TraeTools.app"), "old");
            WriteBundle(Path.Combine(stage2d, "TraeTools.app"), "new");
            using (var sleeper = Process.Start(new ProcessStartInfo("/bin/sleep")
            {
                ArgumentList = { "30" },
                UseShellExecute = false,
            }))
            {
                if (sleeper is null)
                {
                    Console.Error.WriteLine("FAIL 2d: 起不了占位进程，无法验证等待分支");
                    ok = false;
                }
                else
                {
                    var rc2d = RunSwapScript(stage2d, dst2d, sleeper.Id.ToString(), "2");
                    var marker2d = ReadMarker(dst2d);
                    try { sleeper.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                    if (rc2d == 0 || marker2d != "old")
                    {
                        Console.Error.WriteLine($"FAIL 2d: 旧进程未退出却动了包 rc={rc2d} marker={marker2d ?? "<丢了>"}");
                        ok = false;
                    }
                    else if (Directory.Exists(Path.Combine(dst2d, ".TraeTools.app.new"))
                             || Directory.Exists(Path.Combine(dst2d, ".TraeTools.app.old")))
                    {
                        Console.Error.WriteLine("FAIL 2d: 放弃换包却留下了 .new/.old 中间目录");
                        ok = false;
                    }
                    else Console.Error.WriteLine("OK 2d: 旧进程未退出 → 放弃换包，现有安装零改动");
                }
            }

            // ---- 3) 解包：真做一个 dmg，走 hdiutil 挂载 + ditto 复制 ----
            var bundleSrc = Path.Combine(work, "dmgsrc");
            WriteBundle(Path.Combine(bundleSrc, "TraeTools.app"), "dmged");
            WriteLoginHost(Path.Combine(bundleSrc, "TraeTools.app"));
            var dmg = Path.Combine(work, "fake.dmg");
            var p = Process.Start(new ProcessStartInfo("/usr/bin/hdiutil")
            {
                ArgumentList = { "create", "-quiet", "-srcfolder", bundleSrc, "-ov", "-format", "UDZO", dmg },
                UseShellExecute = false,
            });
            p?.WaitForExit(60_000);
            if (p is null || p.ExitCode != 0 || !File.Exists(dmg))
            {
                Console.Error.WriteLine("FAIL 3: 造测试 dmg 失败，无法验证解包");
                ok = false;
            }
            else
            {
                try
                {
                    var staged = TraeTools.Services.UpdaterService.PreparePackage(dmg, work);
                    var got = ReadMarkerPath(Path.Combine(staged, "TraeTools.app"));
                    if (got != "dmged")
                    {
                        Console.Error.WriteLine($"FAIL 3: dmg 解包后 marker={got ?? "<无>"}，期望 dmged");
                        ok = false;
                    }
                    else Console.Error.WriteLine("OK 3: hdiutil 挂载 + ditto 解包正常");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"FAIL 3: PreparePackage 抛异常：{ex.Message}");
                    ok = false;
                }
            }

            // ---- 4) 缺登录宿主的包必须拒绝安装 ----
            var badSrc = Path.Combine(work, "badsrc");
            WriteBundle(Path.Combine(badSrc, "TraeTools.app"), "nohost");   // 不写 LoginHost
            var badDmg = Path.Combine(work, "bad.dmg");
            var bp = Process.Start(new ProcessStartInfo("/usr/bin/hdiutil")
            {
                ArgumentList = { "create", "-quiet", "-srcfolder", badSrc, "-ov", "-format", "UDZO", badDmg },
                UseShellExecute = false,
            });
            bp?.WaitForExit(60_000);
            try
            {
                TraeTools.Services.UpdaterService.PreparePackage(badDmg, work);
                Console.Error.WriteLine("FAIL 4: 缺登录宿主的包竟然被接受，装完自动登录会静默退化");
                ok = false;
            }
            catch (InvalidDataException)
            {
                Console.Error.WriteLine("OK 4: 缺登录宿主的包被拒绝");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL 4: 抛了预期外的异常 {ex.GetType().Name}：{ex.Message}");
                ok = false;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: 自检异常 {ex.GetType().Name}: {ex.Message}");
            ok = false;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* 临时目录残留无害 */ }
        }

        Console.WriteLine(ok ? "UPDATE SWAP SELFTEST: PASS" : "UPDATE SWAP SELFTEST: FAIL");
        return ok ? 0 : 1;
    }

    private static void WriteBundle(string appDir, string marker)
    {
        var macos = Path.Combine(appDir, "Contents", "MacOS");
        Directory.CreateDirectory(macos);
        File.WriteAllText(Path.Combine(macos, "marker.txt"), marker);
    }

    private static void WriteLoginHost(string appDir)
    {
        var host = Path.Combine(appDir, "Contents", "MacOS", "TraeTools.LoginHost");
        File.WriteAllText(host, "#!/bin/sh\nexit 3\n");
        File.SetUnixFileMode(host, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string? ReadMarker(string dst) => ReadMarkerPath(Path.Combine(dst, "TraeTools.app"));

    private static string? ReadMarkerPath(string appDir)
    {
        var f = Path.Combine(appDir, "Contents", "MacOS", "marker.txt");
        return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
    }

    /// <summary>
    /// 跑一次真实的换包脚本。waitPid/timeout 由调用方给：正常场景传一个已不存在的 PID
    /// （等价于"旧进程已退出"），超时场景传一个活着的 PID + 很短的超时。
    /// </summary>
    private static int RunSwapScript(string staging, string dst, string waitPid, string timeoutSecs = "5")
    {
        var sh = Path.Combine(Path.GetTempPath(), $"swaptest_{Guid.NewGuid():N}.sh");
        File.WriteAllText(sh, TraeTools.Services.UpdaterService.BuildMacSwapScript(dryRun: true));
        try
        {
            // 路径与 PID 走参数而非拼进脚本体，与生产调用方式保持一致
            using var p = Process.Start(new ProcessStartInfo("/bin/bash")
            {
                ArgumentList = { sh, staging, dst, waitPid, timeoutSecs },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (p is null) return -1;
            // 必须先读完再等退出：只等不退会把管道缓冲区写满，脚本和我们都卡住。
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);
            if (!string.IsNullOrWhiteSpace(err)) Console.Error.Write(err);
            return p.HasExited ? p.ExitCode : -1;
        }
        finally { try { File.Delete(sh); } catch { /* 忽略 */ } }
    }

    /// <summary>一个肯定不存在的 PID，代表「旧进程已经退出了」。</summary>
    private static string DeadPid()
    {
        for (int pid = 999999; pid > 999900; pid--)
            if (!Process.GetProcesses().Any(p => p.Id == pid)) return pid.ToString();
        return "999999";
    }

    /// <summary>
    /// 切换模块的相对路径解析自检。
    /// 白名单以反斜杠作比较键（已固化进 vault，改不得），但反斜杠在 macOS 上是合法
    /// 文件名而不是分隔符 —— 解析错了不会报错，只会「切换报成功但一个文件都没动」，
    /// 或者把不存在的边车当成有效备份。这是唯一能在无客户端环境下自动验证的一环。
    /// </summary>
    private static int RunSwitchPathSelftest()
    {
        var root = Path.Combine(Path.GetTempPath(), "trae_switch_selftest_" + Guid.NewGuid().ToString("N"));
        var whitelist = TraeSwitch.Services.CarrierDefaults.FingerprintWhitelist;
        try
        {
            Directory.CreateDirectory(root);
            string[] created =
            [
                @"User\globalStorage\state.vscdb",
                @"User\globalStorage\storage.json",
                @"Local Storage\leveldb\CURRENT",
                @"aha\device.json",
                @"machineid",
            ];
            foreach (var rel in created)
            {
                var full = TraeSwitch.Services.CarrierDefaults.ResolvePath(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "x");
            }

            bool ok = true;
            foreach (var rel in created)
            {
                if (!TraeSwitch.Services.CarrierDefaults.Exists(root, rel))
                {
                    Console.Error.WriteLine($"FAIL: 文件已造出但判定不存在：{rel}");
                    ok = false;
                }
            }

            // 没造的必须判不存在：判成存在会把空快照当成有效备份，恢复时用垃圾覆盖真登录态
            if (TraeSwitch.Services.CarrierDefaults.Exists(root, @"User\globalStorage\state.vscdb-wal"))
            {
                Console.Error.WriteLine("FAIL: 未创建的边车被判为存在");
                ok = false;
            }

            if (!OperatingSystem.IsWindows())
            {
                var probe = TraeSwitch.Services.CarrierDefaults.ResolvePath(root, @"User\globalStorage\state.vscdb");
                if (probe.Contains('\\'))
                {
                    Console.Error.WriteLine($"FAIL: 解析结果含字面反斜杠，必然找不到文件：{probe}");
                    ok = false;
                }
            }

            // 白名单本身不该有重复的比较键（重复会让快照/恢复按同一项做两次）
            var dup = whitelist.Select(TraeSwitch.Services.CarrierDefaults.NormalizeRel)
                               .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                               .Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
            if (dup.Length > 0)
            {
                Console.Error.WriteLine("FAIL: 白名单存在重复项：" + string.Join(", ", dup));
                ok = false;
            }

            Console.WriteLine($"（白名单 {whitelist.Length} 项）");
            Console.WriteLine(ok ? "SWITCH PATH SELFTEST: PASS" : "SWITCH PATH SELFTEST: FAIL");
            return ok ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 临时目录残留无害 */ }
        }
    }
}
