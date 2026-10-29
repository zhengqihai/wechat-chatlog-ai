using System.Text.Json.Serialization;

namespace ChatlogAI.Client.Models;

/// <summary>对外暴露的供应商信息（**不含明文 Key**）。</summary>
public sealed record ProviderInfo
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary>预设类型：deepseek / kimi / zhipu / qwen / custom。</summary>
    [JsonPropertyName("provider_type")] public string ProviderType { get; init; } = "";

    /// <summary>OpenAI 兼容基址，**不含** /chat/completions。</summary>
    [JsonPropertyName("base_url")] public string BaseUrl { get; init; } = "";

    [JsonPropertyName("model")] public string Model { get; init; } = "";

    [JsonPropertyName("api_key_masked")] public string ApiKeyMasked { get; init; } = "";

    /// <summary>等价于 balance_path 非空。</summary>
    [JsonPropertyName("balance_supported")] public bool BalanceSupported { get; init; }

    [JsonPropertyName("active")] public bool Active { get; init; }
}

/// <summary>
/// 供应商预设（PROVIDER_PRESETS 的一项）。
/// 界面在用户选择 provider_type 后用它自动填充 base_url / model，
/// 所以必须由后端下发而不是在客户端硬编码——预设表属于服务端事实。
/// </summary>
public sealed record ProviderPreset
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    [JsonPropertyName("base_url")] public string BaseUrl { get; init; } = "";

    [JsonPropertyName("model")] public string Model { get; init; } = "";

    [JsonPropertyName("balance_path")] public string? BalancePath { get; init; }

    [JsonPropertyName("balance_parser")] public string? BalanceParser { get; init; }
}

/// <summary>GET /api/providers 响应。</summary>
public sealed record ProviderListResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("providers")] public IReadOnlyList<ProviderInfo> Providers { get; init; } = [];

    [JsonPropertyName("active_id")] public string ActiveId { get; init; } = "";

    [JsonPropertyName("presets")] public IReadOnlyDictionary<string, ProviderPreset> Presets { get; init; }
        = new Dictionary<string, ProviderPreset>();
}

/// <summary>
/// POST /api/providers 请求体。
/// **更新时 <see cref="ApiKey"/> 传 null 表示「沿用旧 Key」**（序列化时被忽略）；
/// 传空串与传 null 在服务端等价，但客户端统一用 null 以免误触 400。
/// </summary>
public sealed record SaveProviderRequest
{
    /// <summary>传入 = 更新；不传（null）= 新增。</summary>
    [JsonPropertyName("id")] public string? Id { get; init; }

    [JsonPropertyName("provider_type")] public string? ProviderType { get; init; }

    [JsonPropertyName("name")] public string? Name { get; init; }

    [JsonPropertyName("base_url")] public string BaseUrl { get; init; } = "";

    [JsonPropertyName("model")] public string Model { get; init; } = "";

    [JsonPropertyName("api_key")] public string? ApiKey { get; init; }

    [JsonPropertyName("balance_path")] public string? BalancePath { get; init; }

    [JsonPropertyName("balance_parser")] public string? BalanceParser { get; init; }
}

/// <summary>POST /api/providers 响应：返回落库后的 id（新增时由服务端生成）。</summary>
public sealed record SaveProviderResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("id")] public string Id { get; init; } = "";
}

/// <summary>POST /api/providers/validate 请求体。</summary>
public sealed record ValidateProviderRequest
{
    [JsonPropertyName("base_url")] public string BaseUrl { get; init; } = "";

    [JsonPropertyName("model")] public string Model { get; init; } = "";

    [JsonPropertyName("api_key")] public string ApiKey { get; init; } = "";
}

/// <summary>POST /api/providers/activate 请求体。</summary>
public sealed record ActivateProviderRequest
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
}

/// <summary>形状为 <c>{"status":"success","message":"..."}</c> 的通用响应。</summary>
public sealed record ApiMessageResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

/// <summary>供应商余额。字段随 parser 不同，因此除 <see cref="Text"/> 外全部可空。</summary>
public sealed record BalanceInfo
{
    [JsonPropertyName("available")] public bool Available { get; init; }

    /// <summary>可直接展示的一行文案；deepseek 形如「CNY 12.34；USD 0.00」。</summary>
    [JsonPropertyName("text")] public string Text { get; init; } = "";

    [JsonPropertyName("message")] public string Message { get; init; } = "";

    [JsonPropertyName("available_balance")] public string? AvailableBalance { get; init; }

    [JsonPropertyName("voucher_balance")] public string? VoucherBalance { get; init; }

    [JsonPropertyName("cash_balance")] public string? CashBalance { get; init; }
}

/// <summary>GET /api/providers/balance 响应。不支持余额时 <see cref="Balance"/> 为 null。</summary>
public sealed record BalanceResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("supported")] public bool Supported { get; init; }

    [JsonPropertyName("message")] public string Message { get; init; } = "";

    [JsonPropertyName("balance")] public BalanceInfo? Balance { get; init; }
}
