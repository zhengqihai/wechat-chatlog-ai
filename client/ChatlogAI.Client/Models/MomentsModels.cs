using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatlogAI.Client.Models;

/// <summary>GET /api/sns/users 里的一个人（§14.8.1 实测：username / displayName / postCount）。</summary>
public sealed record SnsUser
{
    [JsonPropertyName("username")] public string Username { get; init; } = "";

    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";

    [JsonPropertyName("postCount")] public int PostCount { get; init; }
}

/// <summary>GET /api/sns/users 响应。</summary>
public sealed record SnsUsersResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("items")] public IReadOnlyList<SnsUser> Items { get; init; } = [];

    [JsonPropertyName("count")] public int Count { get; init; }

    [JsonPropertyName("limit")] public int Limit { get; init; }
}

/// <summary>media 的尺寸（§14.8.1.1：size = width / height / totalSize）。</summary>
public sealed record SnsMediaSize
{
    [JsonPropertyName("width")] public int Width { get; init; }

    [JsonPropertyName("height")] public int Height { get; init; }

    [JsonPropertyName("totalSize")] public int TotalSize { get; init; }
}

/// <summary>
/// 时间线里的一条媒体（§14.8.1.1 实测字段）。
///
/// <see cref="UrlAttrs"/> / <see cref="ThumbAttrs"/> 是**解密参数**（含 token / key），
/// 客户端不解析、不拆字段，取图时把其中的一整套**原样**回传给后端（见 <see cref="SnsMediaRequest"/>）。
/// 二者是**两个不同的 token**，绝不能混用或拼装。
/// </summary>
public sealed record SnsMedia
{
    [JsonPropertyName("type")] public string Type { get; init; } = "";

    [JsonPropertyName("id")] public string Id { get; init; } = "";

    [JsonPropertyName("url")] public string Url { get; init; } = "";

    [JsonPropertyName("thumb")] public string Thumb { get; init; } = "";

    /// <summary>原图参数（含 md5 / key / token）。为空表示这条取不到原图。</summary>
    [JsonPropertyName("urlAttrs")] public JsonElement? UrlAttrs { get; init; }

    /// <summary>缩略图参数（含 key / token，**没有 md5**）。为空表示这条取不到缩略图。</summary>
    [JsonPropertyName("thumbAttrs")] public JsonElement? ThumbAttrs { get; init; }

    [JsonPropertyName("size")] public SnsMediaSize? Size { get; init; }

    /// <summary>能否取到原图。</summary>
    [JsonIgnore] public bool HasFullImage => UrlAttrs is not null;

    /// <summary>能否取到缩略图。</summary>
    [JsonIgnore] public bool HasThumbnail => ThumbAttrs is not null;

    /// <summary>
    /// 是否有可用的解密参数。为 false 时（实测视频号那条：thumb / urlAttrs / thumbAttrs / size 全空）
    /// 这条走不了图片代理，九宫格必须容忍这个空格子，退化为外链文本或占位。
    /// </summary>
    [JsonIgnore] public bool CanRenderImage => UrlAttrs is not null || ThumbAttrs is not null;

    /// <summary>宽度 / 高度（无法解析时为 0），供九宫格算格子比例。</summary>
    [JsonIgnore] public double AspectRatio => Size is { Width: > 0, Height: > 0 }
        ? (double)Size.Width / Size.Height
        : 1.0;
}

/// <summary>朋友圈动态（字段逐字对齐 §14.8.1 实测清单）。</summary>
public sealed record SnsPost
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    [JsonPropertyName("tid")] public string Tid { get; init; } = "";

    [JsonPropertyName("username")] public string Username { get; init; } = "";

    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = "";

    /// <summary>原始 createTime，后端归一为文本；取图时要把它当 create_time 原样回传。</summary>
    [JsonPropertyName("createTime")] public string CreateTime { get; init; } = "";

    /// <summary>可读时间文本，界面直接展示这个。</summary>
    [JsonPropertyName("createTimeText")] public string CreateTimeText { get; init; } = "";

    [JsonPropertyName("contentDesc")] public string ContentDesc { get; init; } = "";

    [JsonPropertyName("location")] public string Location { get; init; } = "";

    [JsonPropertyName("sourceName")] public string SourceName { get; init; } = "";

    [JsonPropertyName("media")] public IReadOnlyList<SnsMedia> Media { get; init; } = [];

    /// <summary>点赞列表。结构未实测，后端原样透传，界面按需解析。</summary>
    [JsonPropertyName("likes")] public JsonElement? Likes { get; init; }

    /// <summary>评论列表。结构未实测，后端原样透传，界面按需解析。</summary>
    [JsonPropertyName("comments")] public JsonElement? Comments { get; init; }

    /// <summary>动态类型（实测视频号为 "28"），也用于取图时的 post_type。</summary>
    [JsonPropertyName("type")] public string Type { get; init; } = "";

    [JsonPropertyName("title")] public string Title { get; init; } = "";

    [JsonPropertyName("contentUrl")] public string ContentUrl { get; init; } = "";

    /// <summary>视频号内容。结构未实测，原样透传。</summary>
    [JsonPropertyName("finderFeed")] public JsonElement? FinderFeed { get; init; }

    /// <summary>公众号内容。结构未实测，原样透传。</summary>
    [JsonPropertyName("official")] public JsonElement? Official { get; init; }
}

/// <summary>
/// GET /api/sns/timeline 响应（字段对齐 §14.8.1 实测）。
///
/// <see cref="SourceFallback"/> 是**正常降级**信号：实时库连不上、当前看的是解密库快照。
/// 界面必须把它提示出来，否则用户会以为朋友圈停更了。
/// </summary>
public sealed record SnsTimelineResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("timeline")] public IReadOnlyList<SnsPost> Timeline { get; init; } = [];

    [JsonPropertyName("hasMore")] public bool HasMore { get; init; }

    [JsonPropertyName("limit")] public int Limit { get; init; }

    [JsonPropertyName("offset")] public int Offset { get; init; }

    [JsonPropertyName("source")] public string Source { get; init; } = "";

    [JsonPropertyName("cover")] public JsonElement? Cover { get; init; }

    [JsonPropertyName("covers")] public JsonElement? Covers { get; init; }

    [JsonPropertyName("sourceRequested")] public string SourceRequested { get; init; } = "";

    [JsonPropertyName("sourceFallback")] public bool SourceFallback { get; init; }

    [JsonPropertyName("sourceFallbackReason")] public string SourceFallbackReason { get; init; } = "";

    [JsonPropertyName("sourceFallbackMessage")] public string SourceFallbackMessage { get; init; } = "";

    [JsonPropertyName("sourceFallbackRetryAfterSeconds")] public int SourceFallbackRetryAfterSeconds { get; init; }
}

/// <summary>POST /api/sns/sync-latest 请求体。</summary>
public sealed record SnsSyncLatestRequest
{
    /// <summary>最多扫描多少条，null 用后端默认值 200。</summary>
    [JsonPropertyName("max_scan")] public int? MaxScan { get; init; }

    [JsonPropertyName("force")] public bool Force { get; init; }
}

/// <summary>
/// POST /api/sns/sync-latest 响应。上游返回体字段名未实测，
/// 除 <c>status</c> 外的内容放在 <see cref="Extra"/> 里逐字透传，不做字段猜测。
/// </summary>
public sealed record SnsSyncLatestResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// POST /api/sns/media 请求体：只表达"哪一条动态的哪一张图"。
///
/// 后端职责仅为"字段白名单 + 长度上限 + 转发"，它不会替客户端挑 attrs ——
/// 因为 §14.8.1.1 实测：原图与缩略图是**两个不同的 token**。
/// </summary>
public sealed record SnsMediaRequest
{
    /// <summary>所属动态 id（= <see cref="SnsPost.Id"/>）。</summary>
    [JsonPropertyName("post_id")] public string PostId { get; init; } = "";

    /// <summary>= <see cref="SnsPost.Type"/>。</summary>
    [JsonPropertyName("post_type")] public string PostType { get; init; } = "";

    /// <summary>= <see cref="SnsPost.CreateTime"/>，原样回传。</summary>
    [JsonPropertyName("create_time")] public string CreateTime { get; init; } = "";

    /// <summary>第几张（九宫格下标），从 0 开始。</summary>
    [JsonPropertyName("idx")] public string Idx { get; init; } = "";

    /// <summary>= <see cref="SnsMedia.Id"/>。</summary>
    [JsonPropertyName("media_id")] public string MediaId { get; init; } = "";

    /// <summary>= <see cref="SnsMedia.Type"/>。</summary>
    [JsonPropertyName("media_type")] public string MediaType { get; init; } = "";

    [JsonPropertyName("url")] public string Url { get; init; } = "";

    [JsonPropertyName("width")] public int? Width { get; init; }

    [JsonPropertyName("height")] public int? Height { get; init; }

    [JsonPropertyName("total_size")] public int? TotalSize { get; init; }

    /// <summary>
    /// 定位参数：整个 <see cref="SnsMedia.UrlAttrs"/> 或 <see cref="SnsMedia.ThumbAttrs"/> 对象原样回传，
    /// **不要自己拆成 md5 / key / token 再传**。为 null 时后端会回 400（该媒体不可渲染）。
    /// </summary>
    [JsonPropertyName("attrs")] public JsonElement? Attrs { get; init; }

    [JsonPropertyName("use_cache")] public string UseCache { get; init; } = "1";

    /// <summary>"full" 取原图 / "thumb" 取缩略图，仅作说明性字段；选哪套参数由 <see cref="Attrs"/> 决定。</summary>
    [JsonPropertyName("variant")] public string? Variant { get; init; }

    /// <summary>
    /// 从时间线的一条 media 构造请求。<paramref name="fullSize"/>=true 用 urlAttrs（原图），
    /// false 用 thumbAttrs（缩略图）——**整套挑一套**，不拼装。
    /// </summary>
    public static SnsMediaRequest ForPostMedia(SnsPost post, SnsMedia media, int index, bool fullSize)
        => new()
        {
            PostId = post.Id,
            PostType = post.Type,
            CreateTime = post.CreateTime,
            Idx = index.ToString(CultureInfo.InvariantCulture),
            MediaId = media.Id,
            MediaType = media.Type,
            Url = media.Url,
            Width = media.Size?.Width,
            Height = media.Size?.Height,
            TotalSize = media.Size?.TotalSize,
            Attrs = fullSize ? media.UrlAttrs : media.ThumbAttrs,
            Variant = fullSize ? "full" : "thumb",
        };
}

/// <summary>
/// POST /api/sns/export 请求体。
/// 刻意不含 output_dir（后端不接受落盘路径），也不含 encrypt / content_key_base64（密钥不进客户端）。
/// </summary>
public sealed record SnsExportRequest
{
    /// <summary>selected（默认，按 <see cref="Usernames"/> 导）或 all。</summary>
    [JsonPropertyName("scope")] public string Scope { get; init; } = "selected";

    [JsonPropertyName("usernames")] public IReadOnlyList<string>? Usernames { get; init; }

    /// <summary>html（默认）/ json / txt / excel。格式由微信数据源提供，不含 Markdown。</summary>
    [JsonPropertyName("format")] public string Format { get; init; } = "html";

    [JsonPropertyName("file_name")] public string? FileName { get; init; }

    [JsonPropertyName("use_cache")] public bool UseCache { get; init; } = true;
}

/// <summary>
/// POST /api/sns/export 与 GET /api/sns/export/{id} 的响应。
/// <see cref="Export"/> 是上游任务对象原样透传（落盘路径已由后端剔除），字段名未实测，按需解析。
/// </summary>
public sealed record SnsExportResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }

    [JsonPropertyName("export_id")] public string ExportId { get; init; } = "";

    [JsonPropertyName("export")] public JsonElement? Export { get; init; }
}
