using System.Text.Json.Serialization;

namespace ChatlogAI.Client.Models;

/// <summary>AI 对话历史的一条。role 仅 user / assistant。</summary>
public sealed record HistoryItem
{
    [JsonPropertyName("role")] public string Role { get; init; } = "";

    [JsonPropertyName("content")] public string Content { get; init; } = "";

    [JsonIgnore] public bool IsUser => Role == "user";
}

/// <summary>送入分析的消息（只用到 sender / time / content 三字段）。</summary>
public sealed record AnalyzeMessage
{
    [JsonPropertyName("sender")] public string Sender { get; init; } = "";

    [JsonPropertyName("time")] public string Time { get; init; } = "";

    [JsonPropertyName("content")] public string Content { get; init; } = "";
}

/// <summary>分析类型，对应后端的 system prompt 选择。</summary>
public static class AnalyzeType
{
    public const string General = "general";
    public const string Meeting = "meeting";
    public const string Todo = "todo";
    public const string Topics = "topics";
}

/// <summary>POST /api/summarize 请求体。</summary>
public sealed record SummarizeRequest
{
    [JsonPropertyName("target_name")] public string TargetName { get; init; } = "";

    [JsonPropertyName("messages")] public IReadOnlyList<AnalyzeMessage> Messages { get; init; } = [];

    /// <summary>general（默认）/ meeting / todo / topics。</summary>
    [JsonPropertyName("type")] public string Type { get; init; } = AnalyzeType.General;

    [JsonPropertyName("prompt")] public string? Prompt { get; init; }

    /// <summary>多轮上下文。服务端只取最后 10 条、每条内容截断 8000 字符。</summary>
    [JsonPropertyName("history")] public IReadOnlyList<HistoryItem>? History { get; init; }

    /// <summary>服务端二次过滤：只保留最近 N 小时（与界面筛选可叠加）。</summary>
    [JsonPropertyName("recent_hours")] public double? RecentHours { get; init; }

    [JsonPropertyName("recent_count")] public int? RecentCount { get; init; }
}

/// <summary>POST /api/summarize 响应。</summary>
public sealed record SummarizeResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    /// <summary>模型返回的 Markdown 正文。</summary>
    [JsonPropertyName("summary")] public string Summary { get; init; } = "";

    /// <summary>实际参与分析的消息条数（过滤后）。</summary>
    [JsonPropertyName("analyzed_count")] public int AnalyzedCount { get; init; }
}

/// <summary>GET /api/history 响应。</summary>
public sealed record HistoryResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("history")] public IReadOnlyList<HistoryItem> History { get; init; } = [];
}

/// <summary>
/// POST /api/history 请求体。
/// 语义是**整体覆盖** <c>summaries[target_id]</c>，不是追加。
/// </summary>
public sealed record SaveHistoryRequest
{
    [JsonPropertyName("target_id")] public string TargetId { get; init; } = "";

    [JsonPropertyName("history")] public IReadOnlyList<HistoryItem> History { get; init; } = [];
}
