using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TraeTools.Services.Login;

/// <summary>本机客户端导入结果。</summary>
public sealed class LocalImportResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public string? Token { get; init; }
    public string? Session { get; init; }
    public string? DeviceId { get; init; }
    /// <summary>命中的数据来源描述（用于告诉用户「从哪里读到的」）。</summary>
    public string Source { get; init; } = "";
}

/// <summary>
/// 从本机 TRAE 官方客户端的数据目录直接读取登录态（免手动复制粘贴）。
///
/// 实现说明（务必了解其局限）：
///   · TRAE 桌面端是 VS Code 系衍生，登录态主要落在
///     `%APPDATA%\{客户端}\User\globalStorage\storage.json`（设备号等元信息）
///     与 `%APPDATA%\{客户端}\Local Storage\leveldb\*`（JWT 等键值）。
///   · 官方客户端并未承诺这些文件的格式，不同版本可能变化；因此本实现采取
///     「多来源、多策略、失败即明确报错」的方式，不做静默兜底——
///     找不到时明确告知用户改用「会话 Cookie 导入 / 手动粘贴」。
///   · 全程只读，不修改客户端任何文件。
/// </summary>
public static class LocalClientImporter
{
    /// <summary>已知的 TRAE 客户端数据目录名（2026-10 起 TraeWork 与 TraeCode 合并，覆盖新旧命名）。</summary>
    private static readonly string[] ClientDirNames =
    {
        "TRAE", "TRAE CN", "TRAE SOLO CN", "Trae CN", "TRAE SOLO", "Trae",
    };

    /// <summary>单个文件扫描上限（leveldb 可能较大，超出则只扫前若干 MB）。</summary>
    private const long MaxScanBytes = 24L * 1024 * 1024;

    /// <summary>总共最多扫描的文件数（防止极端目录规模拖慢启动）。</summary>
    private const int MaxScanFiles = 200;

    /// <summary>JWT 形态匹配（header.payload.signature，base64url）。</summary>
    private static readonly Regex JwtPattern = new(
        @"eyJ[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]{6,}",
        RegexOptions.Compiled);

    /// <summary>本机是否存在任一 TRAE 客户端数据目录。</summary>
    public static bool HasAnyClient()
    {
#if ANDROID
        return false;
#else
        try { return CandidateDirs().Any(Directory.Exists); }
        catch { return false; }
#endif
    }

    /// <summary>
    /// 客户端数据目录候选：Windows 在 %APPDATA%，macOS 在 ~/Library/Application Support。
    /// 不能用 SpecialFolder.ApplicationData 一刀切 —— .NET 在 macOS 上把它映射到 ~/.config，
    /// 拼出来的 "%CONFIG%/Trae CN" 永远不存在，导入功能会静默失效。
    /// </summary>
    private static IEnumerable<string> CandidateDirs()
    {
        var root = TraeSwitch.Services.CarrierDefaults.ClientDataRoot;
        foreach (var n in ClientDirNames)
            yield return Path.Combine(root, n);
    }

    /// <summary>
    /// 尝试导入。返回结果中的 Message 直接面向用户展示，
    /// 无论成功失败都给出可执行的下一步建议。
    ///
    /// 注意：本方法内部可能发起网络请求（用会话换 Token），因此必须是异步的——
    /// 若用同步等待（.GetAwaiter().GetResult()）在 UI 线程上阻塞，
    /// 而续体又要回到 UI 线程，就会直接死锁。
    /// </summary>
    public static async Task<LocalImportResult> TryImportAsync()
    {
#if ANDROID
        await Task.CompletedTask;
        return new LocalImportResult
        {
            Ok = false,
            Message = "手机端无法读取桌面客户端数据，请使用「会话 Cookie 导入」或手动粘贴。",
        };
#else
        try
        {
            var candidates = CandidateDirs()
                .Where(Directory.Exists)
                .ToList();

            if (candidates.Count == 0)
            {
                return new LocalImportResult
                {
                    Ok = false,
                    Message = "未在本机找到 TRAE 客户端数据目录。请先在电脑上安装并登录 TRAE 客户端，或改用「打开浏览器登录 + 粘贴 Token」。",
                };
            }

            string? deviceId = null;
            string? token = null;
            string? session = null;
            string tokenSource = "";
            int scanned = 0;

            foreach (var dir in candidates)
            {
                // ---- 1) globalStorage/storage.json：设备号 + 可能的 JWT ----
                var storage = Path.Combine(dir, "User", "globalStorage", "storage.json");
                if (File.Exists(storage))
                {
                    scanned++;
                    var text = SafeReadText(storage);
                    deviceId ??= ExtractDeviceId(text);
                    if (token == null)
                    {
                        var hit = JwtPattern.Match(text);
                        if (hit.Success)
                        {
                            token = hit.Value;
                            tokenSource = Path.GetFileName(dir) + "\\storage.json";
                        }
                    }
                    session ??= ExtractSession(text);
                }

                // ---- 2) Local Storage/leveldb：JWT / 会话 主要来源 ----
                foreach (var root in new[] { "Local Storage\\leveldb", "Session Storage" })
                {
                    var ldb = Path.Combine(dir, root);
                    if (!Directory.Exists(ldb)) continue;
                    foreach (var file in SafeFiles(ldb))
                    {
                        if (scanned++ > MaxScanFiles) break;
                        var text = SafeReadText(file, MaxScanBytes);
                        if (text.Length == 0) continue;

                        if (token == null)
                        {
                            var hit = JwtPattern.Match(text);
                            if (hit.Success)
                            {
                                token = hit.Value;
                                tokenSource = Path.GetFileName(dir) + "\\" + Path.GetFileName(file);
                            }
                        }
                        session ??= ExtractSession(text);
                        if (token != null && session != null) break;
                    }
                }

                if (token != null) break;
            }

            if (token == null && session == null)
            {
                return new LocalImportResult
                {
                    Ok = false,
                    DeviceId = deviceId,
                    Message = deviceId == null
                        ? "已找到 TRAE 客户端目录，但未能读取到登录凭证（可能尚未登录，或客户端已更新存储格式）。请改用「会话 Cookie 导入」。"
                        : "已找到客户端数据（含设备号），但未读取到登录凭证。请确认客户端已登录，或改用「会话 Cookie 导入」。",
                };
            }

            // ---- 3) 只有会话时，用它换 Token ----
            string? finalToken = token;
            string note;
            if (finalToken == null && session != null)
            {
                var api = new TraeCheckin.TraeApiClient();
                finalToken = await api.GetUserTokenAsync(session);
                note = finalToken == null
                    ? "已读取到会话，但换取 Token 失败（会话可能已过期）"
                    : "已由客户端会话换取 Token";
            }
            else
            {
                note = "已从本机客户端读取 Token";
            }

            if (finalToken == null)
            {
                return new LocalImportResult
                {
                    Ok = false,
                    Session = session,
                    DeviceId = deviceId,
                    Message = note,
                };
            }

            return new LocalImportResult
            {
                Ok = true,
                Token = finalToken,
                Session = session,
                DeviceId = deviceId,
                Source = tokenSource,
                Message = $"{note}（来源：{tokenSource}）",
            };
        }
        catch (Exception ex)
        {
            return new LocalImportResult { Ok = false, Message = "读取本机客户端失败：" + ex.Message };
        }
#endif
    }

#if !ANDROID
    private static IEnumerable<string> SafeFiles(string dir)
    {
        try
        {
            return Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => SafeLength(f))
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    /// <summary>按 Latin-1 读取（逐字节映射，不丢信息），便于对二进制 leveldb 做文本匹配。</summary>
    private static string SafeReadText(string path, long maxBytes = MaxScanBytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var len = Math.Min(fs.Length, maxBytes);
            var buf = new byte[len];
            int read = 0;
            while (read < len)
            {
                int n = fs.Read(buf, read, (int)(len - read));
                if (n <= 0) break;
                read += n;
            }
            return Encoding.Latin1.GetString(buf, 0, read);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>从 storage.json 里取 Aha 设备号（复用与配置加载同源的解析口径）。</summary>
    private static string? ExtractDeviceId(string text)
    {
        try
        {
            var m = Regex.Match(text, @"""iCubeAuthInfo://icube-dc:(?<id>\d{8,})""");
            return m.Success ? m.Groups["id"].Value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>在文本中定位 X-Cloudide-Session 的值（leveldb 中的分隔符不固定，取其后首个长串）。</summary>
    private static string? ExtractSession(string text)
    {
        try
        {
            const string name = "X-Cloudide-Session";
            int idx = text.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            int i = idx + name.Length;
            // 跳过键值之间的分隔符
            while (i < text.Length && !IsSessionChar(text[i])) i++;
            int start = i;
            while (i < text.Length && IsSessionChar(text[i])) i++;
            var value = text.Substring(start, i - start);
            return value.Length >= 16 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>会话串的合法字符集（字母数字与常见 base64url 符号）。</summary>
    private static bool IsSessionChar(char c)
        => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == ':' || c == '+';
#endif
}
