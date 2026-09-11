using System.IO;
using System.Text.Json;
using System.Windows.Input;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.Services;

namespace ChatlogAI.Client.ViewModels;

/// <summary>导出格式的一个下拉项。格式串由微信数据源定义（html / json / txt / excel），客户端不做映射表之外的加工。</summary>
public sealed class MomentExportFormatOption
{
    internal MomentExportFormatOption(string label, string format)
    {
        Label = label;
        Format = format;
    }

    public string Label { get; }

    /// <summary>下发给 <c>SnsExportRequest.Format</c> 的原始值。</summary>
    public string Format { get; }
}

/// <summary>导出范围的一个下拉项（scope selected / all）。</summary>
public sealed class MomentExportScopeOption
{
    internal MomentExportScopeOption(string label, string scope, bool canSelect)
    {
        Label = label;
        Scope = scope;
        CanSelect = canSelect;
    }

    public string Label { get; }

    /// <summary>下发给 <c>SnsExportRequest.Scope</c> 的原始值。</summary>
    public string Scope { get; }

    /// <summary>左栏一个人都没勾选时，「当前筛选的人」这一项不可选 —— 选了它只会导出一份空包。</summary>
    public bool CanSelect { get; }
}

/// <summary>
/// 「导出朋友圈」弹窗的视图模型（T23）。
///
/// 与联系人导出（同步落盘）**完全不同**：朋友圈导出是**异步任务**，
/// 上游把图片 / 视频打进 ZIP，客户端只负责"创建 → 轮询 → 下载 → 另存为"（§14.8.4 路线 A）。
///
/// 三条设计约束：
/// 1. <b>轮询而不是 SSE</b>：<c>/events</c> 那个端点本项目没有代理，第一版不上流式读取（§14.8.4）。
/// 2. <b>进度字段只做"试探"</b>：<c>SnsExportResponse.Export</c> 结构未实测（§7.11），
///    认得出来就显示，认不出来就只写"导出中…"，**不猜字段名、不编造百分比**。
/// 3. <b>允许中途关窗</b>：本轮不做"取消导出任务"（后端只代理了 create / status / download，
///    没有暴露上游的 DELETE，见 §14.9）。关窗只停止"跟踪"，不算失败、不弹错。
/// </summary>
public sealed class MomentsExportViewModel : ObservableObject, IDisposable
{
    /// <summary>轮询间隔（§14.8.4：每秒一次）。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 轮询的保险丝。万一"完成"信号一直认不出来（上游改了字段），
    /// 无限轮询会把一个必然失败的等待伪装成"还在跑"；到点必须停下来告诉用户真相。
    /// </summary>
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(15);

    /// <summary>连续几次状态查询失败就认定"跟踪不下去了"（一次网络抖动不该中断等待）。</summary>
    private const int MaxConsecutivePollFailures = 3;

    /// <summary>
    /// 状态串里能判定"已完成"的取值。
    ///
    /// 刻意**不含** <c>success</c> / <c>ok</c>：实测这两个是**信封**的取值（"这次 API 调用成功"），
    /// 不代表任务完成。收进来就会在任务刚创建、ZIP 还没打包时判定成完成，
    /// 直接去下载并撞上上游的 409 Conflict。任务对象自己的完成信号是 <c>zipReady</c> 与 <c>done</c>。
    /// </summary>
    private static readonly string[] DoneStates =
    [
        "succeeded", "done", "finished", "complete", "completed",
    ];

    /// <summary>状态串里能判定"已失败"的取值。</summary>
    private static readonly string[] FailedStates =
    [
        "failed", "fail", "error", "canceled", "cancelled",
    ];

    private readonly ChatlogApiClient _api;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IReadOnlyList<string> _usernames;

    private MomentExportFormatOption _selectedFormat;
    private MomentExportScopeOption _selectedScope;
    private bool _isBusy;
    private bool _isError;
    private string _statusMessage = "尚未开始";
    private string _progressText = "";

    /// <param name="api">模块共享的客户端。本 VM **不持有**它的所有权：
    /// <see cref="Dispose"/> 只取消自己的在途请求（轮询 / 下载），不会把主界面还在用的连接池一起释放。</param>
    /// <param name="usernames">左栏**当前勾选**的发布人（勾选即生效，不要求先点过「应用」——
    /// "我勾了谁就能导谁"是这个下拉唯一说得通的语义）。空集合表示没有勾选任何人。</param>
    public MomentsExportViewModel(ChatlogApiClient api, IReadOnlyList<string> usernames)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _usernames = usernames ?? [];

        Formats =
        [
            new MomentExportFormatOption("HTML 网页（默认）", "html"),
            new MomentExportFormatOption("JSON 数据", "json"),
            new MomentExportFormatOption("纯文本 TXT", "txt"),
            new MomentExportFormatOption("Excel 表格", "excel"),
        ];

        // 范围选项的**顺序**有讲究，不能固定成「当前筛选的人」在前：
        // ComboBox 在 ItemsSource 落地时，若当时还没有选中项，会自己把 SelectedIndex 置 0，
        // 并通过 TwoWay 绑定回写 VM。而"当前筛选的人"在左栏一个人都没勾选时是**禁用项**，
        // 它排第一就会被顶成默认值 —— 用户随后既选不动它（灰的）、点导出又会被下面那条守卫拦下，
        // 表现就是"下拉框点不了 + 一直提示没选联系人"。
        // 因此永远让第一项可选：有人 → 「当前筛选的人」，没人 → 「全部」。
        var selectedOption = new MomentExportScopeOption(
            _usernames.Count > 0 ? $"当前筛选的人（{_usernames.Count} 人）" : "当前筛选的人（左栏未勾选）",
            "selected",
            _usernames.Count > 0);
        var allOption = new MomentExportScopeOption("全部", "all", true);

        var scopes = new List<MomentExportScopeOption>(2);
        if (_usernames.Count > 0)
        {
            scopes.Add(selectedOption);
            scopes.Add(allOption);
        }
        else
        {
            scopes.Add(allOption);
            scopes.Add(selectedOption);
        }

        Scopes = scopes;

        _selectedFormat = Formats[0];
        _selectedScope = Scopes[0];

        ExportCommand = new AsyncRelayCommand(ExportAsync);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    public IReadOnlyList<MomentExportFormatOption> Formats { get; }

    public IReadOnlyList<MomentExportScopeOption> Scopes { get; }

    public MomentExportFormatOption SelectedFormat
    {
        get => _selectedFormat;
        set
        {
            // ComboBox 在 ItemsSource 变动等情况下可能推一个 null 过来；
            // 本属性声明为非空（默认第一项），这里守住这个不变式。
            if (value is not null)
            {
                SetProperty(ref _selectedFormat, value);
            }
        }
    }

    public MomentExportScopeOption SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _selectedScope, value);
            }
        }
    }

    /// <summary>导出进行中时选项整块禁用：避免"改着改着，导出的是另一套参数"。</summary>
    public bool IsIdle => !_isBusy;

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

    /// <summary>进度行（"导出中… 42"）。认不出任何进度字段时为空串。</summary>
    public string ProgressText
    {
        get => _progressText;
        private set
        {
            if (SetProperty(ref _progressText, value))
            {
                OnPropertyChanged(nameof(HasProgressText));
            }
        }
    }

    public bool HasProgressText => _progressText.Length > 0;

    /// <summary>
    /// 写死在界面上的说明。必须写：格式是**微信数据源**提供的，
    /// 不说清楚用户会以为能选 Markdown（而导出产物是 ZIP）。
    /// </summary>
    public string FormatHint =>
        "导出格式由微信数据源提供（html / json / txt / excel，ZIP 打包）。"
        + "关键词只作用于时间线，不参与导出范围（导出接口没有该参数）。";

    /// <summary>关窗不取消上游任务的说明（本轮没有"取消任务"入口，见类注释第 3 条）。</summary>
    public string CloseHint =>
        "导出在微信数据源侧进行。关闭本窗口不会取消该任务，只是本窗口不再跟踪它的进度。";

    public ICommand ExportCommand { get; }

    public ICommand CloseCommand { get; }

    // ==================== 与 View 的边界 ====================

    /// <summary>
    /// 由 View 在构造时挂上：弹一次"另存为"，返回用户选定的完整路径；取消则返回 <c>null</c>。
    /// 做成委托属性而不是事件，是因为这里需要"调用并拿到返回值"。
    /// </summary>
    public Func<ExportSaveRequest, string?>? SaveFilePicker { get; set; }

    /// <summary>请求关闭对话框（用户点「关闭」）。</summary>
    public event Action? CloseRequested;

    // ==================== 主流程 ====================

    private async Task ExportAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var scope = SelectedScope;

        // "当前筛选的人"却没勾选任何人：明确提示，**不能**静默地改成"全部"——
        // 那会让用户拿到一份自己没预期的完整朋友圈导出。
        if (scope.Scope == "selected" && _usernames.Count == 0)
        {
            IsError = true;
            StatusMessage = "左栏没有勾选任何发布人。请先在左栏勾选要导的人，或把范围改为「全部」。";
            return;
        }

        IsBusy = true;
        IsError = false;
        ProgressText = "";
        StatusMessage = "正在创建导出任务……";

        try
        {
            var created = await _api.CreateSnsExportAsync(
                new SnsExportRequest
                {
                    Scope = scope.Scope,
                    Usernames = scope.Scope == "selected" ? _usernames : null,
                    Format = SelectedFormat.Format,
                    // 文件名交给上游按自己的规则生成：客户端既不知道它的命名规则，也不该替它决定。
                    FileName = null,
                    UseCache = true,
                },
                _lifetime.Token);

            var exportId = created.ExportId;
            if (string.IsNullOrWhiteSpace(exportId))
            {
                IsError = true;
                StatusMessage = "导出任务创建失败：后端没有返回任务 id，无法跟踪进度。";
                return;
            }

            if (!await WaitForCompletionAsync(exportId))
            {
                return;
            }

            StatusMessage = "导出完成，正在下载压缩包……";
            var (bytes, _, fileName) = await _api.DownloadSnsExportAsync(exportId, _lifetime.Token);

            if (bytes.Length == 0)
            {
                IsError = true;
                StatusMessage = "导出产物是空文件（0 字节），已放弃保存。";
                return;
            }

            var picker = SaveFilePicker;
            if (picker is null)
            {
                return;
            }

            var path = picker(new ExportSaveRequest
            {
                DefaultFileName = UseZipExtension(fileName),
                Filter = "ZIP 压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
                DefaultDirectory = DefaultDirectory(),
            });

            // 用户取消了「另存为」：不是失败（与联系人导出同一取舍），任务本身已经成功，
            // 所以状态文案要写清"任务完成了、只是没保存"，否则用户会以为整件事白做了。
            if (string.IsNullOrEmpty(path))
            {
                StatusMessage = "已取消保存。导出任务本身已完成，可再点「开始导出」重试保存。";
                return;
            }

            await File.WriteAllBytesAsync(path, bytes, _lifetime.Token);

            StatusMessage = $"已保存 {FormatSize(bytes.Length)} 到 {path}";
            ProgressText = "";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 用户关掉了窗口（或退出应用）：在途请求被取消是预期行为，不必再回写状态。
        }
        catch (ChatlogApiException ex)
        {
            IsError = true;
            StatusMessage = "导出失败：" + (string.IsNullOrWhiteSpace(ex.Message) ? "未知原因" : ex.Message);
        }
        catch (IOException ex)
        {
            // 写盘失败（文件被占用 / 磁盘满 / 路径过长）：给出明确文案，不静默。
            IsError = true;
            StatusMessage = $"写入文件失败：{ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            IsError = true;
            StatusMessage = $"没有权限写入该文件：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 每秒轮询一次任务状态，直到"完成"或"失败"。返回是否完成。
    ///
    /// 单次查询失败不立刻放弃（连续 <see cref="MaxConsecutivePollFailures"/> 次才算）；
    /// 超过 <see cref="PollTimeout"/> 仍未识别出完成信号则停下来说明情况，
    /// 而不是把"认不出来"一直伪装成"还在跑"。
    /// </summary>
    private async Task<bool> WaitForCompletionAsync(string exportId)
    {
        var deadline = DateTimeOffset.UtcNow + PollTimeout;
        var failures = 0;

        while (true)
        {
            await Task.Delay(PollInterval, _lifetime.Token);

            if (DateTimeOffset.UtcNow > deadline)
            {
                IsError = true;
                StatusMessage = "导出任务已等待 15 分钟仍未返回可识别的完成状态，已停止跟踪。"
                                + "可关闭本窗口，稍后重新发起导出。";
                return false;
            }

            SnsExportResponse status;
            try
            {
                status = await _api.GetSnsExportStatusAsync(exportId, _lifetime.Token);
                failures = 0;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                throw;
            }
            catch (ChatlogApiException)
            {
                if (++failures < MaxConsecutivePollFailures)
                {
                    continue;
                }

                IsError = true;
                StatusMessage = "连续多次查询导出进度失败，已停止跟踪。导出可能仍在数据源侧进行。";
                return false;
            }

            var (state, progress) = DescribeProgress(status.Export);
            ProgressText = progress;

            if (state == ExportState.Done)
            {
                return true;
            }

            if (state == ExportState.Failed)
            {
                IsError = true;
                StatusMessage = "导出任务在数据源侧失败（" + (progress.Length > 0 ? progress : "上游未给出原因") + "）。";
                return false;
            }

            // 进度字段认不出来时就只写"导出中…"（需求：不猜字段名）。
            StatusMessage = progress.Length > 0 ? $"导出中…（{progress}）" : "导出中…";
        }
    }

    /// <summary>进度字段的识别结果。</summary>
    private enum ExportState
    {
        Running,
        Done,
        Failed,
    }

    /// <summary>
    /// 从上游响应里读进度。
    ///
    /// 结构已实测（2026-09-11 直接读上游 <c>GET /api/sns/exports</c> 的 jobs[]）：
    /// <c>{exportId, status("done"|…), error, zipReady, options{…},
    /// progress{postsTotal, postsExported, mediaPrepareTotal, mediaPrepared, …}}</c>。
    /// 但仍然按"探针"写：认不出的字段一律忽略、绝不抛异常 —— 结构将来变了只会少显示一行进度，
    /// 而不是让导出功能整个坏掉。
    ///
    /// <b>两层信封是这个方法最需要小心的地方</b>：外层是后端的信封，我拿到的 <c>export</c> 是**上游的响应**，
    /// 里面还有一层信封，任务对象在最里层。信封的 <c>status:"success"</c> 只说明"这次 API 调用成功"，
    /// 不代表导出任务完成——把它当成完成，就会立刻去下载还没打包好的 ZIP（上游回 409 Conflict）。
    /// 因此先按实测的任务字段把任务对象找出来，再读它自己的状态与进度。
    /// </summary>
    private static (ExportState State, string Text) DescribeProgress(JsonElement? export)
    {
        if (export is not { } value || value.ValueKind != JsonValueKind.Object)
        {
            return (ExportState.Running, string.Empty);
        }

        var task = FindTaskObject(value, depth: 3) ?? value;

        var status = ReadFirstString(task, ["status", "state", "stage"]);
        var state = ResolveState(task, status);

        // 已完成时不再显示这一行：紧接着的状态文案就是"导出完成，正在下载…"，多一行反而乱。
        if (state == ExportState.Done)
        {
            return (state, string.Empty);
        }

        if (state == ExportState.Failed)
        {
            // 失败原因优先取 error（实测任务对象里就是这个名字）。
            return (state, ReadFirstString(task, ["error", "message", "msg", "detail"]));
        }

        // 运行中：认得出来就报"已导出 a/b 条"，认不出就空串（界面只显示"导出中…"）。
        // 刻意**不**把 status 原文（running / queued 这类内部取值）显示给用户。
        return (state, DescribeRunningProgress(task));
    }

    /// <summary>
    /// 在响应里找**任务对象**。判据只用实测字段（<c>zipReady</c>，或 <c>exportId</c> + <c>status</c>），
    /// 不依赖包装键叫什么（列表是 <c>jobs[]</c>、单任务可能是 <c>job</c> / <c>data</c> / 平铺）。
    /// 找不到返回 <c>null</c>，由调用方退回"就地读"。
    /// </summary>
    private static JsonElement? FindTaskObject(JsonElement value, int depth)
    {
        if (depth <= 0)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("zipReady", out _)
                || (value.TryGetProperty("exportId", out _) && value.TryGetProperty("status", out _)))
            {
                return value;
            }

            foreach (var property in value.EnumerateObject())
            {
                if (FindTaskObject(property.Value, depth - 1) is { } found)
                {
                    return found;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (FindTaskObject(item, depth - 1) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static ExportState ResolveState(JsonElement task, string status)
    {
        var normalized = status.ToLowerInvariant();

        // 1) 明确的失败优先：任务已经错了，再等 zipReady 也没有意义。
        if (normalized.Length > 0 && FailedStates.Contains(normalized))
        {
            return ExportState.Failed;
        }

        // 2) zipReady 是"ZIP 能不能下载"的直接信号（实测字段），**优先于 status**：
        //    status=done 只说明导出跑完了，ZIP 可能还在打包，此刻去下载正是 409 的来源。
        if (task.TryGetProperty("zipReady", out var ready))
        {
            if (ready.ValueKind == JsonValueKind.True)
            {
                return ExportState.Done;
            }

            if (ready.ValueKind == JsonValueKind.False)
            {
                return ExportState.Running;
            }
        }

        // 3) 没有 zipReady 字段时，才退回状态串 / 布尔 / 数值进度。
        if (normalized.Length > 0 && DoneStates.Contains(normalized))
        {
            return ExportState.Done;
        }

        if (ReadBool(task, "finished") || ReadBool(task, "done"))
        {
            return ExportState.Done;
        }

        return ReadFirstNumber(task, ["progress", "percent", "percentage"]) is >= 100
            ? ExportState.Done
            : ExportState.Running;
    }

    /// <summary>
    /// 运行中的进度文案。实测 <c>progress</c> 是一个对象而不是数字，
    /// 先看"帖子"维度（postsExported / postsTotal），再看"图片准备"维度，都没有就返回空串。
    /// </summary>
    private static string DescribeRunningProgress(JsonElement value)
    {
        if (!value.TryGetProperty("progress", out var progress) || progress.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var postsTotal = ReadFirstNumber(progress, ["postsTotal"]);
        var postsExported = ReadFirstNumber(progress, ["postsExported"]);
        if (postsTotal is > 0 && postsExported is not null)
        {
            return $"已导出 {postsExported:0}/{postsTotal:0} 条";
        }

        var mediaTotal = ReadFirstNumber(progress, ["mediaPrepareTotal"]);
        var mediaPrepared = ReadFirstNumber(progress, ["mediaPrepared"]);
        return mediaTotal is > 0 && mediaPrepared is not null
            ? $"已准备图片 {mediaPrepared:0}/{mediaTotal:0}"
            : string.Empty;
    }

    private static string ReadFirstString(JsonElement item, string[] keys)
    {
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

    private static double? ReadFirstNumber(JsonElement item, string[] keys)
    {
        foreach (var key in keys)
        {
            if (item.TryGetProperty(key, out var property)
                && property.ValueKind == JsonValueKind.Number
                && property.TryGetDouble(out var number))
            {
                return number;
            }
        }

        return null;
    }

    private static bool ReadBool(JsonElement item, string key)
        => item.TryGetProperty(key, out var property)
           && property.ValueKind is JsonValueKind.True or JsonValueKind.False
           && property.GetBoolean();

    // ==================== 落盘细节 ====================

    /// <summary>
    /// 产物是 ZIP，但后端给的建议名不一定带后缀（或带的是别的），
    /// 这里统一补成 <c>.zip</c>，否则会存出一个双击打不开的文件。
    /// </summary>
    private static string UseZipExtension(string fileName)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "朋友圈导出" : Path.GetFileName(fileName.Trim());
        return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? name : name + ".zip";
    }

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / 1024.0 / 1024.0:0.0} MB"
            : $"{Math.Max(1, bytes / 1024)} KB";

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

    public void Dispose()
    {
        // 先取消在途请求（轮询的 Task.Delay、下载），再释放令牌源。
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
