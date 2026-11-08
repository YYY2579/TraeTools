using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TraeTools.Services.Json;
using TraeTools.Services.Security;

namespace TraeCheckin;

/// <summary>
/// 本地配置：存储 token、设备号、自动签到设置。
/// 保存到 %APPDATA%\TraeTools\config.json（macOS 上即 ~/Library/Application Support/TraeTools）。
/// 敏感字段（Token/Session/GitHubToken/DeviceId）以平台加密机制落盘，
/// 加载时解密回内存；旧版明文在首次加载时自动迁移加密。
/// </summary>
public class AppConfig
{
    /// <summary>
    /// 敏感字段加密版本标记："dpapi-v1"=已加密落盘；null=旧版明文（等待迁移）。
    /// 字面量保留 dpapi 命名以兼容线上配置，实际机制按平台分派（见 <see cref="SecretProtector"/>），
    /// 单个值究竟是 DPAPI 还是 AES-GCM 由密文自身的 "aesg:" 前缀判定，不依赖本标记。
    /// </summary>
    public string? SecretsVaultVersion { get; set; }
    public string? Token { get; set; }
    /// <summary>X-Cloudide-Session 会话 Cookie 值（约 14 天有效），用于 token 失效时静默换新。</summary>
    public string? Session { get; set; }
    public DateTime? TokenUpdatedAt { get; set; }
    /// <summary>Session（X-Cloudide-Session）最近一次写入时间；约 14 天有效，不因换 JWT 续期。</summary>
    public DateTime? SessionUpdatedAt { get; set; }
    /// <summary>GitHub OAuth 设备码授权得到的 access_token（用于云端自动签到部署）。</summary>
    public string? GitHubToken { get; set; }
    /// <summary>GitHub 授权后的登录用户名（fork 目标 owner）。</summary>
    public string? GitHubLogin { get; set; }
    /// <summary>飞书机器人 webhook（签到结果推送；为空则关闭推送）。</summary>
    public string? FeishuWebhook { get; set; }
    public string DeviceId { get; set; } = GenerateDeviceId();
    public bool AutoCheckinEnabled { get; set; } = true;
    public string AutoCheckinTime { get; set; } = "08:00";
    /// <summary>多账号签到间隔（秒），每个账号签到完成后等待该时长再签到下一个；默认 5 秒。</summary>
    public int CheckinIntervalSeconds { get; set; } = 5;
    public DateTime? LastCheckinDate { get; set; }
    /// <summary>
    /// 上次发出「到点仍未签到」告警的日期。用于每天只告警一次（重启程序也不会重复轰炸）。
    /// </summary>
    public DateTime? LastMissedAlertDate { get; set; }
    public double LastRemaining { get; set; } = -1;
    /// <summary>全部 Trae 账号（多账号模型主存储）。</summary>
    public List<TraeAccount> Accounts { get; set; } = new();
    /// <summary>仪表盘当前展示的账号 Id；为空时取 Accounts[0]。</summary>
    public string? ActiveAccountId { get; set; }
    /// <summary>主窗口上次关闭时的位置与尺寸（正常状态的边界），null/无效时用默认尺寸居中。</summary>
    public int? WindowLeft { get; set; }
    public int? WindowTop { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    /// <summary>是否已同意首次启动的用户协议（EULA）。同意后不再弹出。</summary>
    public bool EulaAccepted { get; set; }
    /// <summary>云端部署成功后是否已询问过用户「愿不愿意给源仓库点 star」。置 true 后不再打扰。</summary>
    public bool StarAskedAfterDeploy { get; set; }
    /// <summary>关闭主窗口时是否最小化到系统托盘（托盘驻留）。</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>
    /// 云端签到每日运行时间（北京时间 HH:mm）。部署/「应用到云端」时改写 fork 中
    /// checkin.yml 的 cron（换算为 UTC）。空或非法值按默认 08:00 处理。
    /// </summary>
    public string CloudCheckinTime { get; set; } = "08:00";

    /// <summary>
    /// 上次写入 GitHub Actions Secret 的账号凭据指纹（Session/DeviceId 的 SHA-256，不落明文）。
    /// 打开云端页面时若指纹与当前账号不一致，会自动把新凭据同步到云端，避免 Session 更新后云端仍用旧值。
    /// </summary>
    public string CloudSecretsHash { get; set; } = "";

    // ===== 最近一次已知的云端运行结果（用于「不实时检测、下次打开时提醒」）=====
    // 落盘原因：GitHub 接口不可达时（断网、代理故障）也能把上次的失败原因和修复建议展示出来，
    // 而不是让用户面对一个空白的部署状态区。

    /// <summary>最近一次已知 run 的 id（换 run 时才重新拉日志诊断，避免每次进页面都下载）。</summary>
    public long CloudLastRunId { get; set; }
    /// <summary>最近一次已知 run 的结论：success / failure / ""。</summary>
    public string CloudLastRunConclusion { get; set; } = "";
    /// <summary>最近一次已知 run 的完成时间（用于「昨天 08:12 的那次运行失败了」这类表述）。</summary>
    public DateTime? CloudLastRunAt { get; set; }
    /// <summary>失败原因（由运行日志归类得出，如「云端登录态已失效」）。</summary>
    public string CloudLastRunReason { get; set; } = "";
    /// <summary>对应的修复建议。</summary>
    public string CloudLastRunSuggestion { get; set; } = "";

    /// <summary>是否启用网络代理（签到/用量/GitHub 接口与登录浏览器统一走此代理）。</summary>
    public bool ProxyEnabled { get; set; }

    /// <summary>
    /// 代理地址：host:port（视为 http）、http://host:port、socks5://[user:pass@]host:port。
    /// 各账号可在 TraeAccount.Proxy 覆盖；空 = 跟随全局。
    /// </summary>
    public string ProxyServer { get; set; } = "";

    // ==================== 以下为本次 PR 追加的配置项 ====================
    // 注意：本类的落盘走 CloneForStorage 白名单（见该方法注释），
    // 因此**每新增一个属性都必须同步加进 CloneForStorage 与 UnsealSecrets**，
    // 否则该配置永远不会被写入 config.json（表现为「改完重启就失效」）。

    /// <summary>旧版深色开关（仅 true/false 二态）。保留用于旧配置迁移，新代码请改用 ThemeMode。</summary>
    public bool DarkMode { get; set; }

    /// <summary>主题模式："system"（跟随系统）/ "light" / "dark"。空字符串表示旧配置待迁移。</summary>
    public string ThemeMode { get; set; } = "";

    /// <summary>日志页是否自动滚动到最新一行。</summary>
    public bool LogAutoScroll { get; set; } = true;

    /// <summary>签到完成后是否触发推送（总开关，关闭则所有渠道都不发）。</summary>
    public bool NotifyOnCheckin { get; set; } = true;

    /// <summary>系统本地通知渠道。</summary>
    public bool NotifySystemEnabled { get; set; }

    /// <summary>自定义 SMTP 邮箱渠道。</summary>
    public bool EmailEnabled { get; set; }
    public string SmtpHost { get; set; } = "";
    /// <summary>SMTP 端口，默认 465（隐式 SSL）。常见取值：465=SSL、587=STARTTLS、25=明文。</summary>
    public int SmtpPort { get; set; } = 465;
    /// <summary>连接安全方式："ssl"（隐式 TLS）/ "starttls" / "none"。</summary>
    public string SmtpSecurity { get; set; } = "ssl";
    public string SmtpUser { get; set; } = "";
    /// <summary>SMTP 密码 / 授权码（加密落盘）。</summary>
    public string? SmtpPassword { get; set; }
    public string SmtpFrom { get; set; } = "";
    public string SmtpTo { get; set; } = "";

    /// <summary>通用 Webhook 渠道。</summary>
    public bool WebhookEnabled { get; set; }
    /// <summary>目标 URL（加密落盘：地址里常带 token 类查询参数）。</summary>
    public string? WebhookUrl { get; set; }
    /// <summary>请求体模板，支持 {title} / {content} 占位符。</summary>
    public string WebhookBodyTemplate { get; set; } = "{\"title\":\"{title}\",\"content\":\"{content}\"}";
    public string WebhookContentType { get; set; } = "application/json";

    /// <summary>钉钉群机器人。</summary>
    public bool DingTalkEnabled { get; set; }
    public string? DingTalkWebhook { get; set; }
    /// <summary>加签密钥（可选）。机器人安全设置选择「加签」时必填。</summary>
    public string? DingTalkSecret { get; set; }

    /// <summary>企业微信群机器人。</summary>
    public bool WeComEnabled { get; set; }
    public string? WeComWebhook { get; set; }

    /// <summary>Telegram Bot 推送。</summary>
    public bool TelegramEnabled { get; set; }
    /// <summary>Bot Token（加密落盘）。</summary>
    public string? TelegramBotToken { get; set; }
    /// <summary>目标会话 ID（私聊为数字 ID，群为负数）。</summary>
    public string TelegramChatId { get; set; } = "";

    private static string ConfigDir => TraeTools.Services.DataPaths.Root;

    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = AppJsonIndented.Deserialize<AppConfig>(json);
                if (cfg != null)
                {
                    // 加密迁移：已是 dpapi-v1 则解密回内存使用；旧明文标记版本，稍后统一加密落盘
                    var needSeal = cfg.SecretsVaultVersion != SecretVaultVersionV1;
                    bool secretsLost = UnsealSecrets(cfg);
                    cfg.SecretsVaultVersion = SecretVaultVersionV1;

                    // 有密文解不开时先把原文件另存一份，并跳过本次所有自动保存：
                    // 此刻内存里的凭据全是 null，任何一次落盘都会把「在另一台机器上
                    // 还能用」的密文永久覆盖掉。留一份原件，用户回到原机器仍可恢复。
                    if (secretsLost) PreserveUnreadableConfig();

                    // 多账号迁移：旧单账号转成 Accounts[0]
                    if (TryMigrateLegacy(cfg))
                    {
                        // 迁移后清空旧字段，避免双份数据源（DeviceId 已复制进新账号）
                        cfg.Token = null;
                        cfg.Session = null;
                        cfg.TokenUpdatedAt = null;
                        cfg.LastCheckinDate = null;
                        if (string.IsNullOrEmpty(cfg.ActiveAccountId))
                            cfg.ActiveAccountId = cfg.Accounts[0].Id;
                        if (!secretsLost) cfg.Save();
                    }
                    // 迁移：优先复用官方客户端的真实 Aha 设备 ID，否则签到接口风控会返回 9074
                    var aha = TryResolveAhaDeviceId();
                    if (aha != null && aha != cfg.DeviceId)
                    {
                        cfg.DeviceId = aha;
                        if (!secretsLost) cfg.Save();
                    }
                    // 旧明文首次加载：立即加密落盘，避免明文长期留存
                    // 主题模式迁移：旧配置只有 DarkMode 布尔值，此处补齐三态 ThemeMode
                    bool themeMigrated = cfg.NormalizeTheme();
                    if ((needSeal || themeMigrated) && !secretsLost) cfg.Save();
                    return cfg;
                }
            }
        }
        catch { /* 配置损坏时回退默认 */ }

        var fresh = new AppConfig();
        fresh.NormalizeTheme();
        var resolved = TryResolveAhaDeviceId();
        if (resolved != null) fresh.DeviceId = resolved;
        return fresh;
    }

    /// <summary>
    /// 单账号 → 多账号迁移：当 Accounts 为空且存在旧版 Token/Session 时，
    /// 生成第一个账号并放入 Accounts。返回是否发生迁移。幂等（已有账号则不动）。
    /// </summary>
    public static bool TryMigrateLegacy(AppConfig cfg)
    {
        if (cfg.Accounts.Count > 0) return false;
        if (string.IsNullOrEmpty(cfg.Token) && string.IsNullOrEmpty(cfg.Session)) return false;

        cfg.Accounts.Add(new TraeAccount
        {
            Token = cfg.Token,
            Session = cfg.Session,
            DeviceId = string.IsNullOrEmpty(cfg.DeviceId) ? "" : cfg.DeviceId,
            TokenUpdatedAt = cfg.TokenUpdatedAt,
            SessionUpdatedAt = cfg.SessionUpdatedAt,
            LastCheckinDate = cfg.LastCheckinDate
        });
        return true;
    }

    /// <summary>
    /// 生成一个 16 位十进制设备 ID（与 TraeWork Aha SDK 的设备号格式一致）。
    /// 签到接口风控要求 x-device-id 为数字设备号，使用 GUID/UUID 会触发 9074。
    /// </summary>
    private static string GenerateDeviceId()
    {
        return Random.Shared.NextInt64(1_000_000_000_000_000L, 10_000_000_000_000_000L).ToString();
    }

    /// <summary>
    /// 从本机 TraeWork 官方客户端的数据目录解析 Aha 设备 ID（16 位数字）。
    /// storage.json 中存在形如 "iCubeAuthInfo://icube-dc:1234567890123456" 的键。
    /// </summary>
    internal static string? TryResolveAhaDeviceId()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        // 顺序重要：现役客户端 Trae CN 必须排在最前。融合迁移后旧目录（TRAE SOLO CN / TRAE SOLO）
        // 可能仍残留 storage.json 且带着**失效的旧设备号**，先读旧目录会把过期设备号当成当前设备。
        var dirs = new[] { "Trae CN", "TRAE SOLO CN", "TRAE SOLO" };
        foreach (var dir in dirs)
        {
            var path = Path.Combine(appData, dir, "User", "globalStorage", "storage.json");
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    const string prefix = "iCubeAuthInfo://icube-dc:";
                    if (!prop.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var id = prop.Name.Substring(prefix.Length);
                    if (id.Length >= 8 && id.All(char.IsDigit)) return id;
                }
            }
            catch { /* 忽略读取/解析失败，继续尝试其他目录 */ }
        }
        return null;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            // 写「加密副本」而非 this：敏感字段加密落盘，且不修改内存中的明文（各调用方无感）
            var json = AppJsonIndented.Serialize(CloneForStorage());
            File.WriteAllText(ConfigPath, json);
        }
        catch { /* 保存失败（含 DPAPI 异常）时保留磁盘旧文件，下次启动仍可读取 */ }
    }

    /// <summary>敏感字段加密版本号。</summary>
    private const string SecretVaultVersionV1 = "dpapi-v1";

    /// <summary>
    /// 主题模式规整：把旧版仅有的 <see cref="DarkMode"/> 布尔值迁移为三态字符串；
    /// 取值非法时回退 "system"。返回是否发生了修正（调用方据此决定是否落盘）。
    /// </summary>
    public bool NormalizeTheme()
    {
        var normalized = ThemeMode switch
        {
            "system" or "light" or "dark" => ThemeMode,
            _ => DarkMode ? "dark" : "system",
        };
        if (normalized == ThemeMode) return false;
        ThemeMode = normalized;
        return true;
    }

    /// <summary>
    /// 生成一份敏感字段已 DPAPI 加密的副本。
    /// 敏感字段：<c>Token</c> / <c>Session</c> / <c>GitHubToken</c> / <c>DeviceId</c>，
    /// 以及本次 PR 追加的推送渠道凭证 <c>SmtpPassword</c> / <c>WebhookUrl</c> /
    /// <c>DingTalkWebhook</c> / <c>DingTalkSecret</c> / <c>WeComWebhook</c> / <c>TelegramBotToken</c>
    /// （这些 URL / 令牌本身就是凭据，明文落盘等于泄露发送权限）。
    /// </summary>
    private AppConfig CloneForStorage()
    {
        return new AppConfig
        {
            SecretsVaultVersion = SecretVaultVersionV1,
            Token = EncryptSecret(Token),
            Session = EncryptSecret(Session),
            TokenUpdatedAt = TokenUpdatedAt,
            SessionUpdatedAt = SessionUpdatedAt,
            GitHubToken = EncryptSecret(GitHubToken),
            GitHubLogin = GitHubLogin,
            FeishuWebhook = FeishuWebhook,
            // DeviceId 非可空：加密失败时退回明文，而不是写入 null。
            // 明文 DeviceId 在下次加载解密失败后会被原样保留，属可自愈的降级，比落盘 null 安全。
            DeviceId = EncryptSecret(DeviceId) ?? DeviceId,
            AutoCheckinEnabled = AutoCheckinEnabled,
            AutoCheckinTime = AutoCheckinTime,
            CheckinIntervalSeconds = CheckinIntervalSeconds,
            LastCheckinDate = LastCheckinDate,
            LastMissedAlertDate = LastMissedAlertDate,
            LastRemaining = LastRemaining,
            ActiveAccountId = ActiveAccountId,
            WindowLeft = WindowLeft,
            WindowTop = WindowTop,
            WindowWidth = WindowWidth,
            WindowHeight = WindowHeight,
            EulaAccepted = EulaAccepted,
            StarAskedAfterDeploy = StarAskedAfterDeploy,
            MinimizeToTray = MinimizeToTray,
            CloudCheckinTime = CloudCheckinTime,
            CloudSecretsHash = CloudSecretsHash,
            CloudLastRunId = CloudLastRunId,
            CloudLastRunConclusion = CloudLastRunConclusion,
            CloudLastRunAt = CloudLastRunAt,
            CloudLastRunReason = CloudLastRunReason,
            CloudLastRunSuggestion = CloudLastRunSuggestion,
            ProxyEnabled = ProxyEnabled,
            ProxyServer = ProxyServer,
            // ===== 本次 PR 追加项 =====
            DarkMode = DarkMode,
            ThemeMode = ThemeMode,
            LogAutoScroll = LogAutoScroll,
            NotifyOnCheckin = NotifyOnCheckin,
            NotifySystemEnabled = NotifySystemEnabled,
            EmailEnabled = EmailEnabled,
            SmtpHost = SmtpHost,
            SmtpPort = SmtpPort,
            SmtpSecurity = SmtpSecurity,
            SmtpUser = SmtpUser,
            SmtpPassword = EncryptSecret(SmtpPassword),
            SmtpFrom = SmtpFrom,
            SmtpTo = SmtpTo,
            WebhookEnabled = WebhookEnabled,
            WebhookUrl = EncryptSecret(WebhookUrl),
            WebhookBodyTemplate = WebhookBodyTemplate,
            WebhookContentType = WebhookContentType,
            DingTalkEnabled = DingTalkEnabled,
            DingTalkWebhook = EncryptSecret(DingTalkWebhook),
            DingTalkSecret = EncryptSecret(DingTalkSecret),
            WeComEnabled = WeComEnabled,
            WeComWebhook = EncryptSecret(WeComWebhook),
            TelegramEnabled = TelegramEnabled,
            TelegramBotToken = EncryptSecret(TelegramBotToken),
            TelegramChatId = TelegramChatId,
            Accounts = Accounts.Select(a => new TraeAccount
            {
                Id = a.Id,
                Name = a.Name,
                Token = EncryptSecret(a.Token),
                Session = EncryptSecret(a.Session),
                // 同 DeviceId：非可空，加密失败时保留明文而不是赋 null
                DeviceId = EncryptSecret(a.DeviceId) ?? a.DeviceId,
                AccountUid = a.AccountUid,
                TokenUpdatedAt = a.TokenUpdatedAt,
                SessionUpdatedAt = a.SessionUpdatedAt,
                LastCheckinDate = a.LastCheckinDate,
                RemainingCredits = a.RemainingCredits,
                Enabled = a.Enabled,
                IsMember = a.IsMember,
                Proxy = a.Proxy,
                ScreenName = a.ScreenName,
                MobileMasked = a.MobileMasked,
                AvatarUrl = a.AvatarUrl,
                IsStudent = a.IsStudent,
            }).ToList(),
        };
    }

    /// <summary>
    /// 密文解不开时把原配置另存一份，供用户回到原机器（或找回原用户账户）后恢复。
    /// 已存在备份就不覆盖 —— 保留最早那份，避免后续被更差的状态冲掉。
    /// </summary>
    private static void PreserveUnreadableConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var dir = Path.GetDirectoryName(ConfigPath)!;
            if (Directory.GetFiles(dir, "config.json.unreadable-*").Length > 0) return;
            var dst = Path.Combine(dir,
                $"config.json.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}");
            File.Copy(ConfigPath, dst, overwrite: false);
        }
        catch { /* 备份失败不能阻塞启动 */ }
    }

    /// <summary>
    /// 已加密配置加载后：解密敏感字段回内存（仅 dpapi-v1 生效；旧明文保持原样等迁移）。
    /// 返回 true 表示**有密文解不开**（换了 Windows 用户、重装系统，或把 Windows 的
    /// config.json 拷到了 macOS —— 两边加密机制不同且都只在本机可解）。
    /// </summary>
    private static bool UnsealSecrets(AppConfig cfg)
    {
        if (cfg.SecretsVaultVersion != SecretVaultVersionV1) return false;
        bool lost = false;

        // 解不开而原值又非空 → 记为丢失。此时内存里是 null，一旦落盘就把原本在
        // 另一台机器上还能用的凭据永久抹掉，所以调用方要据此避免自动保存。
        string? Dec(string? stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored;
            var v = DecryptSecret(stored);
            if (v is null) lost = true;
            return v;
        }

        cfg.Token = Dec(cfg.Token);
        cfg.Session = Dec(cfg.Session);
        cfg.GitHubToken = Dec(cfg.GitHubToken);
        // 这些敏感字段是新增字段，历史上从未以明文落盘，
        // 因此不需要像 FeishuWebhook 那样做「解密失败保留原值」的兼容处理。
        cfg.SmtpPassword = Dec(cfg.SmtpPassword);
        cfg.WebhookUrl = Dec(cfg.WebhookUrl);
        cfg.DingTalkWebhook = Dec(cfg.DingTalkWebhook);
        cfg.DingTalkSecret = Dec(cfg.DingTalkSecret);
        cfg.WeComWebhook = Dec(cfg.WeComWebhook);
        cfg.TelegramBotToken = Dec(cfg.TelegramBotToken);
        var dev = DecryptSecret(cfg.DeviceId);
        if (!string.IsNullOrEmpty(dev)) cfg.DeviceId = dev; // 解密失败保留原值，等重新登录刷新
        else if (!string.IsNullOrEmpty(cfg.DeviceId)) lost = true;
        foreach (var a in cfg.Accounts)
        {
            a.Token = Dec(a.Token);
            a.Session = Dec(a.Session);
            var aDev = DecryptSecret(a.DeviceId);
            if (!string.IsNullOrEmpty(aDev)) a.DeviceId = aDev;
            else if (!string.IsNullOrEmpty(a.DeviceId)) lost = true;
        }
        return lost;
    }

    /// <summary>
    /// 落盘加密。保护失败抛异常，由调用方兜底（保留旧文件）。
    /// 平台分派见 <see cref="SecretProtector"/>：Windows 走 DPAPI，其余走 AES-GCM。
    /// </summary>
    private static string? EncryptSecret(string? plain)
        => string.IsNullOrEmpty(plain) ? plain : SecretProtector.Protect(plain);

    /// <summary>解密；失败（换机/换用户/密钥丢失）返回 null，触发重新登录。</summary>
    private static string? DecryptSecret(string? enc)
        => SecretProtector.Unprotect(enc);
}