using System.Globalization;
using System.Text.Json;
using ChatlogAI.Client.Models;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// 动态流里的一张卡片（T23）：头像 + 昵称 + 时间 + 文字 + 九宫格 + 点赞 / 评论。
///
/// 三点设计约束：
/// 1. <b>展示字段在构造时算完</b>。DTO 是不可变 record，"应用筛选"是整批重建卡片，
///    因此除了图片（由格子 VM 异步回填）之外没有任何可变状态。
/// 2. <b>媒体格子是子 VM 的集合，不是数据</b>。请求的触发点、状态机、缓存都在格子那侧，
///    卡片只负责"给每一格算好尺寸"（单图 / 九宫格尺寸不同）。
/// 3. <b>likes / comments / finderFeed / official 一律不猜字段名</b>（§7.11 已知限制）：
///    能稳妥取到的文本就渲染，取不到就降级成"有 N 条评论 / N 人赞"这类计数提示，
///    连计数都拿不到就整块不显示 —— 绝不编造、也绝不抛异常。
/// </summary>
public sealed class MomentItemViewModel
{
    /// <summary>点赞人名字段的候选名（实测过的联系人字段口径）：命中第一个非空即用，全不命中就只显示计数。</summary>
    private static readonly string[] NameKeys =
    [
        "displayName", "nickname", "nickName", "userName", "username", "name",
    ];

    /// <summary>评论正文字段的候选名。同样只做"试探"，不命中就退化成计数。</summary>
    private static readonly string[] CommentTextKeys =
    [
        "content", "text", "comment", "desc",
    ];

    /// <summary>计数类字段的候选名，用于"结构不认识但有总数"的降级展示。</summary>
    private static readonly string[] CountKeys =
    [
        "count", "total", "size", "length",
    ];

    /// <summary>列表容器字段的候选名：{ list: [...] } / { users: [...] } 这种包裹形态。</summary>
    private static readonly string[] ContainerKeys =
    [
        "list", "items", "users", "likeUsers", "friends", "comments",
    ];

    internal MomentItemViewModel(SnsPost post, MomentMediaLoader loader)
    {
        Post = post;

        DisplayName = FirstNonEmpty(post.DisplayName, post.Username, "未知发布者");
        AvatarText = BuildAvatarText(DisplayName);
        TimeText = FirstNonEmpty(post.CreateTimeText, post.CreateTime);
        ContentText = post.ContentDesc.Trim();

        // 位置与来源合成一行辅助信息：两者都是"可选的一句"，各占一行会把卡片撑得很松。
        MetaText = string.Join(
            " · ",
            new[] { post.Location, post.SourceName }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part.Trim()));

        // 视频号 / 公众号这类内容没有正文，只有标题与外链。这里退化成"标题 + 外链文本"，
        // 刻意不做"点击打开浏览器"：那是一个进程外副作用，不该藏在一个纯展示的卡片里。
        TitleText = post.Title.Trim();
        ContentUrlText = post.ContentUrl.Trim();

        var single = post.Media.Count == 1;
        var viewerCaption = $"{DisplayName} · {TimeText}";
        Media = post.Media
            .Select((media, index) => new MomentMediaViewModel(loader, post, media, index, single, viewerCaption))
            .ToList();

        LikeLine = BuildLikeLine(post.Likes);
        CommentLine = BuildCommentLine(post.Comments);
    }

    /// <summary>原始 DTO（取图、导出都用它，本类不复制数据）。</summary>
    public SnsPost Post { get; }

    public string DisplayName { get; }

    /// <summary>圆形头像里的字（朋友圈没有头像地址字段，只能用首字母头像）。</summary>
    public string AvatarText { get; }

    /// <summary>后端归一好的可读时间（<c>CreateTimeText</c>），拿不到时退回原始值。</summary>
    public string TimeText { get; }

    public string ContentText { get; }

    public bool HasContent => ContentText.Length > 0;

    public string MetaText { get; }

    public bool HasMeta => MetaText.Length > 0;

    public string TitleText { get; }

    public bool HasTitle => TitleText.Length > 0;

    public string ContentUrlText { get; }

    public bool HasContentUrl => ContentUrlText.Length > 0;

    /// <summary>九宫格（含"无参数"的空格子，见 <see cref="MomentMediaViewModel"/>）。</summary>
    public IReadOnlyList<MomentMediaViewModel> Media { get; }

    public bool HasMedia => Media.Count > 0;

    /// <summary>「赞：张三、李四」；结构不认识时降级为「N 人赞」；什么都没有则为空串。</summary>
    public string LikeLine { get; }

    public bool HasLikes => LikeLine.Length > 0;

    /// <summary>「张三：好」逐条换行；结构不认识时降级为「有 N 条评论」；什么都没有则为空串。</summary>
    public string CommentLine { get; }

    public bool HasComments => CommentLine.Length > 0;

    /// <summary>点赞 / 评论区整体是否出现：没有可见信息时整块不画，避免一张空白的灰底块。</summary>
    public bool HasSocial => HasLikes || HasComments;

    private static string BuildLikeLine(JsonElement? likes)
    {
        if (!TryUnwrapItems(likes, out var items, out var container))
        {
            // 不是数组、也不是认识的包裹对象：只剩"能数出个数"这一条退路。
            return TryReadCount(container, out var count) ? $"{count} 人赞" : string.Empty;
        }

        var names = new List<string>();
        foreach (var item in items)
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    names.Add(text.Trim());
                }

                continue;
            }

            // 点赞项若是对象，能取到名字就取；取不到（比如只有头像地址）就跳过这一项，
            // 计数仍由下面的降级分支兜住，不会因为"名字读不出"而整块消失。
            var name = ReadFirstString(item, NameKeys);
            if (name.Length > 0)
            {
                names.Add(name);
            }
        }

        return names.Count > 0
            ? "赞：" + string.Join("、", names)
            : $"{items.Count} 人赞";
    }

    private static string BuildCommentLine(JsonElement? comments)
    {
        if (!TryUnwrapItems(comments, out var items, out var container))
        {
            return TryReadCount(container, out var count) ? $"有 {count} 条评论" : string.Empty;
        }

        var lines = new List<string>();
        foreach (var item in items)
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    lines.Add(text.Trim());
                }

                continue;
            }

            var author = ReadFirstString(item, NameKeys);
            var content = ReadFirstString(item, CommentTextKeys);
            if (content.Length > 0)
            {
                lines.Add(author.Length > 0 ? $"{author}：{content}" : content);
            }
            else if (author.Length > 0)
            {
                // 正文读不出来但读到了人：这一条不编造内容，只留作者名。
                lines.Add(author);
            }
        }

        return lines.Count > 0 ? string.Join("\n", lines) : $"有 {items.Count} 条评论";
    }

    /// <summary>
    /// 把 likes / comments 归一成"一个数组"。三种形态都认：
    /// 直接是数组、{ list: [...] } 这类包裹对象、以及"什么都不是"（此时 <paramref name="container"/> 留给计数兜底）。
    /// 全过程只看 <see cref="JsonValueKind"/>，不做任何会抛异常的操作。
    /// </summary>
    private static bool TryUnwrapItems(JsonElement? element, out List<JsonElement> items, out JsonElement container)
    {
        items = [];
        container = default;

        if (element is not { } value)
        {
            return false;
        }

        var kind = value.ValueKind;
        if (kind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        if (kind == JsonValueKind.Array)
        {
            items = [.. value.EnumerateArray()];
            return items.Count > 0;
        }

        if (kind != JsonValueKind.Object)
        {
            return false;
        }

        container = value;
        foreach (var key in ContainerKeys)
        {
            if (value.TryGetProperty(key, out var nested) && nested.ValueKind == JsonValueKind.Array)
            {
                items = [.. nested.EnumerateArray()];
                return items.Count > 0;
            }
        }

        return false;
    }

    private static string ReadFirstString(JsonElement item, string[] keys)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var key in keys)
        {
            if (!item.TryGetProperty(key, out var property) || property.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = property.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        return string.Empty;
    }

    private static bool TryReadCount(JsonElement container, out int count)
    {
        count = 0;
        if (container.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var key in CountKeys)
        {
            if (!container.TryGetProperty(key, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
            {
                count = number;
                return true;
            }
        }

        return false;
    }

    private static string BuildAvatarText(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return "?";
        }

        // 用 StringInfo 取"第一个文本元素"而不是 Substring(0,1)：昵称里的 emoji
        // 按 UTF-16 切半会切出孤立代理项，界面上就是一个方框（与联系人列表同一处理）。
        var first = StringInfo.GetNextTextElement(displayName.Trim());
        return first.Length == 0 ? "?" : first.ToUpperInvariant();
    }

    private static string FirstNonEmpty(params string[] candidates)
        => candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
