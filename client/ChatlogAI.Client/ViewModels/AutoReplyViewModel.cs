using System.Collections.ObjectModel;
using System.Globalization;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.Services;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// 「自动回复设置」弹窗的视图模型，逐条对照 <c>app.js</c> 的
/// <c>loadAutoReplyConfig</c> / <c>applyAutoReplyConfigToForm</c> /
/// <c>loadGuardChatOptions</c> / <c>renderGuardTargetList</c> / <c>collectGuardTargets</c> /
/// <c>saveAutoReplyConfig</c> / <c>testHotkey</c> / <c>testFullFlow</c>。
///
/// 它与 <see cref="MainViewModel"/> 的分工：主 VM 只负责"状态栏那一行自动回复文案 + 3 秒轮询"，
/// 本 VM 只在弹窗存活期间存在，负责表单状态与四个写操作。之所以不复用主 VM，
/// 是因为弹窗里的一切（勾选态、提示词草稿）都是"未提交的编辑中状态"，
/// 一旦和主界面的状态混在一个对象里，就很难说清"哪份才是服务端的真值"。
///
/// 三处刻意偏离原前端（都是为了避免静默数据丢失，原实现是会真出事的）：
/// 1. 原前端读配置失败时直接拿内置默认值填表，用户不编辑就点保存会把服务端配置整份覆盖成默认值。
///    这里改为：读失败就禁用保存并弹一次错误，宁可这次改不了，也不覆盖。
/// 2. 原前端 <c>/api/chats</c> 失败时列表为空且文案停在"正在加载微信会话……"，
///    保存则会提交空的守护列表（等于清空守护对象）。这里同样改为禁用保存 + 明确文案。
/// 3. 原前端 <c>context_count</c> 走 HTML <c>type=number</c>，输入非法时 <c>Number()</c> 得到 NaN，
///    序列化成 <c>null</c> 被后端跳过；这里改为保存前显式校验并给出提示，不做静默兜底。
/// </summary>
public sealed class AutoReplyViewModel : ObservableObject
{
    // 上限来自 docs/API_REFERENCE.md §6.2：好友 8 / 群聊 3，两组各自独立计数，超限后端 400。
    private const int MaxFriendTargets = 8;
    private const int MaxGroupTargets = 3;

    private const int MinContextCount = 1;
    private const int MaxContextCount = 2000;
    private const int DefaultContextCount = 80;

    // 同一会话的最小回复间隔：上限与服务端一致（服务端夹到 [0, 600]）；0 表示不限制。
    private const int MinReplyCooldownSeconds = 0;
    private const int MaxReplyCooldownSeconds = 600;
    private const int DefaultReplyCooldownSeconds = 10;

    // 快捷键默认值来自 app.js applyAutoReplyConfigToForm()：值为空时用这些兜底。
    private const string DefaultOpenWechatHotkey = "ctrl+alt+w";
    private const string DefaultSearchHotkey = "ctrl+f";
    private const string DefaultPasteHotkey = "ctrl+v";
    private const string DefaultSendHotkey = "enter";

    /// <summary>
    /// 「搜索后确认进入会话」的默认值（§14.3.4）。旧配置里没有 confirm 字段，
    /// <see cref="ApplyConfig"/> 的 Fallback 会落到这里，因此升级后行为不变。
    /// </summary>
    private const string DefaultConfirmHotkey = "enter";

    // 快捷键提示区的两句文案（分两层，见 §14.3.2 第 3 条 / §14.3.6 风险 4）：
    // 常驻引导讲"要你自己填"，点「自动识别」后才换成"识别不到、仍要你自己填"。
    // 两句都必须含"请手工填写"，只写"识别失败"会让用户一直以为自己用错了。
    private const string InitialHotkeyHint = "请手工填写微信「设置 → 快捷键」中的值（程序不会自动读取），改完点「保存」生效。";
    private const string DetectFailedHotkeyHint = "未能自动识别（微信 4.1 未提供可读的快捷键设置），请手工填写微信「设置 → 快捷键」中的值。";

    /// <summary>完整流程测试用的固定文案，与 app.js <c>testFullFlow()</c> 逐字一致。</summary>
    public const string FullFlowTestText = "这是一条 Chatlog AI 自动回复测试消息";

    private readonly ChatlogApiClient _api;
    private readonly Func<string?> _currentChatName;
    private readonly CancellationTokenSource _lifetime = new();

    private bool _configLoaded;
    private bool _guardLoaded;
    private bool _guardLoading = true;

    /// <summary>服务端已保存的守护对象，供候选列表回填勾选态。</summary>
    private IReadOnlyList<GuardTarget> _savedTargets = [];

    /// <summary>知识文档列表（T-F4），供「关联文档」选择器列出可选文档。</summary>
    private IReadOnlyList<DocItem> _docs = [];

    private bool _enabled;
    private string _contextCountText = DefaultContextCount.ToString(CultureInfo.InvariantCulture);
    private string _replyCooldownText = DefaultReplyCooldownSeconds.ToString(CultureInfo.InvariantCulture);
    private string _replyPrompt = "";
    private string _guardPlaceholder = "正在加载微信会话……";

    private string _openWechatHotkey = DefaultOpenWechatHotkey;
    private string _searchHotkey = DefaultSearchHotkey;
    private string _pasteHotkey = DefaultPasteHotkey;
    private string _sendHotkey = DefaultSendHotkey;
    private string _confirmHotkey = DefaultConfirmHotkey;
    private string _hotkeyHintText = InitialHotkeyHint;

    /// <param name="api">共享的 REST 客户端，由 <see cref="MainViewModel"/> 传入。</param>
    /// <param name="currentChatName">
    /// 返回主界面当前选中的会话名，用于"测试完整发送流程"的 <c>target_name</c>。
    /// 传委托而不是整个主 VM：本 VM 只需要这一个信息，不该知道主界面还有什么。
    /// </param>
    public AutoReplyViewModel(ChatlogApiClient api, Func<string?> currentChatName)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _currentChatName = currentChatName ?? throw new ArgumentNullException(nameof(currentChatName));

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => _configLoaded && _guardLoaded);
        TestHotkeyCommand = new AsyncRelayCommand(TestHotkeyAsync);
        TestFullFlowCommand = new AsyncRelayCommand(TestFullFlowAsync);
        DetectHotkeysCommand = new AsyncRelayCommand(DetectHotkeysAsync);
        RestoreDefaultsCommand = new RelayCommand(RestoreDefaults);
    }

    // ==================== 对外事件（View 职责，VM 不引用 MessageBox） ====================

    /// <summary>需要弹一个提示框（对应原前端的 <c>window.alert</c>）。</summary>
    public event Action<string>? AlertRequested;

    /// <summary>需要一次"是/否"确认（对应原前端的 <c>window.confirm</c>）；返回用户是否确认。</summary>
    public event Func<string, bool>? ConfirmRequested;

    /// <summary>保存成功，View 据此关闭弹窗。</summary>
    public event Action? CloseRequested;

    // ==================== 命令 ====================

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand TestHotkeyCommand { get; }

    public AsyncRelayCommand TestFullFlowCommand { get; }

    /// <summary>「自动识别」：探测快捷键，结果只更新提示区，不自动填表单、不落盘。</summary>
    public AsyncRelayCommand DetectHotkeysCommand { get; }

    /// <summary>「恢复默认值」：只把五个框填回默认值，仍需用户自己点「保存」。</summary>
    public RelayCommand RestoreDefaultsCommand { get; }

    // ==================== 表单状态 ====================

    /// <summary>两个真正写操作的前置条件都满足时，才允许保存（见类注释的偏离说明 1、2）。</summary>
    public bool IsReadyToSave => _configLoaded && _guardLoaded;

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    /// <summary>
    /// 上下文条数。<b>刻意用 string 承载</b>：WPF 的 <c>TextBox</c> 绑 <c>int</c> 时，
    /// 输入非法字符会让绑定更新静默失败、源值保持旧值，用户看不出自己输入被忽略了。
    /// 这里保留原始文本，保存时再校验并明确报错。
    /// </summary>
    public string ContextCountText
    {
        get => _contextCountText;
        set => SetProperty(ref _contextCountText, value);
    }

    /// <summary>
    /// 同一会话的最小回复间隔（秒）。与 <see cref="ContextCountText"/> 一样用 string 承载，
    /// 理由见上面那段注释：输入非法字符要让用户看得见，而不是被绑定静默忽略。
    /// </summary>
    public string ReplyCooldownText
    {
        get => _replyCooldownText;
        set => SetProperty(ref _replyCooldownText, value);
    }

    public string ReplyPrompt
    {
        get => _replyPrompt;
        set => SetProperty(ref _replyPrompt, value);
    }

    public string OpenWechatHotkey
    {
        get => _openWechatHotkey;
        set => SetProperty(ref _openWechatHotkey, value);
    }

    public string SearchHotkey
    {
        get => _searchHotkey;
        set => SetProperty(ref _searchHotkey, value);
    }

    public string PasteHotkey
    {
        get => _pasteHotkey;
        set => SetProperty(ref _pasteHotkey, value);
    }

    public string SendHotkey
    {
        get => _sendHotkey;
        set => SetProperty(ref _sendHotkey, value);
    }

    /// <summary>「搜索后确认进入会话」的键（§14.3.4）。</summary>
    public string ConfirmHotkey
    {
        get => _confirmHotkey;
        set => SetProperty(ref _confirmHotkey, value);
    }

    /// <summary>
    /// 快捷键区的一行提示。初始为"请手工填写"的常驻引导，点「自动识别」后替换为结果口径；
    /// **不弹错误框**（§14.3.2 第 3 条）。只有接口本身失败才走 <see cref="AlertRequested"/>。
    /// </summary>
    public string HotkeyHintText
    {
        get => _hotkeyHintText;
        set => SetProperty(ref _hotkeyHintText, value);
    }

    /// <summary>文档列表（T-F4），供 View 打开「关联文档」选择器时取用。</summary>
    public IReadOnlyList<DocItem> Docs => _docs;

    // ==================== 守护对象 ====================

    /// <summary>好友候选（原前端 <c>renderGroup('好友', ...)</c> 那一组）。</summary>
    public ObservableCollection<GuardTargetItemViewModel> FriendTargets { get; } = [];

    /// <summary>群聊候选。</summary>
    public ObservableCollection<GuardTargetItemViewModel> GroupTargets { get; } = [];

    public string FriendCountText => $"好友（已选 {FriendTargets.Count(t => t.IsSelected)}/{MaxFriendTargets}）";

    public string GroupCountText => $"群聊（已选 {GroupTargets.Count(t => t.IsSelected)}/{MaxGroupTargets}）";

    /// <summary>候选为空（含尚未加载完）时显示占位文案，此时连分组表头一起不显示。</summary>
    public bool ShowGuardPlaceholder => FriendTargets.Count == 0 && GroupTargets.Count == 0;

    public string GuardPlaceholder
    {
        get => _guardPlaceholder;
        private set => SetProperty(ref _guardPlaceholder, value);
    }

    // ==================== 加载 ====================

    /// <summary>
    /// 打开弹窗时拉一次配置与候选会话。
    /// <b>必须串行</b>：候选列表要用已保存的守护对象回填勾选态，配置没回来就渲染，
    /// 会出现"列表渲染完了但一个都没勾上"；原前端也是先 <c>applyAutoReplyConfigToForm()</c>
    /// 再 <c>loadGuardChatOptions()</c>。
    /// </summary>
    public async Task InitializeAsync()
    {
        await LoadConfigAsync();
        await LoadDocsAsync();

        // 用户在配置还没回来时就关了窗口：此时 _lifetime 已被 CancelPending() 取消并释放，
        // 再去读 _lifetime.Token 会抛 ObjectDisposedException。必须在"取令牌"之前收手。
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        await LoadGuardChatsAsync();
    }

    /// <summary>弹窗被关闭时调用：取消在途请求，避免"窗口已经没了还在写"。</summary>
    public void CancelPending()
    {
        if (!_lifetime.IsCancellationRequested)
        {
            _lifetime.Cancel();
        }

        _lifetime.Dispose();
    }

    private async Task LoadConfigAsync()
    {
        try
        {
            var data = await _api.GetAutoReplyConfigAsync(_lifetime.Token);
            ApplyConfig(data.Config);
            _configLoaded = true;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ChatlogApiException ex)
        {
            // 不拿默认值填表（见类注释的偏离说明 1），保存会被 IsReadyToSave 挡住。
            AlertRequested?.Invoke($"读取自动回复配置失败：{ex.Message}\n为避免覆盖服务端现有配置，本次不允许保存。");
        }

        NotifyReadyState();
    }

    /// <summary>加载知识文档列表（T-F4）。失败静默：文档是可选增强，不阻断自动回复配置。</summary>
    private async Task LoadDocsAsync()
    {
        try
        {
            _docs = (await _api.GetDocsAsync(_lifetime.Token)).Docs;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChatlogApiException)
        {
            _docs = [];
        }
    }

    private async Task LoadGuardChatsAsync()
    {
        try
        {
            var data = await _api.GetChatsAsync(_lifetime.Token);
            BuildGuardCandidates(data.Chats, _savedTargets);
            _guardLoaded = true;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ChatlogApiException)
        {
            // 同理：候选没读到就不能提交守护列表，否则等于把已有守护对象清空。
            GuardPlaceholder = "读取微信会话失败，请关闭本窗口后重试。";
        }

        _guardLoading = false;
        UpdateGuardPlaceholder();
        NotifyReadyState();
    }

    private void NotifyReadyState()
    {
        OnPropertyChanged(nameof(IsReadyToSave));
        SaveCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 把服务端配置灌进表单。字段为空时的兜底与 app.js <c>applyAutoReplyConfigToForm</c> 一致
    /// （用 <c>||</c>，即空串也算缺省）。
    /// </summary>
    private void ApplyConfig(AutoReplyConfig? config)
    {
        Enabled = config?.Enabled == true;

        var count = config?.ContextCount ?? DefaultContextCount;
        ContextCountText = count.ToString(CultureInfo.InvariantCulture);

        var cooldown = config?.ReplyCooldownSeconds ?? DefaultReplyCooldownSeconds;
        ReplyCooldownText = cooldown.ToString(CultureInfo.InvariantCulture);

        ReplyPrompt = config?.ReplyPrompt ?? "";

        var hotkeys = config?.Hotkeys;
        OpenWechatHotkey = Fallback(hotkeys?.OpenWechat, DefaultOpenWechatHotkey);
        SearchHotkey = Fallback(hotkeys?.Search, DefaultSearchHotkey);
        PasteHotkey = Fallback(hotkeys?.Paste, DefaultPasteHotkey);
        SendHotkey = Fallback(hotkeys?.Send, DefaultSendHotkey);
        // 旧配置没有 confirm（服务端合并后会给 "enter"，但这里仍走 Fallback，双保险）。
        ConfirmHotkey = Fallback(hotkeys?.Confirm, DefaultConfirmHotkey);

        _savedTargets = config?.GuardTargets ?? [];
    }

    /// <summary>
    /// 分组渲染候选会话。
    /// 只渲染 <c>/api/chats</c> 返回的会话——与前端一致：服务端配置里若有已不在会话列表中的
    /// 守护对象，本界面不展示，保存时会随之丢失（原前端同此行为，此处不擅自改变语义）。
    /// </summary>
    private void BuildGuardCandidates(IReadOnlyList<ChatSummary> chats, IReadOnlyList<GuardTarget> savedTargets)
    {
        // 前端用对象做 id 索引，重复 id 后者覆盖前者，这里保持一致。
        var savedById = new Dictionary<string, GuardTarget>(StringComparer.Ordinal);
        foreach (var target in savedTargets)
        {
            if (!string.IsNullOrEmpty(target.Id))
            {
                savedById[target.Id] = target;
            }
        }

        FriendTargets.Clear();
        GroupTargets.Clear();

        foreach (var chat in chats)
        {
            var item = new GuardTargetItemViewModel(chat, OnGuardSelectionChanged);
            if (savedById.TryGetValue(chat.Id, out var saved))
            {
                item.Prompt = saved.Prompt;
                item.IsSelected = true;
                item.SetDocIds(saved.DocIds);
            }

            (item.IsGroup ? GroupTargets : FriendTargets).Add(item);
        }

        OnPropertyChanged(nameof(FriendCountText));
        OnPropertyChanged(nameof(GroupCountText));
        OnPropertyChanged(nameof(ShowGuardPlaceholder));

        // 分组重建后必须整体重算一次禁用态：否则回填出来的已选对象
        // 会把名额占满，而后面那些未选中行还是默认可点（IsEnabled=true）。
        RecalculateAvailability();
    }

    /// <summary>
    /// 一组勾选态变化 → 两组可用性整体重算。
    /// 判据来自前端：<c>checkbox.disabled = !isChecked &amp;&amp; currentCount &gt;= maxCount</c>。
    /// 已选中项恒为可点，否则用户无法取消勾选来腾出名额。
    /// </summary>
    private void OnGuardSelectionChanged()
    {
        RecalculateAvailability();

        OnPropertyChanged(nameof(FriendCountText));
        OnPropertyChanged(nameof(GroupCountText));
    }

    private void RecalculateAvailability()
    {
        var friendSelected = FriendTargets.Count(t => t.IsSelected);
        var groupSelected = GroupTargets.Count(t => t.IsSelected);

        foreach (var target in FriendTargets)
        {
            target.IsEnabled = target.IsSelected || friendSelected < MaxFriendTargets;
        }

        foreach (var target in GroupTargets)
        {
            target.IsEnabled = target.IsSelected || groupSelected < MaxGroupTargets;
        }
    }

    private void UpdateGuardPlaceholder()
    {
        if (_guardLoaded)
        {
            GuardPlaceholder = "未读取到微信会话。";
            return;
        }

        if (!_guardLoading)
        {
            // 加载失败时保持 LoadGuardChatsAsync 里写好的失败文案。
            return;
        }

        GuardPlaceholder = "正在加载微信会话……";
    }

    // ==================== 写操作 ====================

    private async Task SaveAsync()
    {
        if (!TryReadContextCount(out var contextCount))
        {
            return;
        }

        if (!TryReadReplyCooldown(out var replyCooldownSeconds))
        {
            return;
        }

        // 全量提交六个字段（原前端的五个 + 本项目新增的 reply_cooldown_seconds），
        // 理由与原 app.js saveAutoReplyConfig() 相同：这是"保存整份配置"的表单，不是单字段补丁。
        // AutoReplyConfigPatch 的可空能力留给"只改单个字段"的场景，这里不用。
        var patch = new AutoReplyConfigPatch
        {
            Enabled = Enabled,
            GuardTargets = CollectGuardTargets(),
            ContextCount = contextCount,
            ReplyCooldownSeconds = replyCooldownSeconds,
            ReplyPrompt = ReplyPrompt.Trim(),
            Hotkeys = new AutoReplyHotkeys
            {
                OpenWechat = OpenWechatHotkey.Trim(),
                Search = SearchHotkey.Trim(),
                Paste = PasteHotkey.Trim(),
                Send = SendHotkey.Trim(),
                Confirm = ConfirmHotkey.Trim(),
            },
        };

        try
        {
            await _api.SaveAutoReplyConfigAsync(patch, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ChatlogApiException ex)
        {
            // 与原前端一致：保存失败**不关弹窗**，用户改了半天不能白改。
            AlertRequested?.Invoke($"保存自动回复配置失败：{ex.Message}");
            return;
        }

        CloseRequested?.Invoke();
    }

    /// <summary>校验并读取上下文条数（见类注释的偏离说明 3）。</summary>
    private bool TryReadContextCount(out int contextCount)
    {
        contextCount = DefaultContextCount;

        var text = ContextCountText.Trim();
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < MinContextCount
            || value > MaxContextCount)
        {
            AlertRequested?.Invoke($"上下文条数必须是 {MinContextCount} 至 {MaxContextCount} 之间的整数。");
            return false;
        }

        contextCount = value;
        return true;
    }

    /// <summary>校验并读取"同一会话的最小回复间隔"（0 表示不限制）。</summary>
    private bool TryReadReplyCooldown(out int replyCooldownSeconds)
    {
        replyCooldownSeconds = DefaultReplyCooldownSeconds;

        var text = ReplyCooldownText.Trim();
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < MinReplyCooldownSeconds
            || value > MaxReplyCooldownSeconds)
        {
            AlertRequested?.Invoke(
                $"回复间隔必须是 {MinReplyCooldownSeconds} 至 {MaxReplyCooldownSeconds} 之间的整数（0 表示不限制）。");
            return false;
        }

        replyCooldownSeconds = value;
        return true;
    }

    private List<GuardTarget> CollectGuardTargets()
    {
        var targets = new List<GuardTarget>();

        foreach (var item in FriendTargets.Concat(GroupTargets))
        {
            if (item.IsSelected)
            {
                targets.Add(item.ToModel());
            }
        }

        return targets;
    }

    private async Task TestHotkeyAsync(object? parameter)
    {
        if (parameter is not string action || action.Length == 0)
        {
            return;
        }

        try
        {
            var data = await _api.TestAutoReplyAsync(new AutoReplyTestRequest { Action = action }, _lifetime.Token);
            AlertRequested?.Invoke(string.IsNullOrEmpty(data.Message) ? "已触发" : data.Message);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChatlogApiException ex)
        {
            AlertRequested?.Invoke($"测试失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 完整发送流程测试：会真实往微信发一条消息，所以每次都要二次确认。
    /// 与 app.js <c>testFullFlow()</c> 一致，先要求"主界面已选中会话"。
    /// </summary>
    private async Task TestFullFlowAsync()
    {
        var chatName = _currentChatName();
        if (string.IsNullOrEmpty(chatName))
        {
            AlertRequested?.Invoke("请先选择一个会话");
            return;
        }

        if (ConfirmRequested?.Invoke($"将真实发送一条测试消息到「{chatName}」，确认继续？") != true)
        {
            return;
        }

        try
        {
            var data = await _api.TestAutoReplyAsync(
                new AutoReplyTestRequest { Action = "full", TargetName = chatName, Text = FullFlowTestText },
                _lifetime.Token);
            AlertRequested?.Invoke(string.IsNullOrEmpty(data.Message) ? "已执行" : data.Message);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChatlogApiException ex)
        {
            AlertRequested?.Invoke($"完整流程测试失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 「自动识别」：调探测接口，结果**只更新提示区**——不自动填写表单、不自动落盘
    /// （§14.3.2：识别结果不直接生效；§14.3.6 风险 2 同此姿态）。
    /// found=false 是**正常结果**，不弹框；只有接口本身失败（网络 / 后端 5xx）才走错误提示，
    /// 否则会把真故障藏进"未能自动识别"里。
    /// </summary>
    private async Task DetectHotkeysAsync()
    {
        try
        {
            var data = await _api.DetectHotkeysAsync(_lifetime.Token);
            HotkeyHintText = string.IsNullOrEmpty(data.Message) ? DetectFailedHotkeyHint : data.Message;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChatlogApiException ex)
        {
            AlertRequested?.Invoke($"自动识别失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 「恢复默认值」：只把五个快捷键框填回内置默认值，**不落盘**。
    /// 本表单的既有语义是"编辑后保存"，不引入"某个按钮立即生效"的例外——用户填完仍要点「保存」。
    /// </summary>
    private void RestoreDefaults()
    {
        OpenWechatHotkey = DefaultOpenWechatHotkey;
        SearchHotkey = DefaultSearchHotkey;
        PasteHotkey = DefaultPasteHotkey;
        SendHotkey = DefaultSendHotkey;
        ConfirmHotkey = DefaultConfirmHotkey;
    }

    private static string Fallback(string? value, string fallback)
        => string.IsNullOrEmpty(value) ? fallback : value;
}
