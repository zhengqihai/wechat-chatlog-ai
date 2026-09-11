using System.Text.Json.Serialization;

namespace ChatlogAI.Client.Models;

/// <summary>
/// 导出格式（规格见 docs/DOTNET_CLIENT_PLAN.md §8）。
/// 枚举值只用于内部判断，界面上的下拉用字符串 key 表达（见 <see cref="ExportViewModel"/> 之外的 XAML）。
/// </summary>
public enum ExportFormat
{
    Markdown,
    Html,
    Json,
    Csv,
}

/// <summary>
/// 下拉框里的一项（显示名 + 枚举值）。
///
/// 为什么用"选项对象 + SelectedItem 绑定"而不是主界面筛选区那种
/// "SelectedValuePath=Tag 的 ComboBoxItem + 字符串 key"：
/// 主界面绑的是 VM 里的字符串状态（none / recent），XAML 里写死一组 ComboBoxItem 是合适的；
/// 而这里的取值是<b>枚举</b>，若在 XAML 里再写一遍字符串映射，就多出一处"改了枚举忘了改 XAML"
/// 的漂移点。选项对象让 XAML 只负责显示，映射关系留在 C# 里。
/// </summary>
public sealed record ExportFormatOption(string Label, ExportFormat Format);

/// <summary>时间单位下拉项（Tag 取值与主界面 TimeUnit 同义：minutes / hours / days）。</summary>
public sealed record ExportTimeUnitOption(string Label, string Unit);

/// <summary>
/// 打开导出对话框时对主界面状态的<b>一次性快照</b>。
///
/// 为什么是快照而不是直接传 <c>MainViewModel</c>：
/// 1. 导出期间主界面仍在 5 秒轮询，会话与筛选随时可能变化；导出必须锁定"点下按钮那一刻"的上下文，
///    否则会出现"对话框里写的是 A 会话，导出的却是 B 会话"。
/// 2. 避免导出模块反向依赖主 VM —— 主 VM 是 T9/T10 的改动热点，
///    导出只读这一个不可变记录，两边的改动面完全不相交。
/// </summary>
public sealed record ExportContext
{
    public string ChatId { get; init; } = "";

    public string ChatName { get; init; } = "";

    /// <summary>"群聊" / "私聊"（<c>ChatItemViewModel.TypeText</c>，已是展示文案）。</summary>
    public string ChatTypeText { get; init; } = "";

    /// <summary>默认是否限制时间范围；取自主界面当前筛选（<c>RecentHours is not null</c>）。</summary>
    public bool HasTimeLimit { get; init; }

    /// <summary>minutes / hours / days，与主界面 <c>TimeUnit</c> 同义。</summary>
    public string TimeUnit { get; init; } = "hours";

    public int TimeValue { get; init; } = 24;
}

/// <summary>
/// 实际生效的导出时间范围。既用于 JSON 的结构化字段，也用于 Markdown / HTML 的说明行。
/// </summary>
public sealed record ExportRange
{
    /// <summary>none（不限制）/ recent（最近 N 分钟·小时·天）。</summary>
    [JsonPropertyName("mode")] public string Mode { get; init; } = "none";

    [JsonPropertyName("unit")] public string? Unit { get; init; }

    [JsonPropertyName("value")] public int? Value { get; init; }

    /// <summary>面向读者的范围描述，如"最近 24 小时"。</summary>
    [JsonPropertyName("text")] public string Text { get; init; } = "不限制时间";
}

/// <summary>JSON 格式里 <c>chat</c> 字段的投影。</summary>
public sealed record ExportChatInfo
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary>群聊 / 私聊（展示文案；微信原始取值 group / friend 未在客户端 VM 上暴露）。</summary>
    [JsonPropertyName("type")] public string Type { get; init; } = "";
}

/// <summary>JSON 格式里 <c>messages</c> 数组的一条。</summary>
public sealed record ExportMessage
{
    [JsonPropertyName("time")] public string Time { get; init; } = "";

    [JsonPropertyName("sender")] public string Sender { get; init; } = "";

    /// <summary>微信 renderType（text / image / voice …）。</summary>
    [JsonPropertyName("type")] public string Type { get; init; } = "";

    [JsonPropertyName("content")] public string Content { get; init; } = "";

    /// <summary>
    /// 图片消息的本服务代理地址；非图片消息为 <c>null</c>，由
    /// <c>JsonIgnoreCondition.WhenWritingNull</c> 略过，从而不改变原有输出的形状。
    /// </summary>
    [JsonPropertyName("image_url")] public string? ImageUrl { get; init; }
}

/// <summary>
/// JSON 格式的顶层结构：<c>{chat, exported_at, range, messages[], ai_history[]?}</c>（规格 §8）。
/// <c>ai_history</c> 为 <c>null</c> 时会被 <c>JsonIgnoreCondition.WhenWritingNull</c> 略过，
/// 从而满足"未勾选含 AI 历史时不出现该字段"。
/// </summary>
public sealed record ExportPayload
{
    [JsonPropertyName("chat")] public ExportChatInfo Chat { get; init; } = new();

    /// <summary>ISO 8601 带时区偏移，如 <c>2026-09-10T15:04:05+08:00</c>。</summary>
    [JsonPropertyName("exported_at")] public string ExportedAt { get; init; } = "";

    [JsonPropertyName("range")] public ExportRange Range { get; init; } = new();

    [JsonPropertyName("messages")] public IReadOnlyList<ExportMessage> Messages { get; init; } = [];

    [JsonPropertyName("ai_history")] public IReadOnlyList<HistoryItem>? AiHistory { get; init; }
}

/// <summary>
/// 格式化产物 + 编码要求。
/// 只有 CSV 需要 BOM：Excel 对不带 BOM 的 UTF-8 会按本地代码页解码，中文必然乱码；
/// JSON / Markdown / HTML 带 BOM 反而会让严格的解析器出错（JSON 规范明确不建议）。
/// </summary>
public sealed record ExportDocument(string Text, bool WriteBom);

/// <summary>
/// VM 请求 View 弹"另存为"的参数。
/// 与 <c>MainViewModel.AlertRequested</c> 同一思路：VM 不引用 <c>Microsoft.Win32</c>，
/// 那是 View 的职责，也是 MVVM 里唯一不该由 VM 触碰的边界。
/// </summary>
public sealed record ExportSaveRequest
{
    public string DefaultFileName { get; init; } = "";

    /// <summary>SaveFileDialog 的 Filter 串。</summary>
    public string Filter { get; init; } = "";

    /// <summary>默认目录（「文档」）；为空表示交给系统决定。</summary>
    public string DefaultDirectory { get; init; } = "";
}
