using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TraeTools.Services;
using TraeTools.ViewModels;

namespace TraeTools.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _autoCheckinTimer;
    private TrayIcon? _trayIcon;
    /// <summary>true=用户从托盘「退出」真正退出；false=关闭窗口仅最小化到托盘。 </summary>
    private bool _realExit;
    /// <summary>计时器重入守卫：上一轮任务（可能数十秒）未结束前跳过本轮 Tick（#33）。</summary>
    private bool _tickBusy;
    private readonly WindowNotificationManager _notify;

    public MainWindow()
    {
        _notify = new WindowNotificationManager(this) { Position = NotificationPosition.BottomRight, MaxItems = 3 };
        InitializeComponent();

        // 任务栏窗口图标：复用托盘同款 avalonia-logo.ico（缺省时任务栏不显示图标）
        try
        {
            using var iconStream = AssetLoader.Open(new Uri("avares://TraeTools/Assets/avalonia-logo.ico"));
            Icon = new WindowIcon(iconStream);
        }
        catch { /* 图标加载失败不影响主程序 */ }

        SetupWindowChrome();

        // 自动签到定时器：每 30 秒检查一次，到点且当天未签时自动签到
        _autoCheckinTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        // 防重入：上一轮未结束则跳过本轮，避免长任务在常驻托盘场景下叠加（#33）
        _autoCheckinTimer.Tick += async (_, _) =>
        {
            if (_tickBusy) return;
            _tickBusy = true;
            try
            {
                if (DataContext is MainViewModel vm)
                {
                    await vm.AutoCheckinIfDue();
                    // 到点 +10 分钟仍没签上 → 告警（每天一次，落盘去重）
                    await vm.CheckMissedCheckinIfDue();
                    await vm.RefreshSnapshotIfNeeded();
                }
            }
            finally { _tickBusy = false; }
        };

        // 到点未签告警：即使没配任何推送渠道，本机也要弹一条
        MainViewModel.MissedAlertRaised += OnMissedAlert;

        BuildTray();
        Closing += OnWindowClosing;
    }

    /// <summary>
    /// 自绘无边框外壳：把标题栏区域标记为 TitleBar（由系统接管拖拽/双击最大化/贴靠），
    /// 交通灯与主题按钮标记为 User（穿透装饰区、正常接收点击）；并挂接交通灯行为与代码兜底拖拽。
    /// </summary>
    private void SetupWindowChrome()
    {
        try
        {
            WindowDecorationProperties.SetElementRole(SideTitleBar, WindowDecorationsElementRole.TitleBar);
            WindowDecorationProperties.SetElementRole(ContentTitleBar, WindowDecorationsElementRole.TitleBar);
            // 标题栏内的可交互控件必须标记为 User，否则点击会被系统当作标题栏非客户区吞掉
            foreach (Control b in new Control[] { BtnClose, BtnMin, BtnMax, BtnTheme })
                WindowDecorationProperties.SetElementRole(b, WindowDecorationsElementRole.User);
        }
        catch { /* 平台不支持该机制时，下面挂的代码拖拽兜底仍然可用 */ }

        BtnClose.Click += (_, _) => Close();
        BtnMin.Click += (_, _) => WindowState = WindowState.Minimized;
        BtnMax.Click += (_, _) => ToggleMaximize();

        // 兜底：若平台未接管标题栏，则由代码完成拖拽与双击最大化
        SideTitleBar.PointerPressed += TitleBar_PointerPressed;
        ContentTitleBar.PointerPressed += TitleBar_PointerPressed;
        SideTitleBar.DoubleTapped += TitleBar_DoubleTapped;
        ContentTitleBar.DoubleTapped += TitleBar_DoubleTapped;
    }

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>标题栏空白处按下 → 拖动窗口；落在按钮上则交给按钮处理。</summary>
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) != null) return;
        BeginMoveDrag(e);
    }

    private void TitleBar_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) != null) return;
        ToggleMaximize();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WindowChrome.ApplyCorners(this);
        OnOpenedEulaCheck();
        _autoCheckinTimer.Start();
        _ = CheckMissedOnStartupAsync();
    }

    /// <summary>
    /// 启动即检查一次「到点仍未签到」。
    /// 覆盖：昨天云端定时任务失败（Session 过期）、本机整点没开机、自动签到被风控拦下。
    /// 延迟 2.5 秒是为了等配置/MainViewModel 初始化完成，避免抢在窗口就绪前弹通知。
    /// </summary>
    private async Task CheckMissedOnStartupAsync()
    {
        try
        {
            await Task.Delay(2500);
            if (DataContext is MainViewModel vm) await vm.CheckMissedCheckinIfDue();
        }
        catch { /* 启动检查失败不影响正常使用 */ }
    }

    /// <summary>到点未签告警的本机提示（推送渠道之外的最后一道兜底）。</summary>
    private void OnMissedAlert(string title, string content)
    {
        try
        {
            _notify.Show(new Notification(
                title,
                "已过签到时间 10 分钟。到「每日签到」查看并补签；云端失败请重新登录后重新部署。",
                NotificationType.Error,
                TimeSpan.FromSeconds(10)));
            AccountHelpers.AppLog("checkin", "", $"[告警] {title}");
        }
        catch { /* 通知失败不影响已发出的推送 */ }
    }

    /// <summary>窗口状态变化（最大化/还原）时刷新 DWM 圆角：最大化不圆角，避免四角露白。</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
            WindowChrome.ApplyCorners(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _autoCheckinTimer.Stop();
        MainViewModel.MissedAlertRaised -= OnMissedAlert;
        _trayIcon?.Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// 关闭窗口：默认最小化到托盘（与 TraeCheckin 一致）；仅托盘「退出」真正退出。
    /// 「最小化到托盘」开关关闭时（方案 B）：弹一次确认，用户点「退出」才退，取消则留在窗口
    /// （不缩托盘、不交互 —— 关掉托盘驻留就是关掉托盘驻留）。
    /// </summary>
    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_realExit) return;

        bool minimizeToTray = MainViewModel.AppConfig?.MinimizeToTray ?? true;
        if (minimizeToTray)
        {
            e.Cancel = true;
            Hide();
            _notify.Show(new Notification("TraeTools", "已最小化到托盘，后台继续自动签到", NotificationType.Information, TimeSpan.FromSeconds(2)));
            return;
        }

        // 托盘驻留已关闭 → 先拦住关闭（异步确认期间窗口不能先没了），确认后再决定
        e.Cancel = true;
        bool autoCheckin = MainViewModel.AppConfig?.AutoCheckinEnabled == true;
        try
        {
            var ask = new PromptWindow(
                "退出 TraeTools",
                autoCheckin
                    ? "「最小化到托盘」已关闭，退出后本次运行的自动签到将停止（下次开机或手动启动才会恢复）。\n\n确定退出吗？\n\n提示：可在「设置 → 自动签到」重新开启「最小化到托盘」让程序常驻后台。"
                    : "「最小化到托盘」已关闭，确定退出 TraeTools 吗？",
                "退出", "取消");
            bool quit = await ask.ShowDialog<bool>(this);
            if (quit)
            {
                _realExit = true;
                Close();   // 二次进入时 _realExit 已置位，直接放行
            }
        }
        catch
        {
            // 确认框异常时保守处理：留在窗口内，不静默退出
        }
    }

    private void BuildTray()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://TraeTools/Assets/avalonia-logo.ico"));
            var menu = new NativeMenu();
            var show = new NativeMenuItem("显示主窗口");
            show.Click += (_, _) => ShowAndActivate();
            // 托盘只签「当前激活账号」，与页面上的「一键签到」（批量+间隔）区分开，避免语义混淆
            var checkin = new NativeMenuItem("立即签到（当前账号）");
            checkin.Click += async (_, _) => await TrayCheckinAsync();
            var exit = new NativeMenuItem("退出");
            exit.Click += (_, _) =>
            {
                _realExit = true;
                Close();
            };
            menu.Items.Add(show);
            menu.Items.Add(checkin);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exit);

            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(stream),
                ToolTipText = "TraeTools 签到 + 账号切换",
                Menu = menu,
                IsVisible = true
            };
            // 左键单击托盘图标 → 显示/激活主窗口
            _trayIcon.Clicked += (_, _) => ShowAndActivate();
        }
        catch
        {
            // 托盘初始化失败（如系统不支持）不影响主程序
        }
    }

    private void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private async Task TrayCheckinAsync()
    {
        if (DataContext is MainViewModel vm)
        {
            bool ok = await vm.QuickCheckinAsync();
            _notify.Show(new Notification(
                ok ? "签到成功" : "签到失败",
                ok ? "今日积分已到账（会员含连签加成）" : "请检查网络或 Token 是否有效",
                ok ? NotificationType.Success : NotificationType.Error));
        }
    }

    /// <summary>
    /// 首次启动弹出免责声明协议（EULA）。同意标记独立存放于 %APPDATA%\TraeTools\eula_accepted，
    /// 与 TraeCheckin 的已同意配置互相独立，保证本程序首次启动必定展示协议；
    /// 不同意则直接退出。
    /// </summary>
    private async void OnOpenedEulaCheck()
    {
        var flag = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TraeTools", "eula_accepted");
        if (System.IO.File.Exists(flag)) return;

        var eula = new EulaWindow();
        bool agreed = await eula.ShowDialog<bool>(this);
        if (agreed)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(flag)!);
                System.IO.File.WriteAllText(flag, DateTime.UtcNow.ToString("o"));
            }
            catch { /* 标记写失败下次仍会弹，可接受 */ }
        }
        else
        {
            _realExit = true;
            Close();
        }
    }
}
