using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatlogAI.Client.Models;

namespace ChatlogAI.Client.Services;

/// <summary>
/// 导出格式化器（T11，规格见 docs/DOTNET_CLIENT_PLAN.md §8）。
///
/// 为什么把它做成<b>无状态静态类</b>、且完全不碰 UI / 网络：
/// 1. 四种格式的区别只有"排版"，不该各自去拉一次数据 —— 数据获取与排版分开，
///    将来加一种格式（如 TXT）只要加一个 <c>Format</c> 分支，不碰取数逻辑；
/// 2. 纯函数才能被单元测试直接钉住：给一组消息，比较输出字符串即可，
///    不需要起窗口、不需要假后端。这是"导出"这种一次性动作最值得投资的地方。
///
/// 注意这里<b>刻意</b>重复了 <c>MainViewModel</c> 里的两小段逻辑（时间戳归一化、时间范围裁剪）。
/// 原因：主 VM 是 T9/T10 两个并行改造窗口的改动热点，从导出侧去调用它就必须把它改成 public，
/// 等于把两个互不相干的功能耦合在一起。重复 5 行代码换取"改动面完全不相交"，是划算的。
/// </summary>
public static class ChatExporter
{
    /// <summary>
    /// 单次导出的消息条数上限。
    /// 必须与后端 <c>/api/messages</c> 的 <c>limit</c> 上限（<c>app/server.py</c> 的 <c>le=2000</c>）
    /// 以及 <c>MainViewModel.UnlimitedRequestLimit</c> 三处保持一致，
    /// 否则会出现"界面看得到、导出却少一截"这种最难发现的错。
    /// </summary>
    public const int MaxMessages = 2000;

    private const string UnitMinutes = "minutes";
    private const string UnitHours = "hours";
    private const string UnitDays = "days";
    private const string RangeModeNone = "none";
    private const string RangeModeRecent = "recent";

    /// <summary>JSON 输出的序列化约定，与 <c>ChatlogApiClient.WriteOptions</c> 同源。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Python 侧是 ensure_ascii=False；导出文件是给人看的，中文不该被写成 \uXXXX。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // ai_history 未勾选时为 null，靠这个条件被整个字段略过（规格 §8：不出现该字段）。
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ==================== 元信息（供对话框拼文件名 / Filter） ====================

    public static string ExtensionOf(ExportFormat format) => format switch
    {
        ExportFormat.Markdown => ".md",
        ExportFormat.Html => ".html",
        ExportFormat.Json => ".json",
        ExportFormat.Csv => ".csv",
        _ => ".txt",
    };

    public static string DescribeFormat(ExportFormat format) => format switch
    {
        ExportFormat.Markdown => "Markdown 文档",
        ExportFormat.Html => "HTML 网页",
        ExportFormat.Json => "JSON 数据",
        ExportFormat.Csv => "CSV 表格",
        _ => "文件",
    };

    /// <summary>SaveFileDialog 的 Filter 串。格式说明与扩展名成对出现，用户才看得懂自己在存什么。</summary>
    public static string BuildFileFilter(ExportFormat format)
        => $"{DescribeFormat(format)} (*{ExtensionOf(format)})|*{ExtensionOf(format)}|所有文件 (*.*)|*.*";

    /// <summary>文件名 <c>&lt;会话名&gt;_&lt;时间戳&gt;.&lt;ext&gt;</c>（规格 §8）。</summary>
    public static string BuildDefaultFileName(ExportContext context, ExportFormat format, DateTimeOffset now)
        => $"{SanitizeFileName(DisplayName(context))}_{now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}{ExtensionOf(format)}";

    // ==================== 时间范围 ====================

    /// <summary>不限制时间（范围描述的默认值）。</summary>
    public static ExportRange UnlimitedRange { get; } = new();

    /// <summary>由「最近 N 分钟 / 小时 / 天」构造范围；同时产出给读者看的 <c>Text</c>。</summary>
    public static ExportRange CreateRecentRange(string unit, int value)
    {
        var safe = Math.Max(1, value);
        return new ExportRange
        {
            Mode = RangeModeRecent,
            Unit = NormalizeUnit(unit),
            Value = safe,
            Text = $"最近 {safe} {UnitLabel(unit)}",
        };
    }

    /// <summary>折算成小时（服务端 <c>recent_hours</c> 参数的单位）；不限制时为 <c>null</c>。</summary>
    public static double? ToRecentHours(ExportRange range)
    {
        if (range.Mode != RangeModeRecent || range.Value is not { } value)
        {
            return null;
        }

        return range.Unit switch
        {
            UnitMinutes => value / 60d,
            UnitDays => value * 24d,
            _ => (double)value,
        };
    }

    /// <summary>
    /// 按时间范围裁剪（本地二次过滤，语义与 <c>MainViewModel.ApplyFilters</c> 一致）：
    /// 时间戳解析不出来的消息会被丢弃 —— 这一点必须和界面保持一致，
    /// 否则用户会看到"导出的条数比界面上更多"。
    ///
    /// 为什么服务端已经传了 <c>recent_hours</c> 还要再过滤一遍：
    /// 服务端的 <c>filter_messages</c> 对"解析不出时间"的消息是<b>保留</b>的，而界面是丢弃的，
    /// 两边判据不同，必须在客户端对齐到界面的口径。
    /// </summary>
    public static IReadOnlyList<ChatMessage> FilterByRange(
        IReadOnlyList<ChatMessage> messages,
        ExportRange range,
        DateTimeOffset now)
    {
        if (ToRecentHours(range) is not { } hours)
        {
            return messages;
        }

        var cutoff = now.AddHours(-hours);
        return messages.Where(message => ResolveTimestamp(message) is { } value && value >= cutoff).ToList();
    }

    /// <summary>时间戳归一化：上游同一条链路里既有秒也有毫秒（见 docs/API_REFERENCE.md §8.2）。</summary>
    public static DateTimeOffset? ResolveTimestamp(ChatMessage message)
        => message.TimestampValue
           ?? (DateTimeOffset.TryParse(
                   message.Time,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out var parsed)
               ? parsed
               : null);

    // ==================== 排版 ====================

    /// <param name="aiHistory">
    /// 勾选"包含 AI 历史"时传入 <c>/api/history</c> 的结果（即使为空也给空列表，
    /// 这样 JSON 里会明确出现 <c>ai_history: []</c>，而不是让人分不清"没勾"与"勾了但没有"）；
    /// 未勾选时传 <c>null</c>。
    /// </param>
    /// <param name="imageDataUris">
    /// 消息 id → <c>data:image/...;base64,...</c>。仅 HTML 格式用得上（内嵌图片、离线可看）。
    /// 为什么由调用方下载而不是本类自己下载：本类是无网络、无状态的纯格式化器（见类注释），
    /// 一旦它自己发起请求，就再也无法用"给一组消息、比较输出字符串"的方式测试。
    /// 为 <c>null</c> 或缺少某条消息时，该图片退化成链接。
    /// </param>
    public static ExportDocument Format(
        ExportFormat format,
        ExportContext context,
        ExportRange range,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<HistoryItem>? aiHistory,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string>? imageDataUris = null)
        => format switch
        {
            // 只有 CSV 带 BOM：Excel 对不带 BOM 的 UTF-8 会按本地代码页解码，中文必然乱码；
            // JSON / Markdown / HTML 带 BOM 反而会让严格的解析器报错（JSON 规范明确不建议）。
            ExportFormat.Markdown => new ExportDocument(FormatMarkdown(context, range, messages, aiHistory, now), false),
            ExportFormat.Html => new ExportDocument(FormatHtml(context, range, messages, aiHistory, now, imageDataUris), false),
            ExportFormat.Json => new ExportDocument(FormatJson(context, range, messages, aiHistory, now), false),
            // CSV 是固定四列的表格，塞不下图片本体；图片消息沿用 content 里的 [图片] 标签。
            ExportFormat.Csv => new ExportDocument(FormatCsv(messages), true),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "不支持的导出格式。"),
        };

    // ---------- Markdown ----------

    private static string FormatMarkdown(
        ExportContext context,
        ExportRange range,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<HistoryItem>? aiHistory,
        DateTimeOffset now)
    {
        var builder = new StringBuilder();

        builder.Append("# ").Append(DisplayName(context)).AppendLine();
        builder.AppendLine();
        builder.Append("- 会话类型：").AppendLine(context.ChatTypeText);
        builder.Append("- 导出时间：").AppendLine(DescribeTime(now));
        builder.Append("- 时间范围：").AppendLine(range.Text);
        builder.Append("- 消息条数：").AppendLine(messages.Count.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.AppendLine("## 聊天记录");
        builder.AppendLine();

        if (messages.Count == 0)
        {
            builder.AppendLine("（该时间范围内没有消息）");
        }
        else
        {
            foreach (var message in messages)
            {
                builder
                    .Append("- [").Append(message.Time).Append("] ")
                    .Append(SenderOf(message)).Append(": ");
                // Markdown 不内嵌图片（内嵌只能上 data URI，会让 .md 变得无法阅读），
                // 改为链接到本服务代理地址：看图需要服务在运行，服务不在时至少链接还在。
                builder.AppendLine(message.IsImage
                    ? $"![图片]({message.ImageUrl})"
                    : OneLine(ContentOf(message)));
            }
        }

        if (aiHistory is { } history)
        {
            builder.AppendLine();
            builder.AppendLine("## AI 对话历史");
            builder.AppendLine();

            if (history.Count == 0)
            {
                builder.AppendLine("（该会话没有 AI 对话记录）");
            }
            else
            {
                foreach (var item in history)
                {
                    // AI 回答本身是 Markdown（可能有多段），这里刻意不再压成一行：
                    // 压平会毁掉它的可读性，而这一节本来就与时间线无关。
                    builder.Append("**").Append(RoleNameOf(item)).Append("**").AppendLine();
                    builder.AppendLine();
                    builder.AppendLine(item.Content.TrimEnd());
                }
            }
        }

        return builder.ToString();
    }

    // ---------- HTML ----------

    private static string FormatHtml(
        ExportContext context,
        ExportRange range,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<HistoryItem>? aiHistory,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string>? imageDataUris)
    {
        var title = WebUtility.HtmlEncode($"{DisplayName(context)} 聊天记录");
        var builder = new StringBuilder();

        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"zh-CN\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\">");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.Append("<title>").Append(title).AppendLine("</title>");
        builder.AppendLine("<style>");
        builder.AppendLine(HtmlStyles);
        builder.AppendLine("</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("<main class=\"sheet\">");

        builder.Append("<h1>").Append(WebUtility.HtmlEncode(DisplayName(context))).AppendLine("</h1>");
        builder.AppendLine("<p class=\"meta\">");
        builder.Append("<span>会话类型：").Append(WebUtility.HtmlEncode(context.ChatTypeText)).AppendLine("</span>");
        builder.Append("<span>导出时间：").Append(WebUtility.HtmlEncode(DescribeTime(now))).AppendLine("</span>");
        builder.Append("<span>时间范围：").Append(WebUtility.HtmlEncode(range.Text)).AppendLine("</span>");
        builder.Append("<span>消息条数：")
            .Append(messages.Count.ToString(CultureInfo.InvariantCulture))
            .AppendLine("</span>");
        builder.AppendLine("</p>");

        builder.AppendLine("<h2>聊天记录</h2>");
        if (messages.Count == 0)
        {
            builder.AppendLine("<p class=\"empty\">（该时间范围内没有消息）</p>");
        }
        else
        {
            builder.AppendLine("<ul class=\"timeline\">");
            foreach (var message in messages)
            {
                builder.Append("<li").Append(message.IsSelf ? " class=\"mine\"" : "").Append(">");
                builder.Append("<span class=\"time\">[").Append(WebUtility.HtmlEncode(message.Time)).Append("]</span> ");
                builder.Append("<span class=\"sender\">").Append(WebUtility.HtmlEncode(SenderOf(message))).Append("</span>");
                if (message.IsImage)
                {
                    // 优先内嵌 base64：导出文件被拷到别的机器上时依然能看到图。
                    // 拿不到字节（下载失败 / 未预取）时退化成链接，不产生破图。
                    if (imageDataUris is not null && imageDataUris.TryGetValue(message.Id, out var dataUri))
                    {
                        builder.Append("<img class=\"photo\" alt=\"图片\" src=\"").Append(dataUri).Append("\">");
                    }
                    else
                    {
                        builder.Append("<a class=\"photo-link\" href=\"")
                            .Append(WebUtility.HtmlEncode(message.ImageUrl))
                            .Append("\">[图片]</a>");
                    }
                }
                else
                {
                    builder.Append("<span class=\"content\">").Append(MultiLineHtml(ContentOf(message))).Append("</span>");
                }

                builder.AppendLine("</li>");
            }

            builder.AppendLine("</ul>");
        }

        if (aiHistory is { } history)
        {
            builder.AppendLine("<h2>AI 对话历史</h2>");
            if (history.Count == 0)
            {
                builder.AppendLine("<p class=\"empty\">（该会话没有 AI 对话记录）</p>");
            }
            else
            {
                builder.AppendLine("<div class=\"ai\">");
                foreach (var item in history)
                {
                    builder.Append("<article class=\"").Append(item.IsUser ? "ask" : "answer").Append("\">");
                    builder.Append("<h3>").Append(WebUtility.HtmlEncode(RoleNameOf(item))).AppendLine("</h3>");
                    // AI 回答是 Markdown，但导出成 HTML 时不做 Markdown 渲染：
                    // 这里的目标是"原样留存"，转义 + 保留换行就够，中间不引入第二次解释。
                    builder.Append("<p>").Append(MultiLineHtml(item.Content)).AppendLine("</p>");
                    builder.AppendLine("</article>");
                }

                builder.AppendLine("</div>");
            }
        }

        builder.AppendLine("</main>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    /// <summary>
    /// 单文件内联样式：不引任何外部资源，双击就能看、Ctrl+P 就能存 PDF。
    /// 色值刻意与 <c>Themes/Theme.xaml</c> 一一对应（浅灰底 / 白卡片 / 深字 / 微信绿），
    /// 让导出的文件与应用看起来是同一个东西；这里是独立 HTML，引不到 DynamicResource，
    /// 只能各写一份，改主题时两处要一起改。
    /// </summary>
    private const string HtmlStyles = """
        :root { color-scheme: light; }
        * { box-sizing: border-box; }
        body {
            margin: 0;
            padding: 32px 16px;
            background: #F2F3F5;
            color: #1F2329;
            font-family: "Microsoft YaHei UI", "Segoe UI", system-ui, sans-serif;
            font-size: 14px;
            line-height: 1.7;
        }
        .sheet {
            max-width: 820px;
            margin: 0 auto;
            padding: 28px 32px 36px;
            background: #FFFFFF;
            border: 1px solid #E5E7EB;
            border-radius: 8px;
        }
        h1 { margin: 0 0 12px; font-size: 20px; font-weight: 600; }
        h2 {
            margin: 32px 0 14px;
            padding-bottom: 8px;
            font-size: 15px;
            font-weight: 600;
            border-bottom: 1px solid #E5E7EB;
        }
        h3 { margin: 0 0 6px; font-size: 12px; font-weight: 600; color: #626A78; }
        .meta { margin: 0; font-size: 12px; color: #626A78; }
        .meta span { display: inline-block; margin-right: 16px; }
        .empty { color: #626A78; }
        .timeline { margin: 0; padding: 0; list-style: none; }
        .timeline li {
            margin: 0 0 10px;
            padding: 8px 10px;
            background: #F7F8FA;
            border: 1px solid #E5E7EB;
            border-radius: 6px;
            overflow-wrap: anywhere;
        }
        .timeline li.mine { background: #E6F7EE; border-color: #BFE7D1; }
        .timeline .time { color: #626A78; font-size: 12px; }
        .timeline .sender { font-weight: 600; margin-left: 6px; }
        .timeline .sender::after { content: ":"; font-weight: 400; color: #5A6270; }
        .timeline .content { white-space: pre-wrap; }
        /* 图片单独成行（块级），否则会挤在"发送者:"同一行上；限制最大边长避免一张长图吃掉整屏。 */
        .timeline img.photo {
            display: block;
            margin: 6px 0 0;
            max-width: 320px;
            max-height: 320px;
            border-radius: 4px;
        }
        .timeline .photo-link { color: #0A7F45; }
        .ai article {
            margin: 0 0 12px;
            padding: 10px 12px;
            background: #F7F8FA;
            border: 1px solid #E5E7EB;
            border-radius: 6px;
        }
        .ai article.answer { background: #FFFFFF; }
        .ai p { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; }
        @media print {
            body { padding: 0; background: #FFFFFF; }
            .sheet { max-width: none; border: 0; border-radius: 0; padding: 0; }
        }
        """;

    // ---------- JSON ----------

    private static string FormatJson(
        ExportContext context,
        ExportRange range,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<HistoryItem>? aiHistory,
        DateTimeOffset now)
    {
        var payload = new ExportPayload
        {
            Chat = new ExportChatInfo
            {
                Id = context.ChatId,
                Name = DisplayName(context),
                Type = context.ChatTypeText,
            },
            // ISO 8601 带时区偏移：导出文件可能被拷到别的时区的机器上，
            // 只写本地"年月日时分秒"会丢掉"这是哪个时区的时间"这个关键信息。
            ExportedAt = now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            Range = range,
            Messages = messages
                .Select(message => new ExportMessage
                {
                    Time = message.Time,
                    Sender = SenderOf(message),
                    Type = TypeOf(message),
                    Content = ContentOf(message),
                    // 非图片消息保持 null，由 WhenWritingNull 略过，原有字段形状不变。
                    ImageUrl = message.IsImage ? message.ImageUrl : null,
                })
                .ToList(),
            AiHistory = aiHistory,
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    // ---------- CSV ----------

    private static string FormatCsv(IReadOnlyList<ChatMessage> messages)
    {
        var builder = new StringBuilder();
        builder.Append("时间,发送者,类型,内容").Append("\r\n");

        foreach (var message in messages)
        {
            builder
                .Append(EscapeCsv(message.Time)).Append(',')
                .Append(EscapeCsv(SenderOf(message))).Append(',')
                .Append(EscapeCsv(TypeOf(message))).Append(',')
                .Append(EscapeCsv(ContentOf(message)))
                .Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// RFC 4180 转义：含逗号 / 双引号 / 换行的字段整体加双引号，内部双引号写成两个。
    /// 不做这一步，只要有一条消息带逗号，整张表的列就会错位。
    /// 另外换行必须写死 <c>\r\n</c>（RFC 4180 规定），不能跟 <c>Environment.NewLine</c>。
    /// </summary>
    private static string EscapeCsv(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var needsQuote = value.IndexOfAny([',', '"', '\r', '\n']) >= 0;
        return needsQuote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    // ---------- 公共取值口径 ----------
    /*
     * 下面三个 SenderOf / TypeOf / ContentOf 刻意与 MessageItemViewModel 的兜底文案一致：
     * 导出文件与用户在界面上看到的内容必须是同一个东西，
     * 否则会出现"界面上写着 [消息]，导出里是空白"这种解释不清的差异。
     */

    private static string SenderOf(ChatMessage message)
        => string.IsNullOrEmpty(message.Sender) ? "未知发送者" : message.Sender;

    private static string TypeOf(ChatMessage message)
        => string.IsNullOrEmpty(message.MessageType) ? "text" : message.MessageType;

    private static string ContentOf(ChatMessage message)
        => string.IsNullOrEmpty(message.Content) ? "[消息]" : message.Content;

    private static string RoleNameOf(HistoryItem item) => item.IsUser ? "我" : "AI";

    private static string DisplayName(ExportContext context)
        => string.IsNullOrEmpty(context.ChatName) ? "未命名会话" : context.ChatName;

    private static string DescribeTime(DateTimeOffset now)
        => now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Markdown 的时间线必须一条消息一行，正文里的换行会打断列表，统一压成单行。</summary>
    private static string OneLine(string value)
        => value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>HTML 侧：先转义再补 <c>&lt;br&gt;</c>，顺序反了就会把 <c>&lt;br&gt;</c> 本身也转义掉。</summary>
    private static string MultiLineHtml(string value)
        => WebUtility.HtmlEncode(value)
            .Replace("\r\n", "<br>")
            .Replace("\n", "<br>")
            .Replace("\r", "<br>");

    private static string NormalizeUnit(string unit) => unit switch
    {
        UnitMinutes or UnitHours or UnitDays => unit,
        _ => UnitHours,
    };

    private static string UnitLabel(string unit) => NormalizeUnit(unit) switch
    {
        UnitMinutes => "分钟",
        UnitDays => "天",
        _ => "小时",
    };

    /// <summary>
    /// 会话名会进文件名，必须剔除 Windows 非法字符（<c>\ / : * ? " &lt; &gt; |</c>）；
    /// 另外限长，避免"会话名 + 时间戳"顶破 260 字符的路径上限。
    /// </summary>
    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);

        foreach (var ch in value)
        {
            if (builder.Length >= 80)
            {
                break;
            }

            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        var result = builder.ToString().Trim().TrimEnd('.');
        return result.Length == 0 ? "聊天记录" : result;
    }
}
