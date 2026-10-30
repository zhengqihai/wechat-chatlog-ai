using System.Text.Json.Serialization;

namespace ChatlogAI.Client.Models;

/// <summary>守护会话。上限由服务端强制：最多 8 个好友 + 3 个群聊，超限 400。</summary>
public sealed record GuardTarget
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    /// <summary>发送时用它搜索微信会话，因此必须与微信里显示的会话名一致。</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary>friend 或 group。</summary>
    [JsonPropertyName("type")] public string Type { get; init; } = "";

    /// <summary>该会话的**内层**提示词（单独口吻）。</summary>
    [JsonPropertyName("prompt")] public string Prompt { get; init; } = "";

    /// <summary>关联的知识文档 id 列表（T-F4，每会话独立）。</summary>
    [JsonPropertyName("doc_ids")] public IReadOnlyList<string> DocIds { get; init; } = [];

    [JsonIgnore] public bool IsGroup => string.Equals(Type, "group", StringComparison.OrdinalIgnoreCase);
}

/// <summary>微信快捷键映射。值形如 <c>ctrl+alt+w</c> / <c>enter</c>。</summary>
public sealed record AutoReplyHotkeys
{
    [JsonPropertyName("open_wechat")] public string OpenWechat { get; init; } = "";

    [JsonPropertyName("search")] public string Search { get; init; } = "";

    [JsonPropertyName("paste")] public string Paste { get; init; } = "";

    [JsonPropertyName("send")] public string Send { get; init; } = "";

    /// <summary>
    /// 「搜索后确认进入会话」用的键（§14.3.4）。旧配置里没有该字段，反序列化后为空串，
    /// 客户端回填时会落到内置默认值 <c>enter</c>。
    /// </summary>
    [JsonPropertyName("confirm")] public string Confirm { get; init; } = "";
}

/// <summary>自动回复完整配置（服务端下发的形态）。</summary>
public sealed record AutoReplyConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }

    [JsonPropertyName("guard_targets")] public IReadOnlyList<GuardTarget> GuardTargets { get; init; } = [];

    /// <summary>生成回复时携带的上下文条数，服务端夹到 [1, 2000]，默认 80。</summary>
    [JsonPropertyName("context_count")] public int ContextCount { get; init; } = 80;

    /// <summary>
    /// 同一会话的最小回复间隔（秒）。服务端夹到 [0, 600]，默认 10；0 = 不限制。
    ///
    /// 注意这是"每个会话各自用多久"：冷却计时**按会话分别记**，
    /// 所以给 A 回复过不会压住给 B 的回复。
    /// </summary>
    [JsonPropertyName("reply_cooldown_seconds")] public int ReplyCooldownSeconds { get; init; } = 10;

    /// <summary>**外层**提示词（全局身份），空则用内置默认身份。</summary>
    [JsonPropertyName("reply_prompt")] public string ReplyPrompt { get; init; } = "";

    [JsonPropertyName("hotkeys")] public AutoReplyHotkeys Hotkeys { get; init; } = new();
}

/// <summary>GET /api/auto-reply/config 响应。</summary>
public sealed record AutoReplyConfigResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("config")] public AutoReplyConfig? Config { get; init; }

    /// <summary>
    /// 自动化功能是否可用（Python 侧 = automation 可导入 且 os.name == "nt"）。
    /// 为 false 时界面应隐藏或禁用相关入口。
    /// </summary>
    [JsonPropertyName("available")] public bool Available { get; init; }
}

/// <summary>
/// POST /api/auto-reply/config 的**部分更新**请求体。
///
/// 用「可空 + 序列化时忽略 null」表达「本字段不变」：
/// 服务端对 None 值也是直接跳过，两者语义一致。
/// 因此 <see cref="Enabled"/> 必须用 <c>bool?</c>——若用 <c>bool</c>，
/// 只想改提示词时也会把 enabled 强制写成 false。
/// </summary>
public sealed record AutoReplyConfigPatch
{
    [JsonPropertyName("enabled")] public bool? Enabled { get; init; }

    [JsonPropertyName("guard_targets")] public IReadOnlyList<GuardTarget>? GuardTargets { get; init; }

    [JsonPropertyName("context_count")] public int? ContextCount { get; init; }

    [JsonPropertyName("reply_cooldown_seconds")] public int? ReplyCooldownSeconds { get; init; }

    /// <summary>传空串是「清空并回退到内置默认身份」，与传 null（不改）语义不同。</summary>
    [JsonPropertyName("reply_prompt")] public string? ReplyPrompt { get; init; }

    [JsonPropertyName("hotkeys")] public AutoReplyHotkeys? Hotkeys { get; init; }
}

/// <summary>自动回复引擎的一条日志。</summary>
public sealed record AutoReplyLogEntry
{
    [JsonPropertyName("time")] public string Time { get; init; } = "";

    /// <summary>info 或 error。</summary>
    [JsonPropertyName("level")] public string Level { get; init; } = "";

    [JsonPropertyName("message")] public string Message { get; init; } = "";

    [JsonIgnore] public bool IsError => Level == "error";
}

/// <summary>GET /api/auto-reply/status 响应。服务端日志上限 deque(maxlen=200)。</summary>
public sealed record AutoReplyStatusResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    /// <summary>配置里是否启用。</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }

    /// <summary>后台线程是否存活。可能出现 enabled=true 但 running=false（线程已死）。</summary>
    [JsonPropertyName("running")] public bool Running { get; init; }

    /// <summary>
    /// 发送闸门是否被拦住（T19：账号切换待确认期间恒为 true，见 §7.17 闸门语义）。
    ///
    /// 存在的理由：闸门只拦"发送动作"、不拦"检测"，因此 enabled / running 都还是正常值，
    /// 界面若只看这两个字段就会显示"运行中"却什么都不发，用户完全无从理解。
    /// </summary>
    [JsonPropertyName("sendPaused")] public bool SendPaused { get; init; }

    /// <summary>被拦住的原因（后端与引擎日志、409 文案共用同一句），直接显示。</summary>
    [JsonPropertyName("sendPausedReason")] public string SendPausedReason { get; init; } = "";

    /// <summary>微信当前激活的会话名，用于跳过重复搜索。</summary>
    [JsonPropertyName("active_chat_name")] public string ActiveChatName { get; init; } = "";

    [JsonPropertyName("log")] public IReadOnlyList<AutoReplyLogEntry> Log { get; init; } = [];
}

/// <summary>POST /api/auto-reply/test 请求体。</summary>
public sealed record AutoReplyTestRequest
{
    /// <summary>
    /// open_wechat / search / paste / send / confirm（即 hotkeys 的键名）/ clipboard / full。
    /// </summary>
    [JsonPropertyName("action")] public string Action { get; init; } = "";

    /// <summary>action = full 时必填；clipboard 不用。</summary>
    [JsonPropertyName("target_name")] public string? TargetName { get; init; }

    /// <summary>clipboard 时默认「测试剪贴板」；full 时必填。</summary>
    [JsonPropertyName("text")] public string? Text { get; init; }
}

/// <summary>
/// POST /api/auto-reply/detect-hotkeys 响应。
///
/// 服务端当前恒返回 <see cref="Found"/>=false（§14.3.0 已取证：微信 4.1 读不到热键），
/// 字段结构保留是为了将来接上探测源时接口不必改。
/// </summary>
public sealed record AutoReplyDetectHotkeysResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    /// <summary>是否识别到。false 是**正常结果**，不是错误，界面据此只更新提示区。</summary>
    [JsonPropertyName("found")] public bool Found { get; init; }

    /// <summary>命中来源（用于将来展示"从哪读到的"），当前恒为空。</summary>
    [JsonPropertyName("source")] public string Source { get; init; } = "";

    /// <summary>识别到的快捷键，键名与 auto-reply 配置的 hotkeys 一致；当前恒为空。</summary>
    [JsonPropertyName("hotkeys")] public IReadOnlyDictionary<string, string> Hotkeys { get; init; }
        = new Dictionary<string, string>();

    /// <summary>给用户看的一行说明，直接显示；已包含"请手工填写"的动作指引。</summary>
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}
