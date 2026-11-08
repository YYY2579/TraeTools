using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TraeTools.Services.Json;

namespace TraeCheckin;

/// <summary>设备码授权请求返回的 code 信息（snake_case 字段映射）。</summary>
public class GitHubDeviceCode
{
    [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = "";
    [JsonPropertyName("user_code")] public string UserCode { get; set; } = "";
    [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = "";
    [JsonPropertyName("interval")] public int Interval { get; set; } = 5;
}

/// <summary>设备码授权轮询结果。</summary>
public enum DeviceAuthState
{
    Pending,   // 用户尚未在网页完成授权，继续轮询
    Success,   // 已拿到 access_token
    Failed     // 授权被拒绝或过期
}

/// <summary>云端部署状态检测结果。</summary>
public class DeploymentStatus
{
    /// <summary>access_token 是否仍有效。</summary>
    public bool IsAuthorized { get; set; } = true;
    /// <summary>fork 仓库是否已存在。</summary>
    public bool IsForked { get; set; }
    /// <summary>TRAE_SESSION 与 TRAE_DEVICE_ID 两个 secret 是否都已写入。</summary>
    public bool HasSecrets { get; set; }
    /// <summary>已部署（session+device 成对存在）的连续账号数；0 表示尚未写入。</summary>
    public int DeployedAccountCount { get; set; }
    /// <summary>checkin.yml 定时 workflow 是否已启用（state=active）。</summary>
    public bool IsWorkflowEnabled { get; set; }
    /// <summary>是否已完成一次完整部署（fork + secrets + workflow 全部就绪）。</summary>
    public bool IsDeployed => IsForked && HasSecrets && IsWorkflowEnabled;
}

/// <summary>手动粘贴 Token（PAT）的校验结果。</summary>
public class PatValidation
{
    public bool IsValid { get; set; }
    public string? Login { get; set; }
    public bool CanWrite { get; set; }
    public string? Error { get; set; }
}

/// <summary>最近一次 workflow run 的简要信息（用于本地监控云端签到状态）。</summary>
public class WorkflowRunInfo
{
    /// <summary>run id（拉取该次运行日志诊断失败原因时用）。</summary>
    public long Id { get; set; }
    /// <summary>运行结论：success / failure / null（尚未完成）。</summary>
    public string? Conclusion { get; set; }
    /// <summary>运行状态：completed / in_progress / queued 等。</summary>
    public string? Status { get; set; }
    /// <summary>创建时间（ISO8601）。</summary>
    public string? CreatedAt { get; set; }
    /// <summary>运行编号。</summary>
    public long RunNumber { get; set; }
    /// <summary>运行详情页地址。</summary>
    public string? HtmlUrl { get; set; }
}

/// <summary>
/// 云端运行失败的诊断结论：一句话原因 + 可执行的修复建议。
/// 由 <see cref="GitHubApiClient.DiagnoseRunFailureAsync"/> 依据 run 日志归类产生。
/// </summary>
public class CloudFailureDiagnosis
{
    /// <summary>分类键：secret / session / risk / github / account / unknown。</summary>
    public string Key { get; set; } = "unknown";
    public string Reason { get; set; } = "";
    public string Suggestion { get; set; } = "";

    public static CloudFailureDiagnosis Unknown(string reason) => new()
    {
        Key = "unknown",
        Reason = reason,
        Suggestion = "点「打开 Actions」查看该次运行日志；常见原因是 Session 过期或命中风控，可先重新登录再重新部署。",
    };
}

/// <summary>
/// GitHub API 客户端：封装设备码授权（Device Flow）与云端自动签到部署所需的
/// fork / 写 secret / 启用 workflow / 触发 workflow 等 REST 接口。
/// </summary>
public class GitHubApiClient
{
    private const string ClientId = "Ov23lix0Kb9ldJHrOpKv";
    // 二开源仓库：fork / 部署 / 更新检查全部指向本 fork（用户以此仓库为上游继续分发）
    private const string SourceOwner = "YYY2579";
    private const string SourceRepo = "TraeTools";
    public const string SessionSecretName = "TRAE_SESSION";
    public const string DeviceIdSecretName = "TRAE_DEVICE_ID";
    public const string FeishuWebhookSecretName = "FEISHU_WEBHOOK";

    /// <summary>第 index 个（从 1 起）账号的 Session secret 名；1 → TRAE_SESSION，N → TRAE_SESSION_N。</summary>
    public static string SessionSecretNameFor(int index)
        => index <= 1 ? SessionSecretName : "TRAE_SESSION_" + index;

    /// <summary>第 index 个（从 1 起）账号的 DeviceId secret 名；1 → TRAE_DEVICE_ID，N → TRAE_DEVICE_ID_N。</summary>
    public static string DeviceSecretNameFor(int index)
        => index <= 1 ? DeviceIdSecretName : "TRAE_DEVICE_ID_" + index;

    private const string WorkflowPath = ".github/workflows/checkin.yml";

    private readonly HttpClient _http;

    public string? LastError { get; private set; }

    public GitHubApiClient() : this(TraeTools.Services.NetProxy.GetClient(TraeTools.Services.NetProxy.Global)) { }

    /// <summary>测试专用：注入可拦截的 HttpClient。</summary>
    internal GitHubApiClient(HttpClient http)
    {
        _http = http;
        // GitHub API 对缺 User-Agent 的匿名请求返回 403；版本号从程序集动态取，避免升级后残旧
        var ver = typeof(GitHubApiClient).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TraeCheckin/" + ver);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    // ---------- 设备码授权 ----------

    /// <summary>向 GitHub 申请设备码，返回需要展示给用户的 user_code 与验证地址。</summary>
    public async Task<GitHubDeviceCode?> RequestDeviceCodeAsync()
    {
        try
        {
            var body = AppJson.Serialize(new GitHubDeviceCodeRequest { ClientId = ClientId, Scope = "repo workflow" });
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"申请设备码失败：HTTP {(int)resp.StatusCode} {json}";
                return null;
            }
            return AppJson.Deserialize<GitHubDeviceCode>(json);
        }
        catch (Exception ex) { LastError = ex.Message; return null; }
    }

    /// <summary>轮询设备码授权结果。Pending 表示继续等待，Success 返回 token，Failed 表示已失败。</summary>
    public async Task<(DeviceAuthState State, string? Token)> PollForAccessTokenAsync(string deviceCode)
    {
        try
        {
            var body = AppJson.Serialize(new GitHubTokenPollRequest
            {
                ClientId = ClientId,
                DeviceCode = deviceCode,
                GrantType = "urn:ietf:params:oauth:grant-type:device_code"
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("access_token", out var tok) && tok.ValueKind == JsonValueKind.String)
                return (DeviceAuthState.Success, tok.GetString());

            var err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
            if (err == "authorization_pending" || err == "slow_down") return (DeviceAuthState.Pending, null);
            LastError = "授权失败：" + err;
            return (DeviceAuthState.Failed, null);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return (DeviceAuthState.Failed, null);
        }
    }

    /// <summary>用 access_token 获取当前登录用户名。</summary>
    public async Task<string?> GetLoginAsync(string token)
    {
        using var resp = await SendApiAsync(HttpMethod.Get, "/user", token);
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            LastError = $"获取用户信息失败：HTTP {(int)resp.StatusCode} {json}";
            return null;
        }
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("login", out var login) ? login.GetString() : null;
    }

    // ---------- 部署流程 ----------

    /// <summary>fork 源仓库到当前用户账号，并轮询直到 fork 完成。</summary>
    public async Task<bool> ForkAsync(string token, string login)
    {
        // 源仓库 owner 自己部署时无需 fork（GitHub 不允许 fork 自己的仓库）
        if (string.Equals(login, SourceOwner, StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            using var resp = await SendApiAsync(HttpMethod.Post, $"/repos/{SourceOwner}/{SourceRepo}/forks", token);
            if (resp.StatusCode != System.Net.HttpStatusCode.Accepted && !resp.IsSuccessStatusCode)
            {
                // 401/403 提示要针对「建 fork」本身：fork 靠的既不是 classic repo scope（POST /forks
                // 的 X-Accepted-Oauth-Scopes 为空），也非 fork 之后写 secret/Actions 用的那批权限——
                // 之前把 BuildPatScopeHint 里那 5 项（Actions/Contents/Secrets/Workflows）当成解法，
                // 是 #26 指出的方向性错误（那批权限不作用于建 fork，照单给齐仍 403）。
                int code = (int)resp.StatusCode;
                LastError = code == 401 || code == 403
                    ? BuildForkError(code)
                    : $"fork 失败：HTTP {code}";
                return false;
            }
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(2000);
                using var check = await SendApiAsync(HttpMethod.Get, $"/repos/{login}/{SourceRepo}", token);
                if (check.IsSuccessStatusCode) return true;
            }
            LastError = "fork 超时未完成";
            return false;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    /// <summary>检测用户是否已 fork 过源仓库。返回 HTTP 状态码；-1=调用异常。</summary>
    public async Task<int> CheckForkAsync(string token, string login)
    {
        try
        {
            using var resp = await SendApiAsync(HttpMethod.Get, $"/repos/{login}/{SourceRepo}", token);
            // 401/403 时把具体原因写进 LastError，否则上层 HandleDeployFailure 读到空/残留错误
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                LastError = BuildForkError((int)resp.StatusCode);
            }
            return (int)resp.StatusCode;   // 200=已存在 404=不存在 401/403=鉴权问题
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return -1;
        }
    }

    /// <summary>删除用户自己的 fork 仓库（重建前清理）。owner 本人无 fork 可删，直接成功；404 视为已不存在。</summary>
    public async Task<bool> DeleteForkAsync(string token, string login)
    {
        if (string.Equals(login, SourceOwner, StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            using var resp = await SendApiAsync(HttpMethod.Delete, $"/repos/{login}/{SourceRepo}", token);
            if (resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return true;
            LastError = $"删除 fork 失败：HTTP {(int)resp.StatusCode}";
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>401/403 时面向小白的根因诊断文案（部署失败时提示到日志区）。</summary>
    public static string BuildAuthFailureHint(int statusCode)
        => statusCode == 401
            ? "GitHub 授权已失效或权限不足（HTTP 401）：请先点击「重新授权」；若仍失败，请到 GitHub 网页删除旧的 TraeTools fork 仓库后重试。"
            : statusCode == 403
                ? "GitHub 权限不足（HTTP 403）：设备码授权时请允许 repo 权限；若使用 PAT 请按提示补足 Workflows/Secrets/Contents 写权限后重新授权。"
                : $"HTTP {statusCode}";

    /// <summary>
    /// 「建 fork」接口（POST /forks）的 401/403 诊断文案。
    /// 与 BuildAuthFailureHint 的区别：fork 失败时不能指向 fork 之后写 secret/Actions 用的
    /// Actions/Contents/Secrets/Workflows 那批权限——它们不作用于建 fork，给了也白给（#26 根因）。
    /// 建 fork 需要的是对源仓库的 fork 能力：细粒度 PAT 需在 Repository access 选中源仓库
    /// 并开启 Administration: Read and write（含 fork），classic PAT/oAuth 需含 repo scope。
    /// </summary>
    public static string BuildForkError(int statusCode)
        => statusCode == 401
            ? "fork 失败：GitHub 授权已失效（HTTP 401），请先「重新授权」再部署。"
            : (statusCode == 403
                ? "fork 失败：Token 对源仓库没有 fork 权限（HTTP 403）。建 fork 不靠 Actions/Secrets/Contents 那批写权限——"
                  + $"请改为：① 细粒度 PAT → Repository access 选中 {SourceOwner}/{SourceRepo}（若已 fork 另加你自己的 fork 仓库），"
                  + "并在 Permissions 开启 Administration: Read and write（其中含 fork）；"
                  + "② classic PAT / 设备码授权 → 需包含 repo scope。按此调整后需重新生成 Token/重新授权。"
                : $"fork 失败：HTTP {statusCode}");

    /// <summary>
    /// 给源仓库点 star。仅在云端部署成功、用户弹窗确认愿意支持时调用，不再自动点赞。
    /// 204 表示成功；owner 本人直接返回 true，失败返回 false（不影响部署流程）。
    /// </summary>
    public async Task<bool> StarSourceRepoAsync(string token, string login)
    {
        if (ShouldSkipStar(login)) return true;
        try
        {
            using var resp = await SendApiAsync(HttpMethod.Put, $"/user/starred/{SourceOwner}/{SourceRepo}", token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>是否应跳过给源仓库点星（仓库 owner 本人给自己点星无意义）。</summary>
    public static bool ShouldSkipStar(string login)
        => !string.IsNullOrEmpty(login) && string.Equals(login, SourceOwner, StringComparison.OrdinalIgnoreCase);

    /// <summary>workflow 相关错误文案；409 表示仓库未开启 Actions，附解决指引。</summary>
    public static string BuildWorkflowError(int statusCode)
        => statusCode == 409
            ? "GitHub 仓库未开启 Actions：请到仓库 Settings → Actions → General 勾选 Allow owner actions and reusable workflows（或 Allow all actions）后重试"
            : $"HTTP {statusCode}";

    /// <summary>预检：是否像是一段可用的 GitHub token（PAT 或 OAuth token 均放行）。</summary>
    public static bool IsPlausibleToken(string? token)
        => !string.IsNullOrWhiteSpace(token) && token.Trim().Length >= 20;

    /// <summary>解析 GET /repos/{owner}/{repo} 响应中的 permissions.push（无写权限返回 false）。</summary>
    public static bool ParsePermissionsPush(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return ParsePermissionsPush(doc.RootElement);
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    /// <summary>解析 permissions.push 的 JsonElement 版本。</summary>
    public static bool ParsePermissionsPush(System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("permissions", out var perms) || perms.ValueKind != System.Text.Json.JsonValueKind.Object)
            return false;
        return perms.TryGetProperty("push", out var push) && push.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    /// <summary>PAT 对目标仓库写权限不足时的指引文案。</summary>
    public static string BuildPatScopeHint()
        => "该 Token 对目标仓库没有写权限。请确认：1) Repository access 已包含 TraeTools"
           + "（若已 fork 还需包含你自己的 fork 仓库）；"
           + "2) Permissions 中 Actions / Contents / Pull requests / Secrets / Workflows 均为 Read and write，"
           + "Metadata 为只读（自动带出）。修改后需重新生成 Token。";

    /// <summary>用仓库公开密钥加密后，把 secret 写入仓库的指定 Actions secret。</summary>
    public async Task<bool> SetSecretAsync(string token, string login, string secretName, string secretValue)
    {
        try
        {
            using var pkResp = await SendApiAsync(HttpMethod.Get, $"/repos/{login}/{SourceRepo}/actions/secrets/public-key", token);
            var pkJson = await pkResp.Content.ReadAsStringAsync();
            if (!pkResp.IsSuccessStatusCode)
            {
                LastError = pkResp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "GitHub 授权已失效，请重新授权"
                    : $"获取公开密钥失败：HTTP {(int)pkResp.StatusCode}";
                return false;
            }
            using var doc = JsonDocument.Parse(pkJson);
            string? key = doc.RootElement.TryGetProperty("key", out var k) ? k.GetString() : null;
            string? keyId = doc.RootElement.TryGetProperty("key_id", out var id) ? id.GetString() : null;
            if (key == null || keyId == null)
            {
                LastError = "公开密钥响应缺少 key/key_id";
                return false;
            }

            var encrypted = GitHubSecret.Encrypt(secretValue, key);
            var body = AppJson.Serialize(new GitHubSecretPayload { EncryptedValue = encrypted, KeyId = keyId });
            using var resp = await SendApiAsync(HttpMethod.Put, $"/repos/{login}/{SourceRepo}/actions/secrets/{secretName}", token, body);
            if (resp.IsSuccessStatusCode) return true;
            LastError = $"写入 secret 失败：HTTP {(int)resp.StatusCode}";
            return false;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    /// <summary>查找 checkin.yml 对应的 workflow id。</summary>
    public async Task<long> GetWorkflowIdAsync(string token, string login)
    {
        using var resp = await SendApiAsync(HttpMethod.Get, $"/repos/{login}/{SourceRepo}/actions/workflows", token);
        if (!resp.IsSuccessStatusCode)
        {
            // 409 表示仓库未开启 Actions，附带解决指引
            LastError = BuildWorkflowError((int)resp.StatusCode);
            return -1;
        }
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("workflows", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var wf in arr.EnumerateArray())
            {
                if (wf.TryGetProperty("path", out var p) && p.GetString() == WorkflowPath &&
                    wf.TryGetProperty("id", out var id) && id.TryGetInt64(out var v))
                    return v;
            }
        }
        LastError = "未找到 workflow：" + WorkflowPath;
        return -1;
    }

    /// <summary>
    /// 开启仓库级 GitHub Actions（等价于 Settings → Actions 的启用开关）。
    /// 公共仓库被 fork 后定时 workflow 默认禁用，GitHub 不会登记 .github/workflows/checkin.yml，
    /// 导致 workflow 列表为空；须先在 fork 上启用 Actions 才会登记。
    /// </summary>
    public async Task<bool> EnsureActionsEnabledAsync(string token, string login)
    {
        try
        {
            var body = AppJson.Serialize(new GitHubActionsPermissionPayload());
            using var resp = await SendApiAsync(HttpMethod.Put, $"/repos/{login}/{SourceRepo}/actions/permissions", token, body);
            if (resp.IsSuccessStatusCode) return true;
            LastError = "开启 GitHub Actions 失败：HTTP " + (int)resp.StatusCode
                + "（也可让用户到 fork 的 Settings → Actions → General 手动开启后重试）";
            return false;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    /// <summary>启用 workflow（fork 后定时任务默认禁用，需手动启用）。</summary>
    public async Task<bool> EnableWorkflowAsync(string token, string login, long workflowId)
    {
        using var resp = await SendApiAsync(HttpMethod.Put, $"/repos/{login}/{SourceRepo}/actions/workflows/{workflowId}/enable", token);
        if (resp.IsSuccessStatusCode) return true;
        LastError = $"启用 workflow 失败：HTTP {(int)resp.StatusCode}";
        return false;
    }

    /// <summary>
    /// 把北京时间 HH:mm 换算为 UTC cron 表达式（GitHub Actions schedule 用 UTC）。
    /// "08:00" → "0 0 * * *"；"21:30" → "30 13 * * *"。非法输入返回 null。
    /// </summary>
    public static string? CronFromBeijingTime(string? beijingHHmm)
    {
        if (string.IsNullOrWhiteSpace(beijingHHmm)) return null;
        var parts = beijingHHmm.Trim().Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return null;
        if (h is < 0 or > 23 || m is < 0 or > 59) return null;
        int utcH = (h - 8 + 24) % 24;
        return $"{m} {utcH} * * *";
    }

    /// <summary>
    /// 更新 fork 中 checkin.yml 的定时（cron）。流程：读取现有文件（拿 sha）→
    /// 替换第一条 cron 行 → Contents API 写回。返回是否成功。
    /// </summary>
    public async Task<bool> UpdateWorkflowCronAsync(string token, string login, string cron)
    {
        try
        {
            var path = $"/repos/{login}/{SourceRepo}/contents/{WorkflowPath}";
            var cronRegex = new System.Text.RegularExpressions.Regex(@"-\s+cron:\s*""([^""]*)""");

            // 读取 fork 中的 checkin.yml（sha + 文本）；失败时写入 LastError
            async Task<(string Sha, string Yml)?> ReadFileAsync()
            {
                using var get = await SendApiAsync(HttpMethod.Get, path + "?ref=main", token);
                if (!get.IsSuccessStatusCode)
                {
                    LastError = $"读取 checkin.yml 失败：HTTP {(int)get.StatusCode}";
                    return null;
                }
                var getJson = await get.Content.ReadAsStringAsync();
                using var doc = System.Text.Json.JsonDocument.Parse(getJson);
                if (!doc.RootElement.TryGetProperty("sha", out var shaEl) ||
                    !doc.RootElement.TryGetProperty("content", out var cEl) ||
                    shaEl.ValueKind != JsonValueKind.String || cEl.ValueKind != JsonValueKind.String)
                {
                    LastError = "checkin.yml 响应缺少 sha/content";
                    return null;
                }
                try
                {
                    var text = Encoding.UTF8.GetString(Convert.FromBase64String(cEl.GetString()!.Replace("\n", "")));
                    return (shaEl.GetString()!, text);
                }
                catch (Exception ex)
                {
                    LastError = "checkin.yml 内容解码失败：" + ex.Message;
                    return null;
                }
            }

            // 写入后必须回读确认：GitHub 偶尔返回 2xx 但文件并未真正更新，不能直接报「已生效」
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var file = await ReadFileAsync();
                if (file == null) return false;

                var current = cronRegex.Match(file.Value.Yml);
                if (!current.Success)
                {
                    LastError = "checkin.yml 中未找到 cron 行，无法更新时间";
                    return false;
                }
                if (current.Groups[1].Value == cron)
                {
                    LastError = "";   // 内容本就一致，视为成功（幂等）
                    return true;
                }

                int idx = current.Groups[1].Index;
                var newYml = file.Value.Yml[..idx] + cron + file.Value.Yml[(idx + current.Groups[1].Length)..];
                var payload = AppJson.Serialize(new GitHubContentUpdatePayload
                {
                    Message = "chore: 调整云端签到时间至 " + cron + " (UTC)",
                    Content = Convert.ToBase64String(Encoding.UTF8.GetBytes(newYml)),
                    Sha = file.Value.Sha,
                    Branch = "main",
                });
                using var put = await SendApiAsync(HttpMethod.Put, path, token, payload);
                if (!put.IsSuccessStatusCode)
                {
                    LastError = $"写入 checkin.yml 失败：HTTP {(int)put.StatusCode}";
                    return false;
                }

                var after = await ReadFileAsync();
                if (after != null)
                {
                    var verify = cronRegex.Match(after.Value.Yml);
                    if (verify.Success && verify.Groups[1].Value == cron)
                    {
                        LastError = "";
                        return true;
                    }
                }
                if (attempt == 0) await Task.Delay(1500);   // sha 可能被并发提交刷新，重读后重试一次
            }

            LastError = "写入 checkin.yml 后回读内容未更新，云端定时可能仍是旧时间，请重试";
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>手动触发一次 workflow（用于立即验证）。</summary>
    public async Task<bool> DispatchWorkflowAsync(string token, string login, long workflowId)
    {
        var body = AppJson.Serialize(new GitHubWorkflowDispatchPayload());
        using var resp = await SendApiAsync(HttpMethod.Post, $"/repos/{login}/{SourceRepo}/actions/workflows/{workflowId}/dispatches", token, body);
        if (resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NoContent) return true;
        LastError = $"触发 workflow 失败：HTTP {(int)resp.StatusCode}";
        return false;
    }

    /// <summary>GitHub Releases 最新版本信息（用于「检查更新」）。</summary>
    public class LatestRelease
    {
        /// <summary>发布标签名，如 "v1.5.1"。</summary>
        public string? TagName { get; set; }
        /// <summary>发布时间。</summary>
        public string? PublishedAt { get; set; }
        /// <summary>Release 网页地址。</summary>
        public string? HtmlUrl { get; set; }
        /// <summary>更新说明。</summary>
        public string? Body { get; set; }
    }

    /// <summary>
    /// 获取源仓库最新的 GitHub Release（公开端点，无需认证）。
    /// 返回 null 表示未找到 Release 或请求失败；失败原因写入 LastError。
    /// </summary>
    public async Task<LatestRelease?> GetLatestReleaseAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{SourceOwner}/{SourceRepo}/releases/latest");
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"获取最新版本失败：HTTP {(int)resp.StatusCode}";
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var rel = new LatestRelease();
            if (doc.RootElement.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String)
                rel.TagName = tag.GetString();
            if (doc.RootElement.TryGetProperty("published_at", out var pa) && pa.ValueKind == JsonValueKind.String)
                rel.PublishedAt = pa.GetString();
            if (doc.RootElement.TryGetProperty("html_url", out var hu) && hu.ValueKind == JsonValueKind.String)
                rel.HtmlUrl = hu.GetString();
            if (doc.RootElement.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String)
                rel.Body = body.GetString();
            return rel;
        }
        catch (Exception ex)
        {
            LastError = "网络错误：" + ex.Message;
            return null;
        }
    }

    /// <summary>获取 checkin workflow 最近一次 run 的结论（success/failure/null；运行中为 null）。</summary>
    public async Task<string?> GetLatestRunConclusionAsync(string token, string login)
        => (await GetLatestCheckinRunAsync(token, login, status: null))?.Conclusion;

    /// <summary>获取 checkin workflow 最近一次已完成的 run（结论、状态、时间、编号、地址）。</summary>
    public async Task<WorkflowRunInfo?> GetLatestRunAsync(string token, string login)
        => await GetLatestCheckinRunAsync(token, login, status: "completed");

    /// <summary>
    /// 下载某次 run 的日志并归类失败原因。
    ///
    /// 为什么要拉日志：只凭 conclusion=failure 说不清「是 Session 过期、是风控 9074、还是 Secret 没写进去」，
    /// 用户看到的只是一句「失败了」，无从下手。checkin.py 的失败文案是固定的（见脚本里的
    /// 「账号会话已失效(HTTP 401)」「缺少环境变量 TRAE_SESSION」「命中风控 9074」等），因此可以精确归类。
    ///
    /// 全程尽力而为：日志拿不到就退回「未识别」，绝不因为诊断失败而影响主流程。
    /// </summary>
    public async Task<CloudFailureDiagnosis> DiagnoseRunFailureAsync(string token, string login, long runId)
    {
        if (runId <= 0) return CloudFailureDiagnosis.Unknown("未取到运行编号，无法自动定位原因");
        string text = await TryFetchRunLogTextAsync(token, login, runId);
        return Classify(text);
    }

    /// <summary>下载 run 日志 zip 并解出可读文本（限流限量，失败返回空串）。</summary>
    private async Task<string> TryFetchRunLogTextAsync(string token, string login, long runId)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{login}/{SourceRepo}/actions/runs/{runId}/logs");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Clear();
            req.Headers.Accept.ParseAdd("application/vnd.github+json");

            // 该接口 302 到对象存储；HttpClient 默认跟随跳转，且跨域时会自动去掉 Authorization
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"拉取运行日志失败：HTTP {(int)resp.StatusCode}";
                return "";
            }

            const int MaxBytes = 8 * 1024 * 1024;
            using var ms = new MemoryStream();
            await using (var s = await resp.Content.ReadAsStreamAsync())
            {
                var buf = new byte[64 * 1024];
                int n;
                while ((n = await s.ReadAsync(buf)) > 0 && ms.Length < MaxBytes)
                    ms.Write(buf, 0, n);
            }
            ms.Position = 0;

            using var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);
            var sb = new StringBuilder();
            foreach (var entry in zip.Entries)
            {
                if (entry.Length == 0) continue;
                // 只看签到步骤的日志，跳过程序集初始化等噪声
                var name = entry.FullName;
                if (!name.Contains("checkin", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("Run checkin", StringComparison.OrdinalIgnoreCase)) continue;

                await using var es = entry.Open();
                using var reader = new StreamReader(es, Encoding.UTF8);
                var chunk = await reader.ReadToEndAsync();
                if (chunk.Length > 256 * 1024) chunk = chunk[^262144..];
                sb.AppendLine(chunk);
                if (sb.Length > 512 * 1024) break;
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            LastError = "拉取运行日志异常：" + ex.Message;
            return "";
        }
    }

    /// <summary>按 checkin.py 的固定失败文案归类原因，并给出对应的修复动作。internal 供无界面自检调用。</summary>
    internal static CloudFailureDiagnosis Classify(string log)
    {
        if (string.IsNullOrWhiteSpace(log))
            return CloudFailureDiagnosis.Unknown("无法获取运行日志");

        string s = log;
        bool Has(string k) => s.Contains(k, StringComparison.OrdinalIgnoreCase);

        if (Has("缺少环境变量 TRAE_SESSION") || Has("TRAE_SESSION 为空"))
            return new CloudFailureDiagnosis
            {
                Key = "secret",
                Reason = "云端没有拿到会话凭证：Actions Secret 缺失或为空",
                Suggestion = "回到本页点「重新部署」，程序会重新写入 TRAE_SESSION / TRAE_DEVICE_ID 等 Secret；"
                             + "若仍失败，请到 fork 仓库的 Settings → Secrets 确认这几个 Secret 存在。",
            };

        if (Has("会话已失效") || Has("GetUserToken 失败: HTTP 401") || Has("HTTP 401"))
            return new CloudFailureDiagnosis
            {
                Key = "session",
                Reason = "云端登录态已失效（X-Cloudide-Session 过期，约 14 天）",
                Suggestion = "在本程序重新登录 Trae（设置 → 账号管理 → 重新登录，或本页「重新登录」），"
                             + "然后点「重新部署」把新 Session 写回云端。注意：换 JWT 不会延长 Session。",
            };

        if (Has("9074") || Has("参与用户太多"))
            return new CloudFailureDiagnosis
            {
                Key = "risk",
                Reason = "命中 Trae 风控（9074 参与用户太多）",
                Suggestion = "换一个非整点时间（避开 08:00 高峰），减少云端账号数量，或为各账号换发新设备号后重新部署。",
            };

        if (Has("HTTP 403") || Has("rate limit"))
            return new CloudFailureDiagnosis
            {
                Key = "github",
                Reason = "GitHub 侧拒绝请求（403 / 限流）",
                Suggestion = "稍后重试；若持续失败，重新授权 GitHub 并确认 fork 仓库的 Actions 权限正常。",
            };

        if (Has("签到失败"))
        {
            // 单账号失败但流程继续：把脚本打印的那几行原样带出来
            var line = s.Split('\n')
                        .FirstOrDefault(l => l.Contains("签到失败", StringComparison.OrdinalIgnoreCase))?.Trim() ?? "";
            return new CloudFailureDiagnosis
            {
                Key = "account",
                Reason = "部分账号签到失败：" + (line.Length > 120 ? line[..120] + "…" : line),
                Suggestion = "可能是该账号 Session 过期或设备号被风控；为该账号重新登录后重新部署，或按需换发设备号。",
            };
        }

        return CloudFailureDiagnosis.Unknown("运行失败，但日志里没有可识别的已知原因");
    }

    /// <summary>
    /// 只查询「Trae Daily Checkin」这个 workflow 的运行，避免把仓库里其它 workflow
    /// （如 push 触发的 CI Build、tag 触发的 Release）误当成云端签到结果。
    /// </summary>
    private async Task<WorkflowRunInfo?> GetLatestCheckinRunAsync(string token, string login, string? status)
    {
        long wfId = await GetWorkflowIdAsync(token, login);
        if (wfId < 0) return null;

        var query = $"?workflow_id={wfId}&per_page=1";
        if (!string.IsNullOrEmpty(status)) query += "&status=" + status;

        using var resp = await SendApiAsync(HttpMethod.Get, $"/repos/{login}/{SourceRepo}/actions/runs{query}", token);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("workflow_runs", out var arr) ||
            arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return null;

        var run = arr[0];
        var info = new WorkflowRunInfo();
        info.Id = run.TryGetProperty("id", out var rid) && rid.TryGetInt64(out var ridv) ? ridv : 0;
        info.Conclusion = run.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        info.Status = run.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        info.CreatedAt = run.TryGetProperty("created_at", out var ca) && ca.ValueKind == JsonValueKind.String ? ca.GetString() : null;
        info.RunNumber = run.TryGetProperty("run_number", out var rn) && rn.TryGetInt64(out var rnv) ? rnv : 0;
        info.HtmlUrl = run.TryGetProperty("html_url", out var hu) && hu.ValueKind == JsonValueKind.String ? hu.GetString() : null;
        return info;
    }

    /// <summary>
    /// 校验手动粘贴的 Token：GET /user 确认有效并取 login，再按 login 分派探测目标仓库写权限。
    /// owner 用源仓库校验；非 owner 若尚未 fork（404）放行（fork 后对自有仓库天然可写）。
    /// </summary>
    public async Task<PatValidation> ValidatePatAsync(string pat)
    {
        string token = (pat ?? "").Trim();
        if (!IsPlausibleToken(token))
            return new PatValidation { IsValid = false, Error = "请输入有效的 GitHub Token（github_pat_ 或 gho_ 开头，长度不少于 20）" };

        try
        {
            string login = await GetLoginAsync(token) ?? "";
            if (string.IsNullOrEmpty(login))
            {
                LastError = "Token 无效或已被撤销";
                return new PatValidation { IsValid = false, Error = LastError };
            }

            bool isOwner = string.Equals(login, SourceOwner, StringComparison.OrdinalIgnoreCase);
            string repoPath = isOwner
                ? $"/repos/{SourceOwner}/{SourceRepo}"
                : $"/repos/{login}/{SourceRepo}";

            using var resp = await SendApiAsync(HttpMethod.Get, repoPath, token);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound && !isOwner)
            {
                // 尚未 fork：放行，fork 后对自有仓库天然可写
                return new PatValidation { IsValid = true, Login = login, CanWrite = false };
            }
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)resp.StatusCode}";
                return new PatValidation { IsValid = false, Login = login, Error = LastError };
            }

            var json = await resp.Content.ReadAsStringAsync();
            if (!ParsePermissionsPush(json))
                return new PatValidation { IsValid = false, Login = login, Error = BuildPatScopeHint() };

            return new PatValidation { IsValid = true, Login = login, CanWrite = true };
        }
        catch (Exception ex)
        {
            LastError = "网络错误：" + ex.Message;
            return new PatValidation { IsValid = false, Error = LastError };
        }
    }

    // ---------- 部署状态检测 ----------

    /// <summary>
    /// 检测云端部署状态：授权是否有效、fork 仓库是否存在、secrets 是否已写入、workflow 是否已启用。
    /// </summary>
    public async Task<DeploymentStatus> GetDeploymentStatusAsync(string token, string login)
    {
        var result = new DeploymentStatus();
        try
        {
            // 用 GET /user 探测授权（必须认证；公开仓库 GET 匿名可读，token 失效时也会返回 200，不能用来判断授权）
            using var userResp = await SendApiAsync(HttpMethod.Get, "/user", token);
            if (userResp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                result.IsAuthorized = false;
                return result;
            }
            result.IsAuthorized = true;

            using var repoResp = await SendApiAsync(HttpMethod.Get, $"/repos/{login}/{SourceRepo}", token);
            result.IsForked = repoResp.IsSuccessStatusCode;
            if (!result.IsForked) return result;

            result.DeployedAccountCount = await CountDeployedAccountsAsync(token, login);
            result.HasSecrets = result.DeployedAccountCount >= 1;
            result.IsWorkflowEnabled = await IsWorkflowEnabledAsync(token, login);
        }
        catch (Exception ex)
        {
            // 网络异常等视为授权仍有效，仅状态未知，避免误删授权
            LastError = ex.Message;
            result.IsAuthorized = true;
        }
        return result;
    }

    /// <summary>拉取仓库全部 Actions secret 名；失败返回 null。</summary>
    private async Task<HashSet<string>?> FetchSecretNamesAsync(string token, string login)
    {
        using var resp = await SendApiAsync(HttpMethod.Get, $"/repos/{login}/{SourceRepo}/actions/secrets", token);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("secrets", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in arr.EnumerateArray())
            if (s.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                names.Add(n.GetString() ?? "");
        return names;
    }

    /// <summary>
    /// 统计已部署的连续账号数：账号 1 需要 TRAE_SESSION 与 TRAE_DEVICE_ID 都存在，
    /// 之后依 TRAE_SESSION_2/TRAE_DEVICE_ID_2 … 递增，遇缺失即停止（返回前缀长度）。
    /// </summary>
    public async Task<int> CountDeployedAccountsAsync(string token, string login)
    {
        var names = await FetchSecretNamesAsync(token, login);
        if (names == null) return 0;
        int n = 1;
        while (names.Contains(SessionSecretNameFor(n)) && names.Contains(DeviceSecretNameFor(n)))
            n++;
        return n - 1;
    }

    /// <summary>删除仓库某个 Actions secret；404（不存在）视为成功。删除失败返回 false。</summary>
    public async Task<bool> DeleteSecretAsync(string token, string login, string secretName)
    {
        try
        {
            using var resp = await SendApiAsync(HttpMethod.Delete, $"/repos/{login}/{SourceRepo}/actions/secrets/{secretName}", token);
            if (resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NotFound) return true;
            LastError = $"删除 secret 失败：HTTP {(int)resp.StatusCode}";
            return false;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    /// <summary>检查 checkin.yml workflow 是否已启用（state=active）。</summary>
    private async Task<bool> IsWorkflowEnabledAsync(string token, string login)
    {
        long id = await GetWorkflowIdAsync(token, login);
        if (id < 0) return false;
        using var resp = await SendApiAsync(HttpMethod.Get, $"/repos/{login}/{SourceRepo}/actions/workflows/{id}", token);
        if (!resp.IsSuccessStatusCode) return false;
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() == "active";
    }

    // ---------- 内部 ----------

    private async Task<HttpResponseMessage> SendApiAsync(HttpMethod method, string path, string token, string? body = null)
    {
        using var req = new HttpRequestMessage(method, "https://api.github.com" + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _http.SendAsync(req);
    }
}
