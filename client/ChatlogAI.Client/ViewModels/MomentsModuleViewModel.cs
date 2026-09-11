using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.Services;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// 「朋友圈」模块（T23）的视图模型：时间线分页 + 同步最新 + 按人 / 关键词筛选 + 大图查看器。
///
/// 与 <c>ContactsModuleViewModel</c> 同一挂法：由 <c>MainViewModel.Moments</c> 持有，
/// 模块内用 <c>{Binding Moments.Xxx}</c> 绑定，模块 View 自己不设 DataContext。
///
/// 四条必须守住的设计（都是这类"无限滚动 + 大量图片"最容易做错的地方）：
/// 1. <b>"只加载一次"的标记只在时间线成功后置位</b>。首次进模块时上游 503 是常态路径，
///    若那时就把标记点上，用户点「同步最新」或重进模块都救不回来。
/// 2. <b>失败不清空已有卡片</b>。一次网络抖动不该让用户看到"朋友圈全没了"。
/// 3. <b>翻页按 <c>SnsPost.Id</c> 去重</b>，且 offset 按"服务端返回的条数"累加 ——
///    边界重复是分页接口的常态，而按"实际新增数"累加会漏掉被去重掉的那几条、串页。
/// 4. <b>筛选改动不自动请求</b>，由「应用」/ 回车显式触发（与联系人模块同一条约定）。
///
/// 本模块**没有** AI 分析：不出现「生成分析」按钮，也不调 <c>/api/summarize</c>（§14.8.5 风险 4）。
/// </summary>
public sealed class MomentsModuleViewModel : ObservableObject, IDisposable
{
    /// <summary>每页 20 条（<see cref="ChatlogApiClient.GetSnsTimelineAsync"/> 的默认值）。</summary>
    private const int PageSize = 20;

    private readonly ChatlogApiClient _api;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly MomentMediaLoader _mediaLoader;

    /// <summary>已收到过的动态 id，用于翻页去重（只增不减；换筛选时清空重建）。</summary>
    private readonly HashSet<string> _seenPostKeys = new(StringComparer.Ordinal);

    /// <summary>「应用」那一刻快照下来的筛选条件：之后勾选框再怎么动，都不会影响正在翻的这一份数据。</summary>
    private readonly List<string> _appliedUsernames = [];
    private string _appliedKeyword = "";

    private CancellationTokenSource? _viewerRequest;

    private int _nextOffset;
    private bool _loadedOnce;
    private bool _entering;
    private bool _loadingPage;
    private bool _loadingMore;
    private bool _syncing;
    private bool _usersLoaded;
    private bool _hasMore;
    private bool _isLoading;
    private bool _isLoadingMore;
    private bool _isSyncing;
    private bool _hasError;
    private string _errorMessage = "";
    private bool _hasSourceFallback;
    private string _sourceFallbackText = "";
    private string _syncStatusText = "";
    private bool _hasSyncStatus;
    private string _placeholder = "正在读取朋友圈……";
    private string _keyword = "";
    private bool _isViewerOpen;
    private ImageSource? _viewerImage;
    private string _viewerStateText = "";
    private string _viewerCaption = "";

    public MomentsModuleViewModel(ChatlogApiClient api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _mediaLoader = new MomentMediaLoader(_api);

        ApplyCommand = new AsyncRelayCommand(ApplyFilterAsync);

        // 「同步最新」= 同步 + 重查第一页。只同步不重查的话，用户点了按钮看不到任何变化，
        // 只会以为按钮坏了；代价是已翻到第 N 页的滚动位置会被丢掉（刷新本来就该回到最新一条）。
        SyncLatestCommand = new AsyncRelayCommand(SyncAndReloadAsync);
        ClearUserFilterCommand = new RelayCommand(ClearUserFilter);
        SelectAllUsersCommand = new RelayCommand(SelectAllUsers);
        OpenViewerCommand = new RelayCommand(parameter => OpenViewer(parameter as MomentMediaViewModel));
        CloseViewerCommand = new RelayCommand(CloseViewer);
    }

    // ==================== 动态流 ====================

    public ObservableCollection<MomentItemViewModel> Timeline { get; } = [];

    public bool HasTimeline => Timeline.Count > 0;

    /// <summary>空态 / 加载中的文案（有卡片、或已有错误横幅时为空串）。</summary>
    public string Placeholder
    {
        get => _placeholder;
        private set => SetProperty(ref _placeholder, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsLoadingMore
    {
        get => _isLoadingMore;
        private set => SetProperty(ref _isLoadingMore, value);
    }

    public bool IsSyncing
    {
        get => _isSyncing;
        private set => SetProperty(ref _isSyncing, value);
    }

    /// <summary>是否还有下一页。滚动到底的判据之一，最后由 View 传给 <see cref="LoadNextPageAsync"/>。</summary>
    public bool HasMore => _hasMore;

    public string CountText => Timeline.Count == 0
        ? "尚未加载"
        : $"已加载 {Timeline.Count} 条{(HasMore ? "（可继续下滑）" : "（到底了）")}";

    /// <summary>接口失败（含上游 503）：走这条内联提示，**不清空列表**。</summary>
    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>后端回报 <c>sourceFallback</c>：实时库连不上，当前看的是解密库快照（§14.8.1 末段）。</summary>
    public bool HasSourceFallback
    {
        get => _hasSourceFallback;
        private set => SetProperty(ref _hasSourceFallback, value);
    }

    public string SourceFallbackText
    {
        get => _sourceFallbackText;
        private set => SetProperty(ref _sourceFallbackText, value);
    }

    /// <summary>「同步最新」的结果文案（成功 / 失败都要落在界面上，用户才知道同步到底有没有发生）。</summary>
    public bool HasSyncStatus
    {
        get => _hasSyncStatus;
        private set => SetProperty(ref _hasSyncStatus, value);
    }

    public string SyncStatusText
    {
        get => _syncStatusText;
        private set => SetProperty(ref _syncStatusText, value);
    }

    // ==================== 筛选（改动不自动请求） ====================

    /// <summary>发布人列表（数据来自 <c>GetSnsUsersAsync</c>，含每人动态条数）。</summary>
    public ObservableCollection<MomentUserOption> Users { get; } = [];

    public bool HasUsers => Users.Count > 0;

    /// <summary>
    /// 内容关键词。刻意只 SetProperty、不发请求：搜索是**服务端过滤**，
    /// 每敲一个字符打一次后端是白花，由「应用」/ 回车显式触发一次。
    /// </summary>
    public string Keyword
    {
        get => _keyword;
        set => SetProperty(ref _keyword, value);
    }

    // ==================== 大图查看器 ====================

    /// <summary>
    /// 模块内遮罩是否展示。为什么不新开一个查看器窗口：大图要贴着九宫格出现，
    /// 而"弹窗 + hover 位置"这类做法在 WPF 里既难对齐又抢焦点；本模块是 Visibility 切换、
    /// 不销毁的，所以遮罩状态必须由 View 在模块不可见时显式关掉（见 MomentsModuleView）。
    /// </summary>
    public bool IsViewerOpen
    {
        get => _isViewerOpen;
        private set => SetProperty(ref _isViewerOpen, value);
    }

    public ImageSource? ViewerImage
    {
        get => _viewerImage;
        private set
        {
            if (SetProperty(ref _viewerImage, value))
            {
                OnPropertyChanged(nameof(HasViewerImage));
            }
        }
    }

    public bool HasViewerImage => _viewerImage is not null;

    /// <summary>查看器里的状态文案（加载中 / 加载失败）；拿到图后为空串。</summary>
    public string ViewerStateText
    {
        get => _viewerStateText;
        private set
        {
            if (SetProperty(ref _viewerStateText, value))
            {
                OnPropertyChanged(nameof(HasViewerStateText));
            }
        }
    }

    public bool HasViewerStateText => _viewerStateText.Length > 0;

    /// <summary>查看器顶部的说明（发布人 · 时间），由格子 VM 带着卡片信息传进来。</summary>
    public string ViewerCaption
    {
        get => _viewerCaption;
        private set => SetProperty(ref _viewerCaption, value);
    }

    // ==================== 命令 ====================

    /// <summary>「应用」与回车共用：用当前勾选的人 + 关键词重查第一页。</summary>
    public ICommand ApplyCommand { get; }

    /// <summary>「同步最新」：把实时朋友圈增量同步进解密库，随后重查第一页。</summary>
    public ICommand SyncLatestCommand { get; }

    /// <summary>清空按人筛选（回到"全部"）。选中人的勾选框在 ListBox 里，没有它用户得手动一个个取消。</summary>
    public ICommand ClearUserFilterCommand { get; }

    /// <summary>全选发布人（"只看某几个人"的反向操作）。</summary>
    public ICommand SelectAllUsersCommand { get; }

    public ICommand OpenViewerCommand { get; }

    public ICommand CloseViewerCommand { get; }

    // ==================== 入口 ====================

    /// <summary>
    /// 首次进入本模块时调用（<c>MomentsModuleView</c> 监听 IsVisibleChanged）。
    ///
    /// 顺序是「先同步最新、再取时间线」：同步是把实时增量拉进解密库的动作，
    /// 少了它可能看到旧数据（§14.8.3）。同步失败不阻断取时间线 ——
    /// 时间线自己的 503 会以横幅告知用户，而重试入口有两个（「同步最新」按钮、重进模块）。
    /// </summary>
    public Task EnsureLoadedAsync() => _loadedOnce ? Task.CompletedTask : EnterModuleAsync();

    private async Task EnterModuleAsync()
    {
        // 防重入：首次可见与用户立刻点「同步最新」可能撞在一起，只让第一个跑。
        if (_entering)
        {
            return;
        }

        _entering = true;
        try
        {
            await SyncAndReloadAsync();
            await EnsureUsersAsync();
        }
        finally
        {
            _entering = false;
        }
    }

    /// <summary>同步实时增量，然后重查第一页（「同步最新」与"进模块"共用这一条路径）。</summary>
    private async Task SyncAndReloadAsync()
    {
        await SyncLatestAsync();
        await LoadFirstPageAsync();
    }

    /// <summary>「应用」：先把当前勾选与关键词**快照**下来，再重查第一页。</summary>
    private Task ApplyFilterAsync()
    {
        _appliedUsernames.Clear();
        _appliedUsernames.AddRange(CheckedUsernames());

        _appliedKeyword = Keyword.Trim();
        return LoadFirstPageAsync();
    }

    /// <summary>
    /// 左栏**当前勾选**的发布人（勾选与"点没点过应用"无关）。
    /// 导出弹窗要用这一份而不是 <c>_appliedUsernames</c>：用户勾了人直接点「导出…」是很自然的动作，
    /// 若导出侧要求"先点应用"，就会变成"我明明勾了人，它却说我没选"。
    /// </summary>
    private List<string> CheckedUsernames()
        => Users
            .Where(option => option.IsSelected)
            .Select(option => option.User.Username)
            .Where(username => username.Length > 0)
            .ToList();

    private void ClearUserFilter()
    {
        foreach (var option in Users)
        {
            option.IsSelected = false;
        }
    }

    private void SelectAllUsers()
    {
        foreach (var option in Users)
        {
            option.IsSelected = true;
        }
    }

    // ==================== 同步最新 ====================

    private async Task SyncLatestAsync()
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        IsSyncing = true;
        HasSyncStatus = true;
        SyncStatusText = "正在同步最新朋友圈……";

        try
        {
            var response = await _api.SyncSnsLatestAsync(null, _lifetime.Token);
            SyncStatusText = BuildSyncStatusText(response);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 主窗口正在退出：不再回写界面状态。
        }
        catch (ChatlogApiException ex)
        {
            // 后端 detail 已是面向用户的中文文案（如上游没起来时的 503），原样展示即可。
            SyncStatusText = "同步失败：" + (string.IsNullOrWhiteSpace(ex.Message) ? "未知原因" : ex.Message);
        }
        finally
        {
            _syncing = false;
            IsSyncing = false;
        }
    }

    /// <summary>
    /// 同步成功的文案。
    ///
    /// "已同步最新朋友圈"这句话本身没有信息量：同步了 200 条还是 0 条，用户无从判断，
    /// 所以把上游回传的"扫描 N 条 / 新增 M 条"一并显示出来。
    ///
    /// 只用 <c>scanned</c> 与 <c>upserted</c> 两个键（实测确认过、无歧义）；
    /// 它们不在契约 DTO 里，而是走 <see cref="SnsSyncLatestResponse.Extra"/> 逐字透传，
    /// 因此**两个都拿得到才显示括号**，否则原样退回不带数字的文案 —— 不显示 undefined、不抛异常。
    /// </summary>
    private static string BuildSyncStatusText(SnsSyncLatestResponse response)
    {
        const string baseText = "已同步最新朋友圈";

        if (response.Extra is not { } extra
            || !TryReadInt(extra, "scanned", out var scanned)
            || !TryReadInt(extra, "upserted", out var upserted))
        {
            return baseText;
        }

        return $"{baseText}（扫描 {scanned} 条，新增 {upserted} 条）";
    }

    private static bool TryReadInt(IReadOnlyDictionary<string, JsonElement> extra, string key, out int value)
    {
        value = 0;

        // 只看 ValueKind：字段缺失、类型不对（比如上游给了字符串"200"）都当成"拿不到"，
        // 而不是硬转 —— 显示不出来只是少一个数字，转错才会显示成假数据。
        return extra.TryGetValue(key, out var element)
               && element.ValueKind == JsonValueKind.Number
               && element.TryGetInt32(out value);
    }

    // ==================== 时间线 ====================

    private async Task LoadFirstPageAsync()
    {
        if (_loadingPage)
        {
            return;
        }

        _loadingPage = true;
        IsLoading = true;
        HasError = false;
        RefreshPlaceholder();

        try
        {
            var response = await _api.GetSnsTimelineAsync(
                PageSize, 0, _appliedUsernames, KeywordOrNull(), _lifetime.Token);

            // 成功之后才替换列表：失败保持现有卡片（宁可显示旧数据也不清空）。
            _seenPostKeys.Clear();
            _nextOffset = 0;
            Timeline.Clear();
            AppendPage(response);

            // "只加载一次"的标记只在**成功之后**置位：首次 503 时必须保持未加载，
            // 用户点「同步最新」/ 重进模块才能重试。
            _loadedOnce = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ChatlogApiException ex)
        {
            ErrorMessage = string.IsNullOrWhiteSpace(ex.Message) ? "朋友圈读取失败" : ex.Message;
            HasError = true;
        }
        finally
        {
            _loadingPage = false;
            IsLoading = false;
            RefreshPlaceholder();
        }
    }

    /// <summary>滚动到底由 View 调用（见 MomentsModuleView 的 ScrollChanged）。</summary>
    public async Task LoadNextPageAsync()
    {
        if (_loadingPage || _loadingMore || !_hasMore || Timeline.Count == 0)
        {
            return;
        }

        _loadingMore = true;
        IsLoadingMore = true;

        try
        {
            var response = await _api.GetSnsTimelineAsync(
                PageSize, _nextOffset, _appliedUsernames, KeywordOrNull(), _lifetime.Token);
            AppendPage(response);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ChatlogApiException ex)
        {
            // 翻页失败不打断已有内容：只挂横幅，滚动位置与卡片都保持不变。
            ErrorMessage = string.IsNullOrWhiteSpace(ex.Message) ? "朋友圈读取失败" : ex.Message;
            HasError = true;
        }
        finally
        {
            _loadingMore = false;
            IsLoadingMore = false;
        }
    }

    private void AppendPage(SnsTimelineResponse response)
    {
        foreach (var post in response.Timeline)
        {
            var key = DedupeKey(post);
            if (!_seenPostKeys.Add(key))
            {
                continue;
            }

            Timeline.Add(new MomentItemViewModel(post, _mediaLoader));
        }

        // offset 按"服务端返回的条数"累加，而不是按"新增的条数"：
        // 被去重丢掉的那些同样占用了服务端的这一页，若不计入，下一页会重新取到同一批数据。
        _nextOffset += response.Timeline.Count;

        // 空页但 hasMore=true 会让"滚到底"变成一个死循环（每次触发都取回空页），
        // 因此宁可在这里把 hasMore 收掉。
        _hasMore = response.HasMore && response.Timeline.Count > 0;

        HasSourceFallback = response.SourceFallback;
        SourceFallbackText = response.SourceFallback ? BuildFallbackText(response) : string.Empty;

        OnPropertyChanged(nameof(HasTimeline));
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasUsers));
        RefreshPlaceholder();
    }

    /// <summary>
    /// 去重键。<c>Id</c> 是实测存在的字段，优先用它；缺失时退回 tid，
    /// 再缺就用"发布人 + 原始时间 + 正文"组合 —— 空键会让这些动态被整批丢掉，
    /// 用户看到的是"数据少了"，比偶尔漏一次去重严重得多。
    /// </summary>
    private static string DedupeKey(SnsPost post)
    {
        if (post.Id.Length > 0)
        {
            return post.Id;
        }

        return post.Tid.Length > 0
            ? post.Tid
            : $"{post.Username}|{post.CreateTime}|{post.ContentDesc}";
    }

    private string? KeywordOrNull()
    {
        // 空关键词不下发（由 BuildQuery 处理成省略参数），避免传一个空的 keyword= 上去。
        var keyword = _appliedKeyword.Trim();
        return keyword.Length == 0 ? null : keyword;
    }

    private static string BuildFallbackText(SnsTimelineResponse response)
    {
        var text = "实时库暂不可用，当前为解密库快照";
        if (response.SourceFallbackRetryAfterSeconds > 0)
        {
            text += $"（建议 {response.SourceFallbackRetryAfterSeconds} 秒后重试）";
        }

        return text;
    }

    private void RefreshPlaceholder()
    {
        if (_isLoading)
        {
            Placeholder = "正在读取朋友圈……";
            return;
        }

        // 出错时列表若为空，说明的是错误横幅，不再叠一句空态文案。
        Placeholder = _hasError || Timeline.Count > 0 ? string.Empty : "没有朋友圈动态";
    }

    // ==================== 发布人列表 ====================

    /// <summary>
    /// 取"发过朋友圈的人"。失败重试的开关（<c>_usersLoaded</c>）同样只在成功后置位，
    /// 且失败**不覆盖**时间线已有的错误文案：两条错误同时出现时，时间线那条更值得看。
    /// </summary>
    private async Task EnsureUsersAsync()
    {
        if (_usersLoaded)
        {
            return;
        }

        try
        {
            var response = await _api.GetSnsUsersAsync(null, 5000, _lifetime.Token);

            Users.Clear();
            foreach (var user in response.Items)
            {
                if (!string.IsNullOrWhiteSpace(user.Username))
                {
                    Users.Add(new MomentUserOption(user));
                }
            }

            _usersLoaded = true;
            OnPropertyChanged(nameof(HasUsers));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ChatlogApiException ex)
        {
            if (!HasError)
            {
                ErrorMessage = string.IsNullOrWhiteSpace(ex.Message) ? "发布人列表读取失败" : ex.Message;
                HasError = true;
            }
        }
    }

    // ==================== 大图查看器 ====================

    private void OpenViewer(MomentMediaViewModel? cell)
    {
        // 上游没给取图参数的那些格子点了也没意义（§14.8.1.1 实测的视频号那条）。
        if (cell is null || !cell.CanOpenViewer)
        {
            return;
        }

        // 取消上一次仍在途的原图请求：用户连点几张图时，后到的旧响应不该盖掉新图。
        // 被取消的那次自己负责 Dispose（见下面的 finally），这里不替它释放。
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var previous = _viewerRequest;
        _viewerRequest = request;
        previous?.Cancel();

        IsViewerOpen = true;
        ViewerImage = null;
        ViewerStateText = "正在加载原图……";
        ViewerCaption = cell.ViewerCaption;

        // 取原图**不放在命令体里 await**：这条命令是所有格子共用的一个实例，
        // 而后者的"执行中"状态是全局的（CanExecute 返回 false 且会禁用绑定它的所有按钮）。
        // 若把它 await 到取图结束，一张大图加载期间用户就点不开其它任何一张图。
        _ = LoadViewerImageAsync(cell, request);
    }

    private async Task LoadViewerImageAsync(MomentMediaViewModel cell, CancellationTokenSource request)
    {
        try
        {
            var (image, error) = await _mediaLoader.LoadFullAsync(cell, request.Token);

            // 期间用户可能已经关了查看器、或点了另一张图：这一份结果已经过期，直接丢掉
            // （否则旧响应会盖掉新图）。
            if (!ReferenceEquals(_viewerRequest, request))
            {
                return;
            }

            if (image is null)
            {
                ViewerStateText = error.Length > 0 ? "原图加载失败：" + error : string.Empty;
                return;
            }

            ViewerImage = image;
            ViewerStateText = string.Empty;
        }
        catch (OperationCanceledException)
        {
            // 用户关掉了查看器 / 点了另一张图（或切走了模块）：保持现状即可，
            // 遮罩此时要么已关、要么已被下一次打开重置。
        }
        finally
        {
            if (ReferenceEquals(_viewerRequest, request))
            {
                _viewerRequest = null;
            }

            request.Dispose();
        }
    }

    /// <summary>
    /// 关闭查看器（点遮罩空白处 / Esc / 切模块，见 MomentsModuleView）。
    /// 三种关闭方式走同一个入口，状态复位只写一份。
    /// </summary>
    public void CloseViewer()
    {
        _viewerRequest?.Cancel();
        _viewerRequest = null;

        IsViewerOpen = false;
        ViewerImage = null;
        ViewerStateText = string.Empty;
        ViewerCaption = string.Empty;
    }

    // ==================== 导出（弹窗本体见 Views/MomentsExportWindow） ====================

    /// <summary>
    /// 造一份导出弹窗的 VM（每次打开都新建，见 §7.8 的既有做法）：
    /// 弹窗里全是"未提交的编辑中状态"，复用同一个实例会出现上次没改完的选项又冒出来。
    ///
    /// 共享本模块的 <see cref="ChatlogApiClient"/>：不给弹窗另起客户端，
    /// 否则会多出一份 <c>HttpClient</c> 与连接池，退出时还得单独释放。
    ///
    /// 传入的是**左栏当前勾选**的发布人（不是"已应用"的那份）：勾了人直接点导出是用户的自然动作，
    /// 不该要求他先去点一次「应用」。
    /// </summary>
    public MomentsExportViewModel CreateExportViewModel()
        => new(_api, CheckedUsernames());

    // ==================== 释放 ====================

    /// <summary>释放令牌源（在途的时间线 / 同步 / 取图请求会被取消），并关掉媒体加载器。</summary>
    public void Dispose()
    {
        CloseViewer();
        _mediaLoader.Dispose();

        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

/// <summary>
/// 「按人筛选」列表里的一行。
///
/// 为什么要包一层而不是直接把 <see cref="SnsUser"/> 交给 ListBox：勾选状态必须挂在行上，
/// 而 DTO 是不可变 record（也不该为界面加可变字段）；ListBox 的
/// <c>SelectedItems</c> 又不可绑定，只能靠 <c>ListBoxItem.IsSelected</c> ↔ 行 VM 双向绑定，
/// 与联系人模块同一套做法。
/// </summary>
public sealed class MomentUserOption : ObservableObject
{
    private bool _isSelected;

    internal MomentUserOption(SnsUser user)
    {
        User = user;
        DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName.Trim();
    }

    public SnsUser User { get; }

    public string DisplayName { get; }

    /// <summary>该人发过多少条（右上角的次要信息）。</summary>
    public string CountText => $"{User.PostCount} 条";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
