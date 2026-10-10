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

// ==================== 数据库密钥手动导入（阶段 D3） ====================
//
// 上游的授权页（10392）是唯一能"解析出 db_key"的东西，而它已被移除。
// 此后 `config/wechat-keys.json` 是唯一的密钥来源，所以必须有一个"用户把密钥交给程序"的入口。
// 字段逐字对齐 app/server.py 的 /api/keys/* 两个端点。

/// <summary>
/// 一把候选密钥的现状。
/// <see cref="Verified"/> 是后端**真的解开了本机 session.db 第 1 页**的结果，
/// 不是格式检查 —— 只看格式的话，一把合法但错误的密钥会让界面显示"已就绪"而链路全 503。
/// </summary>
public sealed record KeyEntryInfo
{
    [JsonPropertyName("account")] public string Account { get; init; } = "";

    /// <summary>来源：<c>local-backup</c>（config/wechat-keys.json）/ <c>upstream</c>（上游产物）/ <c>import</c>。</summary>
    [JsonPropertyName("source")] public string Source { get; init; } = "";

    [JsonPropertyName("dbStoragePath")] public string DbStoragePath { get; init; } = "";

    [JsonPropertyName("keyHexLength")] public int KeyHexLength { get; init; }

    [JsonPropertyName("verified")] public bool Verified { get; init; }

    /// <summary>后端给的一句话（成功原因或失败原因），直接显示。</summary>
    [JsonPropertyName("message")] public string Message { get; init; } = "";

    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Account) ? "（未识别账号）" : Account;

    [JsonIgnore] public string VerifiedBadge => Verified ? "可用" : "不可用";

    /// <summary>来源的可读文案。认不出的取值原样显示，不编造。</summary>
    [JsonIgnore] public string SourceText => Source switch
    {
        "local-backup" => "本机备份（config/wechat-keys.json）",
        "upstream" => "上游产物（account_keys.json）",
        "import" => "手动导入",
        _ => Source,
    };
}

/// <summary>密钥可以来自哪些文件（界面要把路径展示给用户，让他知道去哪里拿）。</summary>
public sealed record KeySources
{
    [JsonPropertyName("localKeysFile")] public string LocalKeysFile { get; init; } = "";

    [JsonPropertyName("localKeysFileExists")] public bool LocalKeysFileExists { get; init; }

    [JsonPropertyName("upstreamKeysFile")] public string UpstreamKeysFile { get; init; } = "";

    [JsonPropertyName("upstreamKeysFileExists")] public bool UpstreamKeysFileExists { get; init; }
}

/// <summary>GET /api/keys/status 响应。</summary>
public sealed record KeyStatusResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("localdbAvailable")] public bool LocaldbAvailable { get; init; }

    [JsonPropertyName("keys")] public IReadOnlyList<KeyEntryInfo> Keys { get; init; } = [];

    [JsonPropertyName("activeAccount")] public string ActiveAccount { get; init; } = "";

    [JsonPropertyName("usableCount")] public int UsableCount { get; init; }

    /// <summary>一条密钥都没有时的原因文案；有内容就说明"为什么没得用"。</summary>
    [JsonPropertyName("missingReason")] public string MissingReason { get; init; } = "";

    [JsonPropertyName("sources")] public KeySources? Sources { get; init; }

    /// <summary>本地链路的初始化错误（有值就说明"密钥在但链路没起来"）。</summary>
    [JsonPropertyName("initError")] public string InitError { get; init; } = "";

    /// <summary>自研链路完全不可用时的说明。</summary>
    [JsonPropertyName("detail")] public string Detail { get; init; } = "";
}

/// <summary>POST /api/keys/import 请求体。</summary>
public sealed record KeyImportRequest
{
    /// <summary>粘贴内容：密钥文件全文，或 64 位 hex 的裸 db_key。</summary>
    [JsonPropertyName("content")] public string Content { get; init; } = "";

    /// <summary>只粘贴裸 db_key 时用于推断账号；留空则由后端从库目录名推断。</summary>
    [JsonPropertyName("account")] public string? Account { get; init; }

    /// <summary>只粘贴裸 db_key 时**必填**：数据库目录（…\db_storage）。</summary>
    [JsonPropertyName("db_storage_path")] public string? DbStoragePath { get; init; }
}

/// <summary>POST /api/keys/import 响应。</summary>
public sealed record KeyImportResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("savedTo")] public string SavedTo { get; init; } = "";

    [JsonPropertyName("imported")] public IReadOnlyList<KeyEntryInfo> Imported { get; init; } = [];

    /// <summary>本次未通过实测校验、**没有**写盘的条目。</summary>
    [JsonPropertyName("rejected")] public IReadOnlyList<KeyEntryInfo> Rejected { get; init; } = [];

    [JsonPropertyName("message")] public string Message { get; init; } = "";
}
