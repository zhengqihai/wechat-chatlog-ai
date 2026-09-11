using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ChatlogAI.Client.Services;

/// <summary>
/// Markdown → <see cref="FlowDocument"/>，对应原前端的 <c>renderMarkdown()</c>（app.js L612-L626）。
///
/// 与原实现的差别（有意为之）：
/// 1. 原实现是手写正则逐行拼 HTML，**每一行都变成一个 &lt;p&gt;**，因此普通换行=新段落。
///    这里用 Markdig 做真正的 CommonMark 解析，段落内的单换行是软换行，语义更正确。
/// 2. 只覆盖 AI 输出里真正会出现的语法：标题 / 段落 / 有序无序列表 / 引用 / 代码块 / 行内强调、
///    行内代码、链接 / 分隔线。**表格不解析**——会退化成一整段带竖线的文本。
///    这是骨架阶段的取舍，需要表格时再补 TableBlock 分支（见 docs/DOTNET_CLIENT_PLAN.md §7.5）。
/// 3. 配色不再写死，而是从 Themes/Theme.xaml 按同名 key 取，与应用其余部分同源。
///    之前这里写死深色值，一旦应用切成浅色主题，AI 回答就会"白字白底"直接消失。
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

    private static readonly FontFamily BodyFont = new("Microsoft YaHei UI, Segoe UI");
    private static readonly FontFamily CodeFont = new("Consolas, Cascadia Mono");

    // 这几组 Brush 刻意做成"属性"而不是 "static readonly 字段"：
    // 静态字段在类型首次被使用时初始化，那一刻 Application.Current 可能还是 null
    // （或资源字典尚未挂载），取值会拿到 null 并把整套渲染拖垮。
    // 属性在每次渲染时求值，既保证能拿到已挂载的主题，也保证热更新主题后立刻生效。
    private static Brush BodyBrush => ThemeBrush("TextPrimaryBrush", 0x1F, 0x23, 0x29);
    private static Brush HeadingBrush => ThemeBrush("TextPrimaryBrush", 0x1F, 0x23, 0x29);
    private static Brush QuoteBrush => ThemeBrush("TextSecondaryBrush", 0x5A, 0x62, 0x70);
    private static Brush QuoteBarBrush => ThemeBrush("QuoteBarBrush", 0xD5, 0xD9, 0xDF);
    private static Brush CodeBlockBrush => ThemeBrush("CodeBackgroundBrush", 0xF5, 0xF6, 0xF8);
    private static Brush CodeInlineBrush => ThemeBrush("CodeInlineBackgroundBrush", 0xED, 0xEF, 0xF3);
    private static Brush LinkBrush => ThemeBrush("LinkBrush", 0x0A, 0x7F, 0x45);

    /// <summary>把 Markdown 正文渲染成可直接塞进 <c>FlowDocumentScrollViewer</c> 的文档。</summary>
    public static FlowDocument Render(string? markdown)
    {
        var document = new FlowDocument
        {
            FontFamily = BodyFont,
            FontSize = 13,
            Foreground = BodyBrush,
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
        };

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return document;
        }

        var parsed = Markdig.Markdown.Parse(markdown, Pipeline);
        foreach (var block in parsed)
        {
            AppendBlock(document.Blocks, block);
        }

        return document;
    }

    // ==================== 块级 ====================

    // 形参类型必须写成 Markdig.Syntax.Block：System.Windows.Documents.Block 与它同名，
    // 两个命名空间都已 using，不限定就是 CS0104 歧义。
    private static void AppendBlock(BlockCollection target, Markdig.Syntax.Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                target.Add(BuildHeading(heading));
                break;

            case ParagraphBlock paragraph:
                target.Add(BuildParagraph(paragraph));
                break;

            case ListBlock list:
                target.Add(BuildList(list));
                break;

            case QuoteBlock quote:
                target.Add(BuildQuote(quote));
                break;

            // FencedCodeBlock 派生自 CodeBlock，这一条同时覆盖围栏与缩进代码。
            case CodeBlock code:
                target.Add(BuildCode(code));
                break;

            case ThematicBreakBlock:
                target.Add(BuildThematicBreak());
                break;

            // 其它容器块（如后续启用的自定义块）递归展开，保证内容不丢。
            case ContainerBlock container:
                foreach (var child in container)
                {
                    AppendBlock(target, child);
                }

                break;

            // 未知叶子块：退化成普通段落，宁可样式不对也不要丢内容。
            default:
                if (block is LeafBlock leaf)
                {
                    target.Add(BuildParagraphFromLeaf(leaf));
                }

                break;
        }
    }

    private static Paragraph BuildHeading(HeadingBlock heading)
    {
        var paragraph = new Paragraph
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = heading.Level switch
            {
                1 => 18,
                2 => 16,
                3 => 14.5,
                _ => 13.5,
            },
            Foreground = HeadingBrush,
            Margin = new Thickness(0, 10, 0, 4),
        };

        AppendInlines(paragraph.Inlines, heading.Inline);
        return paragraph;
    }

    private static Paragraph BuildParagraph(ParagraphBlock block)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 3, 0, 3) };
        AppendInlines(paragraph.Inlines, block.Inline);
        return paragraph;
    }

    private static Paragraph BuildParagraphFromLeaf(LeafBlock leaf)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 3, 0, 3) };
        if (leaf.Inline is not null)
        {
            AppendInlines(paragraph.Inlines, leaf.Inline);
        }
        else
        {
            paragraph.Inlines.Add(new Run(GetLeafText(leaf)));
        }

        return paragraph;
    }

    private static List BuildList(ListBlock list)
    {
        var result = new List
        {
            MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(18, 4, 0, 4),
            Padding = new Thickness(0),
        };

        AppendListItems(result.ListItems, list);
        return result;
    }

    /// <summary>
    /// 把 <paramref name="list"/> 的条目追加进 <paramref name="target"/>。
    /// 嵌套列表在这里**递归展开到同一层**：WPF 的 <see cref="ListItem"/> 只有接受
    /// <see cref="Paragraph"/> 的构造函数（没有接受 Block 的版本），子列表塞不进父项里做缩进；
    /// 骨架阶段的取舍是退化成同级项目符号——内容不丢，只是层级感弱一些。
    /// </summary>
    private static void AppendListItems(ListItemCollection target, ListBlock list)
    {
        foreach (var item in list)
        {
            if (item is not ListItemBlock listItem)
            {
                continue;
            }

            var paragraph = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            var nested = new List<ListBlock>();

            foreach (var child in listItem)
            {
                switch (child)
                {
                    case ParagraphBlock childParagraph:
                        AppendInlines(paragraph.Inlines, childParagraph.Inline);
                        break;

                    case ListBlock childList:
                        nested.Add(childList);
                        break;

                    case LeafBlock childLeaf:
                        AppendInlines(paragraph.Inlines, childLeaf.Inline);
                        break;
                }
            }

            target.Add(new ListItem(paragraph));

            foreach (var childList in nested)
            {
                AppendListItems(target, childList);
            }
        }
    }

    private static Section BuildQuote(QuoteBlock quote)
    {
        var section = new Section
        {
            Margin = new Thickness(0, 4, 0, 4),
            Padding = new Thickness(10, 2, 0, 2),
            BorderBrush = QuoteBarBrush,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Foreground = QuoteBrush,
        };

        foreach (var child in quote)
        {
            AppendBlock(section.Blocks, child);
        }

        return section;
    }

    private static Paragraph BuildCode(CodeBlock code)
    {
        var paragraph = new Paragraph
        {
            FontFamily = CodeFont,
            FontSize = 12.5,
            Background = CodeBlockBrush,
            Margin = new Thickness(0, 6, 0, 6),
            Padding = new Thickness(10, 6, 10, 6),
        };

        paragraph.Inlines.Add(new Run(GetLeafText(code)));
        return paragraph;
    }

    private static Paragraph BuildThematicBreak()
    {
        // WPF 没有 <hr>：用一条只有下边框的段落近似。需要一个非空 Run 才能撑出高度。
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 8, 0, 8),
            BorderBrush = QuoteBarBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

        paragraph.Inlines.Add(new Run(" "));
        return paragraph;
    }

    // ==================== 行内 ====================

    private static void AppendInlines(InlineCollection target, ContainerInline? container)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;

                case EmphasisInline emphasis:
                {
                    var span = new Span();
                    AppendInlines(span.Inlines, emphasis);

                    if (emphasis.DelimiterChar == '~')
                    {
                        span.TextDecorations = TextDecorations.Strikethrough;
                    }
                    else if (emphasis.DelimiterCount >= 2)
                    {
                        span.FontWeight = FontWeights.Bold;
                    }
                    else
                    {
                        span.FontStyle = FontStyles.Italic;
                    }

                    target.Add(span);
                    break;
                }

                case CodeInline code:
                    target.Add(new Run(code.Content)
                    {
                        FontFamily = CodeFont,
                        Background = CodeInlineBrush,
                    });
                    break;

                case LinkInline link when !link.IsImage:
                    target.Add(BuildHyperlink(link.Url, link));
                    break;

                // 图片骨架阶段不下载，退化成链接文本，避免出现空白。
                case LinkInline image:
                    target.Add(BuildHyperlink(image.Url, image));
                    break;

                case AutolinkInline autolink:
                    target.Add(BuildHyperlink(autolink.Url, null));
                    break;

                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;

                case HtmlInline html:
                    target.Add(new Run(html.Tag));
                    break;

                case ContainerInline nested:
                    AppendInlines(target, nested);
                    break;
            }
        }
    }

    private static Hyperlink BuildHyperlink(string? url, ContainerInline? content)
    {
        var hyperlink = new Hyperlink { Foreground = LinkBrush };
        if (content is not null)
        {
            AppendInlines(hyperlink.Inlines, content);
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return hyperlink;
        }

        if (hyperlink.Inlines.Count == 0)
        {
            hyperlink.Inlines.Add(new Run(url));
        }

        if (Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri))
        {
            hyperlink.NavigateUri = uri;
            hyperlink.RequestNavigate += (_, args) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true });
                }
                catch (Exception)
                {
                    // 打开浏览器失败不该影响界面，静默即可。
                }

                args.Handled = true;
            };
        }

        return hyperlink;
    }

    // ==================== 工具 ====================

    /// <summary>取叶子块的原始文本（按行拼接）。代码块必须走这里，不能依赖 Inline。</summary>
    private static string GetLeafText(LeafBlock block)
    {
        var builder = new StringBuilder();
        var lines = block.Lines.Lines;

        for (var i = 0; i < block.Lines.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(lines[i].Slice.ToString());
        }

        return builder.ToString();
    }

    /// <summary>
    /// 按 key 从应用主题取 Brush。资源确实缺失（例如在单元测试里没建 Application）时
    /// 才退回兜底色，保证渲染永远不会因为取不到资源而抛空引用。
    /// </summary>
    private static Brush ThemeBrush(string key, byte r, byte g, byte b)
        => Application.Current?.TryFindResource(key) as Brush ?? Frozen(r, g, b);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
