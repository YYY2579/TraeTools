using System.Buffers;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TraeTools.Services.Usage;

/// <summary>
/// 账号保活：用当前账号的 JWT 向 Trae 聊天接口发一条测试消息。
///
/// 契约来源（只读探测 Trae CN 3.4.24 安装目录）：
///   product.json → bootConfig.agent.trae.normal = https://trae-api-cn.mchost.guru
///                  bootConfig.agent.appId = 6eefa01c-1036-4c7e-9ca5-d891f63bfcd8
///   ai-modules-chat/dist/index.mjs → POST /api/ide/v1/chat；请求封装自动追加
///     Authorization / X-User-Region / X-Trae-Client-Type / X-Preferenced-Language / X-Trae-User-Timezone。
///   solo-lite 包内版本提示键 → deepseek_v4_1_flash_1017。
///
/// 模型不写死：优先调 /api/ide/v1/model_list 取当前模型列表，按低成本关键词选一个；
/// 接口失败时用候选模型轮换，避免官方改名后永久卡死。
/// </summary>
public sealed class KeepAliveClient
{
    private const string BaseUrl = "https://trae-api-cn.mchost.guru";
    private const string ChatPath = "/api/ide/v1/chat";
    private const string ModelListPath = "/api/ide/v1/model_list";
    private const string AppId = "6eefa01c-1036-4c7e-9ca5-d891f63bfcd8";

    /// <summary>默认保活模型（客户端版本提示键）；仅作为动态读取失败时的首选。</summary>
    public const string DefaultModel = "deepseek_v4_1_flash_1017";

    /// <summary>模型改名时的候选清单，均为客户端包内出现过的标识。</summary>
    private static readonly string[] ModelCandidates =
    [
        DefaultModel,
        "deepseek-v4.1-flash",
        "deepseek-v4-1-flash",
        "deepseek_v4_1_flash",
        "deepseek-v3.1",
        "deepseek-V3",
    ];

    /// <summary>测试消息内容；只用于保活，不在日志中输出。</summary>
    public const string TestMessage = "你好";

    private readonly HttpClient _http;

    public KeepAliveClient(string? proxy = null)
        => _http = TraeTools.Services.NetProxy.GetClient(proxy);

    private static void AddCommonHeaders(HttpRequestMessage req, string token)
    {
        req.Headers.TryAddWithoutValidation("Authorization", "Cloud-IDE-JWT " + token);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("X-User-Region", "cn");
        req.Headers.TryAddWithoutValidation("X-Trae-Client-Type", "ide");
        req.Headers.TryAddWithoutValidation("X-Preferenced-Language", "zh-cn");
        req.Headers.TryAddWithoutValidation("X-App-Id", AppId);
        req.Headers.TryAddWithoutValidation("X-IDE-Version-Code", "20260212");
    }

    /// <summary>按客户端字段构造请求体；独立成方法便于离线验证。</summary>
    public static string BuildBody(string modelName, string sessionId)
    {
        // 用 Utf8JsonWriter 手写，避免反射序列化触发 IL2026（项目启用了裁剪/AOT 门禁）。
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("user_input", TestMessage);
            writer.WriteString("intent_name", "");
            // 实测：variables 传 {} / [] / 对象均触发 HTTP 400；省略或 null 才进入业务鉴权。
            writer.WriteNull("variables");
            writer.WriteStartArray("chat_history"); writer.WriteEndArray();
            writer.WriteStartArray("context_resolvers"); writer.WriteEndArray();
            writer.WriteString("session_id", sessionId);
            writer.WriteBoolean("generate_suggested_questions", false);
            writer.WriteString("conversation_id", "");
            writer.WriteNumber("current_turn", 1);
            writer.WriteStartArray("valid_turns"); writer.WriteEndArray();
            writer.WriteString("model_name", modelName);
            writer.WriteStartArray("multi_media"); writer.WriteEndArray();
            writer.WriteStartObject("Request"); writer.WriteEndObject();
            writer.WriteNull("last_llm_response_info");
            writer.WriteBoolean("is_preset", false);
            writer.WriteString("provider", "");
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// 从模型列表接口挑一个低成本模型。只读取模型名，不输出凭证或完整响应。
    /// 返回 null 表示无法从服务端确定。
    /// </summary>
    internal async Task<string?> TryResolveCheapModelAsync(string token)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + ModelListPath);
            AddCommonHeaders(req, token);
            req.Content = new StringContent("{\"Request\":{}}", Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json)) return null;
            using var doc = JsonDocument.Parse(json);
            var names = new List<string>();
            CollectModelNames(doc.RootElement, names, 0);
            // 低成本优先：flash / lite / mini；其次 deepseek；再取第一个非空。
            var cheap = names.FirstOrDefault(n => n.Contains("flash", StringComparison.OrdinalIgnoreCase)
                                                  || n.Contains("lite", StringComparison.OrdinalIgnoreCase)
                                                  || n.Contains("mini", StringComparison.OrdinalIgnoreCase));
            cheap ??= names.FirstOrDefault(n => n.Contains("deepseek", StringComparison.OrdinalIgnoreCase));
            return cheap ?? names.FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>从错误响应中只提取业务 code / message，不回显凭证或完整响应体。</summary>
    private static string ExtractBizError(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("{", StringComparison.Ordinal))
            return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? code = null, message = null;
            if (root.TryGetProperty("code", out var c)) code = c.ToString();
            if (root.TryGetProperty("message", out var m)) message = m.GetString();
            else if (root.TryGetProperty("msg", out var mm)) message = mm.GetString();
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(code)) parts.Add("code=" + code);
            if (!string.IsNullOrEmpty(message))
                parts.Add("message=" + (message.Length > 160 ? message[..160] : message));
            return parts.Count == 0 ? "" : "（" + string.Join("，", parts) + "）";
        }
        catch { return ""; }
    }

    private static void CollectModelNames(JsonElement el, List<string> names, int depth)
    {
        if (depth > 6 || names.Count >= 200) return;
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
            {
                if (p.NameEquals("model_name") || p.NameEquals("name") || p.NameEquals("model") || p.NameEquals("id"))
                {
                    if (p.Value.ValueKind == JsonValueKind.String)
                    {
                        var v = p.Value.GetString() ?? "";
                        if (v.Length is > 3 and < 120 && !names.Contains(v)) names.Add(v);
                    }
                }
                else CollectModelNames(p.Value, names, depth + 1);
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
            foreach (var item in el.EnumerateArray()) CollectModelNames(item, names, depth + 1);
    }

    /// <summary>发送一条测试消息。返回 (是否成功, 详情)；详情只含状态/业务码/模型名，不含凭证与响应正文。</summary>
    public async Task<(bool Ok, string Detail)> SendTestMessageAsync(
        string token, string? modelName = null, string? sessionId = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return (false, "Token 为空");

        var dynamicModel = await TryResolveCheapModelAsync(token);
        var models = new List<string>();
        if (!string.IsNullOrWhiteSpace(modelName)) models.Add(modelName.Trim());
        if (!string.IsNullOrWhiteSpace(dynamicModel)) models.Add(dynamicModel!);
        foreach (var c in ModelCandidates) if (!models.Contains(c)) models.Add(c);

        var sid = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId;
        var failures = new List<string>();

        foreach (var model in models)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + ChatPath);
                AddCommonHeaders(req, token);
                req.Content = new StringContent(BuildBody(model, sid), Encoding.UTF8, "application/json");

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                var json = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    var biz = ExtractBizError(json);
                    failures.Add($"{model}: HTTP {(int)resp.StatusCode}{biz}");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(json))
                {
                    failures.Add($"{model}: 空响应");
                    continue;
                }

                if (json.TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("code", out var codeEl))
                    {
                        var code = codeEl.ValueKind == JsonValueKind.Number ? codeEl.GetInt32()
                            : int.TryParse(codeEl.GetString(), out var parsed) ? parsed : -1;
                        if (code != 0) { failures.Add($"{model}: code={code}"); continue; }
                    }
                    if (root.TryGetProperty("error", out var errEl)
                        && errEl.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(errEl.GetString()))
                    {
                        failures.Add($"{model}: error 字段");
                        continue;
                    }
                }
                return (true, $"模型 {model}");
            }
            catch (Exception ex)
            {
                failures.Add($"{model}: 网络或解析异常（{ex.GetType().Name}）");
            }
        }

        return (false, string.Join("；", failures));
    }
}
