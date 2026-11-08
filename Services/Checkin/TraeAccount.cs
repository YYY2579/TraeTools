namespace TraeCheckin;

/// <summary>
/// 一个 Trae 账号。每个账号独立 Token(JWT)/Session(Cookie)/DeviceId，
/// 本地自动签到遍历所有 Enabled 账号，云端部署按顺序写入独立 secret。
/// </summary>
public class TraeAccount
{
    /// <summary>账号唯一标识（也用于隔离 WebView2 用户数据目录）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>用户备注名（如「主号 / 小号」）；为空时 UI 显示「账号 n」。</summary>
    public string? Name { get; set; }
    /// <summary>JWT（约 8 小时，失效时用 Session 静默换新）。</summary>
    public string? Token { get; set; }
    /// <summary>X-Cloudide-Session 会话 Cookie 值（约 14 天）。</summary>
    public string? Session { get; set; }
    /// <summary>x-device-id（16 位数字，风控关键），每账号独立。</summary>
    public string DeviceId { get; set; } = "";
    /// <summary>
    /// 账号唯一标识（JWT payload data.id，16 位数字账号 ID，跨登录会话恒定）。
    /// 用于识别"同一手机号账号"重复添加；为 null 表示尚未解析（旧数据/未登录）。
    /// </summary>
    public string? AccountUid { get; set; }
    public DateTime? TokenUpdatedAt { get; set; }
    /// <summary>
    /// Session（X-Cloudide-Session）最近一次写入时间。
    /// Session 约 14 天有效且**不会**因换取新 JWT 而续期，云端凭证过期判断以此为准
    /// （旧配置无此字段时回退 TokenUpdatedAt 估算）。
    /// </summary>
    public DateTime? SessionUpdatedAt { get; set; }
    /// <summary>该账号最近一次本地签到日期。</summary>
    public DateTime? LastCheckinDate { get; set; }
    /// <summary>该账号最近一次刷新/签到的剩余积分；-1 表示尚未获取过（仪表盘回退用全局 LastRemaining）。</summary>
    public double RemainingCredits { get; set; } = -1;
    /// <summary>是否参与本地自动签到与云端部署。</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// 是否会员：非会员每日签到只获得基础 credits（实测 150），
    /// extra_credits（连签 +50）仅会员到账。默认 false。
    /// </summary>
    public bool IsMember { get; set; }

    /// <summary>
    /// 本账号专属代理（host:port / http://… / socks5://…）。
    /// 空 = 跟随全局设置（AppConfig.ProxyEnabled/ProxyServer）。
    /// 用于多账号出口 IP 隔离：为不同账号配不同代理即可分网签到。
    /// </summary>
    public string Proxy { get; set; } = "";

    // ---- 账号资料（GetUserInfo / student_verification 逆向接口回填）----
    /// <summary>平台昵称（ScreenName）。Name 为空时优先用它展示。</summary>
    public string? ScreenName { get; set; }
    /// <summary>脱敏手机号（如 186******12）。</summary>
    public string? MobileMasked { get; set; }
    /// <summary>头像 URL。</summary>
    public string? AvatarUrl { get; set; }
    /// <summary>是否学生认证（student_verification.status==1）。</summary>
    public bool IsStudent { get; set; }
}
