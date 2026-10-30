using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.Services;

namespace ChatlogAI.Client.ViewModels;

/// <summary>「发送对象」下拉框的一项。包一层是为了给 ComboBox 一个现成的展示文案。</summary>
public sealed record ScheduledTargetOption(ChatSummary Chat)
{
    public string Id => Chat.Id;

    public string Name => string.IsNullOrEmpty(Chat.Name) ? Chat.Id : Chat.Name;

    public string Type => Chat.Type;

    public string TypeText => Chat.IsGroup ? "群聊" : "私聊";

    /// <summary>收起态与展开态都用它（Theme.xaml 的 ComboBox 模板只认 ItemTemplate）。</summary>
    public string Display => $"{Name}（{TypeText}）";
}

/// <summary>
/// 「定时发送任务」编辑弹窗的视图模型。
///
/// 职责边界（很重要）：本 VM <b>只负责编出一条任务</b>，不负责写配置。
/// 保存时把任务交给 <see cref="ScheduledSendViewModel"/> 整份提交——
/// 服务端 <c>tasks</c> 是整份替换语义，若弹窗自己去写数组，
/// 就会与主界面的那次提交互相覆盖。
///
/// 试发（<c>send</c>）与预览（<c>preview</c>）则直接打服务端测试接口，
/// 并且提交的是**完整任务**而不是 id，因此没有保存过的草稿也能先试一条。
/// </summary>
public sealed class ScheduledSendEditorViewModel : ObservableObject
{
    private const int MinIntervalMinutes = 1;
    private const int MaxIntervalMinutes = 10080;
    private const int MaxCooldownMinutes = 10080;
    private const int MaxJitterMinutes = 1440;
    private const int MinContextCount = 1;
    private const int MaxContextCount = 2000;

    private readonly ChatlogApiClient _api;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ScheduledSendTask? _original;

    private ScheduledTargetOption? _selectedChat;
    private string _mode = ScheduledSendModes.Daily;
    private string _timeOfDay = "09:00";
    private bool _monday;
    private bool _tuesday;
    private bool _wednesday;
    private bool _thursday;
    private bool _friday;
    private bool _saturday;
    private bool _sunday;
    private string _intervalMinutesText = "120";
    private string _runDateText = "";
    private string _runTimeText = "09:00";
    private string _cooldownMinutesText = "60";
    private string _jitterMinutesText = "15";
    private string _contextCountText = "80";
    private string _prompt = "";
    private List<string> _docIds = [];
    private IReadOnlyList<DocItem> _docs = [];
    private bool _taskEnabled = true;
    private string _previewText = "";
    private string _chatPlaceholder = "正在读取微信会话……";

    /// <param name="api">共享的 REST 客户端。</param>
    /// <param name="task">要编辑的任务；<c>null</c> 表示新建。</param>
    /// <param name="sendAvailable">服务端是否支持键盘模拟（决定「试发」是否可点）。</param>
    public ScheduledSendEditorViewModel(ChatlogApiClient api, ScheduledSendTask? task, bool sendAvailable)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _original = task;
        IsSendAvailable = sendAvailable;

        SaveCommand = new RelayCommand(Save);
        PreviewCommand = new AsyncRelayCommand(PreviewAsync);
        TestSendCommand = new AsyncRelayCommand(TestSendAsync);
    }

    // ==================== 对外事件 ====================

    public event Action<string>? AlertRequested;

    public event Func<string, bool>? ConfirmRequested;

    /// <summary>用户确认保存，携带编好的任务。由 View 交给主界面整份提交。</summary>
    public event Action<ScheduledSendTask>? SaveRequested;

    /// <summary>该关窗了。</summary>
    public event Action? CloseRequested;

    // ==================== 命令 ====================

    public ICommand SaveCommand { get; }

    public ICommand PreviewCommand { get; }

    public ICommand TestSendCommand { get; }

    /// <summary>「试发」是否可用；不可用时界面隐藏该按钮。</summary>
    public bool IsSendAvailable { get; }

    /// <summary>弹窗标题（新建 / 编辑）。</summary>
    public string Title => _original is null ? "新增定时发送任务" : "编辑定时发送任务";

    // ==================== 发送对象 ====================

    public ObservableCollection<ScheduledTargetOption> ChatOptions { get; } = [];

    public ScheduledTargetOption? SelectedChat
    {
        get => _selectedChat;
        set => SetProperty(ref _selectedChat, value);
    }

    public string ChatPlaceholder
    {
        get => _chatPlaceholder;
        private set => SetProperty(ref _chatPlaceholder, value);
    }

    // ==================== 触发规则 ====================

    /// <summary>daily / weekly / interval / once，取值与 Python 侧 SCHEDULED_MODES 一致。</summary>
    public string Mode
    {
        get => _mode;
        set
        {
            if (SetProperty(ref _mode, value))
            {
                OnPropertyChanged(nameof(IsDailyMode));
                OnPropertyChanged(nameof(IsWeeklyMode));
                OnPropertyChanged(nameof(IsIntervalMode));
                OnPropertyChanged(nameof(IsOnceMode));
                OnPropertyChanged(nameof(IsFixedTimeMode));
            }
        }
    }

    public bool IsDailyMode => Mode == ScheduledSendModes.Daily;

    public bool IsWeeklyMode => Mode == ScheduledSendModes.Weekly;

    public bool IsIntervalMode => Mode == ScheduledSendModes.Interval;

    public bool IsOnceMode => Mode == ScheduledSendModes.Once;

    /// <summary>daily 与 weekly 共用"当天该时刻"这一段输入。</summary>
    public bool IsFixedTimeMode => IsDailyMode || IsWeeklyMode;

    /// <summary>HH:mm。</summary>
    public string TimeOfDay
    {
        get => _timeOfDay;
        set => SetProperty(ref _timeOfDay, value);
    }

    public bool Monday
    {
        get => _monday;
        set => SetProperty(ref _monday, value);
    }

    public bool Tuesday
    {
        get => _tuesday;
        set => SetProperty(ref _tuesday, value);
    }

    public bool Wednesday
    {
        get => _wednesday;
        set => SetProperty(ref _wednesday, value);
    }

    public bool Thursday
    {
        get => _thursday;
        set => SetProperty(ref _thursday, value);
    }

    public bool Friday
    {
        get => _friday;
        set => SetProperty(ref _friday, value);
    }

    public bool Saturday
    {
        get => _saturday;
        set => SetProperty(ref _saturday, value);
    }

    public bool Sunday
    {
        get => _sunday;
        set => SetProperty(ref _sunday, value);
    }

    /// <summary>固定间隔的分钟数。用 string 承载：绑 int 时非法输入会让绑定静默失败。</summary>
    public string IntervalMinutesText
    {
        get => _intervalMinutesText;
        set => SetProperty(ref _intervalMinutesText, value);
    }

    /// <summary>一次性任务的日期，yyyy-MM-dd。</summary>
    public string RunDateText
    {
        get => _runDateText;
        set => SetProperty(ref _runDateText, value);
    }

    /// <summary>一次性任务的时刻，HH:mm。</summary>
    public string RunTimeText
    {
        get => _runTimeText;
        set => SetProperty(ref _runTimeText, value);
    }

    // ==================== 节奏与内容 ====================

    public string CooldownMinutesText
    {
        get => _cooldownMinutesText;
        set => SetProperty(ref _cooldownMinutesText, value);
    }

    public string JitterMinutesText
    {
        get => _jitterMinutesText;
        set => SetProperty(ref _jitterMinutesText, value);
    }

    public string ContextCountText
    {
        get => _contextCountText;
        set => SetProperty(ref _contextCountText, value);
    }

    public string Prompt
    {
        get => _prompt;
        set => SetProperty(ref _prompt, value);
    }

    /// <summary>关联文档按钮的文案：未关联时「关联文档」，已关联时带数量。</summary>
    public string DocCountText => _docIds.Count == 0 ? "关联文档" : $"关联文档（{_docIds.Count}）";

    /// <summary>当前关联的知识文档 id（只读，改动走 <see cref="SetDocIds"/>）。</summary>
    public IReadOnlyList<string> DocIds => _docIds;

    /// <summary>知识文档列表，供 View 打开「关联文档」选择器时取用。</summary>
    public IReadOnlyList<DocItem> Docs => _docs;

    /// <summary>文档选择器返回后写入勾选的文档 id，并刷新按钮文案。</summary>
    public void SetDocIds(IReadOnlyList<string> docIds)
    {
        _docIds = docIds.ToList();
        OnPropertyChanged(nameof(DocCountText));
    }

    public bool TaskEnabled
    {
        get => _taskEnabled;
        set => SetProperty(ref _taskEnabled, value);
    }

    /// <summary>预览 / 试发生成的正文，直接显示在弹窗里，方便调提示词。</summary>
    public string PreviewText
    {
        get => _previewText;
        private set => SetProperty(ref _previewText, value);
    }

    // ==================== 加载 ====================

    /// <summary>打开弹窗时调用：先读会话列表（供选发送对象），再回填表单。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var data = await _api.GetChatsAsync(_lifetime.Token);
            ChatOptions.Clear();
            foreach (var chat in data.Chats)
            {
                ChatOptions.Add(new ScheduledTargetOption(chat));
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ChatlogApiException ex)
        {
            AlertRequested?.Invoke($"读取微信会话失败：{ex.Message}");
        }

        RefreshChatPlaceholder();

        // 取令牌之前先收手：用户可能在会话还没回来时就关了窗口，
        // 那时 _lifetime 已被 CancelPending() 取消并释放。
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        await LoadDocsAsync();
        ApplyOriginal();
    }

    /// <summary>加载知识文档列表（T-F4）。失败静默：文档是可选增强，不阻断任务编辑。</summary>
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

    /// <summary>弹窗关闭时调用：取消在途请求。</summary>
    public void CancelPending()
    {
        if (!_lifetime.IsCancellationRequested)
        {
            _lifetime.Cancel();
        }

        _lifetime.Dispose();
    }

    private void RefreshChatPlaceholder()
        => ChatPlaceholder = ChatOptions.Count == 0 ? "未读取到微信会话，请关闭本窗口后重试。" : "";

    private void ApplyOriginal()
    {
        var fallbackDate = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (_original is null)
        {
            RunDateText = fallbackDate;
            RunTimeText = "09:00";
            return;
        }

        TaskEnabled = _original.Enabled;
        Mode = _original.Mode;
        TimeOfDay = _original.TimeOfDay;
        Monday = _original.Weekdays.Contains(1);
        Tuesday = _original.Weekdays.Contains(2);
        Wednesday = _original.Weekdays.Contains(3);
        Thursday = _original.Weekdays.Contains(4);
        Friday = _original.Weekdays.Contains(5);
        Saturday = _original.Weekdays.Contains(6);
        Sunday = _original.Weekdays.Contains(7);
        IntervalMinutesText = _original.IntervalMinutes.ToString(CultureInfo.InvariantCulture);
        CooldownMinutesText = _original.CooldownMinutes.ToString(CultureInfo.InvariantCulture);
        JitterMinutesText = _original.JitterMinutes.ToString(CultureInfo.InvariantCulture);
        ContextCountText = _original.ContextCount.ToString(CultureInfo.InvariantCulture);
        Prompt = _original.Prompt;
        SetDocIds(_original.DocIds);

        if (_original.Mode == ScheduledSendModes.Once && _original.RunAt.Length >= 16)
        {
            RunDateText = _original.RunAt[..10];
            RunTimeText = _original.RunAt.Substring(11, 5);
        }
        else
        {
            RunDateText = fallbackDate;
            RunTimeText = "09:00";
        }

        SelectChatById(_original.TargetId, _original.TargetName, _original.TargetType);
    }

    /// <summary>
    /// 选中目标会话。若已保存的目标已不在当前会话列表里（被归档 / 未同步），
    /// 就补一个合成项——否则"编辑已有任务"会显示成未选择，一保存就把目标弄丢。
    /// </summary>
    private void SelectChatById(string? id, string? fallbackName = null, string? fallbackType = null)
    {
        if (string.IsNullOrEmpty(id))
        {
            SelectedChat = null;
            return;
        }

        var option = ChatOptions.FirstOrDefault(item => item.Id == id);
        if (option is null && !string.IsNullOrEmpty(fallbackName))
        {
            option = new ScheduledTargetOption(new ChatSummary
            {
                Id = id,
                Name = fallbackName,
                Type = fallbackType ?? "",
            });
            ChatOptions.Add(option);
        }

        SelectedChat = option;
    }

    // ==================== 写操作 ====================

    private void Save()
    {
        if (!TryBuildTask(out var task))
        {
            return;
        }

        SaveRequested?.Invoke(task);
        CloseRequested?.Invoke();
    }

    private async Task PreviewAsync()
    {
        if (!TryBuildTask(out var task))
        {
            return;
        }

        try
        {
            var data = await _api.TestScheduledSendAsync(
                new ScheduledSendTestRequest { Action = "preview", Task = task }, _lifetime.Token);
            PreviewText = data.Text;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChatlogApiException ex)
        {
            AlertRequested?.Invoke($"预览失败：{ex.Message}");
        }
    }

    /// <summary>会真实往微信发一条消息，所以每次都要二次确认。</summary>
    private async Task TestSendAsync()
    {
        if (!TryBuildTask(out var task))
        {
            return;
        }

        if (ConfirmRequested?.Invoke($"将通过微信真实发送一条消息给「{task.TargetName}」，确认继续？") != true)
        {
            return;
        }

        try
        {
            var data = await _api.TestScheduledSendAsync(
                new ScheduledSendTestRequest { Action = "send", Task = task }, _lifetime.Token);
            PreviewText = data.Text;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChatlogApiException ex)
        {
            AlertRequested?.Invoke($"试发失败：{ex.Message}");
        }
    }

    /// <summary>校验表单并编出任务；任一项非法时弹提示并返回 false。</summary>
    private bool TryBuildTask(out ScheduledSendTask task)
    {
        task = null!;

        if (SelectedChat is null)
        {
            AlertRequested?.Invoke("请先选择要发送的联系人。");
            return false;
        }

        if (!TryParseTimeOfDay(TimeOfDay, out var timeOfDay))
        {
            AlertRequested?.Invoke("触发时刻格式应为 HH:mm（例如 09:00）。");
            return false;
        }

        var weekdays = CollectWeekdays();
        if (Mode == ScheduledSendModes.Weekly && weekdays.Count == 0)
        {
            AlertRequested?.Invoke("按星期循环时至少需要勾选一个星期几。");
            return false;
        }

        if (!TryReadRange(IntervalMinutesText, MinIntervalMinutes, MaxIntervalMinutes, "间隔分钟", out var interval))
        {
            return false;
        }

        if (!TryReadRange(CooldownMinutesText, 0, MaxCooldownMinutes, "冷却分钟", out var cooldown))
        {
            return false;
        }

        if (!TryReadRange(JitterMinutesText, 0, MaxJitterMinutes, "随机推迟分钟", out var jitter))
        {
            return false;
        }

        if (!TryReadRange(ContextCountText, MinContextCount, MaxContextCount, "上下文条数", out var contextCount))
        {
            return false;
        }

        var runAt = "";
        if (Mode == ScheduledSendModes.Once)
        {
            if (!TryParseRunAt(RunDateText, RunTimeText, out runAt))
            {
                AlertRequested?.Invoke("一次性任务的日期/时间格式应为 2026-09-12 与 09:00。");
                return false;
            }

            var moment = DateTime.ParseExact(runAt, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
            if (moment <= DateTime.Now)
            {
                AlertRequested?.Invoke("一次性任务的时间必须晚于当前时间。");
                return false;
            }
        }

        task = new ScheduledSendTask
        {
            // 保留原 id（新建时为服务端生成，这里留空）。
            Id = _original?.Id ?? "",
            TargetId = SelectedChat.Id,
            TargetName = SelectedChat.Name,
            TargetType = SelectedChat.Type,
            Enabled = TaskEnabled,
            Mode = Mode,
            TimeOfDay = timeOfDay,
            Weekdays = weekdays,
            IntervalMinutes = interval,
            RunAt = runAt,
            CooldownMinutes = cooldown,
            JitterMinutes = jitter,
            ContextCount = contextCount,
            Prompt = Prompt.Trim(),
            DocIds = _docIds,
        };

        return true;
    }

    private List<int> CollectWeekdays()
    {
        var days = new List<int>();
        if (Monday)
        {
            days.Add(1);
        }

        if (Tuesday)
        {
            days.Add(2);
        }

        if (Wednesday)
        {
            days.Add(3);
        }

        if (Thursday)
        {
            days.Add(4);
        }

        if (Friday)
        {
            days.Add(5);
        }

        if (Saturday)
        {
            days.Add(6);
        }

        if (Sunday)
        {
            days.Add(7);
        }

        return days;
    }

    private bool TryReadRange(string text, int low, int high, string label, out int value)
    {
        value = low;
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < low
            || parsed > high)
        {
            AlertRequested?.Invoke($"{label}必须是 {low} 至 {high} 之间的整数。");
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryParseTimeOfDay(string text, out string normalized)
    {
        normalized = "";
        var trimmed = text.Trim();
        if (!DateTime.TryParseExact(
                trimmed,
                ["H:mm", "HH:mm"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var value))
        {
            return false;
        }

        normalized = value.ToString("HH:mm", CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryParseRunAt(string dateText, string timeText, out string normalized)
    {
        normalized = "";
        var combined = (dateText.Trim() + " " + timeText.Trim()).Trim();
        if (!DateTime.TryParseExact(
                combined,
                ["yyyy-MM-dd H:mm", "yyyy-MM-dd HH:mm", "yyyy/M/d H:mm", "yyyy/M/d HH:mm"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var value))
        {
            return false;
        }

        normalized = value.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        return true;
    }
}
