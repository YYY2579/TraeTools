using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using TraeTools.Services.Login;

namespace TraeTools.LoginHost;

/// <summary>
/// 独立登录宿主（P2-1）。主程序 TraeTools 已不含 WinForms/WebView2（NativeAOT 硬阻塞），
/// 需要"内嵌浏览器自动登录"时由主程序启动本进程，结果经 --out 指定的 JSON 文件回传：
///     成功：{ "token": "&lt;Cloud-IDE-Token&gt;", "session": "&lt;X-Cloudide-Session&gt;" }
///     失败：{ "error": "原因" }
/// 退出码：0 = 已取得 token；2 = 取消/未取得；3 = 参数错误；4 = 启动或写回失败。
/// --selftest 不显示任何窗口，直接回写一份结果，用于在无 GUI 环境校验进程/文件契约。
/// --expected-uid &lt;16位数字&gt;：账号平台用户 ID（JWT data.id）。宿主读到归属不符的
/// token 时不接受、不关窗，防止残留会话自动续期瞬间顶掉窗口（"一闪而过"）并污染账号凭据。
/// --proxy-server &lt;addr&gt;：webview 经指定代理上网（host:port / scheme://…），空 = 直连。
/// </summary>
internal static class Program
{
    private sealed record Options(string UserDataDir, string OutFile, string? InitialToken, string? ExpectedUid, string? ProxyServer, bool DebugCapture, bool SelfTest);

    [STAThread]
    private static int Main(string[] args)
    {
        var opt = ParseArgs(args);
        if (opt == null)
        {
            Console.Error.WriteLine("参数错误：至少需要 --user-data-dir <dir> --out <file>");
            return 3;
        }

        if (opt.SelfTest)
            return WriteResult(opt.OutFile, "selftest-token-" + new string('x', 48), "selftest-session") ? 0 : 4;

        string? token = null;
        string? session = null;
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var form = new LoginHostForm(opt.UserDataDir, opt.InitialToken, opt.ExpectedUid, opt.ProxyServer, opt.DebugCapture, opt.OutFile);
            form.FormClosing += (_, _) =>
            {
                if (!string.IsNullOrEmpty(form.ResultToken))
                {
                    token = form.ResultToken;
                    session = form.ResultSession;
                }
            };
            Application.Run(form);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("登录宿主异常：" + ex.Message);
            WriteError(opt.OutFile, "登录宿主异常：" + ex.Message);
            return 4;
        }

        if (string.IsNullOrEmpty(token)) return 2;   // 用户取消 / 未取得
        return WriteResult(opt.OutFile, token, session) ? 0 : 4;
    }

    private static Options? ParseArgs(string[] args)
    {
        string? userDataDir = null, outFile = null, initialToken = null, expectedUid = null, proxyServer = null;
        bool debug = false, selfTest = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--user-data-dir": if (i + 1 < args.Length) userDataDir = args[++i]; break;
                case "--out": if (i + 1 < args.Length) outFile = args[++i]; break;
                case "--initial-token": if (i + 1 < args.Length) initialToken = args[++i]; break;
                case "--expected-uid": if (i + 1 < args.Length) expectedUid = args[++i]; break;
                case "--proxy-server": if (i + 1 < args.Length) proxyServer = args[++i]; break;
                case "--debug-capture": debug = true; break;
                case "--selftest": selfTest = true; break;
            }
        }
        if (string.IsNullOrWhiteSpace(userDataDir) || string.IsNullOrWhiteSpace(outFile)) return null;
        return new Options(userDataDir, outFile, initialToken, expectedUid, proxyServer, debug, selfTest);
    }

    /// <summary>临时文件 + 原子替换写回结果，避免主程序读到半截 JSON。</summary>
    private static bool WriteResult(string outFile, string token, string? session)
    {
        try
        {
            var full = Path.GetFullPath(outFile);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var json = JsonSerializer.Serialize(new { token, session });
            var tmp = full + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, full, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("写回登录结果失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>初始化/运行失败时把原因写回主程序，让界面能显示具体错误而不是笼统的"未获取到 Token"。</summary>
    internal static void WriteError(string outFile, string message)
    {
        try
        {
            var full = Path.GetFullPath(outFile);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var json = JsonSerializer.Serialize(new { error = message });
            var tmp = full + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, full, overwrite: true);
        }
        catch { /* 尽力而为 */ }
    }
}
