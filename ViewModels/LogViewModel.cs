using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeTools.Services;

namespace TraeTools.ViewModels;

/// <summary>
/// 日志页视图模型：两种视角 + 可选的自动滚动。
///
///   · **最近**：订阅 <see cref="AppLog.EntryAdded"/>，本进程产生的日志随写随到（内存缓冲）；
///   · **全部**：列出磁盘上按模块/月份归档的全部日志文件（模块日志 + 崩溃日志），点击查看内容。
///
/// 「自动滚动」是一个可关闭的开关：关闭后新日志仍然进入列表，但视图停在用户当前查看的位置——
/// 这是必需的，否则用户想翻看早先的记录时会被不断涌入的新日志推着往下走。
/// </summary>
public partial class LogViewModel : ViewModelBase
{
    /// <summary>
    /// 「最近」列表最多保留的行数（超出丢弃最旧的）。
    /// 上限偏保守：Android 端为软件渲染，行数过多会明显拖慢滚动。
    /// </summary>
    private const int MaxRecentRows = 300;

    /// <summary>订阅的是静态事件，必须手动退订，否则 VM 无法回收。</summary>
    private bool _subscribed;

    public ObservableCollection<LogEntry> Recent { get; } = new();

    public ObservableCollection<LogFileItem> Files { get; } = new();

    /// <summary>当前标签页：0=最近，1=全部。</summary>
    [ObservableProperty]
    private int _selectedTab;

    /// <summary>「最近」标签页是否选中（供视图绑定，避免在 XAML 里对 int 做取反）。</summary>
    public bool IsRecentTab => SelectedTab == 0;

    /// <summary>「全部」标签页是否选中。</summary>
    public bool IsAllTab => SelectedTab != 0;

    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsRecentTab));
        OnPropertyChanged(nameof(IsAllTab));
    }

    /// <summary>是否自动滚动到最新一条（最近）／内容末尾（全部）。</summary>
    [ObservableProperty]
    private bool _autoScroll = true;

    partial void OnAutoScrollChanged(bool value)
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg != null && cfg.LogAutoScroll != value)
            {
                cfg.LogAutoScroll = value;
                cfg.Save();
            }
        }
        catch { /* 偏好保存失败不影响功能 */ }
        // 重新打开自动滚动时立即归位到最新
        if (value) RequestScrollToEnd();
    }

    /// <summary>「全部」中选中的日志文件。</summary>
    [ObservableProperty]
    private LogFileItem? _selectedFile;

    partial void OnSelectedFileChanged(LogFileItem? value) => _ = LoadSelectedFileAsync(value);

    [ObservableProperty]
    private string _fileContent = "";

    [ObservableProperty]
    private string _fileHeader = "";

    [ObservableProperty]
    private bool _isFileLoading;

    /// <summary>底部状态消息（自动消失语义与设置页一致，由视图直接展示）。</summary>
    [ObservableProperty]
    private string _status = "";

    /// <summary>是否显示底部状态条。</summary>
    public bool HasStatus => !string.IsNullOrEmpty(Status);

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    /// <summary>最近列表是否为空（驱动空状态提示）。</summary>
    public bool IsRecentEmpty => Recent.Count == 0;

    /// <summary>全部列表是否为空。</summary>
    public bool IsFilesEmpty => Files.Count == 0;

    /// <summary>请求视图把滚动位置移到底部（由视图订阅后调用 ScrollToEnd）。</summary>
    public event Action? ScrollToEndRequested;

    /// <summary>最近列表的统计文案：`N 条 · 错误 x 警告 y`。</summary>
    public string RecentSummary
    {
        get
        {
            int err = Recent.Count(e => e.Level == LogLevel.Error);
            int warn = Recent.Count(e => e.Level == LogLevel.Warn);
            var extra = err + warn > 0 ? $" · 错误 {err} 警告 {warn}" : "";
            return $"{Recent.Count} 条{extra}";
        }
    }

    public LogViewModel()
    {
        try
        {
            AutoScroll = MainViewModel.AppConfig?.LogAutoScroll ?? true;
        }
        catch { /* 保持默认 true */ }

        Recent.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsRecentEmpty));
            OnPropertyChanged(nameof(RecentSummary));
        };
        Files.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsFilesEmpty));

        Subscribe();
        // 先回填当天已落盘的日志（进程重启后「最近」不再是空的）；幂等，只会执行一次
        AppLog.SeedFromTodayFiles();
        ReloadRecent();
        RefreshFiles();
    }

    /// <summary>订阅进程内日志事件（幂等）。</summary>
    public void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        AppLog.EntryAdded += OnEntryAdded;
    }

    /// <summary>退订（页面销毁时调用，避免静态事件持有 VM）。</summary>
    public void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        AppLog.EntryAdded -= OnEntryAdded;
    }

    /// <summary>新日志到达：切回 UI 线程后追加；超出上限则丢弃最旧一条。</summary>
    private void OnEntryAdded(LogEntry entry)
    {
        try
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => OnEntryAdded(entry));
                return;
            }

            Recent.Add(entry);
            while (Recent.Count > MaxRecentRows) Recent.RemoveAt(0);
            if (AutoScroll) RequestScrollToEnd();
        }
        catch { /* 界面更新失败不影响日志记录 */ }
    }

    /// <summary>用内存缓冲整体重建「最近」列表（进入页面时调用）。</summary>
    public void ReloadRecent()
    {
        try
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(ReloadRecent);
                return;
            }

            // 设置页可能改过「自动滚动」偏好，进页面时同步一次。
            // OnAutoScrollChanged 内部有相等判断，因此这里不会产生多余的落盘。
            try { AutoScroll = MainViewModel.AppConfig?.LogAutoScroll ?? AutoScroll; }
            catch { /* 读配置失败则保留当前值 */ }

            // 每次进页面都尝试回填当天日志（SeedFromTodayFiles 内部幂等，二次调用直接返回）。
            // 覆盖「启动时日志目录还没有文件、运行中才产生」的场景。
            AppLog.SeedFromTodayFiles();

            Recent.Clear();
            foreach (var e in AppLog.Recent(MaxRecentRows)) Recent.Add(e);
            if (AutoScroll) RequestScrollToEnd();
        }
        catch { /* 忽略 */ }
    }

    /// <summary>重新扫描磁盘上的日志文件。</summary>
    [RelayCommand]
    public void RefreshFiles()
    {
        try
        {
            var keep = SelectedFile?.Path;
            Files.Clear();
            foreach (var f in AppLog.EnumerateFiles()) Files.Add(f);

            // 尽量保持原选中项，避免刷新后跳到第一个文件
            if (keep != null)
                SelectedFile = Files.FirstOrDefault(f => f.Path == keep) ?? Files.FirstOrDefault();
            else if (SelectedFile == null && Files.Count > 0)
                SelectedFile = Files[0];
        }
        catch (Exception ex)
        {
            Status = "扫描日志文件失败：" + ex.Message;
        }
    }

    private async Task LoadSelectedFileAsync(LogFileItem? item)
    {
        if (item == null)
        {
            FileContent = "";
            FileHeader = "";
            return;
        }
        try
        {
            IsFileLoading = true;
            FileHeader = $"{item.Path}  ·  {item.SizeText}  ·  最后修改 {item.Modified:yyyy-MM-dd HH:mm}";
            // 文件可能很大且读取是同步 IO，放到线程池避免卡住 UI
            FileContent = await Task.Run(() => AppLog.ReadTail(item.Path));
        }
        catch (Exception ex)
        {
            FileContent = "(读取失败) " + ex.Message;
        }
        finally
        {
            IsFileLoading = false;
            if (AutoScroll) RequestScrollToEnd();
        }
    }

    /// <summary>切到「最近」标签页。</summary>
    [RelayCommand]
    private void ShowRecent()
    {
        SelectedTab = 0;
        ReloadRecent();
    }

    /// <summary>切到「全部」标签页（并重新扫描文件，保证看到最新落盘的文件）。</summary>
    [RelayCommand]
    private void ShowAll()
    {
        SelectedTab = 1;
        RefreshFiles();
    }

    /// <summary>手动触发一次滚动到底（即使用户关掉了自动滚动）。</summary>
    [RelayCommand]
    private void ScrollToLatest()
    {
        RequestScrollToEnd();
        Status = "已滚动到最新";
    }

    /// <summary>删除选中的日志文件。</summary>
    [RelayCommand]
    private void DeleteFile()
    {
        var item = SelectedFile;
        if (item == null)
        {
            Status = "请先选择一个日志文件";
            return;
        }
        if (AppLog.DeleteFile(item.Path))
        {
            Status = $"已删除 {item.FileName}";
            SelectedFile = null;
            RefreshFiles();
        }
        else
        {
            Status = "删除失败（文件可能正被占用）";
        }
    }

    /// <summary>清空全部日志（保留文件本身，仅置空内容）与内存缓冲。</summary>
    [RelayCommand]
    private void ClearAll()
    {
        int n = AppLog.ClearAllFiles();
        Recent.Clear();
        FileContent = "";
        FileHeader = "";
        SelectedFile = null;
        RefreshFiles();
        ReloadRecent();
        Status = $"已清空 {n} 个日志文件";
    }

    /// <summary>复制「最近」列表的全部可见内容，便于一次性发回排查。</summary>
    [RelayCommand]
    private async Task CopyRecentAsync()
    {
        try
        {
            var text = string.Join(Environment.NewLine, Recent.Select(e =>
                $"[{e.Time:yyyy-MM-dd HH:mm:ss}] [{e.LevelTag}] [{e.Account}] {e.Message}"));
            // 剪贴板挂在 TopLevel（Window）上；上游 UiHost 只暴露 MainWindow，此处自行取一次
            var clip = UiHost.MainWindow?.Clipboard;
            if (clip == null)
            {
                Status = "无法访问剪贴板";
                return;
            }
            await clip.SetTextAsync(text);
            Status = $"已复制 {Recent.Count} 条日志到剪贴板";
        }
        catch (Exception ex)
        {
            Status = "复制失败：" + ex.Message;
        }
    }

    private void RequestScrollToEnd()
    {
        try { ScrollToEndRequested?.Invoke(); } catch { /* 视图异常不影响 VM */ }
    }
}
