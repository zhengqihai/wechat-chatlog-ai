using System.Text.Json.Serialization;

namespace ChatlogAI.Client.Models;

/// <summary>
/// GET /api/chats 的 status_info。
/// 对应 app/server.py 中 chatlist 接口返回的"数据源状态"。
/// </summary>
public sealed record ChatStatusInfo
{
    /// <summary>数据源是否就绪。成功响应（HTTP 200）时恒为 true，失败走 503。</summary>
    [JsonPropertyName("ready")] public bool Ready { get; init; }

    /// <summary>面向用户的提示文案，前端直接展示在状态栏。</summary>
    [JsonPropertyName("msg")] public string Msg { get; init; } = "";

    [JsonPropertyName("source")] public string Source { get; init; } = "";

    /// <summary>当前微信账号标识。</summary>
    [JsonPropertyName("account")] public string Account { get; init; } = "";
}

/// <summary>会话（微信 username 唯一标识）。</summary>
public sealed record ChatSummary
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary>上游固定取值：<c>group</c> 或 <c>friend</c>。</summary>
    [JsonPropertyName("type")] public string Type { get; init; } = "";

    [JsonPropertyName("source")] public string Source { get; init; } = "";

    [JsonPropertyName("last_message")] public string LastMessage { get; init; } = "";

    /// <summary>上游原样字符串，客户端不解析（展示用）。</summary>
    [JsonPropertyName("last_time")] public string LastTime { get; init; } = "";

    [JsonPropertyName("unread")] public int Unread { get; init; }

    [JsonIgnore] public bool IsGroup => string.Equals(Type, "group", StringComparison.OrdinalIgnoreCase);
}

/// <summary>GET /api/chats 响应。</summary>
public sealed record ChatListResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("status_info")] public ChatStatusInfo? StatusInfo { get; init; }

    [JsonPropertyName("chats")] public IReadOnlyList<ChatSummary> Chats { get; init; } = [];
}

/// <summary>单条聊天消息。</summary>
public sealed record ChatMessage
{
    /// <summary>消息 id，用于去重与列表 diff。</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    /// <summary>自己恒为「我」；他人为昵称 / username；未知为「未知成员」。</summary>
    [JsonPropertyName("sender")] public string Sender { get; init; } = "";

    /// <summary>后端已格式化的本地时间 <c>YYYY-MM-DD HH:MM:SS</c>。</summary>
    [JsonPropertyName("time")] public string Time { get; init; } = "";

    /// <summary>原始时间戳。**可能是秒也可能是毫秒**，取用前必须走 <see cref="TimestampValue"/>。</summary>
    [JsonPropertyName("timestamp")] public double Timestamp { get; init; }

    /// <summary>正文；非文本消息为类型标签（如 <c>[图片]</c>）。</summary>
    [JsonPropertyName("content")] public string Content { get; init; } = "";

    /// <summary>微信 renderType（text / image / voice …），用于图标或分类。</summary>
    [JsonPropertyName("message_type")] public string MessageType { get; init; } = "";

    [JsonPropertyName("source")] public string Source { get; init; } = "";

    /// <summary>
    /// 图片消息的可用地址；非图片消息为空串。
    /// 后端已把上游地址代理成指向本服务的 <c>/api/media/image</c>，可直接用于下载或绑定。
    /// </summary>
    [JsonPropertyName("image_url")] public string ImageUrl { get; init; } = "";

    /// <summary>图片内容指纹（后端透传上游 <c>imageMd5</c>），仅用于排查，不参与渲染。</summary>
    [JsonPropertyName("image_md5")] public string ImageMd5 { get; init; } = "";

    [JsonIgnore] public bool IsSelf => Sender == "我";

    /// <summary>
    /// 是否"可渲染的图片"：必须是 image 类型**且**拿到了地址。
    /// 地址缺失时保持 false，渲染层会退回文本标签，不会出现空白图位。
    /// </summary>
    [JsonIgnore]
    public bool IsImage => string.Equals(MessageType, "image", StringComparison.OrdinalIgnoreCase)
                           && !string.IsNullOrEmpty(ImageUrl);

    /// <summary>
    /// 时间戳归一化：上游同一条链路里既有秒也有毫秒（见 docs/API_REFERENCE.md §8.2）。
    /// 判据与前端一致——大于 1e11 视为毫秒。
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset? TimestampValue => Timestamp > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(Timestamp > 100_000_000_000 ? (long)Timestamp : (long)(Timestamp * 1000))
        : null;
}

/// <summary>GET /api/messages 响应。</summary>
public sealed record MessageListResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    /// <summary>恒按时间正序（旧 → 新）。适配器内部已把上游的倒序 reversed。</summary>
    [JsonPropertyName("messages")] public IReadOnlyList<ChatMessage> Messages { get; init; } = [];

    [JsonPropertyName("source_valid")] public bool SourceValid { get; init; }

    /// <summary>为空表示正常；无消息时是提示文案。</summary>
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}
