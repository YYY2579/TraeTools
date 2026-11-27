using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TraeCheckin;
using TraeSwitch.Services;
using TraeTools.Models;
using TraeTools.Services.Login;
using TraeTools.Services.Notify;

namespace TraeTools.Services.Json;

/// <summary>
/// NativeAOT 安全的 JSON 门面（P2-2）。
/// 反射版 <c>JsonSerializer.Serialize/Deserialize&lt;T&gt;(...)</c> 在 NativeAOT 下会整体失效
/// （61 条 IL2026/IL3050 警告：需要运行时生成代码），因此这里统一改为**源生成**上下文：
///   · <see cref="AppJson"/>          —— 紧凑输出：API 请求体、usage_*.jsonl（一行一条，不能有换行）
///   · <see cref="AppJsonIndented"/>  —— 缩进输出：config.json / switch\settings.json / vault\meta.json
/// 未注册的类型会在调用点直接抛错（fail-fast），而不是在 AOT 运行时静默丢字段。
/// </summary>
public static class AppJson
{
    public static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, Info<T>(CompactJsonContext.Default));

    public static T? Deserialize<T>(string json)
        => (T?)JsonSerializer.Deserialize(json, Info<T>(CompactJsonContext.Default));

    private static JsonTypeInfo Info<T>(JsonSerializerContext ctx)
        => ctx.GetTypeInfo(typeof(T))
           ?? throw new InvalidOperationException(
               "类型未在 JSON 源生成上下文中注册：" + typeof(T).FullName + "（请在 AppJson.cs 的上下文里加 [JsonSerializable]）");
}

/// <summary>缩进版门面，用于人读的配置文件；语义等同旧的 WriteIndented = true。</summary>
public static class AppJsonIndented
{
    public static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, Info<T>());

    public static T? Deserialize<T>(string json)
        => (T?)JsonSerializer.Deserialize(json, Info<T>());

    /// <summary>供 JsonNode.ToJsonString 之类需要 options 的调用点复用（仍走源生成解析器）。</summary>
    public static JsonSerializerOptions Options => IndentedJsonContext.Default.Options;

    private static JsonTypeInfo Info<T>()
        => IndentedJsonContext.Default.GetTypeInfo(typeof(T))
           ?? throw new InvalidOperationException(
               "类型未在 JSON 源生成上下文中注册：" + typeof(T).FullName + "（请在 AppJson.cs 的上下文里加 [JsonSerializable]）");
}

/// <summary>紧凑 JSON 上下文：对外 API 请求体 / 响应体、JSONL 记录。</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(UsageSessionRecord))]
[JsonSerializable(typeof(CheckinStatus))]
[JsonSerializable(typeof(GitHubDeviceCode))]
[JsonSerializable(typeof(FeishuTextMessage))]
[JsonSerializable(typeof(GitHubDeviceCodeRequest))]
[JsonSerializable(typeof(GitHubTokenPollRequest))]
[JsonSerializable(typeof(GitHubSecretPayload))]
[JsonSerializable(typeof(GitHubActionsPermissionPayload))]
[JsonSerializable(typeof(GitHubWorkflowDispatchPayload))]
[JsonSerializable(typeof(GitHubContentUpdatePayload))]
// ===== 本次 PR 追加：推送渠道的 HTTP 请求体 =====
// 这三种渠道消息体原先用匿名类型 + 反射 JsonSerializer 构造，在 full 裁剪 / NativeAOT 下
// 既会产生 IL2026 警告，也会在运行时静默丢字段。改为具名类型 + 此处登记后完全走源生成。
[JsonSerializable(typeof(TextRobotPayload))]
[JsonSerializable(typeof(TelegramSendPayload))]
// 账号备份的明文负载（外层已 AES-GCM 加密，故用紧凑输出；其中含 List<TraeAccount>，由生成器递归展开）
[JsonSerializable(typeof(AccountBackup.Payload))]
internal sealed partial class CompactJsonContext : JsonSerializerContext
{
}

/// <summary>缩进 JSON 上下文：本地配置与存档（config.json / settings.json / meta.json）。</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(AppSettingsData))]
[JsonSerializable(typeof(VaultMeta))]
[JsonSerializable(typeof(ProfileIsolation.ProfileManifest))]
// DataPaths.Migrate 需要把合并后的 JsonObject 缩进写回（旧 config 合并路径）
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonObject))]
// ===== 本次 PR 追加：账号备份文件的容器 =====
// 备份是给人看/可手工转存的 JSON 文件，故走缩进上下文；
// 明文负载（AccountBackup.Payload）因其外层已由 AES-GCM 加密，走紧凑上下文即可。
[JsonSerializable(typeof(AccountBackup.Envelope))]
internal sealed partial class IndentedJsonContext : JsonSerializerContext
{
}
