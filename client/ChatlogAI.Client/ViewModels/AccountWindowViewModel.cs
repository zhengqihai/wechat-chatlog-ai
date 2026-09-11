using System.Collections.ObjectModel;
using System.Windows.Input;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.Services;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// 「微信账号」窗口的视图模型（T19-B，规格见 docs/DOTNET_CLIENT_PLAN.md §7.17）。
///
/// 这个窗口要解决的不是"切换账号"本身，而是切换带来的**发错人**风险：
/// 程序能改"数据源读哪个账号"，却无法确认"微信客户端现在登录的是谁"，
/// 所以切换后一定进入待确认状态（发送被闸门拦住），直到用户显式点「我已切换」。
///
/// 三条由此推导出来的约束：
/// 1. <b>待确认区优先显示</b>：只要 pending，窗口第一眼就应该是"要做什么"，而不是账号列表。
/// 2. <b>未就绪不允许点切换</b>：后端也会 400，但界面不该让用户白点一次再失败。
/// 3. <b>切换必须二次确认</b>，且确认文案要讲清"数据源会指向谁 / 微信也要切 / 期间发送暂停"。
///
/// 生命周期跟随窗口（每次打开新建一份），<see cref="Dispose"/> 负责停掉授权轮询。
/// </summary>
public sealed class AccountWindowViewModel : ObservableObject, IDisposable
{
    /// <summary>授权轮询周期。与 §7.17 的建议一致：3 秒，就绪即停。</summary>
    private static readonly TimeSpan AuthorizePollInterval = TimeSpan.FromSeconds(3);

    private readonly ChatlogApiClient _api;
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>当前那条授权轮询。换账号重新授权时先取消它，避免两条循环同时改提示文案。</summary>
    private CancellationTokenSource? _authorizePoll;

    private bool _loading;
    private string _placeholder = "正在读取微信账号……";
    private string _statusMessage = "";

    private bool _hasPending;
    private string _pendingAccount = "";

    private bool _isAuthorizing;
    private string _authorizeStatus = "";

    private bool _confirmed;

    public AccountWindowViewModel(ChatlogApiClient api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ConfirmPendingCommand = new AsyncRelayCommand(ConfirmPendingAsync);
        SwitchCommand = new AsyncRelayCommand(SwitchAsync);
        OpenAuthorizeCommand = new AsyncRelayCommand(OpenAuthorizeAsync);
    }

    // ==================== 与 View 的边界（VM 不引用 MessageBox / Process / Close） ====================

    /// <summary>需要弹一个提示框（失败原因一律用后端给的 detail，不吞）。</summary>
    public event Action<string>? AlertRequested;

    /// <summary>需要一次"是 / 否"确认；切换账号的二次确认走它。</summary>
    public event Func<string, bool>? ConfirmRequested;

    /// <summary>请求用系统默认浏览器打开授权页；"怎么打开"是 View 的事。</summary>
    public event Action<string>? AuthorizeRequested;

    /// <summary>该关窗了（目前只有「我已切换」成功后）。</summary>
    public event Action? CloseRequested;

    // ==================== 账号列表 ====================

    public ObservableCollection<AccountRowViewModel> Accounts { get; } = [];

    /// <summary>列表为空（或读取失败）时显示占位文案——两者共用一格，与模型设置窗口同一处理。</summary>
    public bool ShowPlaceholder => Accounts.Count == 0;

    public string Placeholder
    {
        get => _placeholder;
        private set => SetProperty(ref _placeholder, value);
    }

    /// <summary>最近一次操作的结果（切换成功后放后端返回的 message）。空串表示没有可说的。</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => _statusMessage.Length > 0;

    // ==================== 待确认区 ====================

    public bool HasPending => _hasPending;

    public string PendingAccount => _pendingAccount;

    /// <summary>
    /// 待确认区正文。刻意自己拼而不是直接用后端 message：后端那句没有"在此之前自动发送已暂停"，
    /// 而这句话正是用户理解"机器人为什么不回复"的关键（§7.17 闸门语义）。
    /// </summary>
    public string PendingHint
    {
        get
        {
            var target = _pendingAccount.Length > 0 ? $"「{_pendingAccount}」" : "新账号";
            return $"数据源已切到{target}，请把微信客户端的登录账号切到该账号，然后点「我已切换」。"
                   + "在此之前自动发送已暂停。";
        }
    }

    // ==================== 授权（打开授权页 + 轮询密钥是否就绪） ====================

    /// <summary>是否正在等待授权（轮询中）。</summary>
    public bool IsAuthorizing
    {
        get => _isAuthorizing;
        private set => SetProperty(ref _isAuthorizing, value);
    }

    public string AuthorizeStatus
    {
        get => _authorizeStatus;
        private set
        {
            if (SetProperty(ref _authorizeStatus, value))
            {
                OnPropertyChanged(nameof(HasAuthorizeStatus));
            }
        }
    }

    public bool HasAuthorizeStatus => _authorizeStatus.Length > 0;

    // ==================== 命令 ====================

    public ICommand RefreshCommand { get; }
    public ICommand ConfirmPendingCommand { get; }
    public ICommand SwitchCommand { get; }
    public ICommand OpenAuthorizeCommand { get; }

    /// <summary>用户是否完成了「我已切换」。调用方据此决定要不要清空重载（同 AutoReplyWindow.Saved）。</summary>
    public bool Confirmed => _confirmed;

    /// <summary>窗口打开时由 View 调一次；不抛异常，失败转成占位文案。</summary>
    public Task InitializeAsync() => RefreshAsync();

    /// <summary>重新拉账号清单与待确认状态。</summary>
    public async Task RefreshAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        try
        {
            var data = await _api.GetWechatAccountsAsync(_lifetime.Token);

            Accounts.Clear();
            foreach (var info in data.Accounts)
            {
                Accounts.Add(new AccountRowViewModel(info));
            }

            Placeholder = Accounts.Count == 0 ? "没有可用的微信账号（数据源可能还没起来）" : "";
            OnPropertyChanged(nameof(ShowPlaceholder));

            ApplyPending(data.PendingSwitch);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 关窗取消，不改界面。
        }
        catch (ChatlogApiException ex)
        {
            // 上游不可达时这里是 503 + detail：既不静默，也不清空已经拿到的列表。
            Placeholder = ex.Message;
            OnPropertyChanged(nameof(ShowPlaceholder));
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// 「我已切换」：确认微信客户端已经登录新账号，放开闸门。
    /// 这是**唯一**调用 confirm 的地方（误调会清掉待确认状态，正是安全事故本身）。
    /// </summary>
    private async Task ConfirmPendingAsync()
    {
        if (!_hasPending)
        {
            return;
        }

        try
        {
            var response = await _api.ConfirmAccountSwitchAsync(_lifetime.Token);

            _confirmed = true;
            StatusMessage = response.Message;
            ApplyPending(null);
            CloseRequested?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // 关窗导致的中断不算失败。
        }
        catch (ChatlogApiException ex)
        {
            // 不能静默：失败了发送仍然是暂停的，用户必须知道。
            AlertRequested?.Invoke("确认失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 「切换到此账号」。参数是 <see cref="AccountRowViewModel"/>。
    /// 已就绪且不是当前账号才会走到这里（按钮的 IsEnabled 已挡一层，这里再兜一层）。
    /// </summary>
    private async Task SwitchAsync(object? parameter)
    {
        if (parameter is not AccountRowViewModel target || !target.CanSwitch)
        {
            return;
        }

        // 二次确认必须讲清三件事：漏掉任何一件，用户都可能在"会发错人"的窗口里继续操作。
        var confirmed = ConfirmRequested?.Invoke(
            $"确定把数据源切换到「{target.DisplayName}」吗？\n\n"
            + "① 数据源之后会读取该账号的聊天记录；\n"
            + "② 你还需要在微信客户端里把登录账号也切到它；\n"
            + "③ 在你点「我已切换」之前，自动回复与定时发送都会暂停。");
        if (confirmed != true)
        {
            return;
        }

        try
        {
            var response = await _api.SwitchAccountAsync(target.Account, _lifetime.Token);

            StatusMessage = response.Message;
            // 重新拉一次而不是本地改状态：新的 isActive 与 pendingSwitch 都在响应里，
            // 本地猜一半（比如自己把 IsActive 换过去）迟早与后端不一致。
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            // 关窗导致的中断不算失败。
        }
        catch (ChatlogApiException ex)
        {
            // 400 / 409 / 503 的 detail 一律原样转达：后端文案比任何本地拼的都准。
            AlertRequested?.Invoke("切换失败：" + ex.Message);
        }
    }

    /// <summary>「打开授权页」：拿地址（View 去开浏览器）+ 开始轮询密钥是否就绪。</summary>
    private async Task OpenAuthorizeAsync(object? parameter)
    {
        if (parameter is not AccountRowViewModel target)
        {
            return;
        }

        try
        {
            var response = await _api.ResolveWechatAccountAsync(target.Account, _lifetime.Token);
            if (string.IsNullOrWhiteSpace(response.AuthorizeUrl))
            {
                AlertRequested?.Invoke("后端没有返回授权页地址，请先确认数据源地址（wechat_api）已配置。");
                return;
            }

            AuthorizeStatus = $"已打开授权页：请在浏览器里完成「{target.DisplayName}」的密钥解析";
            AuthorizeRequested?.Invoke(response.AuthorizeUrl);
            StartAuthorizePolling(target.Account);
        }
        catch (OperationCanceledException)
        {
            // 关窗导致的中断不算失败。
        }
        catch (ChatlogApiException ex)
        {
            AlertRequested?.Invoke("打开授权页失败：" + ex.Message);
        }
    }

    private void StartAuthorizePolling(string account)
    {
        // 前一条轮询必须先停：换账号重新授权时，两条循环会同时改同一处提示文案。
        _authorizePoll?.Cancel();
        _authorizePoll?.Dispose();
        _authorizePoll = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);

        IsAuthorizing = true;
        _ = PollAuthorizeAsync(account, _authorizePoll.Token);
    }

    private async Task PollAuthorizeAsync(string account, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(AuthorizePollInterval, token);

                var status = await _api.GetAccountResolveStatusAsync(account, token);
                AuthorizeStatus = status.Message;

                if (!status.KeyReady)
                {
                    continue;
                }

                AuthorizeStatus = "密钥已就绪，可以切换了";
                Accounts.FirstOrDefault(item => item.Account == account)
                        ?.UpdateResolveState(status.KeyReady, status.Switchable);
                break; // 就绪后再轮询也不会变；停掉，省掉一整条后台循环
            }
        }
        catch (OperationCanceledException)
        {
            // 停轮询（就绪 / 换账号 / 关窗）都会走到这里，属正常路径。
        }
        catch (ChatlogApiException ex)
        {
            AuthorizeStatus = "授权状态查询失败：" + ex.Message;
        }
        finally
        {
            // 只有"这一条"还是当前轮询时才复位：换账号会另起一条，旧的收尾不该盖掉新的状态。
            if (_authorizePoll is { } current && current.Token == token)
            {
                IsAuthorizing = false;
            }
        }
    }

    private void ApplyPending(PendingAccountSwitch? pending)
    {
        _hasPending = pending?.Pending == true;
        _pendingAccount = _hasPending ? pending!.Account : "";

        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(PendingAccount));
        OnPropertyChanged(nameof(PendingHint));
    }

    /// <summary>窗口关闭时必须调：停轮询 + 取消在途请求。</summary>
    public void Dispose()
    {
        _authorizePoll?.Cancel();
        _authorizePoll?.Dispose();
        _authorizePoll = null;

        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

/// <summary>
/// 账号列表的一行（只读投影）。
///
/// 为什么状态只能由 <see cref="UpdateResolveState"/> 从后端响应更新、不提供公开 setter：
/// 行的"可切换"是安全判据，一旦能被界面就地改，它就从"后端说的"退化成"界面以为的"。
/// </summary>
public sealed class AccountRowViewModel : ObservableObject
{
    private bool _keyReady;
    private bool _switchable;
    private bool _isActive;

    public AccountRowViewModel(WechatAccountInfo info)
    {
        Account = info.Account;
        DisplayName = info.DisplayName;
        HasDecryptedDbs = info.HasDecryptedDbs;
        _keyReady = info.KeyReady;
        _switchable = info.Switchable;
        _isActive = info.IsActive;
    }

    public string Account { get; }

    /// <summary>显示名（name 为空时回退成 account，见 <see cref="WechatAccountInfo.DisplayName"/>）。</summary>
    public string DisplayName { get; }

    public bool HasDecryptedDbs { get; }

    public bool IsActive => _isActive;

    public bool KeyReady => _keyReady;

    public bool Switchable => _switchable;

    public string KeyBadge => _keyReady ? "密钥已就绪" : "密钥未就绪";

    public string SwitchableBadge => _switchable ? "可切换" : "不可切换";

    /// <summary>
    /// 可切换 = 已就绪 且 上游允许 且 不是当前账号。
    /// 未就绪的账号切过去也只是一片空数据，所以界面上直接禁用（后端同样会 400，但那是兜底）。
    /// </summary>
    public bool CanSwitch => _keyReady && _switchable && !_isActive;

    /// <summary>未就绪的账号才提供「打开授权页」。</summary>
    public bool CanResolve => !_keyReady;

    /// <summary>授权轮询发现密钥就绪后，把这一行刷成"可切换"，用户不必再点一次刷新。</summary>
    public void UpdateResolveState(bool keyReady, bool switchable)
    {
        _keyReady = keyReady;
        _switchable = switchable;

        OnPropertyChanged(nameof(KeyReady));
        OnPropertyChanged(nameof(Switchable));
        OnPropertyChanged(nameof(KeyBadge));
        OnPropertyChanged(nameof(SwitchableBadge));
        OnPropertyChanged(nameof(CanSwitch));
        OnPropertyChanged(nameof(CanResolve));
    }
}
