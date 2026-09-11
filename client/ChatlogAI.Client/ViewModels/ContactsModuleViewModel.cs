using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.Services;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// 「联系人」模块（T22）的视图模型：列表（多选 / 全选 / 反选）+ 一键导出。
///
/// 与 <see cref="ScheduledSendViewModel"/> 同一挂法：由 <c>MainViewModel.Contacts</c> 持有，
/// 模块内用 <c>{Binding Contacts.Xxx}</c> 绑定，模块 View 自己不设 DataContext。
///
/// 三条必须守住的设计（都是这类多选列表最容易做错的地方）：
/// 1. <b>已选集合按 username 记账、与"当前可见列表"完全解耦</b>。切分类 / 改搜索词 / 刷新列表
///    都不清空它，于是"选一批好友 → 切到群聊再选几个 → 一起导出"才成立；「全选」是**并入**
///    当前筛选结果而非替换整个选集。
/// 2. <b>"只加载一次"的标记只在成功之后置位</b>。首次进来碰到 503（上游没起来）时必须保持未加载，
///    否则用户点「刷新」也永远救不回来。
/// 3. <b>失败不清空列表</b>。一次网络抖动不该让用户看到"联系人全没了"。
/// </summary>
public sealed class ContactsModuleViewModel : ObservableObject, IDisposable
{
    private readonly ChatlogApiClient _api;
    private readonly CancellationTokenSource _lifetime = new();

    // 已选集合：username 集合管成员、列表管导出顺序（= 勾选顺序）。
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly List<string> _selectedOrder = [];

    // username → 该联系人最新的一行。**只增不减**：已勾选但已不在当前筛选结果里的联系人，
    // 导出时只能靠它拿到 ContactSummary（这就是"跨筛选保留已选"的落点）。
    private readonly Dictionary<string, ContactItemViewModel> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource> _avatarCache = new(StringComparer.Ordinal);

    private CancellationTokenSource? _avatarRequest;

    // 分类勾选的初值必须与后端默认一致：好友 / 群聊 / 公众号默认开，已删除好友 / 黑名单默认关。
    // 界面上无论勾没勾都显式下发（见 BuildQuery），初值只是"第一次进来看到的就是默认那一批"。
    private bool _includeFriends = true;
    private bool _includeGroups = true;
    private bool _includeOfficials = true;
    private bool _includeFormerFriends;
    private bool _includeBlocked;

    private bool _loadedOnce;
    private bool _loading;
    private bool _isLoading;
    private bool _hasError;
    private string _errorMessage = "";
    private bool _hasSourceFallback;
    private string _sourceFallbackText = "";
    private bool _hasStatus;
    private string _statusMessage = "";
    private string _placeholder = "正在读取通讯录……";
    private string _keyword = "";
    private int _total;
    private ExportFormatOption _selectedFormat;
    private ContactItemViewModel? _detailContact;

    public ContactsModuleViewModel(ChatlogApiClient api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));

        // 导出格式下拉项：复用导出模块的 ExportFormatOption（选项对象 + SelectedItem 绑定），
        // 映射关系留在 C# 里，XAML 不再写一遍"字符串 ↔ 枚举"的对照表。
        Formats =
        [
            new ExportFormatOption("Markdown 文档", ExportFormat.Markdown),
            new ExportFormatOption("HTML 网页", ExportFormat.Html),
            new ExportFormatOption("JSON 数据", ExportFormat.Json),
            new ExportFormatOption("CSV 表格", ExportFormat.Csv),
        ];
        _selectedFormat = Formats[0];

        ApplyCommand = new AsyncRelayCommand(LoadAsync);
        SelectAllVisibleCommand = new RelayCommand(SelectAllVisible);
        InvertVisibleSelectionCommand = new RelayCommand(InvertVisibleSelection);
        ExportCommand = new AsyncRelayCommand(ExportAsync);
    }

    public IReadOnlyList<ExportFormatOption> Formats { get; }

    // ==================== 与 View 的边界 ====================

    /// <summary>
    /// 需要弹一个提示框（导出前的空选集提醒、落盘失败）。VM 不引用 <c>MessageBox</c>——
    /// 那是 View 的职责，与 <c>MainViewModel.AlertRequested</c> 是同一条 MVVM 边界。
    /// 本模块的提示由模块 View 直接订阅，不经过主 VM 转发。
    /// </summary>
    public event Action<string>? AlertRequested;

    /// <summary>
    /// 由 View 挂上：弹一次系统「另存为」，返回用户选定的完整路径；取消则返回 <c>null</c>。
    /// 与导出对话框的同名属性同一理由：需要"调用并拿到返回值"。
    /// </summary>
    public Func<ExportSaveRequest, string?>? SaveFilePicker { get; set; }

    // ==================== 筛选（改动不自动请求） ====================

    /*
     * 下面五个属性刻意只 SetProperty、不发请求：搜索是**服务端过滤**，若每敲一个字符、
     * 每点一个勾选框就打一次后端，495 条 × 每次全量返回的开销白花。
     * 由「应用」/ 回车显式触发一次（见 ContactsModuleView 的 OnKeywordKeyDown）。
     */

    public bool IncludeFriends
    {
        get => _includeFriends;
        set => SetProperty(ref _includeFriends, value);
    }

    public bool IncludeGroups
    {
        get => _includeGroups;
        set => SetProperty(ref _includeGroups, value);
    }

    public bool IncludeOfficials
    {
        get => _includeOfficials;
        set => SetProperty(ref _includeOfficials, value);
    }

    public bool IncludeFormerFriends
    {
        get => _includeFormerFriends;
        set => SetProperty(ref _includeFormerFriends, value);
    }

    public bool IncludeBlocked
    {
        get => _includeBlocked;
        set => SetProperty(ref _includeBlocked, value);
    }

    public string Keyword
    {
        get => _keyword;
        set => SetProperty(ref _keyword, value);
    }

    // ==================== 列表 ====================

    public ObservableCollection<ContactItemViewModel> Contacts { get; } = [];

    public bool HasContacts => Contacts.Count > 0;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshPlaceholder();
            }
        }
    }

    /// <summary>列表为空 / 加载中时显示在列表位置的说明文案（有列表时为空串）。</summary>
    public string Placeholder
    {
        get => _placeholder;
        private set => SetProperty(ref _placeholder, value);
    }

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

    /// <summary>后端回报 <c>sourceFallback</c>：实时库连不上，当前看的是解密库快照（§14.8.1 末段同一信号）。</summary>
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

    /// <summary>一次性反馈（导出成功）：用户必须知道文件写到哪儿了，否则无从判断成没成。</summary>
    public bool HasStatus
    {
        get => _hasStatus;
        private set => SetProperty(ref _hasStatus, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public int SelectedCount => _selected.Count;

    public int CurrentCount => Contacts.Count;

    public int TotalCount => _total;

    /// <summary>「已选 N / 当前 M / 共 T」三个数各有含义：N 是跨筛选的选集，M 是眼前这批，T 是后端口径的全量。</summary>
    public string CountText => $"已选 {_selected.Count} / 当前 {Contacts.Count} / 共 {_total}";

    // ==================== 详情区（跟勾选选集走） ====================

    /// <summary>恰好勾选 1 个时才有值；其余情况为 <c>null</c>（详情区显示概览或提示）。</summary>
    public ContactItemViewModel? DetailContact
    {
        get => _detailContact;
        private set
        {
            if (!SetProperty(ref _detailContact, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasDetail));

            if (value is not null)
            {
                // 头像只在这里取：495 行逐条请求头像是无谓压力，只有真正要看的那一个才值得下载。
                _ = LoadAvatarAsync(value);
            }
        }
    }

    public bool HasDetail => _detailContact is not null;

    public bool HasMultiSelection => _selected.Count >= 2;

    public bool HasNoSelection => _selected.Count == 0;

    public string MultiSelectionText => $"已选 {_selected.Count} 位联系人";

    // ==================== 命令 ====================

    /// <summary>「应用」与「刷新」共用：两者都是"用当前勾选/关键词重查一次"，只是入口文案不同。</summary>
    public ICommand ApplyCommand { get; }

    public ICommand SelectAllVisibleCommand { get; }

    public ICommand InvertVisibleSelectionCommand { get; }

    public ICommand ExportCommand { get; }

    public ExportFormatOption SelectedFormat
    {
        get => _selectedFormat;
        set => SetProperty(ref _selectedFormat, value);
    }

    // ==================== 加载 ====================

    /// <summary>
    /// 首次进入本模块时调用（<c>ContactsModuleView</c> 监听 IsVisibleChanged）。
    ///
    /// 为什么不在 MainViewModel 构造时预取：通讯录一次返回全部（实测 495 条，重度用户上千），
    /// 而用户可能根本不进这个模块。
    ///
    /// 关键点：<c>_loadedOnce</c> **只在成功之后**置位。首次进来碰到 503 时必须保持未加载，
    /// 用户点「刷新」才能重试成功 —— "失败把开关一并锁死"是这里最容易写错的一处。
    /// </summary>
    public Task EnsureLoadedAsync() => _loadedOnce ? Task.CompletedTask : LoadAsync();

    /// <summary>强制重查一次（「应用」/「刷新」/ 回车）。</summary>
    private async Task LoadAsync()
    {
        // 防重入：首次可见与用户立刻点「应用」可能撞在一起，只让第一个跑。
        if (_loading)
        {
            return;
        }

        _loading = true;
        IsLoading = true;
        HasError = false;
        RefreshPlaceholder();

        try
        {
            var response = await _api.GetContactsAsync(BuildQuery(), _lifetime.Token);
            ApplyResponse(response);
            _loadedOnce = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 主窗口正在退出：不再回写界面状态。
        }
        catch (ChatlogApiException ex)
        {
            // 后端 detail 已是面向用户的中文文案（如上游没起来时的 503），原样展示即可。
            // 这里刻意不动 Contacts：失败保持列表现状，宁可显示旧数据也不清空。
            ErrorMessage = string.IsNullOrWhiteSpace(ex.Message) ? "联系人读取失败" : ex.Message;
            HasError = true;
        }
        finally
        {
            _loading = false;
            IsLoading = false;
            RefreshPlaceholder();
        }
    }

    private void ApplyResponse(ContactListResponse response)
    {
        _total = response.Total;
        OnPropertyChanged(nameof(TotalCount));

        // 列表整体替换：换了筛选条件后旧行不可能复用，先摘掉订阅，
        // 否则被丢弃的行照样能把 VM 拉回来再算一次。
        foreach (var item in Contacts)
        {
            item.PropertyChanged -= OnContactPropertyChanged;
        }

        Contacts.Clear();

        foreach (var contact in response.Contacts)
        {
            // 没有 username 的行既进不了选集也导不出去，直接不收（后端理论上不会给）。
            if (string.IsNullOrWhiteSpace(contact.Username))
            {
                continue;
            }

            // 勾选状态按 username 回填：这就是"切筛选、刷新都不清空已选"的落点。
            var item = new ContactItemViewModel(contact, _selected.Contains(contact.Username));
            item.PropertyChanged += OnContactPropertyChanged;

            if (_avatarCache.TryGetValue(contact.Username, out var cached))
            {
                item.AvatarImage = cached;
            }

            Contacts.Add(item);
            _known[contact.Username] = item;
        }

        OnPropertyChanged(nameof(HasContacts));

        HasSourceFallback = response.SourceFallback;
        SourceFallbackText = response.SourceFallback ? BuildFallbackText(response) : string.Empty;

        RefreshCounts();
        RefreshDetail();
    }

    private ContactQuery BuildQuery()
    {
        var keyword = _keyword.Trim();

        return new ContactQuery
        {
            // 五个勾选框显式下发七个开关（引用字段而不是同名属性，避免读成自赋值）。
            IncludeFriends = _includeFriends,
            IncludeGroups = _includeGroups,
            // 公众号在微信里分订阅号 / 服务号（上游还多一个企业号），一个勾选框同时控制三个开关：
            // 否则用户勾了"公众号"，却可能因为上游另外两个开关的默认值而一条都看不到。
            IncludeOfficials = _includeOfficials,
            IncludeOfficialSubscriptions = _includeOfficials,
            IncludeOfficialServices = _includeOfficials,
            IncludeFormerFriends = _includeFormerFriends,
            IncludeBlocked = _includeBlocked,
            // 空关键词不下发（由客户端 BuildQuery 处理成 null 即省略参数）。
            Keyword = keyword.Length == 0 ? null : keyword,
        };
    }

    private static string BuildFallbackText(ContactListResponse response)
    {
        var text = "实时库暂不可用，当前为解密库快照";
        if (response.SourceFallbackRetryAfterSeconds > 0)
        {
            text += $"（建议 {response.SourceFallbackRetryAfterSeconds} 秒后重试）";
        }

        return text;
    }

    // ==================== 全选 / 反选 / 计数 ====================

    private void SelectAllVisible()
    {
        // 「全选」= 把**眼前这一批（当前筛选结果）并入**已选集合。
        // 不是替换整个选集（否则切一次筛选就丢掉之前的勾选），更不是"整个通讯录"——
        // 语义若反过来，用户搜了"张"再点全选就会导出一份自己没预期的名单（§14.7.3）。
        foreach (var item in Contacts)
        {
            item.IsSelected = true;
        }
    }

    private void InvertVisibleSelection()
    {
        // 反选只作用于眼前这一批；当前筛选之外已经勾好的那些本次不动。
        foreach (var item in Contacts)
        {
            item.IsSelected = !item.IsSelected;
        }
    }

    private void OnContactPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ContactItemViewModel.IsSelected) || sender is not ContactItemViewModel item)
        {
            return;
        }

        if (item.IsSelected)
        {
            _known[item.Username] = item;
            if (_selected.Add(item.Username))
            {
                _selectedOrder.Add(item.Username);
            }
        }
        else
        {
            _selected.Remove(item.Username);
            _selectedOrder.Remove(item.Username);
        }

        RefreshCounts();
        RefreshDetail();
    }

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CurrentCount));
        OnPropertyChanged(nameof(CountText));
    }

    private void RefreshDetail()
    {
        // 详情区只跟勾选选集走：勾 1 个看详情（此时才取头像），勾 ≥2 个给概览，一个没勾给提示。
        DetailContact = _selectedOrder.Count == 1 && _known.TryGetValue(_selectedOrder[0], out var single)
            ? single
            : null;

        OnPropertyChanged(nameof(HasMultiSelection));
        OnPropertyChanged(nameof(HasNoSelection));
        OnPropertyChanged(nameof(MultiSelectionText));
    }

    private void RefreshPlaceholder()
    {
        if (_isLoading)
        {
            Placeholder = "正在读取通讯录……";
            return;
        }

        // 出错时列表若为空，说明的是错误横幅，不再叠一句空态文案。
        Placeholder = _hasError || Contacts.Count > 0 ? string.Empty : "没有符合条件的联系人";
    }

    // ==================== 详情头像（懒加载 + 本地缓存） ====================

    private async Task LoadAvatarAsync(ContactItemViewModel item)
    {
        if (string.IsNullOrEmpty(item.Contact.Avatar))
        {
            return;
        }

        if (_avatarCache.TryGetValue(item.Username, out var cached))
        {
            // 来回切换详情对象时不重复请求：缓存命中直接贴回去。
            item.AvatarImage = cached;
            return;
        }

        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var previous = _avatarRequest;
        _avatarRequest = request;

        // 取消上一次在途请求：后到的旧响应不该盖上"用户现在已经选中的那个人"。
        // 被取消的那次自己负责 Dispose（见下面的 finally），这里不替它释放。
        previous?.Cancel();

        try
        {
            var (bytes, _) = await _api.DownloadContactAvatarAsync(item.Contact.Avatar, request.Token);
            if (CreateImage(bytes) is not { } image)
            {
                return;
            }

            _avatarCache[item.Username] = image;

            // 回填给"当前列表里的那一行"：详情对象可能已因刷新换成了新实例。
            var target = _known.TryGetValue(item.Username, out var latest) ? latest : item;
            target.AvatarImage = image;
        }
        catch (OperationCanceledException)
        {
            // 被下一次选择取消，或主窗口退出：都是预期行为。
        }
        catch (ChatlogApiException)
        {
            // 头像只是详情区的锦上添花：失败就保持首字母头像，不弹提示打扰用户。
        }
        finally
        {
            if (ReferenceEquals(_avatarRequest, request))
            {
                _avatarRequest = null;
            }

            request.Dispose();
        }
    }

    private static ImageSource? CreateImage(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            // OnLoad：立刻解码进内存，之后才能关掉流（BitmapImage 默认延迟解码，流一关就成空图）。
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            // 冻结后跨线程可用，也免去每次重绘的校验开销。
            image.Freeze();
            return image;
        }
        catch (NotSupportedException)
        {
            // 上游返回的不是图片（错误页 / 空体）：退回首字母头像，不让一张坏图炸掉整个详情区。
            return null;
        }
    }

    // ==================== 导出 ====================

    private async Task ExportAsync()
    {
        // 空选集导出必须明确提示，**不能**静默地导出全部：
        // 那会让用户拿到一份自己没预期的完整通讯录（"全选"按钮才是表达那个意图的地方）。
        if (_selectedOrder.Count == 0)
        {
            AlertRequested?.Invoke("请先选择要导出的联系人");
            return;
        }

        var picker = SaveFilePicker;
        if (picker is null)
        {
            return;
        }

        var format = _selectedFormat.Format;
        var now = DateTimeOffset.Now;

        var path = picker(new ExportSaveRequest
        {
            DefaultFileName = ContactExporter.BuildDefaultFileName(format, now),
            Filter = ContactExporter.BuildFileFilter(format),
            DefaultDirectory = DefaultDirectory(),
        });

        // 用户取消了「另存为」：这不是失败，静默返回。
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        // 导出顺序 = 勾选顺序；联系人取自 _known，因此当前筛选里看不见但已勾选的那些也在内。
        var contacts = _selectedOrder
            .Where(username => _known.ContainsKey(username))
            .Select(username => _known[username].Contact)
            .ToList();

        try
        {
            HasStatus = false;
            StatusMessage = string.Empty;

            var document = ContactExporter.Format(format, contacts, now);

            // 只有 CSV 需要 BOM（Excel 中文乱码的解药），判据由格式化层给出，这里不重复判断格式。
            await File.WriteAllTextAsync(path, document.Text, new UTF8Encoding(document.WriteBom));

            StatusMessage = $"已导出 {contacts.Count} 位联系人到 {path}";
            HasStatus = true;
        }
        catch (IOException ex)
        {
            // 写盘失败（文件被占用 / 磁盘满 / 路径过长）：给出明确文案，不静默。
            AlertRequested?.Invoke($"写入文件失败：{ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            AlertRequested?.Invoke($"没有权限写入该文件：{ex.Message}");
        }

        // 其余异常继续往外抛给 App 的全局兜底：把编码错误静默成一句提示，
        // 会让真正的问题永远查不出来（与 ExportViewModel 同一取舍）。
    }

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

    // ==================== 释放 ====================

    /// <summary>
    /// 释放令牌源（在途的通讯录请求与头像请求会被取消）。
    ///
    /// 注意：主 VM 的 <c>Dispose</c> 目前没有调用到这里（本模块是 T22 新增的，主 VM 只加了一个属性），
    /// 所以进程退出前这里多半跑不到；保留实现是为了让"谁来释放"这件事不欠账，
    /// 由主窗口决定是否在 <c>MainViewModel.Dispose</c> 里补一行。
    /// </summary>
    public void Dispose()
    {
        _avatarRequest?.Cancel();
        _avatarRequest = null;

        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
