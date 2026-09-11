using System.Text.Json.Serialization;

namespace ChatlogAI.Client.Models;

/// <summary>
/// 定时发送任务。<c>Id</c> 之外还有 <c>TargetId</c>，前者是任务自身的身份（用于状态映射），
/// 后者是微信会话 id（用于取上下文与发送时搜索会话名），两者不要混用。
/// </summary>
public sealed record ScheduledSendTask
{
    /// <summary>任务 id，形如 <c>sched-&lt;10位hex&gt;</c>；为空 = 服务端新建。</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    [JsonPropertyName("target_id")] public string TargetId { get; init; } = "";

    /// <summary>发送时用它在微信里搜索会话，必须与微信显示的会话名一致。</summary>
    [JsonPropertyName("target_name")] public string TargetName { get; init; } = "";

    /// <summary>friend 或 group。</summary>
    [JsonPropertyName("target_type")] public string TargetType { get; init; } = "";

    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;

    /// <summary>daily / weekly / interval / once。</summary>
    [JsonPropertyName("mode")] public string Mode { get; init; } = ScheduledSendModes.Daily;

    /// <summary>HH:MM，daily 与 weekly 生效。</summary>
    [JsonPropertyName("time_of_day")] public string TimeOfDay { get; init; } = "09:00";

    /// <summary>1=周一 … 7=周日，weekly 生效。</summary>
    [JsonPropertyName("weekdays")] public IReadOnlyList<int> Weekdays { get; init; } = [];

    /// <summary>固定间隔的分钟数，interval 生效。服务端夹到 [1, 10080]。</summary>
    [JsonPropertyName("interval_minutes")] public int IntervalMinutes { get; init; } = 120;

    /// <summary>YYYY-MM-DDTHH:MM，once 生效。</summary>
    [JsonPropertyName("run_at")] public string RunAt { get; init; } = "";

    /// <summary>距上次发送未满该分钟数一律不发，对所有模式生效。</summary>
    [JsonPropertyName("cooldown_minutes")] public int CooldownMinutes { get; init; } = 60;

    /// <summary>在基础到期时刻之后随机推迟的分钟数，用来模拟真人。</summary>
    [JsonPropertyName("jitter_minutes")] public int JitterMinutes { get; init; } = 15;

    /// <summary>生成招呼时携带的上下文条数，服务端夹到 [1, 2000]，默认 80。</summary>
    [JsonPropertyName("context_count")] public int ContextCount { get; init; } = 80;

    /// <summary>该任务专属提示词，为空时只用内置的 scheduled-greeting。</summary>
    [JsonPropertyName("prompt")] public string Prompt { get; init; } = "";

    [JsonIgnore] public bool IsGroup => string.Equals(TargetType, "group", StringComparison.OrdinalIgnoreCase);
}

/// <summary>任务模式常量，与 Python 侧 <c>SCHEDULED_MODES</c> 逐字对应。</summary>
public static class ScheduledSendModes
{
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const string Interval = "interval";
    public const string Once = "once";
}

/// <summary>定时发送完整配置（服务端下发的形态）。</summary>
public sealed record ScheduledSendConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }

    [JsonPropertyName("tasks")] public IReadOnlyList<ScheduledSendTask> Tasks { get; init; } = [];
}

/// <summary>GET /api/scheduled-send/config 响应。</summary>
public sealed record ScheduledSendConfigResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("config")] public ScheduledSendConfig? Config { get; init; }

    /// <summary>自动化功能是否可用（Python 侧 = automation 可导入 且 os.name == "nt"）。</summary>
    [JsonPropertyName("available")] public bool Available { get; init; }
}

/// <summary>
/// POST /api/scheduled-send/config 的**部分更新**请求体。
/// 与 <see cref="AutoReplyConfigPatch"/> 同理：可空 + 序列化忽略 null 表达"该字段不变"，
/// 因此 <see cref="Enabled"/> 必须是 <c>bool?</c>，否则"只改任务列表"会把总开关写成 false。
/// </summary>
public sealed record ScheduledSendConfigPatch
{
    [JsonPropertyName("enabled")] public bool? Enabled { get; init; }

    /// <summary>整份覆盖：服务端按数组整体替换，不做增量合并。</summary>
    [JsonPropertyName("tasks")] public IReadOnlyList<ScheduledSendTask>? Tasks { get; init; }
}

/// <summary>单个任务的运行状态。</summary>
public sealed record ScheduledSendTaskStatus
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    [JsonPropertyName("enabled")] public bool Enabled { get; init; }

    /// <summary>下次触发的 Unix 秒（不含随机抖动）；无下次则为 null。</summary>
    [JsonPropertyName("next_fire_at")] public double? NextFireAt { get; init; }

    [JsonPropertyName("last_sent_at")] public double? LastSentAt { get; init; }
}

/// <summary>GET /api/scheduled-send/status 响应。</summary>
public sealed record ScheduledSendStatusResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    /// <summary>总开关是否打开。</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }

    /// <summary>后台线程是否存活。</summary>
    [JsonPropertyName("running")] public bool Running { get; init; }

    [JsonPropertyName("tasks")] public IReadOnlyList<ScheduledSendTaskStatus> Tasks { get; init; } = [];

    /// <summary>与自动回复共用同一套日志行结构（time / level / message）。</summary>
    [JsonPropertyName("log")] public IReadOnlyList<AutoReplyLogEntry> Log { get; init; } = [];
}

/// <summary>
/// POST /api/scheduled-send/test 请求体。
/// 刻意提交**完整任务**而不是 task_id：界面上还没保存的草稿也要能预览 / 试发。
/// </summary>
public sealed record ScheduledSendTestRequest
{
    /// <summary>preview（只生成不发送）或 send（生成并真实发送）。</summary>
    [JsonPropertyName("action")] public string Action { get; init; } = "";

    [JsonPropertyName("task")] public ScheduledSendTask? Task { get; init; }
}

/// <summary>POST /api/scheduled-send/test 响应。</summary>
public sealed record ScheduledSendTestResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    /// <summary>生成的招呼正文（preview 与 send 都会返回）。</summary>
    [JsonPropertyName("text")] public string Text { get; init; } = "";

    [JsonPropertyName("message")] public string Message { get; init; } = "";
}
