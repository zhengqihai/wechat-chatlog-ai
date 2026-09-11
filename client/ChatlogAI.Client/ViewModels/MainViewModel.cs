using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.Services;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// 主界面的模块（T16）：对应左侧图标栏的五个入口，决定内容宿主显示哪一块主面板。
///
/// 刻意只描述"当前是哪个视图"，不带任何界面信息（图标、标题、顺序都不在这层）：
/// 一旦把界面细节塞进来，加一个模块就要同时改 VM 和 XAML 两处，模块化就白做了。
/// </summary>
public enum MainModule
{
    Chat,
    ScheduledSend,
    Moments,
    Contacts,
    Settings,
}

/// <summary>
/// 主界面视图模型，逐条对照 <c>app/static/app.js</c> 的全局状态与函数实现。
///
/// 三条必须守住的约束（都来自原前端的既有语义，不是额外发明）：
///
/// 1. <b>会话实例必须按 id 复用</b>。<c>ListBox.SelectedItem</c> 按对象引用比对，
///    而会话每 10 秒刷新一次；如果每次轮询都重建 VM，选中项会在轮询后丢失，
///    连带把消息区清空。因此 <see cref="Chats"/> 采用「原地更新 + 增量同步」而不是整体重建。
///
/// 2. <b>轮询回调必须回到 UI 线程</b>。四个 <see cref="DispatcherTimer"/> 的 Tick 本身就在 UI 线程，
///    而本类内部所有 <c>await</c> 都**不加</c>ConfigureAwait(false)</c>，让续体回到 UI 线程，
///    这样对 <see cref="ObservableCollection{T}"/> 的增删天然安全，无需到处 Dispatcher.Invoke。
///
/// 3. <b>消息的"可信"判据是 <c>status == success &amp;&amp; source_valid == true</c></b>，
///    缺一不可；只看 HTTP 200 会把"数据源没接上但接口没报错"当成成功，把空列表刷进界面。
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    // ==================== 轮询周期（对照 app.js L22-L25） ====================
    private static readonly TimeSpan ChatPollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MessagePollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AutoReplyPollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BalancePollInterval = TimeSpan.FromSeconds(60);

    /// <summary>定时发送的运行状态（下次触发时间 / 引擎是否在跑）。触发本身在 Python 侧，这里只刷展示。</summary>
    private static readonly TimeSpan ScheduledSendPollInterval = TimeSpan.FromSeconds(15);

    /// <summary>不限制条数时向服务端请求的上限；服务端 <c>/api/messages</c> 自身也夹在 1–2000。</summary>
    private const int UnlimitedRequestLimit = 2000;

    private const string TimeModeNone = "none";
    private const string TimeModeRecent = "recent";
    private const string CountModeUnlimited = "unlimited";
    private const string CountModeRecent = "recent";

    /// <summary>四个预设按钮的提示词，文案与原前端 <c>runPreset()</c> 完全一致。</summary>
    public static readonly IReadOnlyDictionary<string, string> PresetPrompts = new Dictionary<string, string>
    {
        [AnalyzeType.General] = "请综合总结当前筛选范围内的聊天记录",
        [AnalyzeType.Meeting] = "请整理会议纪要与关键决策",
        [AnalyzeType.Todo] = "请提取待办事项、责任人和时间",
        [AnalyzeType.Topics] = "请分析核心话题及关注度",
    };

    private readonly ChatlogApiClient _api;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<DispatcherTimer> _timers = [];

    /// <summary>会话 id → 行 VM。存在的意义就是"同一个会话永远对应同一个实例"（见类注释第 1 条）。</summary>
    private readonly Dictionary<string, ChatItemViewModel> _chatIndex = new(StringComparer.Ordinal);

    /// <summary>服务端最新的会话原始数据；搜索过滤只作用在"投影"这一步，不作用在缓存上。</summary>
    private IReadOnlyList<ChatSummary> _chatModels = [];

    private List<ChatMessage> _rawMessages = [];
    private List<ChatMessage> _visibleMessages = [];

    private IReadOnlyList<ProviderInfo> _providers = [];
    private string _activeProviderId = "";
    private AutoReplyConfig? _autoReplyConfig;

    /// <summary>
    /// 服务端是否支持自动回复（Python 侧 = automation 可导入 且 os.name == "nt"），
    /// 决定主界面「自动回复」按钮是否可点。
    /// 默认给 <c>true</c>：只有服务端明确回报不可用才禁用入口。
    /// 若反过来默认 <c>false</c>，一次配置读取失败就会让按钮永久点不动，用户没有任何自救手段。
    /// </summary>
    private bool _autoReplyAvailable = true;

    private bool _chatLoading;
    private bool _messageLoading;
    private bool _balanceLoading;
    private bool _autoReplyLoading;

    // ==================== 账号切换（T19-B）的私有状态 ====================

    /// <summary>当前生效账号（<c>config.wechat_account</c>，可能为空）。</summary>
    private string _activeAccount = "";

    /// <summary>当前账号的显示名，来自上游 <c>accounts[].name</c>；拿不到时为空（显示降级）。</summary>
    private string _activeAccountName = "";

    private bool _hasPendingAccountSwitch;
    private string _pendingSwitchAccount = "";

    /// <summary>发送闸门是否被拦住 + 原因（来自 /api/auto-reply/status 的 sendPaused 字段）。</summary>
    private bool _sendPaused;
    private string _sendPausedReason = "";

    private bool _accountsLoading;

    /// <summary>
    /// 上游就绪后只补拉一次账号显示名（§7.17：不为它新增第 5 个轮询）。
    /// 失败时把 <see cref="_accountInfoRequested"/> 复位，等下一轮会话轮询成功后再试。
    /// </summary>
    private bool _accountInfoRequested;
    private bool _accountInfoFresh;

    public MainViewModel(ChatlogApiClient api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));

        // 命令在构造器里赋值而不是用属性初始化器：属性初始化器引用不到实例方法之外的界面状态，
        // 而这里的命令体（RunPresetAsync / SelectAnalysisType…）都要读写状态。
        RunPresetCommand = new AsyncRelayCommand(RunPresetAsync);
        SelectAnalysisTypeCommand = new RelayCommand(SelectAnalysisType);
        SendCustomPromptCommand = new AsyncRelayCommand(SendCustomPromptAsync);
        ClearAiThreadCommand = new RelayCommand(ClearAiThread);
        CopyAiTurnCommand = new RelayCommand(CopyAiTurn);
        ApplyFilterCommand = new RelayCommand(ApplyFilter);

        // 左侧窄栏「定时发送」面板：与主界面同生命周期（它是常驻栏，不是弹窗）。
        // 面板自己只抛事件，弹框是 View 的职责；这里转发一层，让 View 只需要订阅主 VM。
        ScheduledSend = new ScheduledSendViewModel(_api);
        ScheduledSend.AlertRequested += message => AlertRequested?.Invoke(message);
        ScheduledSend.ConfirmRequested += message => ConfirmRequested?.Invoke(message) ?? false;

        // 「联系人」模块（T22）：与 ScheduledSend 同一挂法，但这里**只 new、不加载** ——
        // 通讯录一次返回全部（实测 495 条，重度用户上千），而用户可能根本不进这个模块。
        // 加载由模块首次可见时触发（ContactsModuleView 的 IsVisibleChanged）。
        // 它自己的 AlertRequested 不在这里转发：弹框由模块 View 直接订阅，主 VM 完全不必知道这个模块的存在。
        Contacts = new ContactsModuleViewModel(_api);

        // 「朋友圈」模块（T23）：与 Contacts 同一挂法 —— 只 new、不加载。
        // 时间线要分页（每页 20 条），进模块还得先同步一次实时增量，用户不进这个模块就都是白花。
        // 加载由模块首次可见时触发（MomentsModuleView 的 IsVisibleChanged）。
        Moments = new MomentsModuleViewModel(_api);
    }

    // ==================== 对外事件（供 View 处理"必须由它做"的事） ====================

    /// <summary>消息列表被替换，View 据此滚到底部（对照 <c>renderMessages()</c> 末尾的 <c>scrollTop = scrollHeight</c>）。</summary>
    public event Action? MessagesUpdated;

    /// <summary>AI 线程被替换，View 据此滚到底部。</summary>
    public event Action? AiThreadUpdated;

    /// <summary>
    /// 需要弹一个提示框。VM 不直接引用 <c>MessageBox</c>——
    /// 那是 View 的职责，也是 MVVM 里唯一不该由 VM 触碰的边界（对应原前端的 <c>window.alert</c>）。
    /// </summary>
    public event Action<string>? AlertRequested;

    /// <summary>
    /// 需要一次"是/否"确认（对应原前端的 <c>window.confirm</c>）；返回用户是否确认。
    /// 目前只有"删除定时发送任务"会用到。
    /// </summary>
    public event Func<string, bool>? ConfirmRequested;

    /// <summary>
    /// 左侧窄栏「定时发送」面板。常驻栏，随主 VM 一起构造与释放。
    /// </summary>
    public ScheduledSendViewModel ScheduledSend { get; }

    /// <summary>
    /// 「联系人」模块（T22）。常驻子 VM，随主 VM 一起构造；模块内用 <c>{Binding Contacts.Xxx}</c> 绑定。
    ///
    /// 为什么构造时只 new 不加载：通讯录接口一次返回全部（实测 495 条），而用户可能根本不进这个模块；
    /// 加载由模块首次可见时调 <see cref="ContactsModuleViewModel.EnsureLoadedAsync"/> 触发。
    /// </summary>
    public ContactsModuleViewModel Contacts { get; }

    /// <summary>
    /// 「朋友圈」模块（T23）。常驻子 VM，随主 VM 一起构造；模块内用 <c>{Binding Moments.Xxx}</c> 绑定。
    ///
    /// 为什么构造时只 new 不加载：时间线分页（每页 20 条）+ 进模块先同步实时增量，
    /// 而用户可能根本不进这个模块；加载由模块首次可见时调
    /// <see cref="MomentsModuleViewModel.EnsureLoadedAsync"/> 触发。
    /// </summary>
    public MomentsModuleViewModel Moments { get; }

    /// <summary>
    /// 需要打开「模型与 API Key 设置」弹窗（T9）。目前只有一个触发点：
    /// <see cref="InitializeAsync"/> 末尾发现服务端没有生效供应商时自动弹一次，
    /// 对照 app.js 的 <c>if (!activeProviderId) openModelModal()</c>。
    ///
    /// 为什么是 VM 抛事件、而不是让 View 自己去查"到底配没配模型"：
    /// 判据 <c>active_id</c> 是 VM 在 InitializeAsync 里取回来的，而 View 的 Loaded
    /// 早于那次请求返回，此刻去查只能查到"还没加载"这个中间态。
    /// </summary>
    public event Action? ModelSettingsRequested;

    // ==================== 主界面模块切换（T16 导航重构） ====================

    private MainModule _currentModule = MainModule.Chat;

    /// <summary>
    /// 当前显示的模块。图标栏的选中态与内容宿主的可见性都只挂在这一处状态上，
    /// 避免出现"图标高亮在 A、主面板却停着 B"这种两处状态各说各话的情况。
    /// </summary>
    public MainModule CurrentModule
    {
        get => _currentModule;
        set
        {
            if (!SetProperty(ref _currentModule, value))
            {
                return;
            }

            // 下面五个只读布尔是一次比较的投影，必须逐个显式通知：
            // XAML 里的 DataTrigger 只监听自己那条绑定用到的属性名，
            // 光通知 CurrentModule 不会让它们重算。
            OnPropertyChanged(nameof(IsChatModule));
            OnPropertyChanged(nameof(IsScheduledSendModule));
            OnPropertyChanged(nameof(IsMomentsModule));
            OnPropertyChanged(nameof(IsContactsModule));
            OnPropertyChanged(nameof(IsSettingsModule));
        }
    }

    /// <summary>
    /// 图标栏每个入口的选中态要用 <c>RadioButton</c> 承载，而它表达"选中"的唯一属性是
    /// <c>IsChecked</c>（bool），所以这里必须给出 bool 投影，而不是让 XAML 去比枚举。
    ///
    /// 为什么 setter 只认 <c>true</c>：RadioButton 点击时组内互斥，点 B 会顺带把 A 的
    /// IsChecked 写成 false，并沿默认的 TwoWay 绑定回写过来。若这里对 false 也做出反应，
    /// 就等于"用户选 B"这条消息里又混进了"取消 A"，会把 CurrentModule 改回头。
    /// </summary>
    public bool IsChatModule
    {
        get => _currentModule == MainModule.Chat;
        set
        {
            if (value)
            {
                CurrentModule = MainModule.Chat;
            }
        }
    }

    public bool IsScheduledSendModule
    {
        get => _currentModule == MainModule.ScheduledSend;
        set
        {
            if (value)
            {
                CurrentModule = MainModule.ScheduledSend;
            }
        }
    }

    public bool IsMomentsModule
    {
        get => _currentModule == MainModule.Moments;
        set
        {
            if (value)
            {
                CurrentModule = MainModule.Moments;
            }
        }
    }

    public bool IsContactsModule
    {
        get => _currentModule == MainModule.Contacts;
        set
        {
            if (value)
            {
                CurrentModule = MainModule.Contacts;
            }
        }
    }

    public bool IsSettingsModule
    {
        get => _currentModule == MainModule.Settings;
        set
        {
            if (value)
            {
                CurrentModule = MainModule.Settings;
            }
        }
    }

    // ==================== 会话列表 ====================

    public ObservableCollection<ChatItemViewModel> Chats { get; } = [];

    public bool HasChats => Chats.Count > 0;

    private string _chatsPlaceholder = "正在读取真实会话";
    public string ChatsPlaceholder
    {
        get => _chatsPlaceholder;
        private set => SetProperty(ref _chatsPlaceholder, value);
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RefreshChatItems();
            }
        }
    }

    private ChatItemViewModel? _selectedChat;
    public ChatItemViewModel? SelectedChat
    {
        get => _selectedChat;
        set
        {
            if (ReferenceEquals(_selectedChat, value))
            {
                return;
            }

            _selectedChat = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedChat));

            // 值为 null 有两种来源：用户真的取消了选择，或 ListBox 在 ItemsSource 变动时
            // 临时把 SelectedItem 置空。原前端的 currentChat 在这两种情况下都不会被清掉，
            // 所以这里同样保持已有内容不动，避免"刷新一次会话列表就把消息区清空"。
            if (value is null)
            {
                return;
            }

            // 切换会话：立刻丢掉上一会话的残留，否则会出现"新会话标题 + 旧会话消息"的错位。
            AiThread.Clear();
            _rawMessages.Clear();
            _visibleMessages.Clear();
            Messages.Clear();
            RaiseMessagesUpdated();

            ChatTitle = value.Name;
            ChatSubtitle = $"微信 · {value.TypeText} · 实时来源";
            UpdateAnalysisCount();

            _ = LoadHistoryAsync();
            _ = ReloadMessagesAsync();
        }
    }

    public bool HasSelectedChat => _selectedChat is not null;

    /// <summary>会话区标题（对照 <c>selectChat()</c> 里的 <c>chatTitle</c>）。</summary>
    private string _chatTitle = "未选择会话";
    public string ChatTitle
    {
        get => _chatTitle;
        private set => SetProperty(ref _chatTitle, value);
    }

    private string _chatSubtitle = "未选择实时会话";
    public string ChatSubtitle
    {
        get => _chatSubtitle;
        private set => SetProperty(ref _chatSubtitle, value);
    }

    // ==================== 筛选条件（对照 getRangeConfig()） ====================

    private string _timeMode = TimeModeNone;
    public string TimeMode
    {
        get => _timeMode;
        set => SetFilter(ref _timeMode, value);
    }

    private string _timeUnit = "hours";
    public string TimeUnit
    {
        get => _timeUnit;
        set => SetFilter(ref _timeUnit, value);
    }

    private int _timeValue = 24;
    public int TimeValue
    {
        get => _timeValue;
        set => SetFilter(ref _timeValue, Math.Max(1, value));
    }

    private string _countMode = CountModeRecent;
    public string CountMode
    {
        get => _countMode;
        set => SetFilter(ref _countMode, value);
    }

    private int _countInput = 500;
    public int CountInput
    {
        get => _countInput;
        set => SetFilter(ref _countInput, Math.Clamp(value, 1, UnlimitedRequestLimit));
    }

    /// <summary>时间筛选折算成小时；<c>null</c> = 不限制。minutes ÷ 60、days × 24（对照 getRangeConfig）。</summary>
    public double? RecentHours => TimeMode != TimeModeRecent
        ? null
        : TimeUnit switch
        {
            "minutes" => TimeValue / 60d,
            "days" => TimeValue * 24d,
            _ => (double)TimeValue,
        };

    /// <summary>本地二次过滤的条数上限；<c>null</c> = 不限制。</summary>
    public int? Count => CountMode == CountModeRecent ? CountInput : null;

    /// <summary>
    /// 向服务端请求的条数。
    /// 注意：**筛选变更不会立刻重新拉取**——与原前端一致，新上限在下一个 5 秒轮询才生效。
    /// </summary>
    public int RequestLimit => CountMode == CountModeRecent ? CountInput : UnlimitedRequestLimit;

    /// <summary>时间数值框与时间单位是否可编辑；对照 <c>toggleRangeControls()</c> 里的 <c>disabled</c>。</summary>
    public bool IsTimeRangeEnabled => TimeMode == TimeModeRecent;

    /// <summary>条数输入框是否可编辑。</summary>
    public bool IsCountInputEnabled => CountMode == CountModeRecent;

    /// <summary>
    /// 筛选字段的统一 setter。三个派生属性（RecentHours / Count / RequestLimit）
    /// 各自依赖多个字段，若只在某个字段的 setter 里通知必然漏；集中一处通知，
    /// 以后新增筛选条件也不容易忘。
    /// </summary>
    private void SetFilter<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return;
        }

        OnPropertyChanged(nameof(RecentHours));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(RequestLimit));
        OnPropertyChanged(nameof(IsTimeRangeEnabled));
        OnPropertyChanged(nameof(IsCountInputEnabled));
    }

    // ==================== 消息列表 ====================

    public ObservableCollection<MessageItemViewModel> Messages { get; } = [];

    public bool HasMessages => Messages.Count > 0;

    private string _messagesPlaceholder = "";
    public string MessagesPlaceholder
    {
        get => _messagesPlaceholder;
        private set => SetProperty(ref _messagesPlaceholder, value);
    }

    // ==================== AI 分析 ====================

    public ObservableCollection<AiThreadItemViewModel> AiThread { get; } = [];

    public bool HasAiThread => AiThread.Count > 0;

    private string _analysisType = AnalyzeType.General;
    public string AnalysisType
    {
        get => _analysisType;
        private set
        {
            if (SetProperty(ref _analysisType, value))
            {
                OnPropertyChanged(nameof(IsGeneralTab));
                OnPropertyChanged(nameof(IsMeetingTab));
                OnPropertyChanged(nameof(IsTodoTab));
                OnPropertyChanged(nameof(IsTopicsTab));
            }
        }
    }

    public bool IsGeneralTab => AnalysisType == AnalyzeType.General;
    public bool IsMeetingTab => AnalysisType == AnalyzeType.Meeting;
    public bool IsTodoTab => AnalysisType == AnalyzeType.Todo;
    public bool IsTopicsTab => AnalysisType == AnalyzeType.Topics;

    private string _customPrompt = "";
    public string CustomPrompt
    {
        get => _customPrompt;
        set => SetProperty(ref _customPrompt, value);
    }

    private bool _isAnalyzing;
    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set => SetProperty(ref _isAnalyzing, value);
    }

    /// <summary>AI 回答的署名：当前生效供应商的名字，没有则 <c>AI</c>（对照 <c>assistantName()</c>）。</summary>
    public string AssistantName
    {
        get
        {
            var active = ActiveProvider;
            return active is null || string.IsNullOrEmpty(active.Name) ? "AI" : active.Name;
        }
    }

    public ICommand RunPresetCommand { get; }
    public ICommand SelectAnalysisTypeCommand { get; }
    public ICommand SendCustomPromptCommand { get; }
    public ICommand ClearAiThreadCommand { get; }
    public ICommand CopyAiTurnCommand { get; }

    /// <summary>
    /// 对照原前端筛选区的「应用」按钮。筛选条件的 setter 只负责夹取值域并通知，
    /// 并**不会**立刻重新拉取消息——消息刷新本来就有 5 秒轮询兜底，
    /// 但没有这个命令时，用户改完条件要等下一次轮询才看到结果，体感像"点了没反应"。
    /// </summary>
    public ICommand ApplyFilterCommand { get; }

    // ==================== 状态栏 ====================

    private string _sourceStatus = "正在连接实时数据源";
    public string SourceStatus
    {
        get => _sourceStatus;
        private set => SetProperty(ref _sourceStatus, value);
    }

    private bool _isSourceReady;
    public bool IsSourceReady
    {
        get => _isSourceReady;
        private set => SetProperty(ref _isSourceReady, value);
    }

    private string _modelStatus = "正在读取模型配置";
    public string ModelStatus
    {
        get => _modelStatus;
        private set => SetProperty(ref _modelStatus, value);
    }

    private string _balanceStatus = "余额：--";
    public string BalanceStatus
    {
        get => _balanceStatus;
        private set => SetProperty(ref _balanceStatus, value);
    }

    private string _autoReplyStatus = "自动回复：未启用";
    public string AutoReplyStatus
    {
        get => _autoReplyStatus;
        private set => SetProperty(ref _autoReplyStatus, value);
    }

    /// <summary>
    /// 自动回复入口是否可用，绑定到主界面「自动回复」按钮的 <c>IsEnabled</c>。
    /// 对照 docs/API_REFERENCE.md §11「非 Windows 隐藏/禁用入口」。
    /// </summary>
    public bool IsAutoReplyAvailable
    {
        get => _autoReplyAvailable;
        private set => SetProperty(ref _autoReplyAvailable, value);
    }

    private string _analysisCount = "将分析 0 条真实消息";
    public string AnalysisCount
    {
        get => _analysisCount;
        private set => SetProperty(ref _analysisCount, value);
    }

    /// <summary>
    /// 窗口是否可见。对应原前端的 <c>document.hidden</c>：
    /// 窗口最小化且自动回复未启用时，跳过消息轮询，避免后台空转。
    /// </summary>
    public bool IsWindowVisible { get; set; } = true;

    // ==================== 账号指示器（T19-B） ====================

    /*
     * 为什么把这些状态放在主 VM、而不是账号窗口自己的 VM 里：
     * rail 顶部头像是**常驻**的安全信息（"当前是哪个账号"），窗口可以不开，标识不能消失。
     * 窗口只是同一份状态的编辑入口，关闭后主界面仍要显示最新结果。
     */

    /// <summary>显示名：优先上游给的 name，拿不到退回 account（§7.17：启动早期上游还没起来）。</summary>
    private string AccountDisplayName
        => !string.IsNullOrWhiteSpace(_activeAccountName) ? _activeAccountName : _activeAccount;

    /// <summary>头像里的简称：取显示名前两个字符；一个字都没有时显示 <c>—</c>。</summary>
    public string CurrentAccountShort
    {
        get
        {
            var display = AccountDisplayName;
            return display.Length == 0 ? "—" : display[..Math.Min(2, display.Length)];
        }
    }

    /// <summary>设置模块那一行的只读文案（「当前微信账号：xxx」）。</summary>
    public string CurrentAccountLine
    {
        get
        {
            var display = AccountDisplayName;
            return display.Length == 0 ? "当前微信账号：—（尚未读取到）" : $"当前微信账号：{display}";
        }
    }

    /// <summary>头像 ToolTip：待确认时先讲清"为什么机器人不回复"，平时就是账号全名 + 入口说明。</summary>
    public string AccountTooltip
    {
        get
        {
            if (_hasPendingAccountSwitch)
            {
                return $"账号切换待确认：「{_pendingSwitchAccount}」\n"
                       + "请把微信客户端的登录账号切到该账号，然后点「我已切换」\n"
                       + "在此之前自动发送已暂停";
            }

            var display = AccountDisplayName;
            return display.Length == 0 ? "点击管理微信账号" : $"当前微信账号：{display}\n点击管理微信账号";
        }
    }

    /// <summary>是否有待确认的账号切换：头像警示标记与设置模块的红字都由它控制。</summary>
    public bool HasPendingAccountSwitch => _hasPendingAccountSwitch;

    /// <summary>发送是否被闸门拦住（T19：待确认期间）。</summary>
    public bool IsSendPaused => _sendPaused;

    /// <summary>暂停原因；未暂停时为空串（界面据此隐藏那一行）。</summary>
    public string SendPausedNotice
    {
        get
        {
            if (!_sendPaused)
            {
                return "";
            }

            return string.IsNullOrWhiteSpace(_sendPausedReason)
                ? "自动发送：已暂停（原因：后端未给出说明）"
                : "自动发送：已暂停 — " + _sendPausedReason;
        }
    }

    /// <summary>造一个「微信账号」窗口的视图模型（弹窗本体见 <c>Views/AccountWindow.xaml</c>）。</summary>
    /// <remarks>
    /// 共享同一个 <see cref="ChatlogApiClient"/>，理由同 T9 / T10：
    /// 不给弹窗另起客户端，否则会多出一份 <c>HttpClient</c> 与连接池。
    /// </remarks>
    public AccountWindowViewModel CreateAccountWindowViewModel() => new(_api);

    /// <summary>供 XAML 设计器 / 预览使用的兜底构造；运行时一律走带参构造（见 App.xaml.cs）。</summary>
    public MainViewModel() : this(new ChatlogApiClient())
    {
    }

    private ProviderInfo? ActiveProvider
        => _providers.FirstOrDefault(p => p.Id == _activeProviderId);

    // ==================== 启动与轮询 ====================

    /// <summary>
    /// 对照 app.js 的 <c>load</c> 事件：先模型配置、再自动回复配置、再会话列表，
    /// 最后开四组定时器。
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await LoadProvidersAsync(cancellationToken);
        await LoadAutoReplyConfigAsync(cancellationToken);
        await ScheduledSend.LoadAsync();
        await LoadChatsAsync(showLoading: true, cancellationToken);

        // 原前端在这里还有 refreshBalance()：**故意不 await**，
        // 余额会真实调用供应商接口（ModelTimeout 180 秒），串行等它会让界面白屏很久。
        _ = RefreshBalanceAsync();

        // 原前端在 activeProviderId 为空时会自动弹出"模型设置"，把用户直接送到配置入口。
        // 这里只负责"喊一声"，开窗动作交给 View —— 而且 View 必须把它排到本次调用栈之后，
        // 否则 ShowDialog 会把下面这行 StartPolling() 一起堵到弹窗关闭为止。
        if (string.IsNullOrEmpty(_activeProviderId))
        {
            ModelSettingsRequested?.Invoke();
        }

        StartPolling();
    }

    private void StartPolling()
    {
        AddTimer(ChatPollInterval, () => _ = LoadChatsAsync(showLoading: false, _lifetime.Token));
        AddTimer(MessagePollInterval, () => _ = RefreshMessagesAsync());
        AddTimer(AutoReplyPollInterval, () => _ = RefreshAutoReplyStatusAsync());
        AddTimer(ScheduledSendPollInterval, () => _ = ScheduledSend.RefreshStatusAsync());
        AddTimer(BalancePollInterval, () => _ = RefreshBalanceAsync());
    }

    private void AddTimer(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        timer.Tick += (_, _) => tick();
        timer.Start();
        _timers.Add(timer);
    }

    // ==================== 会话 ====================

    private async Task LoadChatsAsync(bool showLoading, CancellationToken cancellationToken)
    {
        // 每次 Tick 都发一个请求，若上一轮还没回来就跳过，避免慢响应时请求堆积。
        if (_chatLoading)
        {
            return;
        }

        _chatLoading = true;
        if (showLoading)
        {
            ChatsPlaceholder = "正在读取真实会话";
        }

        try
        {
            var data = await _api.GetChatsAsync(cancellationToken);

            _chatModels = data.Chats;
            SourceStatus = string.IsNullOrEmpty(data.StatusInfo?.Msg) ? "实时数据源已连接" : data.StatusInfo!.Msg;
            IsSourceReady = true;
            RefreshChatItems();

            // 会话拉到了 = 上游已经起来。账号显示名只存在于上游的 accounts[] 里，
            // 所以在这里补拉一次（只拉一次，不新增轮询）：拿不到就等下一次会话轮询成功。
            if (!_accountInfoFresh && !_accountInfoRequested)
            {
                _accountInfoRequested = true;
                _ = RefreshAccountsAsync(_lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正在退出，不再改动界面。
        }
        catch (Exception ex) when (ex is ChatlogApiException)
        {
            _chatModels = [];
            SourceStatus = ex.Message;
            IsSourceReady = false;
            // 上游还没就绪：把"已尝试"复位，让下一次成功时还能补拉账号名。
            _accountInfoRequested = false;
            RefreshChatItems();
        }
        finally
        {
            _chatLoading = false;
        }
    }

    /// <summary>
    /// 把最新的会话数据投影到 <see cref="Chats"/>。
    /// 两个关键点：先**全量**更新所有会话的 VM（含被搜索过滤掉的那些），再按搜索词筛选，
    /// 否则清空搜索框时会短暂显示旧数据；同步过程用"移动"而非"清空重建"，以保住选中项。
    /// </summary>
    private void RefreshChatItems()
    {
        var query = SearchText.Trim();
        var desired = new List<ChatItemViewModel>(_chatModels.Count);

        foreach (var model in _chatModels)
        {
            if (!_chatIndex.TryGetValue(model.Id, out var item))
            {
                item = new ChatItemViewModel(model);
                _chatIndex[model.Id] = item;
            }
            else
            {
                item.Update(model);
            }

            // 注意：过滤用的是**原始 name**，与 app.js 的 `String(chat.name || '')` 一致；
            // ChatItemViewModel.Name 会在空名时回退成 id，不能拿来当过滤依据。
            if (query.Length == 0 || model.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                desired.Add(item);
            }
        }

        SyncChats(desired);

        ChatsPlaceholder = desired.Count == 0
            ? (query.Length == 0 ? "没有可用的真实会话" : "没有匹配的真实会话")
            : "";
        OnPropertyChanged(nameof(HasChats));
    }

    /// <summary>增量同步：只做必要的 删除 / 插入 / 移动，避免 Clear() 把 ListBox 选中项打掉。</summary>
    private void SyncChats(IReadOnlyList<ChatItemViewModel> desired)
    {
        for (var i = Chats.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(Chats[i]))
            {
                Chats.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            var existing = Chats.IndexOf(item);
            if (existing < 0)
            {
                Chats.Insert(i, item);
            }
            else if (existing != i)
            {
                Chats.Move(existing, i);
            }
        }
    }

    // ==================== 消息 ====================

    /// <summary>
    /// 「应用」按钮的落点：用当前筛选条件立刻重取一次消息。
    /// 不 await（命令是同步的 RelayCommand），失败路径由 <see cref="ReloadMessagesAsync"/> 自己兜。
    /// </summary>
    private void ApplyFilter() => _ = ReloadMessagesAsync();

    private async Task ReloadMessagesAsync()
    {
        if (SelectedChat is null || _messageLoading)
        {
            return;
        }

        _messageLoading = true;
        MessagesPlaceholder = "正在读取真实消息";
        try
        {
            await FetchMessagesAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is ChatlogApiException)
        {
            MessagesPlaceholder = ex.Message;
        }
        finally
        {
            _messageLoading = false;
        }
    }

    private async Task RefreshMessagesAsync()
    {
        if (SelectedChat is null || _messageLoading)
        {
            return;
        }

        // 对应 app.js：`if (document.hidden && !autoReplyOn) return;`
        // 自动回复开着时必须继续轮询——那时窗口在后台，但界面数据仍需保持最新。
        if (!IsWindowVisible && _autoReplyConfig?.Enabled != true)
        {
            return;
        }

        _messageLoading = true;
        try
        {
            await FetchMessagesAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is ChatlogApiException)
        {
            SourceStatus = ex.Message;
            IsSourceReady = false;
        }
        finally
        {
            _messageLoading = false;
        }
    }

    private async Task FetchMessagesAsync()
    {
        var chat = SelectedChat!;
        var selectedId = chat.Id;

        var data = await _api.GetMessagesAsync(selectedId, RequestLimit, null, null, _lifetime.Token);

        // status 已由 ChatlogApiClient.EnsureStatus 兜住（非 success 会抛异常）；
        // 这里必须再校验 source_valid —— 它是"这条数据真的来自微信"的判据，
        // 只有 HTTP 200 而数据源没接上时它才是 false。
        if (!data.SourceValid)
        {
            throw new ChatlogApiException(
                null,
                string.IsNullOrEmpty(data.Message) ? "消息读取失败" : data.Message);
        }

        // 请求期间用户可能已经切了会话，此时结果属于上一个会话，必须丢弃。
        if (SelectedChat?.Id != selectedId)
        {
            return;
        }

        _rawMessages = [.. data.Messages];
        ApplyFilters();
        SourceStatus = "微信 实时数据源已连接";
        IsSourceReady = true;
    }

    /// <summary>本地二次过滤（对照 <c>applyFilters()</c>）：先按时间裁，再取末尾 N 条。</summary>
    private void ApplyFilters()
    {
        IEnumerable<ChatMessage> result = _rawMessages;

        if (RecentHours is { } hours)
        {
            var cutoff = DateTimeOffset.Now.AddHours(-hours);
            result = result.Where(message => ResolveTimestamp(message) is { } value && value >= cutoff);
        }

        var list = result.ToList();
        if (Count is { } count)
        {
            list = list.TakeLast(count).ToList();
        }

        _visibleMessages = list;
        RebuildMessages();
        UpdateAnalysisCount();
    }

    /// <summary>
    /// 对照 <c>updateAnalysisCount()</c>：分析条数提示 + 会话副标题。
    /// 注意副标题这里写的是"当前显示 N 条"，而 <see cref="SelectedChat"/> 里刚选中时写的是"实时来源"——
    /// 这是原前端本身就存在的两处文案，不要顺手统一。
    /// </summary>
    private void UpdateAnalysisCount()
    {
        var scope = Count is { } count ? $"最近 {count} 条" : $"最多 {UnlimitedRequestLimit} 条";
        var timeScope = RecentHours is { } hours ? $"最近 {FormatHours(hours)}" : "不限制时间";

        AnalysisCount = $"将分析 {_visibleMessages.Count} 条真实消息（{timeScope}，{scope}）";

        ChatSubtitle = SelectedChat is { } chat
            ? $"微信 · {chat.TypeText} · 当前显示 {_visibleMessages.Count} 条"
            : "未选择实时会话";
    }

    /// <summary>
    /// 对照 <c>formatHours()</c>：不足 1 小时显示分钟、整天显示天、其余显示小时。
    /// 用 <see cref="MidpointRounding.AwayFromZero"/> 是为了对齐 JS 的 <c>Math.round</c>——
    /// C# 的 <see cref="Math.Round(double)"/> 默认是"四舍六入五成双"，在 0.5 这种边界上会与前端不一致。
    /// </summary>
    private static string FormatHours(double hours)
    {
        if (hours < 1)
        {
            return Math.Round(hours * 60, MidpointRounding.AwayFromZero) + " 分钟";
        }

        return hours % 24 == 0 ? $"{hours / 24} 天" : $"{hours} 小时";
    }

    private void RebuildMessages()
    {
        Messages.Clear();
        foreach (var message in _visibleMessages)
        {
            Messages.Add(new MessageItemViewModel(message));
        }

        MessagesPlaceholder = Messages.Count == 0 ? "当前筛选范围内没有真实消息" : "";
        OnPropertyChanged(nameof(HasMessages));
        RaiseMessagesUpdated();
    }

    /// <summary>
    /// 消息时间戳归一化的兜底：<see cref="ChatMessage.TimestampValue"/> 只认数字，
    /// 而原前端的 <c>messageTimestamp()</c> 在数字不可用时还会 <c>Date.parse(message.time)</c>。
    /// 少了这一步，那些"只有格式化时间、没有原始 timestamp"的消息在时间筛选下会被整批误删。
    /// </summary>
    private static DateTimeOffset? ResolveTimestamp(ChatMessage message)
        => message.TimestampValue
           ?? (DateTimeOffset.TryParse(
                   message.Time,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out var parsed)
               ? parsed
               : null);

    // ==================== AI 分析 ====================

    private Task RunPresetAsync(object? parameter)
    {
        var type = parameter as string is { Length: > 0 } value ? value : AnalyzeType.General;
        AnalysisType = type;
        return PresetPrompts.TryGetValue(type, out var prompt)
            ? RunAnalysisAsync(prompt, type)
            : Task.CompletedTask;
    }

    /// <summary>
    /// 切换分析类型标签（对照 <c>selectAnalysisType(button)</c>）。
    /// 只改类型，不触发分析——"先选类型、再点运行"是原前端的交互顺序。
    /// </summary>
    private void SelectAnalysisType(object? parameter)
    {
        var type = parameter as string is { Length: > 0 } value ? value : AnalyzeType.General;
        AnalysisType = type;
    }

    private Task SendCustomPromptAsync()
    {
        var prompt = CustomPrompt.Trim();
        if (prompt.Length == 0)
        {
            return Task.CompletedTask;
        }

        CustomPrompt = "";

        // 注意与原前端一致：自定义提问固定用 general，而不是当前选中的标签页类型。
        return RunAnalysisAsync(prompt, AnalyzeType.General);
    }

    private async Task RunAnalysisAsync(string prompt, string type)
    {
        if (SelectedChat is null)
        {
            AlertRequested?.Invoke("请先选择会话");
            return;
        }

        if (_visibleMessages.Count == 0)
        {
            AlertRequested?.Invoke("当前筛选范围内没有消息");
            return;
        }

        IsAnalyzing = true;
        AiThread.Add(AiThreadItemViewModel.UserMessage(prompt, AssistantName));
        NotifyAiThread();

        try
        {
            // 上下文 = "刚推入的这条提问之前"的最后 10 条（对照 `aiHistory.slice(0, -1).slice(-10)`）。
            var priorHistory = AiThread
                .Take(AiThread.Count - 1)
                .TakeLast(10)
                .Select(turn => turn.ToHistoryItem())
                .ToList();

            var request = new SummarizeRequest
            {
                TargetName = SelectedChat.Name,
                Messages = _visibleMessages
                    .Select(message => new AnalyzeMessage
                    {
                        Sender = message.Sender,
                        Time = message.Time,
                        Content = message.Content,
                    })
                    .ToList(),
                Type = type,
                Prompt = prompt,
                History = priorHistory,
            };

            var data = await _api.SummarizeAsync(request, _lifetime.Token);
            AiThread.Add(AiThreadItemViewModel.AssistantMessage(data.Summary, AssistantName));
            await SaveHistoryAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 退出中发起的取消不算失败，不落"请求失败"气泡。
        }
        catch (Exception ex) when (ex is ChatlogApiException or OperationCanceledException)
        {
            AiThread.Add(AiThreadItemViewModel.AssistantMessage(
                "请求失败：" + ex.Message, AssistantName, isError: true));
        }
        finally
        {
            IsAnalyzing = false;
            NotifyAiThread();
        }
    }

    /// <summary>整体覆盖该会话的历史（POST /api/history 不是追加语义）。</summary>
    private async Task SaveHistoryAsync()
    {
        var chat = SelectedChat;
        if (chat is null || AiThread.Count == 0)
        {
            return;
        }

        try
        {
            await _api.SaveHistoryAsync(
                chat.Id,
                AiThread.Select(turn => turn.ToHistoryItem()).ToList(),
                _lifetime.Token);
        }
        catch (Exception ex) when (ex is ChatlogApiException or OperationCanceledException)
        {
            // 与原前端一致：历史保存失败只在控制台记一笔，不打断已经拿到的分析结果。
            Debug.WriteLine($"[ChatlogAI] 保存 AI 历史失败：{ex.Message}");
        }
    }

    private async Task LoadHistoryAsync()
    {
        var chat = SelectedChat;
        if (chat is null)
        {
            return;
        }

        IReadOnlyList<HistoryItem> history;
        try
        {
            var response = await _api.GetHistoryAsync(chat.Id, _lifetime.Token);
            history = response.History;
        }
        catch (Exception ex) when (ex is ChatlogApiException or OperationCanceledException)
        {
            Debug.WriteLine($"[ChatlogAI] 读取 AI 历史失败：{ex.Message}");
            history = [];
        }

        // 读取期间用户可能又切了会话，此时这份历史属于上一个会话。
        if (SelectedChat?.Id != chat.Id)
        {
            return;
        }

        AiThread.Clear();
        foreach (var item in history)
        {
            AiThread.Add(AiThreadItemViewModel.FromHistory(item, AssistantName));
        }

        NotifyAiThread();
    }

    private void ClearAiThread()
    {
        AiThread.Clear();
        NotifyAiThread();
    }

    /// <summary>
    /// 复制某条 AI 回答。原前端用 <c>copyHistoryItem(index)</c> + 剪贴板 API，
    /// 失败时静默（浏览器不允许非用户手势写剪贴板）；这里同样不把失败弹到界面上。
    /// </summary>
    private static void CopyAiTurn(object? parameter)
    {
        if (parameter is not AiThreadItemViewModel turn)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(turn.RawContent);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            Debug.WriteLine($"[ChatlogAI] 写入剪贴板失败：{ex.Message}");
        }
    }

    private void NotifyAiThread()
    {
        OnPropertyChanged(nameof(HasAiThread));
        AiThreadUpdated?.Invoke();
    }

    private void RaiseMessagesUpdated() => MessagesUpdated?.Invoke();

    // ==================== 状态栏 ====================

    private async Task LoadProvidersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var data = await _api.GetProvidersAsync(cancellationToken);
            _providers = data.Providers;
            _activeProviderId = data.ActiveId;
        }
        catch (Exception ex) when (ex is ChatlogApiException or OperationCanceledException)
        {
            ModelStatus = "模型配置读取失败";
            return;
        }

        UpdateModelStatus();
    }

    private void UpdateModelStatus()
    {
        var active = ActiveProvider;
        if (active is null)
        {
            ModelStatus = "未配置模型，点击“模型设置”添加 API Key";
            return;
        }

        var name = string.IsNullOrEmpty(active.Name) ? active.ProviderType : active.Name;
        ModelStatus = $"{name} / {active.Model}";
        OnPropertyChanged(nameof(AssistantName));
    }

    private async Task LoadAutoReplyConfigAsync(CancellationToken cancellationToken)
    {
        try
        {
            var data = await _api.GetAutoReplyConfigAsync(cancellationToken);
            _autoReplyConfig = data.Config;
            IsAutoReplyAvailable = data.Available;
        }
        catch (Exception ex) when (ex is ChatlogApiException or OperationCanceledException)
        {
            // 读失败就当作"这次没读到"，不重置可用性：
            // 一次网络抖动不该把入口封死，具体错误等用户点进去时由弹窗报。
            _autoReplyConfig = null;
        }

        UpdateAutoReplyStatusFromConfig();
    }

    private void UpdateAutoReplyStatusFromConfig()
    {
        if (_autoReplyConfig?.Enabled != true)
        {
            AutoReplyStatus = "自动回复：未启用";
            return;
        }

        AutoReplyStatus = $"自动回复：已启用（守护 {_autoReplyConfig.GuardTargets.Count} 个会话，"
                          + $"最近 {_autoReplyConfig.ContextCount} 条上下文）";
    }

    private async Task RefreshAutoReplyStatusAsync()
    {
        if (_autoReplyLoading)
        {
            return;
        }

        _autoReplyLoading = true;
        try
        {
            var data = await _api.GetAutoReplyStatusAsync(_lifetime.Token);

            // 闸门状态放在 enabled 判断**之前**：待确认期间自动回复可能压根没开，
            // 但"发送被暂停"与 enabled 无关——漏了它，用户就看不到"为什么不回复"（§7.17 闸门语义）。
            if (_sendPaused != data.SendPaused || _sendPausedReason != data.SendPausedReason)
            {
                _sendPaused = data.SendPaused;
                _sendPausedReason = data.SendPausedReason;
                OnPropertyChanged(nameof(IsSendPaused));
                OnPropertyChanged(nameof(SendPausedNotice));
            }

            if (!data.Enabled)
            {
                AutoReplyStatus = "自动回复：未启用";
                return;
            }

            var running = data.Running ? "运行中" : "未运行";
            var latest = data.Log.Count > 0 ? data.Log[^1].Message : "";
            AutoReplyStatus = string.IsNullOrEmpty(latest)
                ? $"自动回复：{running}"
                : $"自动回复：{running} · {latest}";
        }
        catch (Exception ex) when (ex is ChatlogApiException or OperationCanceledException)
        {
            // 与原前端一致：状态轮询失败保持上一次文案，不刷错误。
        }
        finally
        {
            _autoReplyLoading = false;
        }
    }

    // ==================== T9：模型设置弹窗接入点 ====================

    /// <summary>
    /// 造一个「模型与 API Key 设置」弹窗的视图模型（弹窗本体见 <c>Views/ModelSettingsWindow.xaml</c>）。
    ///
    /// 与 T10 同样的理由共享同一个 <see cref="ChatlogApiClient"/>：不给弹窗另起客户端，
    /// 否则会多出一份 <c>HttpClient</c> 与连接池，退出时还得单独释放。
    ///
    /// 第二个参数是"弹窗改完供应商之后回刷主界面"的钩子：模型名、状态栏、余额、AI 署名
    /// 都住在 MainViewModel 里，弹窗自己改完不通知，关掉弹窗后主界面仍挂着旧模型名。
    /// </summary>
    public ModelSettingsViewModel CreateModelSettingsViewModel()
        => new(_api, RefreshModelStateAsync);

    /// <summary>
    /// 弹窗侧"保存 / 切换 / 删除"成功后回刷：重取供应商列表（连带状态栏与 <c>AssistantName</c>），
    /// 并重查余额。
    ///
    /// 余额这一步不能省：换一个供应商就是换一个账号的钱，
    /// 只等 60 秒一轮的 <c>BalancePollInterval</c> 自己刷，中间这段时间显示的是上一个供应商的余额。
    ///
    /// 已知取舍：<c>RefreshBalanceAsync</c> 带 <c>_balanceLoading</c> 防重入，若此刻正好有一次余额查询在途，
    /// 这一次会被跳过，界面沿用上一次的文案，最迟 60 秒后被轮询纠正。
    /// 这里刻意不去绕开那道防重入：并发两条查询的返回顺序不定，反而可能用旧供应商的余额覆盖新值。
    /// </summary>
    public async Task RefreshModelStateAsync()
    {
        await LoadProvidersAsync(_lifetime.Token);
        await RefreshBalanceAsync();
    }

    // ==================== T10：自动回复弹窗接入点 ====================

    /// <summary>
    /// 造一个「自动回复设置」弹窗的视图模型（弹窗本体见 <c>Views/AutoReplyWindow.xaml</c>）。
    ///
    /// 共享同一个 <see cref="ChatlogApiClient"/>，不给弹窗另起客户端：否则会多出一份
    /// <c>HttpClient</c> 与连接池，退出时还得单独释放，纯属自找麻烦。
    ///
    /// 会话名用委托<b>现取</b>而不是传值：弹窗打开后主界面仍在 5 秒轮询、用户也仍可切换会话，
    /// 而"测试完整发送流程"必须发到"点下按钮那一刻"选中的会话
    /// （对照原前端 <c>testFullFlow()</c> 读的是全局 <c>currentChat</c>）。
    /// </summary>
    public AutoReplyViewModel CreateAutoReplyViewModel()
        => new(_api, () => SelectedChat?.Name);

    /// <summary>
    /// 弹窗保存成功后由 View 调用：立刻回读一次配置，让状态栏马上反映新值。
    ///
    /// 为什么不能只等轮询：3 秒一次的 <c>RefreshAutoReplyStatusAsync</c> 打的是
    /// <c>/api/auto-reply/status</c>，那里只有 enabled / running / log，
    /// 拿不到 guard_targets 与 context_count，也就没法拼出"守护 N 个会话，最近 M 条上下文"。
    /// </summary>
    public Task ReloadAutoReplyConfigAsync() => LoadAutoReplyConfigAsync(_lifetime.Token);

    // ==================== 定时发送接入点 ====================

    /// <summary>
    /// 造一个「定时发送任务」编辑弹窗的视图模型（弹窗本体见 <c>Views/ScheduledSendWindow.xaml</c>）。
    ///
    /// 共享同一个 <see cref="ChatlogApiClient"/>，理由同 T9/T10。
    /// <paramref name="task"/> 为 <c>null</c> 表示新建。
    ///
    /// 保存提交**不在这里做**：弹窗只编出一条任务，由 <see cref="ScheduledSendViewModel.UpsertTaskAsync"/>
    /// 整份提交——服务端 <c>tasks</c> 是整份替换语义，两处都写必然互相覆盖。
    /// </summary>
    public ScheduledSendEditorViewModel CreateScheduledSendEditorViewModel(ScheduledSendTask? task)
        => new(_api, task, ScheduledSend.IsAvailable);

    private async Task RefreshBalanceAsync()
    {
        if (_balanceLoading)
        {
            return;
        }

        if (string.IsNullOrEmpty(_activeProviderId))
        {
            BalanceStatus = "余额：未配置模型";
            return;
        }

        _balanceLoading = true;
        try
        {
            var data = await _api.GetBalanceAsync(_lifetime.Token);
            if (!string.Equals(data.Status, "success", StringComparison.Ordinal))
            {
                BalanceStatus = "余额：查询失败";
                return;
            }

            // 与 app.js 一致：supported 为真且能算出展示文案才算有余额；
            // "接口通了但供应商不提供余额接口"是正常结果，不等于失败。
            BalanceStatus = data is { Supported: true, Balance.Text.Length: > 0 }
                ? "余额：" + data.Balance!.Text
                : "余额：该供应商不支持查询";
        }
        catch (Exception ex) when (ex is ChatlogApiException or OperationCanceledException)
        {
            // 余额失败不影响其它功能，但这一格必须给出确定结果，不能停在旧值上。
            BalanceStatus = "余额：查询失败";
        }
        finally
        {
            _balanceLoading = false;
        }
    }

    // ==================== T19-B：账号切换 ====================

    /// <summary>
    /// 查一次"是否有账号切换待确认"，并顺带补拉一次账号显示名。**只在启动首帧之后调一次**
    /// （以及设置模块/账号窗口需要时的显式刷新），不挂轮询。
    ///
    /// 返回 true 表示调用方应当打开账号窗口并停在待确认区：否则用户只会看到"自动回复不工作"，
    /// 却完全找不到原因（§7.17 客户端一节）。
    /// </summary>
    public async Task<bool> CheckPendingSwitchAsync()
    {
        try
        {
            // 这个接口读的是本项目后端的落盘标记，**不依赖上游**，所以启动早期就能拿到准确结果。
            var pending = await _api.GetPendingAccountSwitchAsync(_lifetime.Token);
            ApplyPendingSwitch(pending.Pending, pending.Account);
        }
        catch (Exception ex) when (ex is ChatlogApiException or OperationCanceledException)
        {
            // 读不到就当作"没有待确认"：启动期弹一个"查询失败"只会吓人，
            // 真正的失败会在用户打开账号窗口时如实显示。
        }

        // 账号显示名住在上游的 accounts[] 里，而它可能还没起来：这里只发一次、失败静默，
        // 不 await（它慢起来会挡住账号窗口的弹出），也不新增轮询。
        _ = RefreshAccountsAsync(_lifetime.Token);

        return HasPendingAccountSwitch;
    }

    /// <summary>供账号窗口关闭后回刷：窗口里可能刚完成切换或授权，头像与设置那一行必须跟上。</summary>
    public Task RefreshAccountStateAsync() => RefreshAccountsAsync(_lifetime.Token);

    /// <summary>
    /// 拉一次账号清单，刷新"当前账号 + 待确认"两处显示。
    /// 上游不可达时后端返回 503：失败**不清空**已有显示（账号名几乎不变，清掉只会让头像闪回"—"）。
    /// </summary>
    private async Task RefreshAccountsAsync(CancellationToken cancellationToken)
    {
        if (_accountsLoading)
        {
            return;
        }

        _accountsLoading = true;
        try
        {
            var data = await _api.GetWechatAccountsAsync(cancellationToken);

            _activeAccount = data.Active;
            _activeAccountName = data.Accounts.FirstOrDefault(item => item.IsActive)?.Name ?? "";
            _accountInfoFresh = true;

            _hasPendingAccountSwitch = data.PendingSwitch?.Pending == true;
            _pendingSwitchAccount = _hasPendingAccountSwitch ? data.PendingSwitch!.Account : "";

            NotifyAccountState();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正在退出，不改界面。
        }
        catch (Exception ex) when (ex is ChatlogApiException)
        {
            // 上游未就绪（503）：保持降级显示，等下一次触发（会话轮询成功 / 打开窗口）再试。
            _accountInfoRequested = false;
        }
        finally
        {
            _accountsLoading = false;
        }
    }

    private void ApplyPendingSwitch(bool pending, string account)
    {
        _hasPendingAccountSwitch = pending;
        _pendingSwitchAccount = pending ? account : "";
        NotifyAccountState();
    }

    private void NotifyAccountState()
    {
        OnPropertyChanged(nameof(CurrentAccountShort));
        OnPropertyChanged(nameof(CurrentAccountLine));
        OnPropertyChanged(nameof(AccountTooltip));
        OnPropertyChanged(nameof(HasPendingAccountSwitch));
    }

    /// <summary>
    /// 账号切换确认后的清空与重载（§7.17 决策④）。
    ///
    /// 为什么必须整块清空：跨账号的"A 的会话 + B 的消息"混在一条时间线上毫无意义；
    /// AI 多轮对话更是针对某个具体会话的，留着会把新账号的分析带偏。
    ///
    /// 为什么不新写一套取数逻辑：清空只是把界面推到"空态"，真正的数据由既有方法重新拉一次，
    /// 之后四个轮询（会话 / 消息 / 自动回复状态 / 定时发送 / 余额）自然接手。
    /// </summary>
    public async Task ReloadAfterAccountSwitchAsync()
    {
        // 会话列表：必须显式 Clear，不能指望 SearchText 置空去触发 RefreshChatItems——
        // 搜索词本来就是空串时 SetProperty 不会通知，旧行会留在列表里。
        _chatModels = [];
        _chatIndex.Clear();
        Chats.Clear();

        _rawMessages.Clear();
        _visibleMessages.Clear();
        Messages.Clear();
        AiThread.Clear();

        SearchText = "";
        SelectedChat = null;
        ChatTitle = "未选择会话";
        ChatSubtitle = "未选择实时会话";
        ChatsPlaceholder = "正在读取真实会话";
        MessagesPlaceholder = "";
        AnalysisCount = "将分析 0 条真实消息";
        SourceStatus = "正在连接实时数据源";
        IsSourceReady = false;
        BalanceStatus = "余额：--";

        OnPropertyChanged(nameof(HasChats));
        OnPropertyChanged(nameof(HasMessages));
        NotifyAiThread();
        RaiseMessagesUpdated();

        // 重新取数：全部走既有方法，不新增任何取数逻辑。
        await LoadChatsAsync(showLoading: true, _lifetime.Token);

        // 自动回复 / 定时发送的配置**按账号分文件**（config/{auto-reply,scheduled-send}/{account}.json），
        // 不重读就会拿着上一个账号的守护列表继续跑。
        await LoadAutoReplyConfigAsync(_lifetime.Token);
        await ScheduledSend.LoadAsync();

        await RefreshAccountsAsync(_lifetime.Token);

        // 余额与账号无关，但界面上它属于"该清空的那几块"，所以要重查一次而不是等 60 秒轮询。
        _ = RefreshBalanceAsync();
        _ = RefreshAutoReplyStatusAsync();
        _ = RefreshMessagesAsync();
    }

    // ==================== 生命周期 ====================

    /// <summary>
    /// 停轮询 → 取消在途请求 → 释放令牌源。
    /// 由 <c>App</c> 在退出路径中调用（见 App.xaml.cs），顺序不能颠倒：
    /// 先停 timer 保证不再有新请求，再 Cancel 才能让在途请求真正收到取消。
    /// </summary>
    public void Dispose()
    {
        foreach (var timer in _timers)
        {
            timer.Stop();
        }

        _timers.Clear();

        // 面板 / 模块先于自身令牌释放：它们各自持有自己的令牌源与在途请求。
        ScheduledSend.Dispose();
        Contacts.Dispose();
        Moments.Dispose();

        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
