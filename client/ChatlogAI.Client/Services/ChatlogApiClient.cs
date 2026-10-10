using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ChatlogAI.Client.Bootstrap;
using ChatlogAI.Client.Models;

namespace ChatlogAI.Client.Services;

/// <summary>
/// 本地 FastAPI 后端（默认 http://127.0.0.1:18765）的 REST 客户端。
/// 接口契约见 docs/API_REFERENCE.md；本类是该文档在 C# 侧的唯一落地实现，
/// 界面层不允许自己拼 HttpClient / JSON —— 否则错误解析、超时、路径拼接会出现多份实现。
///
/// 三条设计约定（都来自 API_REFERENCE.md §11 的对接检查清单）：
/// 1. 读接口一律带 <c>Cache-Control: no-store</c>；
/// 2. 成功 = HTTP 2xx **且** 响应体 <c>status ∈ {success, ok, 缺失}</c>
///    （<c>/api/config</c> 没有 status 字段，<c>/api/health</c> 是 <c>ok</c>，因此不能简单要求 == "success"）；
/// 3. 错误文案优先级 <c>detail → message → status_info.msg</c>，再兜底成 HTTP 码。
/// </summary>
public sealed class ChatlogApiClient : IDisposable
{
    /// <summary>普通请求超时。本地回环调用，30 秒已非常宽松。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 会真实调用模型 API 的接口（验证 Key / 分析 / 余额）的超时。
    /// 服务端自己给模型留了 60 秒，客户端必须比它更长，否则会先于服务端报"超时"。
    /// </summary>
    public static readonly TimeSpan ModelTimeout = TimeSpan.FromSeconds(180);

    /// <summary>
    /// 会真实调上游、且可能慢的接口（通讯录全量、朋友圈时间线、导出任务）的超时。
    /// 上游是本机微信数据源，正常很快；这里给宽是因为"一次返回全部联系人"可能几百上千条。
    /// </summary>
    public static readonly TimeSpan UpstreamTimeout = TimeSpan.FromSeconds(120);

    /// <summary>下载二进制（朋友圈图片 / 视频 / 导出 ZIP）的超时。视频与 ZIP 可能很大。</summary>
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(300);

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        // 契约里全是 snake_case 下划线字段；同时打开大小写不敏感，
        // 避免上游偶发把某个键写成驼峰时整条数据静默变成默认值。
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        // Python 侧是 ensure_ascii=False；这里同样不转义中文，否则中文提示词会变成 \uXXXX。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // null 即"不提交该字段"。后端对 None 值也是跳过，语义一致；
        // 这也是"编辑供应商时 api_key 留空 = 沿用旧值"的实现方式（见 API_REFERENCE.md §11.5）。
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    /// <param name="baseUrl">默认取 <see cref="AppPaths.ChatlogUrl"/>；测试时可注入。</param>
    /// <param name="httpClient">仅供测试注入；传入时本类不负责释放。</param>
    public ChatlogApiClient(string? baseUrl = null, HttpClient? httpClient = null)
    {
        BaseUrl = (baseUrl ?? AppPaths.Instance.ChatlogUrl).TrimEnd('/');

        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            // UseProxy=false：127.0.0.1 绝不能被系统代理劫持（企业环境里很常见）。
            // Timeout=InfiniteTimeSpan：超时统一由下面每个请求自己的 CTS 控制，
            // 否则 HttpClient 会以"整个客户端"为粒度抛 TaskCanceledException，无法区分是哪种超时。
            var handler = new HttpClientHandler { UseProxy = false };
            _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            _ownsHttpClient = true;
        }
    }

    public string BaseUrl { get; }

    // ==================== 2. 静态与页面 ====================

    /// <summary>GET /api/health。用于判断后端是否还活着，因此**不抛异常**，失败即返回 false。</summary>
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await SendAsync<ApiMessageResponse>(
                HttpMethod.Get, "/api/health", null, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            return response.Status == "ok";
        }
        catch (ChatlogApiException)
        {
            return false;
        }
    }

    // ==================== 3. 模型供应商 ====================

    /// <summary>GET /api/config。</summary>
    public Task<ActiveConfigInfo> GetConfigAsync(CancellationToken cancellationToken = default)
        => SendAsync<ActiveConfigInfo>(HttpMethod.Get, "/api/config", null, DefaultTimeout, cancellationToken);

    /// <summary>GET /api/providers。返回体里的 <c>presets</c> 是界面自动填充的来源。</summary>
    public Task<ProviderListResponse> GetProvidersAsync(CancellationToken cancellationToken = default)
        => SendAsync<ProviderListResponse>(HttpMethod.Get, "/api/providers", null, DefaultTimeout, cancellationToken);

    /// <summary>POST /api/providers/validate。会真实调用一次模型（服务端 temperature=0）。</summary>
    public Task<ApiMessageResponse> ValidateProviderAsync(
        ValidateProviderRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync<ApiMessageResponse>(HttpMethod.Post, "/api/providers/validate", request, ModelTimeout, cancellationToken);

    /// <summary>POST /api/providers。id 为空即新增，返回服务端生成的 id。</summary>
    public Task<SaveProviderResponse> SaveProviderAsync(
        SaveProviderRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync<SaveProviderResponse>(HttpMethod.Post, "/api/providers", request, ModelTimeout, cancellationToken);

    /// <summary>POST /api/providers/activate。</summary>
    public Task<ApiMessageResponse> ActivateProviderAsync(
        string providerId,
        CancellationToken cancellationToken = default)
        => SendAsync<ApiMessageResponse>(
            HttpMethod.Post, "/api/providers/activate", new ActivateProviderRequest { Id = providerId },
            DefaultTimeout, cancellationToken);

    /// <summary>DELETE /api/providers/{id}。</summary>
    public Task<ApiMessageResponse> DeleteProviderAsync(
        string providerId,
        CancellationToken cancellationToken = default)
        => SendAsync<ApiMessageResponse>(
            HttpMethod.Delete, "/api/providers/" + Uri.EscapeDataString(providerId), null,
            DefaultTimeout, cancellationToken);

    /// <summary>GET /api/providers/balance。不支持余额时 <c>supported=false</c>，不是错误。</summary>
    public Task<BalanceResponse> GetBalanceAsync(CancellationToken cancellationToken = default)
        => SendAsync<BalanceResponse>(HttpMethod.Get, "/api/providers/balance", null, ModelTimeout, cancellationToken);

    // ==================== 4. 聊天数据 ====================

    /// <summary>GET /api/chats。数据源不可用时后端返回 503，会抛 <see cref="ChatlogApiException"/>。</summary>
    public Task<ChatListResponse> GetChatsAsync(CancellationToken cancellationToken = default)
        => SendAsync<ChatListResponse>(HttpMethod.Get, "/api/chats", null, DefaultTimeout, cancellationToken);

    /// <summary>GET /api/messages。返回顺序恒为时间正序。</summary>
    /// <param name="targetId">会话 id，来自 <see cref="GetChatsAsync"/>。</param>
    /// <param name="limit">读取条数上限，服务端约束 1–2000，默认 500。</param>
    /// <param name="recentHours">只保留最近 N 小时（服务端过滤），null 表示不限。</param>
    /// <param name="recentCount">在 recentHours 之后再取最近 N 条，null 表示不限。</param>
    public Task<MessageListResponse> GetMessagesAsync(
        string targetId,
        int limit = 500,
        double? recentHours = null,
        int? recentCount = null,
        CancellationToken cancellationToken = default)
    {
        var query = new StringBuilder("/api/messages?target_id=").Append(Uri.EscapeDataString(targetId));
        query.Append("&limit=").Append(limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (recentHours.HasValue)
        {
            query.Append("&recent_hours=").Append(recentHours.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (recentCount.HasValue)
        {
            query.Append("&recent_count=").Append(recentCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return SendAsync<MessageListResponse>(HttpMethod.Get, query.ToString(), null, DefaultTimeout, cancellationToken);
    }

    // ==================== 5. AI 分析与历史 ====================

    /// <summary>POST /api/summarize。模型调用，超时按 <see cref="ModelTimeout"/> 计。</summary>
    public Task<SummarizeResponse> SummarizeAsync(
        SummarizeRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync<SummarizeResponse>(HttpMethod.Post, "/api/summarize", request, ModelTimeout, cancellationToken);

    /// <summary>GET /api/history。注意接口**未接受时间/条数筛选**，历史是全量。</summary>
    public Task<HistoryResponse> GetHistoryAsync(
        string targetId,
        CancellationToken cancellationToken = default)
        => SendAsync<HistoryResponse>(
            HttpMethod.Get, "/api/history?target_id=" + Uri.EscapeDataString(targetId), null,
            DefaultTimeout, cancellationToken);

    /// <summary>POST /api/history。**整体覆盖**该会话的历史，不是追加。</summary>
    public Task<ApiMessageResponse> SaveHistoryAsync(
        string targetId,
        IReadOnlyList<HistoryItem> history,
        CancellationToken cancellationToken = default)
        => SendAsync<ApiMessageResponse>(
            HttpMethod.Post, "/api/history",
            new SaveHistoryRequest { TargetId = targetId, History = history },
            DefaultTimeout, cancellationToken);

    // ==================== 6. 自动回复 ====================

    /// <summary>GET /api/auto-reply/config。</summary>
    public Task<AutoReplyConfigResponse> GetAutoReplyConfigAsync(CancellationToken cancellationToken = default)
        => SendAsync<AutoReplyConfigResponse>(
            HttpMethod.Get, "/api/auto-reply/config", null, DefaultTimeout, cancellationToken);

    /// <summary>POST /api/auto-reply/config。部分更新，null 字段不变更。</summary>
    public Task<AutoReplyConfigResponse> SaveAutoReplyConfigAsync(
        AutoReplyConfigPatch patch,
        CancellationToken cancellationToken = default)
        => SendAsync<AutoReplyConfigResponse>(
            HttpMethod.Post, "/api/auto-reply/config", patch, DefaultTimeout, cancellationToken);

    /// <summary>GET /api/auto-reply/status。界面按 3 秒轮询。</summary>
    public Task<AutoReplyStatusResponse> GetAutoReplyStatusAsync(CancellationToken cancellationToken = default)
        => SendAsync<AutoReplyStatusResponse>(
            HttpMethod.Get, "/api/auto-reply/status", null, DefaultTimeout, cancellationToken);

    /// <summary>POST /api/auto-reply/test。</summary>
    public Task<ApiMessageResponse> TestAutoReplyAsync(
        AutoReplyTestRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync<ApiMessageResponse>(
            HttpMethod.Post, "/api/auto-reply/test", request, DefaultTimeout, cancellationToken);

    /// <summary>
    /// POST /api/auto-reply/detect-hotkeys。探测微信快捷键，转发探测源列表当前为空，
    /// 因此恒返回 found=false；接口本身失败才抛 <see cref="ChatlogApiException"/>。
    /// </summary>
    public Task<AutoReplyDetectHotkeysResponse> DetectHotkeysAsync(CancellationToken cancellationToken = default)
        => SendAsync<AutoReplyDetectHotkeysResponse>(
            HttpMethod.Post, "/api/auto-reply/detect-hotkeys", null, DefaultTimeout, cancellationToken);

    // ==================== 6.5 定时发送 ====================

    /// <summary>GET /api/scheduled-send/config。</summary>
    public Task<ScheduledSendConfigResponse> GetScheduledSendConfigAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<ScheduledSendConfigResponse>(
            HttpMethod.Get, "/api/scheduled-send/config", null, DefaultTimeout, cancellationToken);

    /// <summary>POST /api/scheduled-send/config。部分更新，null 字段不变更；tasks 为整份覆盖。</summary>
    public Task<ScheduledSendConfigResponse> SaveScheduledSendConfigAsync(
        ScheduledSendConfigPatch patch,
        CancellationToken cancellationToken = default)
        => SendAsync<ScheduledSendConfigResponse>(
            HttpMethod.Post, "/api/scheduled-send/config", patch, DefaultTimeout, cancellationToken);

    /// <summary>GET /api/scheduled-send/status。界面按 15 秒轮询。</summary>
    public Task<ScheduledSendStatusResponse> GetScheduledSendStatusAsync(
        CancellationToken cancellationToken = default)
        => SendAsync<ScheduledSendStatusResponse>(
            HttpMethod.Get, "/api/scheduled-send/status", null, DefaultTimeout, cancellationToken);

    /// <summary>
    /// POST /api/scheduled-send/test。提交完整任务（支持未保存草稿）；
    /// preview 与 send 都会真实调用模型，因此超时按 <see cref="ModelTimeout"/> 计。
    /// </summary>
    public Task<ScheduledSendTestResponse> TestScheduledSendAsync(
        ScheduledSendTestRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync<ScheduledSendTestResponse>(
            HttpMethod.Post, "/api/scheduled-send/test", request, ModelTimeout, cancellationToken);

    // ==================== 7. 系统控制 ====================

    /// <summary>
    /// POST /api/shutdown。
    /// 后端会 touch 退出信号文件后 <c>os._exit(0)</c>，整个 Job Object 一起结束；
    /// 因此这个调用**成功返回也可能紧接着连接被掐断**，调用方不应把随后的连接异常当成错误。
    /// </summary>
    public Task<ApiMessageResponse> ShutdownAsync(CancellationToken cancellationToken = default)
        => SendAsync<ApiMessageResponse>(HttpMethod.Post, "/api/shutdown", null, DefaultTimeout, cancellationToken);

    // ==================== 7.5 微信账号切换（T19） ====================
    //
    // 两个写方法（SwitchAccountAsync / ConfirmAccountSwitchAsync）是契约的一部分，但**调用点只有一个**：
    // 账号窗口里用户显式点「切换到此账号」/「我已切换」。任何"顺手调一下"都是安全事故——
    // switch 会真的改 config.json 的 wechat_account 并关掉发送闸门，confirm 会误清待确认状态。

    /// <summary>
    /// GET /api/wechat/accounts。账号清单 + 待确认状态一次给全（后端刻意合成一个接口）。
    /// 上游不可达时后端返回 503，会抛 <see cref="ChatlogApiException"/>（detail 里有明确原因）。
    /// </summary>
    public Task<WechatAccountsResponse> GetWechatAccountsAsync(CancellationToken cancellationToken = default)
        => SendAsync<WechatAccountsResponse>(
            HttpMethod.Get, "/api/wechat/accounts", null, UpstreamTimeout, cancellationToken);

    /// <summary>POST /api/wechat/account/resolve，拿授权页地址（后端降级为上游界面首页）。</summary>
    public Task<AccountResolveResponse> ResolveWechatAccountAsync(
        string account,
        CancellationToken cancellationToken = default)
        => SendAsync<AccountResolveResponse>(
            HttpMethod.Post, "/api/wechat/account/resolve", new AccountRequest { Account = account },
            DefaultTimeout, cancellationToken);

    /// <summary>GET /api/wechat/account/resolve-status，授权轮询用（3 秒一次）。</summary>
    public Task<AccountResolveStatusResponse> GetAccountResolveStatusAsync(
        string account,
        CancellationToken cancellationToken = default)
        => SendAsync<AccountResolveStatusResponse>(
            HttpMethod.Get,
            "/api/wechat/account/resolve-status?account=" + Uri.EscapeDataString(account), null,
            UpstreamTimeout, cancellationToken);

    /// <summary>
    /// GET /api/wechat/account/pending。**不依赖上游**（读的是本项目后端的落盘标记），
    /// 因此客户端启动早期就能拿到正确结果——头像上的警示标记靠它。
    /// </summary>
    public Task<AccountPendingResponse> GetPendingAccountSwitchAsync(CancellationToken cancellationToken = default)
        => SendAsync<AccountPendingResponse>(
            HttpMethod.Get, "/api/wechat/account/pending", null, DefaultTimeout, cancellationToken);

    /// <summary>
    /// POST /api/wechat/account/switch。会真改 <c>config.json</c> 的 <c>wechat_account</c>
    /// 并关闭发送闸门；仅由账号窗口的二次确认后调用。
    /// </summary>
    public Task<AccountSwitchResponse> SwitchAccountAsync(
        string account,
        CancellationToken cancellationToken = default)
        => SendAsync<AccountSwitchResponse>(
            HttpMethod.Post, "/api/wechat/account/switch", new AccountRequest { Account = account },
            UpstreamTimeout, cancellationToken);

    /// <summary>POST /api/wechat/account/confirm：删标记文件 + 放开闸门，仅由「我已切换」调用。</summary>
    public Task<AccountConfirmResponse> ConfirmAccountSwitchAsync(CancellationToken cancellationToken = default)
        => SendAsync<AccountConfirmResponse>(
            HttpMethod.Post, "/api/wechat/account/confirm", null, DefaultTimeout, cancellationToken);

    // ==================== 9.2 数据库密钥（阶段 D3：手动导入） ====================

    /// <summary>
    /// GET /api/keys/status：密钥现状。每一条都带后端**实测**（解开本机 session.db 第 1 页）的结果，
    /// 不是格式检查 —— 所以这个调用会比其他 GET 慢几十毫秒（PBKDF2 256000 次），属预期。
    /// </summary>
    public Task<KeyStatusResponse> GetKeysStatusAsync(CancellationToken cancellationToken = default)
        => SendAsync<KeyStatusResponse>(
            HttpMethod.Get, "/api/keys/status", null, DefaultTimeout, cancellationToken);

    /// <summary>
    /// POST /api/keys/import：导入密钥（密钥文件全文，或 64 位 hex 的裸 db_key）。
    /// 后端逐条实测校验，未通过的**不写盘**并在响应里列出原因；全部未通过时返回 400。
    /// </summary>
    public Task<KeyImportResponse> ImportKeyAsync(
        KeyImportRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync<KeyImportResponse>(
            HttpMethod.Post, "/api/keys/import", request, DefaultTimeout, cancellationToken);

    // ==================== 8. 通讯录 ====================

    /// <summary>
    /// GET /api/contacts。入参直接对齐上游的 <c>include_*</c> 布尔开关（§14.7.2），
    /// 未设置的开关不下发，由上游默认值决定（好友 / 群聊 / 公众号默认开）。
    /// **该接口没有分页**，一次返回全部，界面必须自己做虚拟化。
    /// </summary>
    public Task<ContactListResponse> GetContactsAsync(
        ContactQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new ContactQuery();
        var path = "/api/contacts" + BuildQuery(
        [
            new("include_friends", Flag(query.IncludeFriends)),
            new("include_groups", Flag(query.IncludeGroups)),
            new("include_officials", Flag(query.IncludeOfficials)),
            new("include_official_subscriptions", Flag(query.IncludeOfficialSubscriptions)),
            new("include_official_services", Flag(query.IncludeOfficialServices)),
            new("include_former_friends", Flag(query.IncludeFormerFriends)),
            new("include_blocked", Flag(query.IncludeBlocked)),
            new("keyword", query.Keyword),
            new("account", query.Account),
        ]);

        return SendAsync<ContactListResponse>(HttpMethod.Get, path, null, UpstreamTimeout, cancellationToken);
    }

    /// <summary>GET /api/contacts/avatar?username=..，取头像原始字节（与图片下载同一条二进制通道）。</summary>
    public Task<(byte[] Bytes, string ContentType)> DownloadContactAvatarAsync(
        string url,
        CancellationToken cancellationToken = default)
        => DownloadImageAsync(url, cancellationToken);

    // ==================== 9. 朋友圈 ====================

    /// <summary>
    /// GET /api/sns/timeline。滚动加载用 <paramref name="offset"/> 翻页（每页 20 条）。
    /// </summary>
    /// <param name="usernames">只看这些人；null / 空集合 = 全部（不会传空串或 [""]）。</param>
    /// <param name="keyword">内容关键词，服务端过滤；null / 空 = 不限。</param>
    public Task<SnsTimelineResponse> GetSnsTimelineAsync(
        int limit = 20,
        int offset = 0,
        IReadOnlyList<string>? usernames = null,
        string? keyword = null,
        CancellationToken cancellationToken = default)
    {
        var path = "/api/sns/timeline" + BuildQuery(
        [
            new("limit", limit.ToString(CultureInfo.InvariantCulture)),
            new("offset", offset.ToString(CultureInfo.InvariantCulture)),
            new("usernames", JoinUsernames(usernames)),
            new("keyword", keyword),
        ]);

        return SendAsync<SnsTimelineResponse>(HttpMethod.Get, path, null, UpstreamTimeout, cancellationToken);
    }

    /// <summary>GET /api/sns/users，取"发过朋友圈的人 + postCount"，供按人筛选。</summary>
    public Task<SnsUsersResponse> GetSnsUsersAsync(
        string? keyword = null,
        int limit = 5000,
        CancellationToken cancellationToken = default)
    {
        var path = "/api/sns/users" + BuildQuery(
        [
            new("keyword", keyword),
            new("limit", limit.ToString(CultureInfo.InvariantCulture)),
        ]);

        return SendAsync<SnsUsersResponse>(HttpMethod.Get, path, null, UpstreamTimeout, cancellationToken);
    }

    /// <summary>
    /// POST /api/sns/sync-latest。把实时朋友圈增量同步进解密库，
    /// **进模块调一次即可**，不要每次滚动都调。
    /// </summary>
    public Task<SnsSyncLatestResponse> SyncSnsLatestAsync(
        SnsSyncLatestRequest? request = null,
        CancellationToken cancellationToken = default)
        => SendAsync<SnsSyncLatestResponse>(
            HttpMethod.Post, "/api/sns/sync-latest", request ?? new SnsSyncLatestRequest(),
            UpstreamTimeout, cancellationToken);

    /// <summary>
    /// POST /api/sns/media，取朋友圈图片字节。
    ///
    /// 走 POST body 而非 GET query：参数含 token / key（解密密钥），
    /// query 会被 uvicorn 写进 logs/chatlog.log（§10 R10 禁外发），body 则不进日志也不进 URL。
    /// </summary>
    public async Task<(byte[] Bytes, string ContentType)> GetSnsMediaAsync(
        SnsMediaRequest request,
        CancellationToken cancellationToken = default)
    {
        var (bytes, contentType, _) = await SendBytesAsync(
            HttpMethod.Post, "/api/sns/media", request, UpstreamTimeout, cancellationToken).ConfigureAwait(false);
        return (bytes, contentType);
    }

    /// <summary>GET /api/sns/video，取本地缓存视频字节（上游这条不含 token / key，故为 GET）。</summary>
    public async Task<(byte[] Bytes, string ContentType)> GetSnsVideoAsync(
        string postId,
        string mediaId,
        CancellationToken cancellationToken = default)
    {
        var path = "/api/sns/video" + BuildQuery(
        [
            new("post_id", postId),
            new("media_id", mediaId),
        ]);

        var (bytes, contentType, _) = await SendBytesAsync(
            HttpMethod.Get, path, null, DownloadTimeout, cancellationToken).ConfigureAwait(false);
        return (bytes, contentType);
    }

    // ==================== 9.1 朋友圈导出（异步任务） ====================

    /// <summary>POST /api/sns/export，创建导出任务，返回上游任务对象（export_id 用于后续轮询）。</summary>
    public Task<SnsExportResponse> CreateSnsExportAsync(
        SnsExportRequest request,
        CancellationToken cancellationToken = default)
        => SendAsync<SnsExportResponse>(
            HttpMethod.Post, "/api/sns/export", request, UpstreamTimeout, cancellationToken);

    /// <summary>GET /api/sns/export/{id}，轮询任务状态（第一版不用 SSE，见 §14.8.4）。</summary>
    public Task<SnsExportResponse> GetSnsExportStatusAsync(
        string exportId,
        CancellationToken cancellationToken = default)
        => SendAsync<SnsExportResponse>(
            HttpMethod.Get, "/api/sns/export/" + Uri.EscapeDataString(exportId), null,
            DefaultTimeout, cancellationToken);

    /// <summary>
    /// GET /api/sns/export/{id}/download，下载导出 ZIP 字节。
    /// 文件名优先取后端 Content-Disposition 里的建议名。
    /// </summary>
    public async Task<(byte[] Bytes, string ContentType, string FileName)> DownloadSnsExportAsync(
        string exportId,
        CancellationToken cancellationToken = default)
    {
        var path = "/api/sns/export/" + Uri.EscapeDataString(exportId) + "/download";
        return await SendBytesAsync(HttpMethod.Get, path, null, DownloadTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    // ==================== 4.1 图片 ====================

    /// <summary>
    /// GET /api/media/image，取图片原始字节与 Content-Type（导出内嵌 base64 用）。
    ///
    /// 为什么单独走一条通道、不复用 <see cref="SendAsync{T}"/>：
    /// 那个方法是为 JSON 设计的（要解析 body、校验 status），图片是二进制流，硬塞进去只会互相污染。
    /// 这里也**不设** <c>Cache-Control: no-store</c> —— 图片按 md5 寻址、内容不可变，
    /// 服务端已给了长缓存头，禁掉缓存纯属浪费。
    /// </summary>
    /// <param name="url">后端下发的图片地址（<c>ChatMessage.ImageUrl</c>，已是绝对地址）。</param>
    public async Task<(byte[] Bytes, string ContentType)> DownloadImageAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(DefaultTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Absolute));

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatlogApiException(null, $"下载图片超时（{DefaultTimeout.TotalSeconds:0} 秒）：{url}");
        }
        catch (HttpRequestException ex)
        {
            throw new ChatlogApiException(null, $"下载图片失败：{ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new ChatlogApiException(
                    response.StatusCode, $"下载图片失败（HTTP {(int)response.StatusCode}）");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            return (bytes, contentType);
        }
    }

    // ==================== 传输层 ====================

    /// <summary>
    /// 把参数拼成查询串；值为 null / 空串的项**整项省略**，而不是拼成 <c>key=</c>。
    /// 后者会被后端当成有效筛选条件（例如 <c>usernames=</c> 变成"只看空用户名"）。
    /// </summary>
    private static string BuildQuery(IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        var builder = new StringBuilder();
        foreach (var pair in parameters)
        {
            if (string.IsNullOrEmpty(pair.Value))
            {
                continue;
            }

            builder.Append(builder.Length == 0 ? '?' : '&');
            builder.Append(pair.Key).Append('=').Append(Uri.EscapeDataString(pair.Value));
        }

        return builder.ToString();
    }

    /// <summary>布尔开关转查询串文本；null = 不下发该参数（由服务端 / 上游默认值决定）。</summary>
    private static string? Flag(bool? value) => value.HasValue ? (value.Value ? "true" : "false") : null;

    /// <summary>
    /// usernames 归一为逗号分隔字符串（§14.8.2）：空集合 / 全空白 → null（不传该参数 = 全部），
    /// 绝不传空串或 <c>[""]</c>。
    /// </summary>
    private static string? JoinUsernames(IReadOnlyList<string>? usernames)
    {
        if (usernames is null || usernames.Count == 0)
        {
            return null;
        }

        var kept = new List<string>(usernames.Count);
        foreach (var item in usernames)
        {
            if (!string.IsNullOrWhiteSpace(item))
            {
                kept.Add(item.Trim());
            }
        }

        return kept.Count == 0 ? null : string.Join(",", kept);
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

        using var request = new HttpRequestMessage(method, new Uri(BaseUrl + path, UriKind.Absolute));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, WriteOptions);
            request.Content = new StringContent(json, new UTF8Encoding(false), "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 调用方没取消，那就是本次请求超时。
            throw new ChatlogApiException(null, $"请求本地服务超时（{timeout.TotalSeconds:0} 秒）：{method} {path}");
        }
        catch (HttpRequestException ex)
        {
            throw new ChatlogApiException(null, $"无法连接本地服务（{BaseUrl}）：{ex.Message}");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new ChatlogApiException(response.StatusCode, DescribeError(response.StatusCode, text));
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                throw new ChatlogApiException(
                    response.StatusCode, $"本地服务返回了非 JSON 内容：{Truncate(text)}");
            }

            using (document)
            {
                EnsureStatus(document.RootElement, path);
                return document.RootElement.Deserialize<T>(ReadOptions)
                       ?? throw new ChatlogApiException(response.StatusCode, "本地服务返回了空响应体。");
            }
        }
    }

    /// <summary>
    /// 二进制通道（朋友圈图片 / 视频 / 导出 ZIP）：不做 JSON 解析、不校验 status，
    /// 失败时从错误体里按 <see cref="DescribeError"/> 的优先级取文案。
    /// </summary>
    private async Task<(byte[] Bytes, string ContentType, string FileName)> SendBytesAsync(
        HttpMethod method,
        string path,
        object? body,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

        using var request = new HttpRequestMessage(method, new Uri(BaseUrl + path, UriKind.Absolute));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, WriteOptions);
            request.Content = new StringContent(json, new UTF8Encoding(false), "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatlogApiException(null, $"请求本地服务超时（{timeout.TotalSeconds:0} 秒）：{method} {path}");
        }
        catch (HttpRequestException ex)
        {
            throw new ChatlogApiException(null, $"无法连接本地服务（{BaseUrl}）：{ex.Message}");
        }

        using (response)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ChatlogApiException(
                    response.StatusCode,
                    DescribeError(response.StatusCode, Encoding.UTF8.GetString(bytes)));
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                           ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                           ?? "";
            return (bytes, contentType, fileName);
        }
    }

    /// <summary>
    /// 成功判定（API_REFERENCE.md §11.3）：
    /// 缺 status 字段的接口（<c>/api/config</c>）视为成功；<c>/api/health</c> 是 <c>ok</c>；
    /// 其余必须是 <c>success</c>。
    /// </summary>
    private static void EnsureStatus(JsonElement root, string path)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var status))
        {
            return;
        }

        if (status.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var value = status.GetString();
        if (value is null or "success" or "ok")
        {
            return;
        }

        throw new ChatlogApiException(null, $"本地服务返回异常状态（{value}）：{path}");
    }

    /// <summary>
    /// 错误文案解析（API_REFERENCE.md §0.1）：detail → message → status_info.msg → HTTP 码。
    /// detail 在 FastAPI 参数校验失败（422）时是**数组**，形如 <c>[{"msg": "..."}]</c>，需一并处理。
    /// </summary>
    private static string DescribeError(HttpStatusCode statusCode, string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryReadText(root, "detail", out var detail))
                {
                    return detail;
                }

                if (TryReadText(root, "message", out var message))
                {
                    return message;
                }

                if (root.TryGetProperty("status_info", out var statusInfo)
                    && statusInfo.ValueKind == JsonValueKind.Object
                    && TryReadText(statusInfo, "msg", out var msg))
                {
                    return msg;
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON 错误体，落到下面的兜底文案。
        }

        return string.IsNullOrWhiteSpace(text)
            ? $"本地服务返回 HTTP {(int)statusCode}"
            : $"本地服务返回 HTTP {(int)statusCode}：{Truncate(text)}";
    }

    private static bool TryReadText(JsonElement root, string name, out string text)
    {
        text = "";
        if (!root.TryGetProperty(name, out var value))
        {
            return false;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                text = value.GetString() ?? "";
                return text.Length > 0;

            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && TryReadText(item, "msg", out var inner))
                    {
                        text = inner;
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    private static string Truncate(string text)
        => text.Length <= 300 ? text : text[..300] + "…";

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
