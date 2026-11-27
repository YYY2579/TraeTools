using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeSwitch.Services;
using TraeTools.Models;
using TraeTools.Views;

namespace TraeTools.ViewModels;

public partial class SwitchViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _userDirectory = "";

    [ObservableProperty]
    private string _clientName = "—";

    [ObservableProperty]
    private int _carrierCount;

    [ObservableProperty]
    private string _vaultPath = "";

    [ObservableProperty]
    private string _logText = "[守护进程] 已就绪";

    /// <summary>账号总数文案（真实建档数）。</summary>
    [ObservableProperty]
    private string _accountCountText = "0 个账号";

    /// <summary>当前切换是否进行中（防止并发切换）。</summary>
    [ObservableProperty]
    private bool _isSwitching;

    /// <summary>手动切换与自动快照保鲜共用，禁止切换中途写回 vault。</summary>
    private readonly SemaphoreSlim _vaultGate = new(1, 1);

    public ObservableCollection<AccountInfo> Accounts { get; } = new();
    public ObservableCollection<SwitchStep> Steps { get; } = new();

    [ObservableProperty]
    private AccountInfo? _selectedAccount;

    public SwitchViewModel()
    {
        Steps.Add(new SwitchStep { Index = 1, Title = "结束客户端", Description = "关闭 Trae 客户端进程", Status = "idle" });
        Steps.Add(new SwitchStep { Index = 2, Title = "定位账号档案", Description = "读取该账号独立数据目录", Status = "idle" });
        Steps.Add(new SwitchStep { Index = 3, Title = "校验设备指纹", Description = "确认 machineid 与其他账号不同", Status = "idle" });
        Steps.Add(new SwitchStep { Index = 4, Title = "隔离启动", Description = "用 --user-data-dir 启动该账号", Status = "idle" });
        Steps.Add(new SwitchStep { Index = 5, Title = "判定结果", Description = "核对账号标识是否匹配", Status = "idle" });

        LoadAccounts();
        EnsureFingerprint();   // 指纹为空/过期时自动重探回填，避免「未配置载体指纹」卡死建档/切换
        ReportAutoFix();       // 旧版 TRAE SOLO CN 配置被自动修正时提示一次
    }

    /// <summary>settings.json 自愈（旧路径/旧进程名/旧指纹）只提示一次，避免用户以为"改了没生效"。</summary>
    private void ReportAutoFix()
    {
        var note = MainViewModel.SwitchSettings?.ConsumeAutoFixNote();
        if (!string.IsNullOrEmpty(note)) AppendLog(note);
    }

    /// <summary>
    /// 载体指纹为空、或**过期**（缺 state.vscdb 及其 -wal/-shm/.backup 边车）时，
    /// 重置为固定 15 项白名单并落盘。
    /// 旧版把"目录扫描出来的窄清单"固化进 settings.json，之后改白名单也不生效（P0-3 根因）；
    /// 这里每次进本页自愈一次，老用户升级后无需手动删配置文件。
    /// </summary>
    private void EnsureFingerprint()
    {
        var settings = MainViewModel.SwitchSettings;
        if (settings?.Data == null) return;
        if (!CarrierDefaults.IsStaleFingerprint(settings.Data.Fingerprint)) return;

        var detected = CarrierDefaults.DetectFingerprint();
        if (detected.Count == 0)
        {
            AppendLog("未检测到客户端登录态载体（可能未安装 Trae CN 客户端），请确认后再建档/切换");
            return;
        }
        if (settings.Data.Fingerprint.Count > 0)
            AppendLog("已固化的载体指纹不完整（缺 state.vscdb 或其边车），已重置为固定白名单");

        settings.Data.Fingerprint.Clear();
        settings.Data.Fingerprint.AddRange(detected);
        try { settings.Save(); } catch { /* 保存失败下次再写 */ }
        AppendLog($"载体指纹已就绪：{detected.Count} 项（当前实际存在 {CarrierDefaults.CountPresent(settings.Data.RootDir)} 项）");
        CarrierCount = settings.Data.Fingerprint.Count;
        LoadAccounts();
    }

    private IClientController? BuildClient()
    {
        var settings = MainViewModel.SwitchSettings;
        if (settings == null) return null;
        return new ClientController(settings.Data.ProcessName, ResolveClientExe(settings.Data.ClientExe));
    }

    /// <summary>
    /// 客户端 exe 解析：配置路径失效（被移动/改名/历史遗留错误值）时，自动回退到自动探测结果，
    /// 避免因 settings.json 里的旧路径把切换/启动卡死（#25）。
    /// </summary>
    private static string ResolveClientExe(string configured)
    {
        try
        {
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
                return configured;
        }
        catch { /* 校验失败走探测 */ }
        return TraeSwitch.Services.CarrierDefaults.DefaultClientExe;
    }

    /// <summary>当前生效的客户端路径（配置值或探测回退）。</summary>
    [ObservableProperty]
    private string _clientExePath = "";

    /// <summary>
    /// 手动选择客户端 exe（文件对话框），写入 settings.json 的 ClientExe 并持久化。
    /// 覆盖自动探测：适用于绿色版 / 自定义安装目录 / 多版本并存等探测无法命中的场景。
    /// </summary>
    [RelayCommand]
    private async Task BrowseClientExe()
    {
        try
        {
            var win = UiHost.MainWindow;
            if (win == null)
            {
                AppendLog("无法打开文件选择（主窗口未就绪）");
                return;
            }
            var files = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 Trae 客户端程序（Trae CN.exe）",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("可执行文件 (*.exe)") { Patterns = new[] { "*.exe" } },
                    new FilePickerFileType("所有文件") { Patterns = new[] { "*" } },
                },
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                AppendLog("未选择有效文件，已取消");
                return;
            }
            SetClientExe(path, $"已手动设置客户端路径：{path}");
        }
        catch (Exception ex)
        {
            AppendLog("选择客户端失败：" + ex.Message);
        }
    }

    /// <summary>重新自动探测客户端路径并写入配置（忽略之前缓存）。</summary>
    [RelayCommand]
    private void ReDetectClientExe()
    {
        try
        {
            CarrierDefaults.ResetDetection();
            var exe = CarrierDefaults.DefaultClientExe;
            SetClientExe(exe, $"已重新探测客户端路径：{exe}");
        }
        catch (Exception ex)
        {
            AppendLog("重新探测失败：" + ex.Message);
        }
    }

    /// <summary>将客户端路径写入 settings.json 并刷新展示。</summary>
    private void SetClientExe(string path, string log)
    {
        var settings = MainViewModel.SwitchSettings;
        if (settings == null)
        {
            AppendLog("切换服务未初始化，路径未保存");
            return;
        }
        settings.Data.ClientExe = path;
        try { settings.Save(); } catch { /* 保存失败仍展示 */ }
        ClientExePath = path;
        ClientName = Path.GetFileNameWithoutExtension(path);
        AppendLog(log);
    }

    /// <summary>加载账号列表：优先读取 TraeSwitch settings.json 真实账号，逐账号容错。</summary>
    private void LoadAccounts()
    {
        var settings = MainViewModel.SwitchSettings;
        var vault = MainViewModel.Vault;

        // 新流程的"数据目录"= 各账号独立数据目录的根，不是旧 vault。
        VaultPath = ProfileIsolation.ProfilesRoot;

        if (settings == null)
        {
            AddMockAccounts("TraeSwitch 服务未初始化");
            return;
        }

        UserDirectory = settings.Data.RootDir;
        var exe = ResolveClientExe(settings.Data.ClientExe);
        if (!string.Equals(exe, settings.Data.ClientExe, StringComparison.OrdinalIgnoreCase))
            AppendLog($"设置中的客户端路径无效（{settings.Data.ClientExe}），已自动回退到探测路径：{exe}");
        ClientName = Path.GetFileNameWithoutExtension(exe);
        ClientExePath = exe;
        CarrierCount = settings.Data.Fingerprint.Count;

        var accountNames = settings.Data.Accounts;
        if (accountNames == null || accountNames.Count == 0)
        {
            AddMockAccounts("尚未建档任何账号");
            return;
        }

        Accounts.Clear();
        var colors = new[] { "#3B82F6", "#10B981", "#F59E0B", "#8B5CF6", "#EC4899" };
        int idx = 0;

        // 当前 live 账号标识：读默认目录的 storage.json，只取哈希，不回显原文。
        string? liveIdentity = null;
        try { liveIdentity = AuthProbe.IdentityHash(AuthProbe.StoragePath(settings.Data.RootDir)); }
        catch { /* 读不到就全部按"已建档"展示，不标当前 */ }

        // 手机号索引：切换页账号名（建档名）→ 脱敏手机号（#14）。
        // 与设置页账号管理同源（TraeAccount.MobileMasked），按备注名/平台昵称忽略大小写匹配。
        var mobileByAccount = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var appAccounts = MainViewModel.AppConfig?.Accounts;
        if (appAccounts != null)
        {
            foreach (var ta in appAccounts)
            {
                if (string.IsNullOrWhiteSpace(ta.MobileMasked)) continue;
                if (!string.IsNullOrWhiteSpace(ta.Name)) mobileByAccount.TryAdd(ta.Name, ta.MobileMasked);
                if (!string.IsNullOrWhiteSpace(ta.ScreenName)) mobileByAccount.TryAdd(ta.ScreenName, ta.MobileMasked);
            }
        }

        foreach (var acc in accountNames)
        {
            idx++;
            try
            {
                var info = ProfileIsolation.GetInfo(acc);
                bool ready = ProfileIsolation.IsReady(acc);
                bool pending = ProfileIsolation.IsPending(acc);

                bool isCurrent = false;
                if (ready && liveIdentity != null)
                {
                    try
                    {
                        var p = AuthProbe.IdentityHash(AuthProbe.StoragePath(ProfileIsolation.ProfileDir(acc)));
                        isCurrent = p != null && string.Equals(p, liveIdentity, StringComparison.OrdinalIgnoreCase);
                    }
                    catch { /* 档案读不到按非当前处理 */ }
                }

                string status;
                string statusType;
                if (isCurrent)
                {
                    status = "当前 ✓";
                    statusType = "ok";
                }
                else if (ready)
                {
                    status = "已就绪";
                    statusType = "info";
                }
                else if (pending)
                {
                    status = "待登录";
                    statusType = "warn";
                }
                else
                {
                    status = "未创建";
                    statusType = "warn";
                }

                Accounts.Add(new AccountInfo
                {
                    Name = acc,
                    Initial = acc.Length > 0 ? acc[0].ToString() : "?",
                    Color = colors[(idx - 1) % colors.Length],
                    Status = status,
                    StatusType = statusType,
                    MobileText = mobileByAccount.TryGetValue(acc, out var m) ? m : "",
                    CreatedAt = info?.CreatedLocal?.ToString("yyyy-MM-dd HH:mm") ?? (pending ? "待登录" : "未创建"),
                    Carriers = info?.Files ?? 0,
                    Similarity = isCurrent ? "100%" : (ready ? "独立" : "—"),
                    IsCurrent = isCurrent
                });
            }
            catch (Exception ex)
            {
                LogText += $"\n[{DateTime.Now:HH:mm:ss}] 加载账号「{acc}」失败：{ex.Message}";
            }
        }

        if (Accounts.Count == 0)
        {
            AddMockAccounts("账号数据读取失败");
            return;
        }

        AccountCountText = $"{Accounts.Count} 个账号";
        LogText = $"[{DateTime.Now:HH:mm:ss}] 已加载 {Accounts.Count} 个账号\n"
                + "新账号：点「新建账号」→ 在弹出的 Trae 窗口登录 → 状态变「已就绪」后即可一键切换";
    }

    private void AddMockAccounts(string reason)
    {
        Accounts.Clear();
        LogText = $"[{DateTime.Now:HH:mm:ss}] {reason}\n请先在 TraeSwitch 或本页「建档」中备份账号登录态";
    }

    private void AppendLog(string line)
    {
        // 防无界增长（#33）：常驻托盘 7×24 运行，日志只进不出会持续涨内存；超限裁掉头部只留尾部
        const int MaxLogChars = 20_000;
        var text = LogText + $"\n[{DateTime.Now:HH:mm:ss}] {line}";
        if (text.Length > MaxLogChars)
            text = "…（日志已截断，只保留最近部分）\n" + text[^MaxLogChars..];
        LogText = text;
        AccountHelpers.AppLog("switch", "", line);
    }

    private void ResetSteps()
    {
        foreach (var s in Steps) s.Status = "idle";
    }

    /// <summary>用资源管理器（macOS 用 open）打开目录；不存在的目录先建出来，避免资源管理器报错。</summary>
    private void OpenFolder(string? path, string what)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                AppendLog($"{what}路径为空，无法打开");
                return;
            }
            Directory.CreateDirectory(path);
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", $"\"{path}\"") { UseShellExecute = true });
            AppendLog($"已打开{what}：{path}");
        }
        catch (Exception ex)
        {
            AppendLog($"打开{what}失败：{ex.Message}");
        }
    }

    /// <summary>打开 TraeTools 数据根目录（config.json / data / logs / vault 都在这里）。</summary>
    [RelayCommand]
    private void OpenDataDir() => OpenFolder(TraeTools.Services.DataPaths.Root, "数据目录");

    /// <summary>打开登录态备份 vault 目录。</summary>
    [RelayCommand]
    private void OpenVaultDir() => OpenFolder(TraeTools.Services.DataPaths.VaultDir, "vault 目录");

    /// <summary>打开当前探测到的客户端程序所在目录。</summary>
    [RelayCommand]
    private void OpenClientDir()
    {
        var exe = ClientExePath;
        OpenFolder(string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe), "客户端目录");
    }

    [RelayCommand]
    private void RefreshList()
    {
        LoadAccounts();
        AppendLog("账号列表已刷新");
    }

    /// <summary>
    /// 从签到配置导入账号名称到切换列表。只补名称，不复制凭证；
    /// 导入的账号仍需逐账号「建档」后才能切换。
    /// </summary>
    [RelayCommand]
    private void ImportFromCheckinAccounts()
    {
        var settings = MainViewModel.SwitchSettings;
        var cfg = MainViewModel.AppConfig;
        if (settings == null || cfg == null)
        {
            AppendLog("导入失败：签到配置或切换配置未初始化");
            return;
        }

        var existing = new HashSet<string>(settings.Data.Accounts, StringComparer.OrdinalIgnoreCase);
        var imported = new List<string>();
        foreach (var acc in cfg.Accounts)
        {
            var name = (acc.Name ?? acc.ScreenName ?? "").Trim();
            if (name.Length == 0 || !existing.Add(name)) continue;
            settings.Data.Accounts.Add(name);
            imported.Add(name);
        }

        if (imported.Count == 0)
        {
            AppendLog("没有需要导入的账号名称（可能已全部存在）");
            return;
        }

        try { settings.Save(); }
        catch (Exception ex) { AppendLog("导入账号名称后保存失败：" + ex.Message); }

        LoadAccounts();
        AppendLog($"已从签到账号导入 {imported.Count} 个名称到切换列表；这些账号仍需先在客户端登录并「建档」后才能切换");
    }

    /// <summary>
    /// 一键冷切换（真实执行）：复用 TraeSwitch SwitcherService 编排——
    /// 结束客户端 → 快照回滚区 → 写回目标账号 vault → 拉起客户端 → 守护判定。
    /// 失败（RolledBack）时服务内部已自动回滚并重启；NeedsConfirm 需要人工确认。
    /// </summary>
    [RelayCommand]
    private async Task SwitchAccount()
    {
        if (IsSwitching) { AppendLog("已在切换中，请等待完成"); return; }
        if (SelectedAccount == null) { AppendLog("请先选择目标账号"); return; }

        var settings = MainViewModel.SwitchSettings;
        var vault = MainViewModel.Vault;
        var client = BuildClient();
        if (settings == null || vault == null || client == null)
        {
            AppendLog("切换服务未初始化（TraeSwitch 配置缺失）");
            return;
        }
        if (!ProfileIsolation.IsReady(SelectedAccount.Name))
        {
            AppendLog(ProfileIsolation.IsPending(SelectedAccount.Name)
                ? $"「{SelectedAccount.Name}」还没登录。点「新建账号」会为它打开专用窗口，登录完成后即可切换"
                : $"「{SelectedAccount.Name}」还没创建独立目录。点「新建账号」创建并在窗口中登录");
            return;
        }
        // 档案里只有 usertag、没有 token（icube.cloudide）= 建档时客户端其实没登录完整，
        // 切过去必然显示未登录。必须在切换前拦下，而不是切完才发现。
        if (AuthProbe.HasAuthCreds(AuthProbe.StoragePath(ProfileIsolation.ProfileDir(SelectedAccount.Name))) != true)
        {
            AppendLog($"「{SelectedAccount.Name}」档案缺少登录凭据（建档时可能未登录完整），已阻止切换。"
                      + "请先在 Trae 登录该账号，再点「建档」重建档案。");
            return;
        }

        await _vaultGate.WaitAsync();
        IsSwitching = true;
        ResetSteps();
        TraeSwitch.Services.SwitchOutcome? outcome = null;
        try
        {
            var name = SelectedAccount.Name;
            var profileDir = ProfileIsolation.ProfileDir(name);
            var sw = new SwitcherService(settings.Data.RootDir, vault, client);
            AppendLog($"开始切换至 {name}（独立目录：{profileDir}）");
            AppendLog("正在结束客户端…");
            Steps[0].Status = "progress";
            await Task.Delay(150);
            Steps[0].Status = "ok"; Steps[1].Status = "ok"; Steps[2].Status = "ok"; Steps[3].Status = "progress";

            outcome = await sw.SwitchToProfileAsync(name, profileDir,
                progress: elapsed =>
                {
                    int sec = (int)elapsed.TotalSeconds;
                    if (sec >= 4 && sec % 4 == 0)
                        AppendLog($"守护中：正在核对账号标识…（{sec} 秒 / 最长 20 秒）");
                });

            Steps[3].Status = "ok"; Steps[4].Status = "progress";

            switch (outcome.Kind)
            {
                case SwitchOutcomeKind.Active:
                    Steps[4].Status = "ok";
                    AppendLog($"切换成功：已进入 {name}（{outcome.Detail}）");
                    SyncActiveAccount(name);
                    break;
                case SwitchOutcomeKind.RolledBack:
                    Steps[4].Status = "warn";
                    AppendLog($"切换失败（{outcome.Detail}），已恢复默认目录并重启客户端");
                    AppendLog("处理：请在该账号的独立目录里重新登录一次，然后点「建档」更新档案");
                    break;
                case SwitchOutcomeKind.NeedsConfirm:
                    Steps[4].Status = "warn";
                    AppendLog($"无法自动判定：{outcome.Detail}");
                    AppendLog("请人工确认客户端右上角账号；若不对，请关闭客户端后重新切换。");
                    break;
            }

            AppendLog("本次切换流程结束");
            LoadAccounts();
        }
        catch (Exception ex)
        {
            AppendLog("切换失败：" + ex.Message);
        }
        finally
        {
            // 仅已在本次流程内完结（成功 或 已自动回滚）才清理快照目录；
            // NeedsConfirm 仍需保留待用户人工"校验/回滚"，否则快照没了会无法恢复旧会话。
            if (outcome is { RollbackDir: { } dir }
                && outcome.Kind is SwitchOutcomeKind.Active or SwitchOutcomeKind.RolledBack)
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* 忽略清理失败 */ }
            }
            IsSwitching = false;
            _vaultGate.Release();
        }
    }

    /// <summary>切换成功（Active）后，把 TraeCheckin 的激活账号同步为目标账号，并刷新账号列表，保证仪表盘/签到页与当前客户端账号一致。</summary>
    private void SyncActiveAccount(string name)
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            var acc = cfg?.Accounts.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (cfg == null || acc == null)
            {
                AppendLog($"同步激活账号失败：未在本地配置中找到「{name}」");
                return;
            }
            if (cfg.ActiveAccountId == acc.Id) { LoadAccounts(); return; }
            cfg.ActiveAccountId = acc.Id;
            try { cfg.Save(); } catch { /* 保存失败不阻断切换 */ }
            AppendLog($"已同步激活账号为「{name}」");
            MainViewModel.NotifyActiveAccountChanged();
        }
        catch (Exception ex) { AppendLog($"同步激活账号异常：{ex.Message}"); }
    }

    [RelayCommand]
    private void VerifyAccount()
    {
        if (SelectedAccount == null) { AppendLog("请先选择账号"); return; }
        try
        {
            var name = SelectedAccount.Name;
            if (!ProfileIsolation.IsReady(name))
            {
                AppendLog($"校验 {name}：无独立数据目录（未建档）");
                return;
            }
            var id = AuthProbe.IdentityHash(AuthProbe.StoragePath(ProfileIsolation.ProfileDir(name)));
            var midHash = MachineIdHash(name);
            var settings2 = MainViewModel.SwitchSettings;
            int duplicates = 0;
            if (settings2 != null && midHash != null)
                duplicates = settings2.Data.Accounts.Count(a =>
                    !string.Equals(a, name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(MachineIdHash(a), midHash, StringComparison.OrdinalIgnoreCase));
            AppendLog($"校验 {name}：身份标识 {(id == null ? "缺失" : "有效")}，设备指纹 {(midHash?[..10] ?? "缺失")}，重复指纹 {duplicates} 个");
        }
        catch (Exception ex) { AppendLog("校验异常：" + ex.Message); }
    }

    /// <summary>轻量模态消息框（纯代码构造，避免为一个提示新建 axaml）。</summary>
    private static async Task MessageBoxShow(Window owner, string title, string message)
    {
        try
        {
            var btn = new Avalonia.Controls.Button { Content = "知道了", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Width = 90 };
            var dlg = new Avalonia.Controls.Window
            {
                Title = title,
                Width = 460,
                SizeToContent = Avalonia.Controls.SizeToContent.Height,
                WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
                Content = new Avalonia.Controls.StackPanel
                {
                    Margin = new Avalonia.Thickness(20),
                    Spacing = 16,
                    Children =
                    {
                        new Avalonia.Controls.TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                        btn,
                    }
                }
            };
            btn.Click += (_, _) => dlg.Close();
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                await dlg.ShowDialog(owner);
            else
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => dlg.ShowDialog(owner));
        }
        catch { /* 弹窗失败不影响阻断（下面仍会写日志并返回） */ }
    }

    /// <summary>只读取某账号独立目录的 machineid 哈希；无档案/读失败返回 null。</summary>
    private static string? MachineIdHash(string account)
    {
        try
        {
            var path = Path.Combine(ProfileIsolation.ProfileDir(account), "machineid");
            if (!File.Exists(path)) return null;
            var bytes = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
        catch { return null; }
    }

    [RelayCommand]
    private void DeleteAccount()
    {
        if (SelectedAccount == null) { AppendLog("请先选择账号"); return; }
        try
        {
            var settings = MainViewModel.SwitchSettings;
            if (settings != null)
            {
                var name = SelectedAccount.Name;
                settings.Data.Accounts.Remove(name);
                settings.Save();
                ProfileIsolation.DeleteProfile(name);
                AppendLog($"已移除「{name}」并删除其独立数据目录");
                LoadAccounts();
            }
            else { AppendLog("删除失败：设置服务未初始化"); }
        }
        catch (Exception ex) { AppendLog("删除失败：" + ex.Message); }
    }

    /// <summary>
    /// 新建账号并直接进入登录：建空目录 → 用该目录启动 Trae → 用户登录后登录态写进该目录。
    /// 不再需要"先在默认目录登录、再复制建档"两步，也就不会把 A 的登录态存成 B。
    /// </summary>
    [RelayCommand]
    private async Task NewAccountAndLogin()
    {
        var settings = MainViewModel.SwitchSettings;
        var client = BuildClient();
        if (settings == null || client == null)
        {
            AppendLog("切换服务未初始化（TraeSwitch 配置缺失）");
            return;
        }

        var owner = UiHost.MainWindow;
        var dlg = new InputWindow(
            "新建账号",
            "输入账号名称（如「主号 / 小号」）。确定后会打开一个专用 Trae 窗口，请在该窗口登录这个账号；"
            + "登录完成后本页状态会自动变为「已就绪」。",
            "如：主号 / 小号", "");
        string? input = owner != null ? await dlg.ShowDialog<string>(owner) : null;
        if (string.IsNullOrWhiteSpace(input)) { AppendLog("已取消新建账号"); return; }

        var name = input.Trim();
        if (settings.Data.Accounts.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            AppendLog($"账号「{name}」已存在。如需重新登录，选中它后点「切换至此」，在窗口里登录即可");
            return;
        }

        await _vaultGate.WaitAsync();
        IsSwitching = true;
        try
        {
            settings.Data.Accounts.Add(name);
            settings.Save();
            ProfileIsolation.CreateEmptyProfile(name);
            AppendLog($"已为「{name}」创建独立数据目录，正在打开专用 Trae 窗口…");

            await Task.Run(() => client.KillAll());
            await Task.Delay(400);
            client.Launch(ProfileIsolation.ProfileDir(name));

            AppendLog($"请在弹出的 Trae 窗口登录「{name}」。该目录已与设备指纹隔离，登录态只属于这个账号。");
            LoadAccounts();
        }
        catch (Exception ex) { AppendLog("新建账号失败：" + ex.Message); }
        finally { IsSwitching = false; _vaultGate.Release(); }
    }

    [RelayCommand]
    private async Task CreateProfile()
    {
        EnsureFingerprint();   // 建档前先确保指纹存在，否则只会备份出一个空壳 meta
        var settings = MainViewModel.SwitchSettings;
        var vault = MainViewModel.Vault;
        if (vault == null || settings == null) { AppendLog("建档失败：服务未初始化"); return; }

        // 识别当前 live 属于哪个已建档账号：用 storage.json 里 usertag 加密 blob 的
        // SHA-256 比对各档案（同一账号跨目录稳定，且不依赖明文 JWT——实测 JWT 是加密的读不到）。
        // 这是"把 A 的登录态存成 B 的档案"的唯一可靠防线。
        string detectedName = "";
        string? liveId = null;
        try { liveId = AuthProbe.ProfileIdentityHash(settings.Data.RootDir); }
        catch { /* 读不到则无法识别，靠下面的兜底提示 */ }

        if (liveId != null)
        {
            foreach (var acc in settings.Data.Accounts)
            {
                if (!ProfileIsolation.IsReady(acc)) continue;
                try
                {
                    var hid = AuthProbe.ProfileIdentityHash(ProfileIsolation.ProfileDir(acc));
                    if (hid != null && string.Equals(hid, liveId, StringComparison.OrdinalIgnoreCase))
                    { detectedName = acc; break; }
                }
                catch { /* 单账户读失败继续 */ }
            }
        }

        // 修复：账号列表已删空时，建档不再卡死——改为弹窗输入新账号名直接建档
        string account = SelectedAccount?.Name ?? "";
        if (!string.IsNullOrEmpty(account) && !string.IsNullOrEmpty(detectedName)
            && !string.Equals(account, detectedName, StringComparison.OrdinalIgnoreCase))
        {
            var owner0 = UiHost.MainWindow;
            var msg = $"当前 Trae 客户端登录的是「{detectedName}」，而不是你选中的「{account}」。\n\n"
                    + $"继续建档会把「{detectedName}」的登录态存进「{account}」档案，之后切换到「{account}」实际进入的仍是「{detectedName}」。\n\n"
                    + "正确做法：先在 Trae 里登录「{account}」，再回来建档；或选中「" + detectedName + "」重新建档。";
            if (owner0 != null)
                await MessageBoxShow(owner0, "建档已阻断", msg);
            AppendLog($"建档已阻断：当前登录「{detectedName}」≠ 选中「{account}」");
            return;
        }
        if (string.IsNullOrEmpty(account))
        {
            // 无选中账号时弹窗输入：把识别到的账号名作为默认值，避免用户手输错名
            string suggested = detectedName;
            if (!string.IsNullOrEmpty(suggested))
                AppendLog($"检测到当前客户端登录：{suggested}（按此名建档即可）");
            else if (liveId != null)
                AppendLog("当前客户端已登录，但该账号尚未建档；请为这份登录态命名");
            else
                AppendLog("未检测到客户端登录态：请先在 Trae 客户端登录后再建档，或手动输入名称");

            var owner = UiHost.MainWindow;
            var dlg = new InputWindow(
                "建档新账号",
                "请为当前登录的 Trae 客户输入一个账号名称（用于区分多个账号的登录态备份，如「主号 / 小号」）。已删除的账号如需恢复，用原来的名称即可。",
                "如：主号 / 小号", suggested);
            string? name = owner != null ? await dlg.ShowDialog<string>(owner) : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                AppendLog("已取消建档（未输入账号名）");
                return;
            }
            account = name.Trim();
            if (settings.Data.Accounts.Contains(account, StringComparer.OrdinalIgnoreCase))
            {
                AppendLog($"账号「{account}」已存在于列表中，请先在列表选择后点击「建档」更新备份");
                LoadAccounts();
                return;
            }
            settings.Data.Accounts.Add(account);
            settings.Save();
            AppendLog($"已新建账号「{account}」，开始建档…");
        }

        try
        {
            // 建档必须由用户明确"哪个目录的哪份登录态"：客户端运行中时它可能正跑在某个
            // 账号的独立目录里，而建档固定从默认目录复制——两者不是同一个账号时，
            // 会把 A 的登录态写进 B 的档案（小蒋档案被小Y覆盖就是这么来的）。
            // 因此运行中直接拒绝，让用户先关客户端、确认默认目录登录的是目标账号。
            var client = BuildClient();
            if (client != null && client.IsRunning())
            {
                AppendLog("建档已中止：Trae 客户端正在运行。请先完全退出 Trae（不是最小化），"
                          + "确认默认目录登录的是目标账号，再点建档。"
                          + "若客户端正跑在某个账号的独立目录里，直接在该窗口登录即可，无需再建档。");
                return;
            }

            var liveRoot = settings.Data.RootDir;
            if (!Directory.Exists(liveRoot))
            {
                AppendLog($"建档失败：客户端数据目录不存在（{liveRoot}）");
                return;
            }

            // 关键前置校验：必须两把凭据键都齐全（usertag = 身份，icube.cloudide = 登录 token）。
            // 之前只校验"有 storage.json"，结果把一份没登录完整的状态存成了小蒋档案，
            // 切过去客户端显示未登录——这是坏档案的来源。
            var liveAuth = AuthProbe.HasAuthCreds(AuthProbe.StoragePath(liveRoot));
            if (liveAuth != true)
            {
                AppendLog(liveAuth == false
                    ? $"建档已中止：当前 Trae 客户端未登录（缺少登录凭据）。请先在客户端登录「{account}」，确认界面已进入后再建档。"
                    : $"建档已中止：读不到客户端登录凭据（文件可能被占用）。请关闭 Trae 后重试。");
                return;
            }

            AppendLog($"开始建档：{account}（从当前登录态复制到独立目录，并重置设备指纹）…");
            int count = await Task.Run(() => ProfileIsolation.CreateProfile(account, liveRoot));

            // 建完再验一次档案本身：复制过程若丢文件，不能留下坏档案。
            var profAuth = AuthProbe.HasAuthCreds(AuthProbe.StoragePath(ProfileIsolation.ProfileDir(account)));
            if (profAuth != true)
            {
                try { ProfileIsolation.DeleteProfile(account); } catch { /* 清理失败不阻断提示 */ }
                AppendLog($"建档失败：档案登录凭据不完整，已删除该坏档案。请重新登录「{account}」后再建档。");
                LoadAccounts();
                return;
            }

            var info = ProfileIsolation.GetInfo(account);
            bool ok = ProfileIsolation.IsReady(account) && count > 0;
            AppendLog($"建档完成：{count} 个文件，独立目录 {ProfileIsolation.ProfileDir(account)}，校验 {(ok ? "通过" : "未通过")}");
            if (!ok) AppendLog("提示：当前客户端可能未登录；请先登录目标账号再建档");
            LoadAccounts();
            // 建档后自动选中新账号，用户可直接点「校验」复核，避免误报「无建档数据」（#34）
            var created = Accounts.FirstOrDefault(a => string.Equals(a.Name, account, StringComparison.OrdinalIgnoreCase));
            if (created != null) SelectedAccount = created;
        }
        catch (Exception ex) { AppendLog("建档失败：" + ex.Message); }
    }

    /// <summary>最近一次快照检查时间；10 分钟内不重复全量哈希（#33：原先每 30 秒全量跑，一晚 2880 轮）。</summary>
    private DateTime _lastRefreshCheckUtc = DateTime.MinValue;

    /// <summary>
    /// 定时巡检（原「快照保鲜」）：新流程下档案就是目录本身，客户端登录完成后
    /// 状态需要从「待登录」自动变成「已就绪」。仅在状态变化时重建列表，避免无谓刷新。
    /// </summary>
    internal Task TryAutoRefreshSnapshotAsync()
    {
        try
        {
            var settings = MainViewModel.SwitchSettings;
            if (settings == null) return Task.CompletedTask;

            bool changed = false;
            foreach (var acc in settings.Data.Accounts)
            {
                bool ready = ProfileIsolation.IsReady(acc);
                if (_lastReadyState.TryGetValue(acc, out var prev))
                {
                    if (prev != ready) { changed = true; _lastReadyState[acc] = ready; }
                }
                else _lastReadyState[acc] = ready;
            }
            if (changed && !IsSwitching) LoadAccounts();
        }
        catch { /* 巡检失败忽略，不影响主流程 */ }
        return Task.CompletedTask;
    }

    /// <summary>各账号最近一次「已就绪」状态，用于检测登录完成。</summary>
    private readonly Dictionary<string, bool> _lastReadyState = new(StringComparer.OrdinalIgnoreCase);

}





