using System.Text.Json.Serialization;

namespace ChatlogAI.Client.Models;

/// <summary>群成员（GET /api/group/members 的 members[]）。</summary>
public sealed record GroupMember
{
    [JsonPropertyName("username")] public string Username { get; init; } = "";

    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";

    /// <summary>指向本服务的头像地址；本机没有缓存时为空串。</summary>
    [JsonPropertyName("avatar")] public string Avatar { get; init; } = "";

    /// <summary>首次观测到的 Unix 秒；从未扫描过时为 null（不编造）。</summary>
    [JsonPropertyName("first_seen")] public double? FirstSeen { get; init; }

    [JsonPropertyName("last_seen")] public double? LastSeen { get; init; }
}

public sealed record GroupMembersResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("chatroom")] public string Chatroom { get; init; } = "";

    [JsonPropertyName("name")] public string Name { get; init; } = "";

    [JsonPropertyName("count")] public int Count { get; init; }

    [JsonPropertyName("last_scan_at")] public double? LastScanAt { get; init; }

    [JsonPropertyName("members")] public IReadOnlyList<GroupMember> Members { get; init; } = [];
}

/// <summary>群成员变动（快照差分的结果，时间 = 检测到的时间）。</summary>
public sealed record GroupChange
{
    /// <summary><c>join</c> 或 <c>leave</c>。</summary>
    [JsonPropertyName("type")] public string Type { get; init; } = "";

    [JsonPropertyName("username")] public string Username { get; init; } = "";

    [JsonPropertyName("name")] public string Name { get; init; } = "";

    [JsonPropertyName("detected_at")] public double DetectedAt { get; init; }

    [JsonIgnore] public bool IsJoin => string.Equals(Type, "join", StringComparison.OrdinalIgnoreCase);
}

public sealed record GroupChangesResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("target_id")] public string TargetId { get; init; } = "";

    [JsonPropertyName("changes")] public IReadOnlyList<GroupChange> Changes { get; init; } = [];
}

public sealed record GroupWatchConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;

    [JsonPropertyName("interval_minutes")] public int IntervalMinutes { get; init; } = 120;
}

public sealed record GroupWatchConfigResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("config")] public GroupWatchConfig Config { get; init; } = new();

    [JsonPropertyName("available")] public bool Available { get; init; }
}

/// <summary>POST /api/group/watch/config 的部分更新请求（null 字段不下发）。</summary>
public sealed record GroupWatchConfigPatch
{
    [JsonPropertyName("enabled")] public bool? Enabled { get; init; }

    [JsonPropertyName("interval_minutes")] public int? IntervalMinutes { get; init; }
}

public sealed record GroupScanResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("groups")] public int Groups { get; init; }

    [JsonPropertyName("baselines")] public int Baselines { get; init; }

    [JsonPropertyName("joined")] public int Joined { get; init; }

    [JsonPropertyName("left")] public int Left { get; init; }

    [JsonPropertyName("changed")] public int Changed { get; init; }

    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

public sealed record GroupWatchStatusResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("enabled")] public bool Enabled { get; init; }

    [JsonPropertyName("available")] public bool Available { get; init; }

    [JsonPropertyName("running")] public bool Running { get; init; }

    [JsonPropertyName("last_scan_at")] public double? LastScanAt { get; init; }

    [JsonPropertyName("last_error")] public string LastError { get; init; } = "";
}

/// <summary>发言排行的一项（GET/POST /api/stats/watch 的 members[]）。</summary>
public sealed record StatsMember
{
    [JsonPropertyName("username")] public string Username { get; init; } = "";

    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";

    [JsonPropertyName("count")] public int Count { get; init; }

    /// <summary>占今日总消息的百分比，0–100。</summary>
    [JsonPropertyName("share")] public double Share { get; init; }
}

public sealed record StatsWatchResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("target_id")] public string TargetId { get; init; } = "";

    /// <summary><c>group</c> 或 <c>friend</c>。</summary>
    [JsonPropertyName("type")] public string Type { get; init; } = "";

    [JsonPropertyName("date")] public string Date { get; init; } = "";

    [JsonPropertyName("window_start")] public string WindowStart { get; init; } = "";

    [JsonPropertyName("updated_at")] public double UpdatedAt { get; init; }

    [JsonPropertyName("total")] public int Total { get; init; }

    [JsonPropertyName("me_count")] public int MeCount { get; init; }

    [JsonPropertyName("other_count")] public int OtherCount { get; init; }

    [JsonPropertyName("member_count")] public int MemberCount { get; init; }

    [JsonPropertyName("members")] public IReadOnlyList<StatsMember> Members { get; init; } = [];
}
