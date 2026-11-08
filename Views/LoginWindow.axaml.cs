using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TraeCheckin;
using TraeTools.Services.Login;
using TraeTools.ViewModels;

namespace TraeTools.Views;

/// <summary>
/// 登录窗口。获取登录态的四种方式：
///   1. 内嵌 WebView2 自动登录（独立宿主进程 TraeTools.LoginHost.exe，主程序不含 WinForms/WebView2）；
///   2. 从剪贴板智能识别（自动判别 Cloud-IDE-Token 与会话串，会话走 GetUserToken 换 Token）；
///   3. 导入账号备份文件（口令派生的 AES-256-GCM 加密包，可跨机迁移）；
///   4. 读取本机 TRAE 客户端的登录态。
/// 另保留手动粘贴 Cloud-IDE-Token 的兜底方案。
/// </summary>
public partial class LoginWindow : Window
{
    private readonly TraeAccount? _account;
    private bool _busy;

    public LoginWindow()
    {
        InitializeComponent();
    }

    public LoginWindow(TraeAccount? account)
        : this()
    {
        _account = account;
    }

    /// <summary>内嵌 WebView2 自动登录：独立 STA 线程宿主，自动读取 Cloud-IDE-Token 与 X-Cloudide-Session。</summary>
    private async void OnAutoLoginClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _account == null) return;
        if (string.IsNullOrEmpty(_account.Token))
        {
            // 首次登录：为账号准备独立的 WebView2 用户数据目录（账号间登录态隔离）
            _ = _account;
        }

        // P2-1：内嵌浏览器已拆到独立宿主进程（TraeTools.LoginHost.exe），主程序不含 WinForms/WebView2。
        // 找不到宿主（未随程序分发）时明确提示改用下方手动粘贴方案。
        if (!LoginHostClient.IsAvailable())
        {
            AutoStatus.Text = $"未找到登录宿主程序 {LoginHostClient.HelperFileName}，请使用下方「手动方案」粘贴 Cloud-IDE-Token";
            return;
        }

        _busy = true;
        AutoStatus.Text = "正在启动浏览器…";
        try
        {
            string webViewDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TraeTools", "WebView", "account_" + (_account.Id.Length > 12 ? _account.Id[..12] : _account.Id));

            // P2-1：内嵌浏览器已拆到独立宿主进程（TraeTools.LoginHost.exe），主程序不含 WinForms/WebView2。
            // expectedUid：让宿主校验读到的 token 是否属于本账号，防止残留会话自动续期瞬间关窗（一闪而过）
            // 并把其他账号的 token 存进来（跨账号凭据污染）。
            // proxy：账号生效代理（账号自有优先，其次全局设置），登录浏览器与签到接口走同一出口。
            var result = await LoginHostClient.RunAsync(webViewDir, _account.Token, debugCapture: false,
                expectedUid: _account.AccountUid, proxy: AccountHelpers.ResolveProxy(_account));

            if (result == null)
            {
                AutoStatus.Text = "未获取到 Token（已取消或登录未完成）";
                return;
            }
            if (result.Token == null)
            {
                // 宿主初始化失败（WebView2 缺失/用户数据目录被锁等）：显示具体原因而不是笼统提示
                AutoStatus.Text = "自动登录失败：" + (result.Error ?? "宿主未返回结果");
                return;
            }

            // 身份二次校验：宿主已挡一轮，这里对返回值再校验一次（双保险）
            var uid = TokenUtils.ParseAccountUid(result.Token);
            if (!string.IsNullOrEmpty(_account.AccountUid) && !string.IsNullOrEmpty(uid)
                && uid != _account.AccountUid)
            {
                AutoStatus.Text = $"登录的账号身份不符（当前账号 uid={_account.AccountUid}，网页登录 uid={uid}），已放弃保存。请退出网页登录后改用当前账号的手机号登录。";
                return;
            }

            _account.Token = result.Token;
            // 关键保护：宿主没抓到 Session Cookie 时绝不能把已有的 Session 清掉
            // （Session 是 token 过期后唯一续命手段，旧代码这里会把好 Session 覆盖成 null）
            // 只有真正拿到新 Session 才刷新签发时间，否则云端「凭证有效期」会被错误续命。
            if (!string.IsNullOrEmpty(result.Session))
            {
                _account.Session = result.Session;
                _account.SessionUpdatedAt = DateTime.Now;
            }
            _account.TokenUpdatedAt = DateTime.Now;
            if (!string.IsNullOrEmpty(uid)) _account.AccountUid = uid;
            AccountHelpers.InvalidateTokenCache(_account.Id);   // 新登录态落盘后立即刷新校验缓存
            MainViewModel.AppConfig?.Save();
            Close(true);
        }
        catch (Exception ex)
        {
            AutoStatus.Text = "自动登录失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnOpenBrowserClick(object? sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://www.trae.cn") { UseShellExecute = true }); }
        catch { /* 打开失败忽略 */ }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var pasted = TokenBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(pasted))
        {
            StatusText.Text = "请先粘贴 Cloud-IDE-Token";
            return;
        }
        var initial = _account?.Token;
        if (!TokenUtils.ShouldAcceptNewToken(initial, pasted))
        {
            StatusText.Text = "Token 无效（长度不足或与当前账号相同），请重新复制";
            return;
        }
        if (_account == null)
        {
            StatusText.Text = "无当前账号，无法保存";
            return;
        }
        _account.Token = pasted;
        _account.TokenUpdatedAt = DateTime.Now;
        var uid = TokenUtils.ParseAccountUid(pasted);
        if (!string.IsNullOrEmpty(uid)) _account.AccountUid = uid;
        MainViewModel.AppConfig?.Save();
        Close(true);
    }

    // ====================================================================
    //            本 PR 新增的三种获取登录方式（剪贴板 / 备份 / 本机客户端）
    // ====================================================================

    /// <summary>
    /// 从剪贴板读取一段文本并智能识别：Token 直接用；会话串则调 GetUserToken 换取 Token。
    /// 识别逻辑在 CredentialImporter，本方法只负责取文本与回显结果。
    /// </summary>
    private async void OnImportClipboardClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is not { } cb)
            {
                AltStatus.Text = "无法访问剪贴板";
                return;
            }

            var raw = await cb.TryGetTextAsync();
            if (string.IsNullOrWhiteSpace(raw))
            {
                AltStatus.Text = "剪贴板为空，请先在浏览器里复制 Token 或会话串";
                return;
            }

            _busy = true;
            AltStatus.Text = "正在识别剪贴板内容…";
            var result = await CredentialImporter.ImportAsync(raw, MainViewModel.CheckinApi);
            if (!result.Ok)
            {
                AltStatus.Text = result.Message;
                return;
            }
            ApplyCredential(result.Token, result.Session, null, result.Message);
        }
        catch (Exception ex)
        {
            AltStatus.Text = "识别失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// 从账号备份文件（.json，口令派生的 AES-256-GCM 加密包）导入登录态。
    /// 优先取与当前账号 AccountUid 相同的那一份，否则取备份中的第一个账号并提示。
    /// </summary>
    private async void OnImportBackupClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null)
            {
                AltStatus.Text = "无法打开文件选择器";
                return;
            }

            var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 TraeTools 账号备份文件",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("TraeTools 账号备份") { Patterns = new[] { "*.json" } },
                    FilePickerFileTypes.All,
                },
            });
            if (picked.Count == 0) return;

            string text;
            await using (var stream = await picked[0].OpenReadAsync())
            using (var reader = new StreamReader(stream))
            {
                text = await reader.ReadToEndAsync();
            }

            var prompt = new InputWindow(
                "账号备份口令",
                "备份由口令派生的密钥加密（PBKDF2 + AES-256-GCM）。请输入导出时设置的口令。",
                "口令至少 6 位");
            var pass = await prompt.ShowDialog<string>(this);
            if (string.IsNullOrEmpty(pass))
            {
                AltStatus.Text = "已取消";
                return;
            }

            var (ok, message, accounts) = AccountBackup.Read(text, pass);
            if (!ok || accounts.Count == 0)
            {
                AltStatus.Text = message;
                return;
            }

            var uid = _account?.AccountUid;
            var pick = (!string.IsNullOrEmpty(uid)
                        ? accounts.FirstOrDefault(a => a.AccountUid == uid)
                        : null)
                       ?? accounts[0];

            var note = accounts.Count > 1
                ? $"{message}；本窗口只能使用其中一个，已取 {(pick.AccountUid ?? pick.Id)}"
                : message;
            ApplyCredential(pick.Token, pick.Session, pick.DeviceId, note);
        }
        catch (Exception ex)
        {
            AltStatus.Text = "导入失败：" + ex.Message;
        }
    }

    /// <summary>读取本机 TRAE 客户端的登录态（storage.json 里的 Token / 会话 / Aha 设备号）。</summary>
    private async void OnImportLocalClientClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            if (!LocalClientImporter.HasAnyClient())
            {
                AltStatus.Text = "未在本机找到 TRAE 客户端的数据目录";
                return;
            }

            _busy = true;
            AltStatus.Text = "正在读取本机客户端登录态…";
            var result = await LocalClientImporter.TryImportAsync();
            if (!result.Ok)
            {
                AltStatus.Text = result.Message;
                return;
            }
            ApplyCredential(result.Token, result.Session, result.DeviceId,
                result.Message + (string.IsNullOrEmpty(result.Source) ? "" : $"（来源：{result.Source}）"));
        }
        catch (Exception ex)
        {
            AltStatus.Text = "读取失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// 把拿到的一组凭证写入当前账号并关闭窗口（返回 true 表示登录成功）。
    ///
    /// 设备号处理：只在「本机其它账号都没用过」时才写入 —— 多账号共用同一设备号会被
    /// Trae 风控判为同一台机器并返回 9074，因此冲突时保留账号原有设备号。
    /// 身份校验：凭证携带的 JWT data.id 与当前账号 AccountUid 不符时拒绝写入，
    /// 防止把别的账号的登录态覆盖进来（跨账号污染）。
    /// </summary>
    private void ApplyCredential(string? token, string? session, string? deviceId, string note)
    {
        try
        {
            if (_account == null)
            {
                AltStatus.Text = "无当前账号，无法保存";
                return;
            }
            if (string.IsNullOrEmpty(token) && string.IsNullOrEmpty(session))
            {
                AltStatus.Text = "未取得可用的凭证";
                return;
            }

            if (!string.IsNullOrEmpty(token))
            {
                var uid = TokenUtils.ParseAccountUid(token);
                if (!string.IsNullOrEmpty(_account.AccountUid) && !string.IsNullOrEmpty(uid)
                    && uid != _account.AccountUid)
                {
                    AltStatus.Text = $"凭证属于另一个账号（当前账号 uid={_account.AccountUid}，凭证 uid={uid}），已拒绝写入。";
                    return;
                }
                _account.Token = token;
            }
            if (!string.IsNullOrEmpty(session))
            {
                _account.Session = session;
                _account.SessionUpdatedAt = DateTime.Now;
            }

            if (!string.IsNullOrEmpty(deviceId)
                && deviceId.Length == 16 && deviceId.All(char.IsDigit)
                && !(MainViewModel.AppConfig?.Accounts.Any(a => a.Id != _account.Id && a.DeviceId == deviceId) ?? true))
            {
                _account.DeviceId = deviceId;
            }

            _account.TokenUpdatedAt = DateTime.Now;
            var newUid = TokenUtils.ParseAccountUid(token ?? _account.Token);
            if (!string.IsNullOrEmpty(newUid)) _account.AccountUid = newUid;

            MainViewModel.AppConfig?.Save();
            AltStatus.Text = note;
            Close(true);
        }
        catch (Exception ex)
        {
            AltStatus.Text = "保存失败：" + ex.Message;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}