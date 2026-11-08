using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TraeTools.Services;

/// <summary>日志级别（Success 仅用于签到成功等正面结果，便于界面着色）。</summary>
public enum LogLevel
{
    Info,
    Success,
    Warn,
    Error,
}

/// <summary>一条日志记录（内存视图用；落盘为纯文本行，读取时再解析）。</summary>
public sealed class LogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; } = LogLevel.Info;
    public string Category { get; init; } = "app";
    public string Account { get; init; } = "";
    public string Message { get; init; } = "";

    /// <summary>单行展示文案：`HH:mm:ss  消息`（账号信息拼在消息前，保持行宽一致）。</summary>
    public string Display
    {
        get
        {
            var who = string.IsNullOrEmpty(Account) || Account == "?" ? "" : $"[{Account}] ";
            return Time.ToString("HH:mm:ss") + "  " + who + Message;
        }
    }

    /// <summary>级别标签（UI 左侧色块用）。</summary>
    public string LevelTag => Level switch
    {
        LogLevel.Error => "错误",
        LogLevel.Warn => "警告",
        LogLevel.Success => "成功",
        _ => "信息",
    };

    // ===== 供视图用 Classes 绑定驱动着色的布尔位（避免把 Avalonia 画刷类型引到日志层） =====
    public bool IsError => Level == LogLevel.Error;
    public bool IsWarn => Level == LogLevel.Warn;
    public bool IsSuccess => Level == LogLevel.Success;

    /// <summary>级别标签的左括号形式，用于单行纯文本导出。</summary>
    public string LevelCode => Level switch
    {
        LogLevel.Error => "ERROR",
        LogLevel.Warn => "WARN",
        LogLevel.Success => "OK",
        _ => "INFO",
    };
}

/// <summary>一个日志文件（「全部日志」列表项）。</summary>
public sealed class LogFileItem
{
    public required string Path { get; init; }
    /// <summary>分组名：模块日志取 category（checkin/account/cloud/…），崩溃日志为 crash。</summary>
    public required string Group { get; init; }
    public required string FileName { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }

    /// <summary>展示用时间：`MM-dd HH:mm`。</summary>
    public string TimeText => Modified.ToString("MM-dd HH:mm");

    /// <summary>展示用体积：小于 1KB 显示 B，其余显示 KB/MB（一位小数）。</summary>
    public string SizeText => Size < 1024
        ? $"{Size} B"
        : Size < 1024 * 1024
            ? $"{Size / 1024.0:F1} KB"
            : $"{Size / 1048576.0:F1} MB";

    /// <summary>列表标题：`checkin_log_202610.txt`。</summary>
    public string Title => FileName;
}

/// <summary>
/// 统一运行日志（分级 + 内存环形缓冲 + 按月分文件落盘）。
///
/// 设计目标：让「日志」页能同时提供两种视角——
///   · 最近：本进程实时产生的日志（内存缓冲，随写随到，可自动滚动）；
///   · 全部：磁盘上按模块/月份归档的历史日志文件（含崩溃日志目录），可逐文件查看。
///
/// 落盘格式与历史版本保持一致（`[时间] [账号] 消息`），仅在中间插入级别标记
/// （`[时间] [级别] [账号] 消息`），因此旧日志文件仍能被「全部」页正常读取展示。
/// 所有写入都在 try/catch 内，日志自身故障绝不影响业务流程。
/// </summary>
public static class AppLog
{
    /// <summary>内存缓冲上限（超出后丢弃最旧一条）。</summary>
    private const int MaxMemoryEntries = 800;

    /// <summary>单文件读取上限：超出只取尾部，避免超大日志把界面卡死。</summary>
    private const int MaxReadBytes = 256 * 1024;

    private static readonly object Sync = new();
    private static readonly LinkedList<LogEntry> Buffer = new();

    private static bool _seeded;

    /// <summary>新日志写入时触发（可能来自后台线程，订阅方需自行切回 UI 线程）。</summary>
    public static event Action<LogEntry>? EntryAdded;

    // ==================== 写入 ====================

    public static void Info(string category, string account, string message)
        => Write(LogLevel.Info, category, account, message);

    public static void Success(string category, string account, string message)
        => Write(LogLevel.Success, category, account, message);

    public static void Warn(string category, string account, string message)
        => Write(LogLevel.Warn, category, account, message);

    public static void Error(string category, string account, string message)
        => Write(LogLevel.Error, category, account, message);

    /// <summary>记录一条日志：先进内存缓冲，再按月追加到 `{category}_log_yyyyMM.txt`。</summary>
    public static void Write(LogLevel level, string category, string account, string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(category)) category = "app";
            if (message == null) message = "";

            var entry = new LogEntry
            {
                Time = DateTime.Now,
                Level = level,
                Category = category,
                Account = account ?? "",
                Message = message,
            };

            lock (Sync)
            {
                Buffer.AddLast(entry);
                while (Buffer.Count > MaxMemoryEntries) Buffer.RemoveFirst();
            }

            // 落盘：与历史格式兼容，仅在中间插入级别标记
            try
            {
                var line = $"[{entry.Time:yyyy-MM-dd HH:mm:ss}] [{LevelLabel(level)}] [{Safe(entry.Account)}] {message}";
                var file = Path.Combine(DataPaths.LogsDir, $"{category}_log_{entry.Time:yyyyMM}.txt");
                AppendLine(file, line);
            }
            catch { /* 落盘失败不影响内存视图 */ }

            try { EntryAdded?.Invoke(entry); } catch { /* 订阅方异常不影响写入 */ }
        }
        catch { /* 日志失败绝不影响业务 */ }
    }

    /// <summary>
    /// 仅写入内存缓冲、不落盘。供 <c>CrashLog</c> 使用：
    /// 崩溃/启动轨迹已有自己的落盘文件（logs/crash/log_日期.txt），
    /// 再落一份会造成同一事件在两个文件里重复出现；但「最近日志」应当能看到它们，
    /// 因此这里只做镜像。
    /// </summary>
    public static void Mirror(LogLevel level, string category, string message)
    {
        try
        {
            var entry = new LogEntry
            {
                Time = DateTime.Now,
                Level = level,
                Category = category,
                Account = "",
                Message = message ?? "",
            };
            lock (Sync)
            {
                Buffer.AddLast(entry);
                while (Buffer.Count > MaxMemoryEntries) Buffer.RemoveFirst();
            }
            try { EntryAdded?.Invoke(entry); } catch { /* 订阅方异常不影响写入 */ }
        }
        catch { /* 日志失败绝不影响业务 */ }
    }

    /// <summary>按行追加（带锁，跨线程安全）。</summary>
    private static void AppendLine(string path, string line)
    {
        lock (Sync)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    private static string LevelLabel(LogLevel level) => level switch
    {
        LogLevel.Error => "ERROR",
        LogLevel.Warn => "WARN",
        LogLevel.Success => "OK",
        _ => "INFO",
    };

    private static string Safe(string? s) => string.IsNullOrWhiteSpace(s) ? "?" : s!;

    // ==================== 读取（最近） ====================

    /// <summary>取最近 count 条（按时间正序）；count &lt;= 0 时返回全部缓冲。</summary>
    public static IReadOnlyList<LogEntry> Recent(int count = 0)
    {
        lock (Sync)
        {
            if (count <= 0 || count >= Buffer.Count) return Buffer.ToList();
            return Buffer.Skip(Buffer.Count - count).ToList();
        }
    }

    /// <summary>
    /// 首次读取「最近」前，把今天已落盘的日志回填进内存缓冲（进程重启后仍能看到今天早些时候的记录）。
    /// 幂等：只执行一次。
    /// </summary>
    public static void SeedFromTodayFiles()
    {
        lock (Sync)
        {
            if (_seeded) return;
            _seeded = true;
        }

        try
        {
            var today = DateTime.Today;
            var lines = new List<LogEntry>();

            foreach (var file in EnumerateFiles())
            {
                if (file.Modified.Date != today) continue;
                try
                {
                    foreach (var raw in TailLines(file.Path, MaxReadBytes))
                    {
                        if (TryParse(raw, file.Group, out var e)) lines.Add(e);
                    }
                }
                catch { /* 单个文件读取失败跳过 */ }
            }

            // 合并后按时间排序，只保留最近的若干条
            lines.Sort((a, b) => a.Time.CompareTo(b.Time));
            if (lines.Count == 0) return;

            lock (Sync)
            {
                // 去重：本进程已写入的行同时存在于 Buffer 与文件里，
                // 直接 AddLast 会让每次启动的日志翻倍。
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in Buffer) seen.Add(Key(e));

                foreach (var e in lines.TakeLast(MaxMemoryEntries))
                {
                    if (!seen.Add(Key(e))) continue;   // 已在缓冲中 → 跳过
                    Buffer.AddLast(e);
                }
                while (Buffer.Count > MaxMemoryEntries) Buffer.RemoveFirst();
            }
        }
        catch { /* 回填失败不影响功能 */ }
    }

    /// <summary>去重键：时间（秒级）+ 分类 + 账号 + 正文。</summary>
    private static string Key(LogEntry e)
        => $"{e.Time:yyyy-MM-dd HH:mm:ss}|{e.Category}|{e.Account}|{e.Message}";

    // ==================== 读取（全部文件） ====================

    /// <summary>枚举所有日志文件（模块日志目录 + 账号切换目录），按修改时间倒序。</summary>
    public static IReadOnlyList<LogFileItem> EnumerateFiles()
    {
        var list = new List<LogFileItem>();

        Collect(DataPaths.LogsDir, list);
        try { Collect(DataPaths.SwitchDir, list); } catch { /* 切换配置目录中的日志（如有） */ }

        return list
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(f => f.Modified)
            .ToList();
    }

    private static void Collect(string? dir, List<LogFileItem> sink)
    {
        try
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            foreach (var path in Directory.GetFiles(dir, "*.txt", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(path);
                    sink.Add(new LogFileItem
                    {
                        Path = path,
                        Group = GroupOf(info.Name),
                        FileName = info.Name,
                        Size = info.Length,
                        Modified = info.LastWriteTime,
                    });
                }
                catch { /* 单个文件信息失败跳过 */ }
            }
        }
        catch { /* 目录枚举失败跳过 */ }
    }

    /// <summary>从文件名推断分组：`checkin_log_202610.txt` → checkin；崩溃日志 `log_20261014.txt` → crash。</summary>
    private static string GroupOf(string fileName)
    {
        var m = Regex.Match(fileName, @"^(?<g>[a-z]+)_log_\d{6}\.txt$", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups["g"].Value.ToLowerInvariant();
        if (Regex.IsMatch(fileName, @"^log_\d{8}\.txt$", RegexOptions.IgnoreCase)) return "crash";
        return "other";
    }

    /// <summary>读取日志文件尾部内容（超过上限只取尾部，并加省略提示）。</summary>
    public static string ReadTail(string path, int maxBytes = MaxReadBytes)
    {
        try
        {
            if (!File.Exists(path)) return "(文件不存在)";
            var info = new FileInfo(path);
            if (info.Length <= maxBytes)
                return File.ReadAllText(path, Encoding.UTF8);

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(-maxBytes, SeekOrigin.End);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var text = reader.ReadToEnd();
            return $"(文件较大，仅显示最后 {maxBytes / 1024} KB){Environment.NewLine}{text}";
        }
        catch (Exception ex)
        {
            return "(读取失败) " + ex.Message;
        }
    }

    /// <summary>删除一个日志文件。</summary>
    public static bool DeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>清空全部模块日志（保留文件，内容置空）。返回清空的文件数。</summary>
    public static int ClearAllFiles()
    {
        int n = 0;
        foreach (var f in EnumerateFiles())
        {
            try
            {
                File.WriteAllText(f.Path, string.Empty, Encoding.UTF8);
                n++;
            }
            catch { /* 单个失败跳过 */ }
        }
        try
        {
            lock (Sync) Buffer.Clear();
        }
        catch { /* 忽略 */ }
        return n;
    }

    // ==================== 解析 ====================

    /// <summary>把磁盘上的一行日志解析为 LogEntry；无法识别返回 false。</summary>
    private static bool TryParse(string line, string fallbackCategory, out LogEntry entry)
    {
        entry = null!;
        if (string.IsNullOrWhiteSpace(line)) return false;

        try
        {
            // 新格式：[yyyy-MM-dd HH:mm:ss] [LEVEL] [account] message
            var mNew = Regex.Match(line,
                @"^\[(?<t>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]\s*\[(?<lv>[A-Za-z]+)\]\s*\[(?<ac>[^\]]*)\]\s*(?<msg>.*)$");
            if (mNew.Success && DateTime.TryParse(mNew.Groups["t"].Value, out var t1))
            {
                entry = new LogEntry
                {
                    Time = t1,
                    Level = ParseLevel(mNew.Groups["lv"].Value),
                    Category = fallbackCategory,
                    Account = mNew.Groups["ac"].Value,
                    Message = mNew.Groups["msg"].Value,
                };
                return true;
            }

            // 旧格式：[yyyy-MM-dd HH:mm:ss] [account] message
            var mOld = Regex.Match(line,
                @"^\[(?<t>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]\s*\[(?<ac>[^\]]*)\]\s*(?<msg>.*)$");
            if (mOld.Success && DateTime.TryParse(mOld.Groups["t"].Value, out var t2))
            {
                entry = new LogEntry
                {
                    Time = t2,
                    Level = LogLevel.Info,
                    Category = fallbackCategory,
                    Account = mOld.Groups["ac"].Value,
                    Message = mOld.Groups["msg"].Value,
                };
                return true;
            }

            // 崩溃日志：`[BOOT HH:mm:ss.fff] message`（无日期，按今天推算）
            var mBoot = Regex.Match(line, @"^\[BOOT (?<t>\d{2}:\d{2}:\d{2})\.?\d*\]\s*(?<msg>.*)$");
            if (mBoot.Success && TimeSpan.TryParse(mBoot.Groups["t"].Value, out var ts))
            {
                entry = new LogEntry
                {
                    Time = DateTime.Today.Add(ts),
                    Level = LogLevel.Info,
                    Category = "crash",
                    Account = "",
                    Message = mBoot.Groups["msg"].Value,
                };
                return true;
            }
        }
        catch { /* 解析失败视为不可识别 */ }

        return false;
    }

    private static LogLevel ParseLevel(string raw) => raw.ToUpperInvariant() switch
    {
        "ERROR" or "FATAL" => LogLevel.Error,
        "WARN" or "WARNING" => LogLevel.Warn,
        "OK" or "SUCCESS" => LogLevel.Success,
        _ => LogLevel.Info,
    };

    /// <summary>读取文件尾部的行（用于回填内存缓冲）。</summary>
    private static IEnumerable<string> TailLines(string path, int maxBytes)
    {
        var text = ReadTail(path, maxBytes);
        return text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0);
    }
}
