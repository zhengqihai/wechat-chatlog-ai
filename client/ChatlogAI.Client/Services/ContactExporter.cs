using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatlogAI.Client.Models;

namespace ChatlogAI.Client.Services;

/// <summary>
/// 联系人导出的格式化器（T22，规格见 docs/DOTNET_CLIENT_PLAN.md §14.7.4）。
///
/// 为什么**另起一个类**而不扩展 <see cref="ChatExporter"/>：
/// <c>ChatExporter</c> 的输入是"某个会话的一批消息"，这里的输入是"一批联系人的属性"，
/// 两者连输入类型都不一样；硬凑进一个类只会把"按会话导出聊天记录"和"按人导出通讯录"
/// 两件不相干的功能焊死（§7.7 决定 4 的同一取舍）。格式枚举与落盘约定复用
/// <see cref="ExportFormat"/> / <see cref="ExportDocument"/>，那才是真正共享的部分。
///
/// 与 <c>ChatExporter</c> 一样是**无状态静态类**、不碰网络与 UI：纯函数才能用
/// "给一组联系人、比较输出字符串"的方式直接钉住，这是导出功能最值得投资的地方。
/// </summary>
public static class ContactExporter
{
    /// <summary>CSV / Markdown / HTML 表格的列顺序（规格 §14.7.4 建议的九列）。</summary>
    private static readonly string[] ColumnHeaders =
    [
        "用户名", "显示名称", "备注", "昵称", "微信号", "性别", "地区", "来源", "添加时间",
    ];

    /// <summary>JSON 输出的序列化约定，与 <c>ChatlogApiClient.WriteOptions</c> 同源。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 导出文件是给人看的，中文不该被写成 \uXXXX（与 ChatExporter 同一理由）。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ==================== 元信息（供"另存为"拼文件名 / Filter） ====================

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

    /// <summary>
    /// SaveFileDialog 的 Filter 串。这里自己写一份、不去复用 <c>ChatExporter</c> 的同名方法：
    /// 那会凭空造出"联系人导出依赖会话导出"的耦合，而重复的只是三行字符串（§14.7.4 的取舍）。
    /// </summary>
    public static string BuildFileFilter(ExportFormat format)
        => $"{DescribeFormat(format)} (*{ExtensionOf(format)})|*{ExtensionOf(format)}|所有文件 (*.*)|*.*";

    /// <summary>默认文件名 <c>联系人_&lt;时间戳&gt;.&lt;ext&gt;</c>；不带联系人数——用户还没点导出，此时无人可数。</summary>
    public static string BuildDefaultFileName(ExportFormat format, DateTimeOffset now)
        => $"联系人_{now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}{ExtensionOf(format)}";

    // ==================== 排版 ====================

    /// <param name="contacts">要导出的联系人，顺序即文件里的行序（由调用方按勾选顺序给出）。</param>
    public static ExportDocument Format(
        ExportFormat format,
        IReadOnlyList<ContactSummary> contacts,
        DateTimeOffset now)
        => format switch
        {
            // 只有 CSV 带 BOM：Excel 对不带 BOM 的 UTF-8 会按本地代码页解码，中文必然乱码；
            // JSON / Markdown / HTML 带 BOM 反而会让严格的解析器出错（与 ChatExporter 同一判据）。
            ExportFormat.Markdown => new ExportDocument(FormatMarkdown(contacts, now), false),
            ExportFormat.Html => new ExportDocument(FormatHtml(contacts, now), false),
            ExportFormat.Json => new ExportDocument(FormatJson(contacts, now), false),
            ExportFormat.Csv => new ExportDocument(FormatCsv(contacts), true),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "不支持的导出格式。"),
        };

    // ---------- Markdown ----------

    private static string FormatMarkdown(IReadOnlyList<ContactSummary> contacts, DateTimeOffset now)
    {
        var builder = new StringBuilder();

        builder.AppendLine("# 联系人导出");
        builder.AppendLine();
        builder.Append("- 导出时间：").AppendLine(DescribeTime(now));
        builder.Append("- 联系人数量：").AppendLine(contacts.Count.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine();

        if (contacts.Count == 0)
        {
            builder.AppendLine("（没有选中任何联系人）");
            return builder.ToString();
        }

        builder.Append("| ").Append(string.Join(" | ", ColumnHeaders)).AppendLine(" |");
        builder.Append('|').Append(string.Join("|", ColumnHeaders.Select(_ => " --- "))).AppendLine("|");
        foreach (var contact in contacts)
        {
            builder.Append("| ")
                .Append(string.Join(" | ", ColumnsOf(contact).Select(MarkdownCell)))
                .AppendLine(" |");
        }

        return builder.ToString();
    }

    // ---------- HTML ----------

    private static string FormatHtml(IReadOnlyList<ContactSummary> contacts, DateTimeOffset now)
    {
        var builder = new StringBuilder();

        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"zh-CN\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\">");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.AppendLine("<title>联系人导出</title>");
        builder.AppendLine("<style>");
        builder.AppendLine(HtmlStyles);
        builder.AppendLine("</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("<main class=\"sheet\">");
        builder.AppendLine("<h1>联系人导出</h1>");
        builder.AppendLine("<p class=\"meta\">");
        builder.Append("<span>导出时间：").Append(WebUtility.HtmlEncode(DescribeTime(now))).AppendLine("</span>");
        builder.Append("<span>联系人数量：")
            .Append(contacts.Count.ToString(CultureInfo.InvariantCulture))
            .AppendLine("</span>");
        builder.AppendLine("</p>");

        if (contacts.Count == 0)
        {
            builder.AppendLine("<p class=\"empty\">（没有选中任何联系人）</p>");
        }
        else
        {
            builder.AppendLine("<table>");
            builder.AppendLine("<thead><tr>");
            foreach (var header in ColumnHeaders)
            {
                builder.Append("<th>").Append(WebUtility.HtmlEncode(header)).AppendLine("</th>");
            }

            builder.AppendLine("</tr></thead>");
            builder.AppendLine("<tbody>");
            foreach (var contact in contacts)
            {
                builder.AppendLine("<tr>");
                foreach (var value in ColumnsOf(contact))
                {
                    builder.Append("<td>").Append(MultiLineHtml(value)).AppendLine("</td>");
                }

                builder.AppendLine("</tr>");
            }

            builder.AppendLine("</tbody>");
            builder.AppendLine("</table>");
        }

        builder.AppendLine("</main>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    /// <summary>
    /// 单文件内联样式：不引任何外部资源，双击就能看。
    /// 色值刻意与 <c>Themes/Theme.xaml</c> 一一对应（浅灰底 / 白卡片 / 深字），
    /// 让导出的文件与应用看起来是同一个东西；独立 HTML 引不到 DynamicResource，只能各写一份。
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
            max-width: 1100px;
            margin: 0 auto;
            padding: 28px 32px 36px;
            background: #FFFFFF;
            border: 1px solid #E5E7EB;
            border-radius: 8px;
        }
        h1 { margin: 0 0 12px; font-size: 20px; font-weight: 600; }
        .meta { margin: 0 0 18px; font-size: 12px; color: #626A78; }
        .meta span { display: inline-block; margin-right: 16px; }
        .empty { color: #626A78; }
        table { width: 100%; border-collapse: collapse; table-layout: fixed; }
        th, td {
            padding: 8px 10px;
            border: 1px solid #E5E7EB;
            text-align: left;
            vertical-align: top;
            overflow-wrap: anywhere;
        }
        th { background: #F7F8FA; font-weight: 600; }
        tbody tr:nth-child(even) { background: #F7F8FA; }
        @media print {
            body { padding: 0; background: #FFFFFF; }
            .sheet { max-width: none; border: 0; border-radius: 0; padding: 0; }
        }
        """;

    // ---------- JSON ----------

    private static string FormatJson(IReadOnlyList<ContactSummary> contacts, DateTimeOffset now)
    {
        var payload = new ContactExportPayload
        {
            // ISO 8601 带时区偏移：文件可能被拷到别的时区，只写本地时间会丢掉"这是哪个时区"。
            ExportedAt = now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            Total = contacts.Count,
            Contacts = contacts.Select(contact => new ContactExportEntry
            {
                Username = contact.Username,
                DisplayName = DisplayNameOf(contact),
                Remark = contact.Remark,
                Nickname = contact.Nickname,
                Alias = contact.Alias,
                Gender = contact.Gender,
                Region = RegionOf(contact),
                Source = contact.Source,
                AddTimeText = contact.AddTimeText,
            }).ToList(),
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    // ---------- CSV ----------

    private static string FormatCsv(IReadOnlyList<ContactSummary> contacts)
    {
        var builder = new StringBuilder();
        builder.Append(string.Join(",", ColumnHeaders.Select(EscapeCsv))).Append("\r\n");

        foreach (var contact in contacts)
        {
            builder.Append(string.Join(",", ColumnsOf(contact).Select(EscapeCsv))).Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// RFC 4180 转义：含逗号 / 双引号 / 换行的字段整体加双引号，内部双引号写成两个。
    /// 换行必须写死 <c>\r\n</c>（RFC 4180 规定），不能跟 <c>Environment.NewLine</c>。
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
     * 关于头像，这里有一处刻意为之的"不导出"：
     * ContactSummary.Avatar 是**本服务的本机代理地址**（/api/contacts/avatar?username=..，
     * 下发时已被 server.py 改写成 127.0.0.1:18765 的绝对地址）—— 把它写进导出文件，
     * 文件一旦发给别人就是一条必然打不开的死链，所以任何格式都不导出它。
     * DTO 里另一个 AvatarLink（微信 CDN 地址）是可外发的，但它在表格里只是一个长 URL，
     * 收益低于噪声，本轮同样不导出；**将来若要带头像，只允许用 AvatarLink**。
     *
     * 下面两个取值口径刻意与 ContactItemViewModel 的展示口径一致：
     * 导出文件与用户在界面上看到的内容必须是同一个东西，否则会出现"界面上写着张三、
     * 导出里是 wxid_xxx"这种解释不清的差异。改一处必须同时改另一处。
     */

    private static string[] ColumnsOf(ContactSummary contact) =>
    [
        contact.Username,
        DisplayNameOf(contact),
        contact.Remark,
        contact.Nickname,
        contact.Alias,
        contact.Gender,
        RegionOf(contact),
        contact.Source,
        contact.AddTimeText,
    ];

    private static string DisplayNameOf(ContactSummary contact)
        => FirstNonEmpty(contact.Remark, contact.Nickname, contact.Username, "未知联系人");

    private static string RegionOf(ContactSummary contact)
    {
        if (!string.IsNullOrWhiteSpace(contact.Region))
        {
            return contact.Region.Trim();
        }

        return string.Join(
            " ",
            new[] { contact.Country, contact.Province, contact.City }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part.Trim()));
    }

    private static string FirstNonEmpty(params string[] candidates)
        => candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string DescribeTime(DateTimeOffset now)
        => now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>单元格里的竖线会截断 Markdown 表格，换行会截断整行，统一处理掉。</summary>
    private static string MarkdownCell(string value)
        => value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace("|", "\\|");

    /// <summary>HTML 侧：先转义再补 <c>&lt;br&gt;</c>，顺序反了会把 <c>&lt;br&gt;</c> 本身也转义掉。</summary>
    private static string MultiLineHtml(string value)
        => WebUtility.HtmlEncode(value)
            .Replace("\r\n", "<br>")
            .Replace("\n", "<br>")
            .Replace("\r", "<br>");
}

/// <summary>JSON 格式的顶层结构。导出内容里刻意不含任何头像字段（理由见 ContactExporter 注释）。</summary>
internal sealed record ContactExportPayload
{
    [JsonPropertyName("exported_at")] public string ExportedAt { get; init; } = "";

    [JsonPropertyName("total")] public int Total { get; init; }

    [JsonPropertyName("contacts")] public IReadOnlyList<ContactExportEntry> Contacts { get; init; } = [];
}

/// <summary>JSON 格式里 <c>contacts[]</c> 的一条。</summary>
internal sealed record ContactExportEntry
{
    [JsonPropertyName("username")] public string Username { get; init; } = "";

    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";

    [JsonPropertyName("remark")] public string Remark { get; init; } = "";

    [JsonPropertyName("nickname")] public string Nickname { get; init; } = "";

    [JsonPropertyName("alias")] public string Alias { get; init; } = "";

    /// <summary>上游未标注类型，后端归一为文本（可能是 "1" / "2" 这类编码），原样导出、不当数字用。</summary>
    [JsonPropertyName("gender")] public string Gender { get; init; } = "";

    [JsonPropertyName("region")] public string Region { get; init; } = "";

    [JsonPropertyName("source")] public string Source { get; init; } = "";

    [JsonPropertyName("addTimeText")] public string AddTimeText { get; init; } = "";
}
