using System.IO;
using System.Text;
using System.Windows.Input;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.Services;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// 「导出聊天记录」对话框的视图模型（T11，规格见 docs/DOTNET_CLIENT_PLAN.md §8）。
///
/// 职责边界只有三条：
/// 1. 收集用户的导出选项（格式 / 独立时间范围 / 是否含 AI 历史）；
/// 2. 按选项取数（<see cref="ChatlogApiClient"/>）、交给 <see cref="ChatExporter"/> 排版、落盘；
/// 3. 把"要选路径"和"该关窗了"两件事抛给 View —— <c>SaveFileDialog</c> 属于 View 的职责，
///    与 <c>MainViewModel.AlertRequested</c> 是同一条 MVVM 边界。
///
/// 为什么数据要<b>重新拉一次</b>而不是复用主界面的消息列表：
/// 用户在对话框里可以选一个与界面筛选不同的时间范围（这正是本次确认的设计），
/// 而且导出上限 2000 条，可能多于界面"最近 500 条"的显示范围。
/// 复用界面数据会导出"看得见的那一部分"，与用户选的范围不一致。
/// </summary>
public sealed class ExportViewModel : ObservableObject, IDisposable
{
    private readonly ExportContext _context;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ChatlogApiClient _api;
    private readonly bool _ownsApi;

    private ExportFormatOption _selectedFormat;
    private ExportTimeUnitOption _selectedTimeUnit;
    private bool _hasTimeLimit;
    private int _timeValue;
    private bool _includeAiHistory;
    private bool _isBusy;
    private bool _isError;
    private string _statusMessage = "";
    private bool _succeeded;
    private string _resultMessage = "";

    /// <param name="api">
    /// 仅测试注入用。默认自建一个 —— <c>MainViewModel</c> 的 <c>ChatlogApiClient</c> 是私有的，
    /// 而导出只与后端打交道、不依赖主 VM 的任何状态，自己拿一个反而让两个模块彻底解耦。
    /// </param>
    public ExportViewModel(ExportContext context, ChatlogApiClient? api = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        _ownsApi = api is null;
        _api = api ?? new ChatlogApiClient();

        Formats =
        [
            new ExportFormatOption("Markdown 文档（.md）", ExportFormat.Markdown),
            new ExportFormatOption("HTML 网页（.html）", ExportFormat.Html),
            new ExportFormatOption("JSON 数据（.json）", ExportFormat.Json),
            new ExportFormatOption("CSV 表格（.csv）", ExportFormat.Csv),
        ];

        TimeUnits =
        [
            new ExportTimeUnitOption("分钟", "minutes"),
            new ExportTimeUnitOption("小时", "hours"),
            new ExportTimeUnitOption("天", "days"),
        ];

        _selectedFormat = Formats[0];
        _selectedTimeUnit = TimeUnits.FirstOrDefault(unit => unit.Unit == context.TimeUnit) ?? TimeUnits[1];
        _hasTimeLimit = context.HasTimeLimit;
        _timeValue = Math.Max(1, context.TimeValue);

        ExportCommand = new AsyncRelayCommand(ExportAsync);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    // ==================== 会话信息（只读，来自打开对话框时的快照） ====================

    public string ChatName => string.IsNullOrEmpty(_context.ChatName) ? "未命名会话" : _context.ChatName;

    public string ChatSubtitle => $"{_context.ChatTypeText} · 微信会话";

    // ==================== 导出选项 ====================

    public IReadOnlyList<ExportFormatOption> Formats { get; }

    public ExportFormatOption SelectedFormat
    {
        get => _selectedFormat;
        set
        {
            // ComboBox 在 ItemsSource 变动等情况下可能推一个 null 过来；
            // 本属性声明为非空（默认第一项），这里守住这个不变式。
            if (value is null || !SetProperty(ref _selectedFormat, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsAiHistoryEnabled));
            OnPropertyChanged(nameof(AiHistoryHint));

            // CSV 是固定四列的表格，塞不下 AI 对话（规格 §8）。
            // 这里把勾选一并清掉，而不是"留着勾、导出时忽略" ——
            // 后者会让成功提示写着"已导出 M 条 AI 对话"，而文件里一条都没有。
            // 代价：切回其它格式后需要用户重新勾选（宁可让他重勾，也不能给假结果）。
            if (!IsAiHistoryEnabled)
            {
                IncludeAiHistory = false;
            }
        }
    }

    public IReadOnlyList<ExportTimeUnitOption> TimeUnits { get; }

    public ExportTimeUnitOption SelectedTimeUnit
    {
        get => _selectedTimeUnit;
        set => SetProperty(ref _selectedTimeUnit, value);
    }

    /// <summary>是否限制时间范围。默认取主界面当前的筛选状态，但之后与主界面各改各的。</summary>
    public bool HasTimeLimit
    {
        get => _hasTimeLimit;
        set
        {
            if (SetProperty(ref _hasTimeLimit, value))
            {
                OnPropertyChanged(nameof(IsTimeRangeEnabled));
            }
        }
    }

    public int TimeValue
    {
        get => _timeValue;
        set => SetProperty(ref _timeValue, Math.Max(1, value));
    }

    /// <summary>时长数值框与单位下拉是否可编辑（与主界面筛选区同一套交互约定）。</summary>
    public bool IsTimeRangeEnabled => _hasTimeLimit;

    /// <summary>
    /// 是否把 <c>/api/history</c> 的 AI 对话追加到文件末尾。
    /// 注意该接口<b>不接受时间范围</b>，历史是全量的（见 ChatlogApiClient 注释）——
    /// 所以这一节的内容不受上面的时间范围约束，界面上必须写清楚。
    /// </summary>
    public bool IncludeAiHistory
    {
        get => _includeAiHistory;
        set => SetProperty(ref _includeAiHistory, value);
    }

    /// <summary>CSV 是固定四列的表格，承载不了 AI 对话，选中它时该勾选项整体禁用。</summary>
    public bool IsAiHistoryEnabled => _selectedFormat.Format != ExportFormat.Csv;

    /// <summary>勾选项下方的说明文案，随格式切换：CSV 讲清"为什么不能用"，其余讲清"不受时间范围限制"。</summary>
    public string AiHistoryHint => IsAiHistoryEnabled
        ? "AI 对话历史为全量读取，不受上面的时间范围限制。"
        : "CSV 为表格格式（时间/发送者/类型/内容），不含 AI 对话历史。";

    // ==================== 执行状态 ====================

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
            }
        }
    }

    /// <summary>导出进行中时整块选项区一起禁用，避免"选着选着，导出的是另一套参数"。</summary>
    public bool IsIdle => !_isBusy;

    /// <summary>当前提示是错误还是普通进度，决定 View 用哪档颜色。</summary>
    public bool IsError
    {
        get => _isError;
        private set => SetProperty(ref _isError, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>导出是否成功。View 用它决定 <c>DialogResult</c>；关闭后主界面据此提示。</summary>
    public bool Succeeded
    {
        get => _succeeded;
        private set => SetProperty(ref _succeeded, value);
    }

    /// <summary>成功后的提示文案（含落盘路径与条数），由主界面弹一次 MessageBox 告知。</summary>
    public string ResultMessage
    {
        get => _resultMessage;
        private set => SetProperty(ref _resultMessage, value);
    }

    public ICommand ExportCommand { get; }

    public ICommand CancelCommand { get; }

    // ==================== 与 View 的边界 ====================

    /// <summary>
    /// 由 View 在构造时挂上：弹一次"另存为"，返回用户选定的完整路径；取消则返回 <c>null</c>。
    /// 做成委托属性而不是事件，是因为这里需要"调用并拿到返回值"，
    /// 用事件还得再配一个回调字段，反而更绕。
    /// </summary>
    public Func<ExportSaveRequest, string?>? SaveFilePicker { get; set; }

    /// <summary>请求关闭对话框（导出成功，或用户点了"关闭"）。</summary>
    public event Action? CloseRequested;

    // ==================== 主流程 ====================

    private async Task ExportAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var picker = SaveFilePicker;
        if (picker is null)
        {
            return;
        }

        var range = BuildRange();
        var now = DateTimeOffset.Now;

        // 先要路径再取数：用户取消"另存为"时不该白跑一次网络请求，
        // 也不该出现"取完数据才发现没地方放"。
        var path = picker(new ExportSaveRequest
        {
            DefaultFileName = ChatExporter.BuildDefaultFileName(_context, SelectedFormat.Format, now),
            Filter = ChatExporter.BuildFileFilter(SelectedFormat.Format),
            DefaultDirectory = DefaultDirectory(),
        });

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        IsBusy = true;
        IsError = false;
        StatusMessage = "正在读取消息……";

        try
        {
            var messages = await FetchMessagesAsync(range).ConfigureAwait(true);

            IReadOnlyList<HistoryItem>? history = null;
            if (IncludeAiHistory)
            {
                StatusMessage = "正在读取 AI 对话历史……";
                history = await FetchHistoryAsync().ConfigureAwait(true);
            }

            // 仅 HTML 需要预取图片字节（内嵌 base64）；其余格式只写链接，不必多跑一遍网络。
            var imageDataUris = await FetchImageDataUrisAsync(messages).ConfigureAwait(true);

            StatusMessage = "正在写入文件……";
            var document = ChatExporter.Format(
                SelectedFormat.Format, _context, range, messages, history, now, imageDataUris);

            // 只有 CSV 需要 BOM（Excel 的中文乱码问题），是否写 BOM 由格式化层给出判据，
            // 这里不要自己再判断一次格式，否则两处判断迟早会不一致。
            await File.WriteAllTextAsync(path, document.Text, new UTF8Encoding(document.WriteBom))
                .ConfigureAwait(true);

            Succeeded = true;
            ResultMessage = BuildResultMessage(messages.Count, history, path);
            StatusMessage = ResultMessage;
            CloseRequested?.Invoke();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 对话框已被关掉，在途请求被取消是预期行为，不必再回写状态。
        }
        catch (Exception ex) when (ex is ChatlogApiException or IOException or UnauthorizedAccessException)
        {
            // 只兜"预期内会失败"的三类：接口/网络、写盘、无权限。
            // 其余异常继续往外抛给 App 的全局兜底 —— 把编码错误静默成一句提示，
            // 会让真正的问题永远查不出来。
            Succeeded = false;
            IsError = true;
            StatusMessage = $"导出失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<IReadOnlyList<ChatMessage>> FetchMessagesAsync(ExportRange range)
    {
        var response = await _api
            .GetMessagesAsync(
                _context.ChatId,
                ChatExporter.MaxMessages,
                ChatExporter.ToRecentHours(range),
                null,
                _lifetime.Token)
            .ConfigureAwait(true);

        // 与 MainViewModel.FetchMessagesAsync 同一判据：status 已由客户端兜住，
        // 但 source_valid 才是"这条数据真的来自微信"的证据，只看 HTTP 200 会把空列表写进文件。
        if (!response.SourceValid)
        {
            throw new ChatlogApiException(
                null,
                string.IsNullOrEmpty(response.Message) ? "消息读取失败" : response.Message);
        }

        return ChatExporter.FilterByRange(response.Messages, range, DateTimeOffset.Now);
    }

    private async Task<IReadOnlyList<HistoryItem>> FetchHistoryAsync()
    {
        var response = await _api.GetHistoryAsync(_context.ChatId, _lifetime.Token).ConfigureAwait(true);
        return response.History;
    }

    /// <summary>
    /// 把导出范围内的图片下载下来并转成 data URI（消息 id → data URI）。仅 HTML 格式需要。
    ///
    /// 单张图失败**不**让整次导出失败：那会让"一张坏图"毁掉一份几百条消息的记录，
    /// 代价远大于收益。失败的图片在 HTML 里退化成链接（<see cref="ChatExporter.Format"/> 已处理）。
    /// 取消（用户关窗）则必须原样抛出，否则会继续跑完剩下的图白耗时间。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>?> FetchImageDataUrisAsync(
        IReadOnlyList<ChatMessage> messages)
    {
        if (SelectedFormat.Format != ExportFormat.Html)
        {
            return null;
        }

        var images = messages.Where(message => message.IsImage).ToList();
        if (images.Count == 0)
        {
            return null;
        }

        var map = new Dictionary<string, string>();
        for (var index = 0; index < images.Count; index++)
        {
            var message = images[index];
            StatusMessage = $"正在下载图片 {index + 1}/{images.Count}……";
            try
            {
                var (bytes, contentType) = await _api
                    .DownloadImageAsync(message.ImageUrl, _lifetime.Token)
                    .ConfigureAwait(true);
                map[message.Id] = $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
            }
            catch (ChatlogApiException)
            {
                // 交给 HTML 层退化成链接，这里只跳过。
            }
        }

        return map;
    }

    private ExportRange BuildRange() => HasTimeLimit
        ? ChatExporter.CreateRecentRange(SelectedTimeUnit.Unit, TimeValue)
        : ChatExporter.UnlimitedRange;

    private static string DefaultDirectory()
    {
        // 「文档」目录在极端情况下会返回空串（未登录 / 目录被重定向到不可用位置），
        // 此时返回空串让 SaveFileDialog 自己决定，不抛异常。
        try
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string BuildResultMessage(int messageCount, IReadOnlyList<HistoryItem>? history, string path)
    {
        var text = new StringBuilder($"已导出 {messageCount} 条消息");
        if (history is { } items)
        {
            text.Append($"、{items.Count} 条 AI 对话");
        }

        return text.Append("：\n").Append(path).ToString();
    }

    public void Dispose()
    {
        // 先取消在途请求，再释放连接：反过来的话，取消时可能已经在读一个被释放的响应。
        _lifetime.Cancel();
        _lifetime.Dispose();

        if (_ownsApi)
        {
            _api.Dispose();
        }
    }
}
