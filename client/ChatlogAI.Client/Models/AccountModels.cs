using System.Text.Json.Serialization;

namespace ChatlogAI.Client.Models;

/// <summary>
/// 微信账号切换（T19）的接口模型。
///
/// 字段的唯一事实来源是 app/server.py 的 <c>/api/wechat/*</c> 那一段（§7.17），不是文档转述：
/// 逐字对齐 <c>_normalize_upstream_account</c> / <c>pending_switch_payload</c> 的键名，
/// 不做别名兜底——上游 / 后端的键一旦对不上，宁可显示默认值让人看出来，也不要"猜中一半"。
///
/// 全部是只读语义的 record：这些对象要么来自响应反序列化，要么是本地状态快照，
/// 界面改动一律走新建对象，不原地改（与 AutoReplyModels 的约定一致）。
/// </summary>
public sealed record WechatAccountInfo
{
    [JsonPropertyName("account")] public string Account { get; init; } = "";

    /// <summary>显示名。后端已在归一化时回退成 account，但上游也可能给空串。</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary>密钥是否已解析就绪——这是"能不能切换"的权威判据（§14.4.1.1）。</summary>
    [JsonPropertyName("keyReady")] public bool KeyReady { get; init; }

    [JsonPropertyName("switchable")] public bool Switchable { get; init; }

    [JsonPropertyName("hasDecryptedDbs")] public bool HasDecryptedDbs { get; init; }

    /// <summary>是否当前生效账号（后端按 config.wechat_account 计算）。</summary>
    [JsonPropertyName("isActive")] public bool IsActive { get; init; }

    /// <summary>显示名兜底：拿不到 name 就退回 account，界面任何一处都不该显示空白。</summary>
    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Account : Name;
}

/// <summary>
/// 待确认状态。<c>GET /api/wechat/account/pending</c> 与 accounts 里的 <c>pendingSwitch</c>
/// 共用同一份结构（后端只保留一个出口），所以客户端也只需要一个类型。
/// </summary>
public sealed record PendingAccountSwitch
{
    [JsonPropertyName("pending")] public bool Pending { get; init; }

    /// <summary>数据源已切到的账号（需要用户在微信客户端登录的那个）。</summary>
    [JsonPropertyName("account")] public string Account { get; init; } = "";

    [JsonPropertyName("previousAccount")] public string PreviousAccount { get; init; } = "";

    /// <summary>ISO 时间串；后端可能给 null。</summary>
    [JsonPropertyName("requestedAt")] public string? RequestedAt { get; init; }
}

/// <summary>GET /api/wechat/accounts 响应。</summary>
public sealed record WechatAccountsResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    /// <summary>当前生效账号（config.wechat_account，为空时取上游 default_account）。</summary>
    [JsonPropertyName("active")] public string Active { get; init; } = "";

    [JsonPropertyName("defaultAccount")] public string DefaultAccount { get; init; } = "";

    [JsonPropertyName("accounts")] public IReadOnlyList<WechatAccountInfo> Accounts { get; init; } = [];

    /// <summary>后端恒返回该对象；给默认值是为了免掉每处调用都判空。</summary>
    [JsonPropertyName("pendingSwitch")] public PendingAccountSwitch PendingSwitch { get; init; } = new();
}

/// <summary>POST /api/wechat/account/resolve 响应：授权页地址（降级为上游界面首页）。</summary>
public sealed record AccountResolveResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("authorizeUrl")] public string AuthorizeUrl { get; init; } = "";
}

/// <summary>GET /api/wechat/account/resolve-status 响应（授权轮询用）。</summary>
public sealed record AccountResolveStatusResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("account")] public string Account { get; init; } = "";

    [JsonPropertyName("keyReady")] public bool KeyReady { get; init; }

    [JsonPropertyName("switchable")] public bool Switchable { get; init; }

    /// <summary>后端给的一句话（"密钥已就绪，可以切换" / "尚未完成密钥解析…"），直接显示。</summary>
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

/// <summary>POST /api/wechat/account/switch 响应。</summary>
public sealed record AccountSwitchResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("previousAccount")] public string PreviousAccount { get; init; } = "";

    [JsonPropertyName("active")] public string Active { get; init; } = "";

    /// <summary>恒为 true：切换后必然进入待确认（这是安全设计的落点，不是可选分支）。</summary>
    [JsonPropertyName("pendingConfirm")] public bool PendingConfirm { get; init; }

    /// <summary>提醒文案，直接显示给用户（含"请把微信客户端切到该账号"）。</summary>
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

/// <summary>GET /api/wechat/account/pending 响应。</summary>
public sealed record AccountPendingResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("pending")] public bool Pending { get; init; }

    [JsonPropertyName("account")] public string Account { get; init; } = "";

    [JsonPropertyName("previousAccount")] public string PreviousAccount { get; init; } = "";

    [JsonPropertyName("requestedAt")] public string? RequestedAt { get; init; }
}

/// <summary>POST /api/wechat/account/confirm 响应。</summary>
public sealed record AccountConfirmResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

/// <summary>resolve / switch 两个端点的请求体，键名都是 <c>account</c>。</summary>
public sealed record AccountRequest
{
    [JsonPropertyName("account")] public string Account { get; init; } = "";
}
