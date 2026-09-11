using System.Text.Json.Serialization;

namespace ChatlogAI.Client.Models;

/// <summary>
/// GET /api/contacts 的 <c>counts</c>（§14.7.1 实测键，逐字对齐，不做改名）。
/// 公众号在微信里分订阅号 / 服务号 / 企业号，三个计数各自独立，不要合并展示。
/// </summary>
public sealed record ContactCounts
{
    [JsonPropertyName("friends")] public int Friends { get; init; }

    [JsonPropertyName("groups")] public int Groups { get; init; }

    [JsonPropertyName("officials")] public int Officials { get; init; }

    [JsonPropertyName("officialSubscriptions")] public int OfficialSubscriptions { get; init; }

    [JsonPropertyName("officialServices")] public int OfficialServices { get; init; }

    [JsonPropertyName("officialEnterprises")] public int OfficialEnterprises { get; init; }

    [JsonPropertyName("officialUnknown")] public int OfficialUnknown { get; init; }

    [JsonPropertyName("formerFriends")] public int FormerFriends { get; init; }

    [JsonPropertyName("blocked")] public int Blocked { get; init; }

    [JsonPropertyName("total")] public int Total { get; init; }
}

/// <summary>
/// 通讯录联系人（字段逐字对齐 docs/DOTNET_CLIENT_PLAN.md §14.7.1 的实测清单）。
/// 后端已做过一层归一：上游多给的字段不会出现在这里。
/// </summary>
public sealed record ContactSummary
{
    [JsonPropertyName("username")] public string Username { get; init; } = "";

    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";

    [JsonPropertyName("remark")] public string Remark { get; init; } = "";

    [JsonPropertyName("nickname")] public string Nickname { get; init; } = "";

    /// <summary>微信号。</summary>
    [JsonPropertyName("alias")] public string Alias { get; init; } = "";

    /// <summary>上游未标注类型，后端已归一为文本（可能是 "1" / "2" 这类编码），展示层不要当数字用。</summary>
    [JsonPropertyName("gender")] public string Gender { get; init; } = "";

    [JsonPropertyName("signature")] public string Signature { get; init; } = "";

    /// <summary>friend / group（实测群聊为 "group"）。</summary>
    [JsonPropertyName("type")] public string Type { get; init; } = "";

    [JsonPropertyName("country")] public string Country { get; init; } = "";

    [JsonPropertyName("province")] public string Province { get; init; } = "";

    [JsonPropertyName("city")] public string City { get; init; } = "";

    [JsonPropertyName("region")] public string Region { get; init; } = "";

    [JsonPropertyName("sourceScene")] public string SourceScene { get; init; } = "";

    [JsonPropertyName("source")] public string Source { get; init; } = "";

    /// <summary>原始添加时间（后端归一为文本），排查用。</summary>
    [JsonPropertyName("addTime")] public string AddTime { get; init; } = "";

    /// <summary>可读的添加时间文本，界面直接展示这个。</summary>
    [JsonPropertyName("addTimeText")] public string AddTimeText { get; init; } = "";

    [JsonPropertyName("commonChatroomCount")] public int CommonChatroomCount { get; init; }

    /// <summary>
    /// **本服务**的头像代理地址（<c>/api/contacts/avatar?username=..</c>），可直接绑定；
    /// 上游 10392 的地址不会下发到客户端。
    /// </summary>
    [JsonPropertyName("avatar")] public string Avatar { get; init; } = "";

    /// <summary>微信 CDN 头像地址。导出内容请用这个，不要用 <see cref="Avatar"/>（本机临时地址，外发即死链）。</summary>
    [JsonPropertyName("avatarLink")] public string AvatarLink { get; init; } = "";

    [JsonPropertyName("pinyinKey")] public string PinyinKey { get; init; } = "";

    [JsonPropertyName("pinyinInitial")] public string PinyinInitial { get; init; } = "";

    [JsonIgnore] public bool IsGroup => string.Equals(Type, "group", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// GET /api/contacts 响应。
/// 注意：该接口**没有分页**（§14.7.1 实测结论 1），一次返回全部，界面必须自己做虚拟化。
/// </summary>
public sealed record ContactListResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("total")] public int Total { get; init; }

    [JsonPropertyName("counts")] public ContactCounts? Counts { get; init; }

    [JsonPropertyName("contacts")] public IReadOnlyList<ContactSummary> Contacts { get; init; } = [];

    [JsonPropertyName("source")] public string Source { get; init; } = "";

    [JsonPropertyName("sourceRequested")] public string SourceRequested { get; init; } = "";

    [JsonPropertyName("sourceFallback")] public bool SourceFallback { get; init; }

    [JsonPropertyName("sourceFallbackReason")] public string SourceFallbackReason { get; init; } = "";

    [JsonPropertyName("sourceFallbackMessage")] public string SourceFallbackMessage { get; init; } = "";

    [JsonPropertyName("sourceFallbackRetryAfterSeconds")] public int SourceFallbackRetryAfterSeconds { get; init; }
}

/// <summary>
/// GET /api/contacts 的查询条件。
/// 七个 <c>Include*</c> 一一对应上游的 <c>include_*</c> 布尔开关（§14.7.2），**不额外造 scope 语法**。
/// 保持 <c>bool?</c>：null 表示"不下发该参数"，由上游默认值决定（好友 / 群聊 / 公众号默认开）。
/// </summary>
public sealed record ContactQuery
{
    public bool? IncludeFriends { get; init; }

    public bool? IncludeGroups { get; init; }

    public bool? IncludeOfficials { get; init; }

    public bool? IncludeOfficialSubscriptions { get; init; }

    public bool? IncludeOfficialServices { get; init; }

    public bool? IncludeFormerFriends { get; init; }

    public bool? IncludeBlocked { get; init; }

    /// <summary>服务端关键词过滤，空 / null 表示不限。</summary>
    public string? Keyword { get; init; }

    /// <summary>指定微信账号；空 / null 由后端取默认账号。</summary>
    public string? Account { get; init; }
}
