using TraeTools.Services.Json;

namespace TraeSwitch.Services;

/// <summary>
/// 账号级独立数据目录隔离。
/// 每个账号一份完整客户端数据目录；启动客户端时通过 --user-data-dir 指定该目录。
/// 切换账号 = 结束进程 + 换目录启动，不再做白名单文件替换，
/// 因此 machineid / 认证缓存 / Cookie / IndexedDB 等全部天然隔离。
/// </summary>
public static class ProfileIsolation
{
    /// <summary>所有账号数据目录的根：%APPDATA%\TraeTools\switch\profiles。</summary>
    public static string ProfilesRoot => Path.Combine(TraeTools.Services.DataPaths.SwitchDir, "profiles");

    /// <summary>某账号的数据目录（目录内即客户端数据根，直接传 --user-data-dir）。</summary>
    public static string ProfileDir(string account) => Path.Combine(ProfilesRoot, VaultService.Sanitize(account));

    public static string ManifestPath(string account) => Path.Combine(ProfileDir(account), "profile-manifest.json");

    /// <summary>与登录态无关、且体积大的目录；建档复制时跳过，避免每个账号多占几百 MB。</summary>
    private static readonly string[] ExcludeDirs =
    [
        "Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache",
        "Crashpad", "logs", "blob_storage", "Service Worker", "Shared Dictionary",
        "VideoDecodeStats", "CachedData", "CachedConfigurations", "CachedExtensionVSIXs",
        "CachedProfilesData", "Backups", "ModularData", "monitor", "shared_proto_db",
        "conversation-tips", "ProjectWorkspaces", "solo-lite-default-workspace",
    ];

    internal sealed record ProfileManifest(string Account, string CreatedUtc, int Files, string MachineIdHash);

    /// <summary>
    /// 该账号是否已就绪：目录存在且客户端已把完整登录凭据写进去。
    /// 不再依赖 manifest —— 新流程是"建空目录 → 用户在客户端登录 → 客户端自己写登录态"，
    /// 登录完成即视为就绪，不需要额外的建档/复制动作。
    /// </summary>
    public static bool IsReady(string account)
    {
        var dir = ProfileDir(account);
        if (!Directory.Exists(dir)) return false;
        try { return AuthProbe.HasAuthCreds(AuthProbe.StoragePath(dir)) == true; }
        catch { return false; }
    }

    /// <summary>目录已建但还没登录（客户端还没写入完整凭据）。</summary>
    public static bool IsPending(string account)
    {
        var dir = ProfileDir(account);
        return Directory.Exists(dir) && !IsReady(account);
    }

    /// <summary>
    /// 为账号创建空数据目录（新流程第一步）。不复制任何登录态：
    /// 目录是全新的，客户端首次运行时会在其中生成独立的 machineid / aha，
    /// 用户在该窗口登录后，登录态自然落在这个目录里，天然隔离。
    /// </summary>
    public static void CreateEmptyProfile(string account)
    {
        if (string.IsNullOrWhiteSpace(account)) throw new ArgumentException("账号名不能为空", nameof(account));
        var dir = ProfileDir(account);
        if (Directory.Exists(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(ManifestPath(account),
            AppJsonIndented.Serialize(new ProfileManifest(account, DateTime.UtcNow.ToString("o"), 0, "")));
    }

    /// <summary>
    /// 从当前 live 数据根复制出一份独立档案，并重置设备指纹（machineid UUID + aha 认证缓存）。
    /// 复制前删除已有档案，语义是"以当前登录态重建该账号档案"。
    /// 返回复制的文件数。
    /// </summary>
    public static int CreateProfile(string account, string liveRoot)
    {
        if (string.IsNullOrWhiteSpace(account)) throw new ArgumentException("账号名不能为空", nameof(account));
        if (!Directory.Exists(liveRoot)) throw new DirectoryNotFoundException("客户端数据目录不存在：" + liveRoot);

        var dir = ProfileDir(account);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        int count = 0;
        CopyTree(liveRoot, dir, ref count);

        // 设备指纹隔离：每个账号独立 UUID；清空 aha 认证缓存，让客户端在本目录重新生成设备凭据。
        var mid = Path.Combine(dir, "machineid");
        var newId = Guid.NewGuid().ToString("D");
        File.WriteAllText(mid, newId);
        var aha = Path.Combine(dir, "aha");
        if (Directory.Exists(aha)) Directory.Delete(aha, recursive: true);
        Directory.CreateDirectory(aha);

        // 单实例锁不能跨目录复制，否则新实例误判已有进程在跑。
        var lockPath = Path.Combine(dir, "code.lock");
        if (File.Exists(lockPath)) File.Delete(lockPath);

        var manifest = new ProfileManifest(account, DateTime.UtcNow.ToString("o"), count,
            Sha256(newId));
        File.WriteAllText(ManifestPath(account), AppJsonIndented.Serialize(manifest));
        return count;
    }

    /// <summary>生成启动参数。Windows 直接传给 exe；macOS 需 open -a --args。</summary>
    public static string BuildLaunchArgument(string account)
        => "--user-data-dir=\"" + ProfileDir(account) + "\"";

    /// <summary>只读读取档案信息（用于界面展示）；无档案返回 null。</summary>
    public static (DateTime? CreatedLocal, int Files)? GetInfo(string account)
    {
        var path = ManifestPath(account);
        if (!File.Exists(path)) return null;
        try
        {
            var m = AppJsonIndented.Deserialize<ProfileManifest>(File.ReadAllText(path));
            if (m == null) return null;
            var ok = DateTime.TryParse(m.CreatedUtc, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var t);
            return (ok ? t.ToLocalTime() : (DateTime?)null, m.Files);
        }
        catch { return null; }
    }

    /// <summary>删除某账号的独立数据目录（用户主动删除账号时调用）。</summary>
    public static void DeleteProfile(string account)
    {
        var dir = ProfileDir(account);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private static void CopyTree(string src, string dst, ref int count)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(src))
        {
            var name = Path.GetFileName(entry);
            if (ExcludeDirs.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (name.Equals("code.lock", StringComparison.OrdinalIgnoreCase)) continue;

            if (Directory.Exists(entry))
            {
                var target = Path.Combine(dst, name);
                Directory.CreateDirectory(target);
                CopyTree(entry, target, ref count);
            }
            else if (File.Exists(entry))
            {
                var target = Path.Combine(dst, name);
                try
                {
                    File.Copy(entry, target, overwrite: true);
                    count++;
                }
                catch { /* 单文件被占用：跳过，其余继续；manifest 记录实际数量 */ }
            }
        }
    }

    private static string Sha256(string s)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
