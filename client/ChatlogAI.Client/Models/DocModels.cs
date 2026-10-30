using System.Text.Json.Serialization;

namespace ChatlogAI.Client.Models;

/// <summary>知识文档元数据（对应 /api/docs 返回的 docs[] 一项）。</summary>
public sealed record DocItem
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    [JsonPropertyName("filename")] public string Filename { get; init; } = "";

    [JsonPropertyName("size")] public long Size { get; init; }

    [JsonPropertyName("uploaded_at")] public long UploadedAt { get; init; }
}

/// <summary>GET /api/docs 响应。</summary>
public sealed record DocListResponse
{
    [JsonPropertyName("docs")] public IReadOnlyList<DocItem> Docs { get; init; } = [];
}

/// <summary>POST /api/docs 请求（JSON：filename + content + overwrite）。</summary>
public sealed record DocUploadRequest
{
    [JsonPropertyName("filename")] public string Filename { get; init; } = "";

    [JsonPropertyName("content")] public string Content { get; init; } = "";

    /// <summary>是否覆盖同名文档（后端按文件名忽略大小写匹配，沿用原 id）。</summary>
    [JsonPropertyName("overwrite")] public bool Overwrite { get; init; }
}

/// <summary>POST /api/docs 响应。</summary>
public sealed record DocUploadResponse
{
    [JsonPropertyName("doc")] public DocItem Doc { get; init; } = new();
}
