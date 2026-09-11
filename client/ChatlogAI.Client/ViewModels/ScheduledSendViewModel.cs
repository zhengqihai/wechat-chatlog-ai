using System.Collections.ObjectModel;
using System.Windows.Input;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.Services;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// 左侧窄栏「定时发送」面板的视图模型。
///
/// 与 <see cref="AutoReplyViewModel"/> / <see cref="ModelSettingsViewModel"/> 的关键差别：
/// 那两个是**弹窗** VM（打开时新建、关闭即废弃），本 VM 与主界面同生命周期、长期存在于左栏。
///
/// 三条必须守住的设计：
/// 1. <b>配置的写入口只有本 VM 一个</b>。弹窗（<c>ScheduledSendWindow</c>）只负责"编一条任务"，
///    编完把整条任务交回这里；由这里把全部任务整份提交。否则两个地方都去改 <c>tasks</c> 数组，
///    后提交的那次会把先提交的覆盖掉（服务端是**整份替换**语义）。
/// 2. <b>保存用信号量排队而不是"忙就丢弃"</b>。勾选行会触发保存，用户连点两下时若直接丢弃，
///    界面显示已改、服务端仍是旧值，且不会有任何提示。
/// 3. <b>读取失败不覆盖已有列表</b>。否则一次网络抖动会让用户看到"任务全没了"，误以为被清空。
/// </summary>
public sealed class ScheduledSendViewModel : ObservableObject, IDisposable
{
    private readonly ChatlogApiClient _api;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private bool _available = true;
    private bool _enabled;
    private bool _configLoaded;
    private bool _loading;
    private string _statusText = "定时发送：未启用";
    private string _placeholder = "正在读取定时发送配置";

    public ScheduledSendViewModel(ChatlogApiClient api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        DeleteTaskCommand = new AsyncRelayCommand(DeleteTaskAsync);
    }

    // ==================== 对外事件（View 职责，VM 不引用 MessageBox） ====================

    /// <summary>需要弹一个提示框。</summary>
    public event Action<string>? AlertRequested;

    /// <summary>需要一次"是/否"确认；返回用户是否确认。</summary>
    public event Func<string, bool>? ConfirmRequested;

    // ==================== 列表 ====================

    public ObservableCollection<ScheduledTaskItemViewModel> Tasks { get; } = [];

    public bool HasTasks => Tasks.Count > 0;

    public string Placeholder
    {
        get => _placeholder;
        private set => SetProperty(ref _placeholder, value);
    }

    /// <summary>
    /// 自动化是否可用（服务端回报 <c>available</c>）。
    /// 默认 <c>true</c>：只有服务端明确回报不可用才禁用入口，
    /// 反过来默认 false 的话，一次读取失败就会让入口永久点不动。
    /// </summary>
    public bool IsAvailable
    {
        get => _available;
        private set => SetProperty(ref _available, value);
    }

    /// <summary>
    /// 总开关。setter 直接落盘——它是"一个勾选框对应一次设置变更"，
    /// 再额外挂个"应用"按钮反而多一步。
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value))
            {
                return;
            }

            if (_configLoaded)
            {
                _ = SubmitAsync();
            }
            else
            {
                UpdateStatusText(null);
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public ICommand DeleteTaskCommand { get; }

    // ==================== 加载 ====================

    /// <summary>主界面初始化时调用一次；弹窗保存 / 删除后由 View 再调一次。</summary>
    public async Task LoadAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        try
        {
            var data = await _api.GetScheduledSendConfigAsync(_lifetime.Token);
            IsAvailable = data.Available;
            ApplyConfig(data.Config);
            _configLoaded = true;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ChatlogApiException ex)
        {
            // 刻意不调用 ApplyConfig(默认值)：那会把界面上的任务列表清空，
            // 用户会以为配置被删了。这里只提示，界面保持原样。
            AlertRequested?.Invoke($"读取定时发送配置失败：{ex.Message}");
        }
        finally
        {
            _loading = false;
        }

        await RefreshStatusAsync();
    }

    /// <summary>
    /// 刷新运行状态（下次触发时间 / 引擎是否在跑）。15 秒一次轮询调用；失败静默保持旧文案。
    /// </summary>
    public async Task RefreshStatusAsync()
    {
        try
        {
            var data = await _api.GetScheduledSendStatusAsync(_lifetime.Token);

            var byId = new Dictionary<string, ScheduledSendTaskStatus>(StringComparer.Ordinal);
            foreach (var status in data.Tasks)
            {
                if (!string.IsNullOrEmpty(status.Id))
                {
                    byId[status.Id] = status;
                }
            }

            foreach (var item in Tasks)
            {
                byId.TryGetValue(item.Id, out var status);
                item.UpdateStatus(status);
            }

            UpdateStatusText(data.Running);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChatlogApiException)
        {
            // 状态轮询失败保持上一次文案，不刷错误（与自动回复状态轮询同一处理）。
        }
    }

    /// <summary>
    /// 新增或更新一条任务后整份提交。弹窗保存由 View 调到这个方法。
    /// </summary>
    public async Task UpsertTaskAsync(ScheduledSendTask task)
    {
        var index = -1;
        for (var i = 0; i < Tasks.Count; i++)
        {
            if (Tasks[i].Id == task.Id)
            {
                index = i;
                break;
            }
        }

        var item = new ScheduledTaskItemViewModel(task, OnTaskEnabledChanged);
        if (index >= 0)
        {
            Tasks[index] = item;
        }
        else
        {
            Tasks.Add(item);
        }

        RefreshPlaceholder();
        await SubmitAsync();
    }

    private void ApplyConfig(ScheduledSendConfig? config)
    {
        // 直接写字段：这里的 Enabled 是"服务端真值回灌"，不该再触发一次保存。
        _enabled = config?.Enabled == true;
        OnPropertyChanged(nameof(Enabled));

        Tasks.Clear();
        foreach (var task in config?.Tasks ?? [])
        {
            Tasks.Add(new ScheduledTaskItemViewModel(task, OnTaskEnabledChanged));
        }

        RefreshPlaceholder();
        UpdateStatusText(null);
    }

    private void OnTaskEnabledChanged() => _ = SubmitAsync();

    private void RefreshPlaceholder()
    {
        OnPropertyChanged(nameof(HasTasks));
        Placeholder = Tasks.Count > 0 ? "" : "还没有定时发送任务，点「新增任务」添加一条。";
    }

    private void UpdateStatusText(bool? running)
    {
        if (!IsAvailable)
        {
            StatusText = "定时发送：当前环境不可用";
            return;
        }

        if (!_enabled)
        {
            StatusText = "定时发送：未启用";
            return;
        }

        var active = Tasks.Count(item => item.Enabled);
        var runningText = running == false ? "引擎未运行" : "运行中";
        StatusText = $"定时发送：{runningText}（{active} 个任务）";
    }

    // ==================== 写操作 ====================

    /// <summary>
    /// 整份提交当前列表（服务端是"整份替换 tasks"语义）。
    /// 用信号量排队：连点两下勾选框时，第二次提交会等第一次完成后再执行，
    /// 而不是被丢弃——丢弃会造成"界面已改、服务端没改"且无任何提示。
    /// </summary>
    private async Task SubmitAsync()
    {
        try
        {
            await _saveGate.WaitAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            // 在锁内读取状态，保证提交的永远是最新值。
            var patch = new ScheduledSendConfigPatch
            {
                Enabled = Enabled,
                Tasks = Tasks.Select(item => item.ToModel()).ToList(),
            };

            var data = await _api.SaveScheduledSendConfigAsync(patch, _lifetime.Token);

            // 服务端会补 id、夹取值域，用返回值回灌，避免界面与真值长期不一致。
            ApplyConfig(data.Config);
            _configLoaded = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChatlogApiException ex)
        {
            AlertRequested?.Invoke($"保存定时发送配置失败：{ex.Message}");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task DeleteTaskAsync(object? parameter)
    {
        if (parameter is not ScheduledTaskItemViewModel item)
        {
            return;
        }

        if (ConfirmRequested?.Invoke($"确定删除发给「{item.TargetName}」的定时发送任务？") != true)
        {
            return;
        }

        Tasks.Remove(item);
        RefreshPlaceholder();
        await SubmitAsync();
    }

    // ==================== 生命周期 ====================

    /// <summary>由 <see cref="MainViewModel.Dispose"/> 调用：停止在途请求，之后不再碰界面。</summary>
    public void Dispose()
    {
        if (!_lifetime.IsCancellationRequested)
        {
            _lifetime.Cancel();
        }

        _lifetime.Dispose();

        // 刻意不 Dispose(_saveGate)：退出瞬间可能还有一个 SubmitAsync 卡在
        // "await 保存请求" 上，它取消后会走 finally 里的 Release()，
        // 若此时信号量已被释放就会抛 ObjectDisposedException。
        // SemaphoreSlim 不访问 AvailableWaitHandle 就没有非托管资源，不释放不会泄漏。
    }
}
