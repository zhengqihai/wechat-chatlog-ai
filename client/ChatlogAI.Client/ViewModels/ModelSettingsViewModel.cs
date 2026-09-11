using System.Collections.ObjectModel;
using System.Windows.Input;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.Services;

namespace ChatlogAI.Client.ViewModels;

/// <summary>下拉框的一个选项：<see cref="Key"/> 是 presets 的 key（即 provider_type），<see cref="Display"/> 是给用户看的中文名。</summary>
public sealed record PresetOption(string Key, string Display);

/// <summary>
/// 「模型与 API Key 设置」弹窗的视图模型（T9），逐条对照 app.js L381-L581 的 12 个函数。
///
/// 三条本类独有的语义，都不是照抄原前端，而是原前端"没有做到"或"做错了"的地方：
///
/// 1. <b>补上了「编辑」</b>。原 <c>collectProviderForm()</c> 从不包含 id，<c>saveProvider()</c>
///    也从不传 id，所以网页版实际上只会新增、不会更新——界面上写"添加 / 修改"是名不副实的。
///    这里引入 <see cref="EditingProviderId"/>：为 null 走新增，非 null 走更新。
///
/// 2. <b>「验证通过才能保存」是一道真门</b>。原前端在验证通过后把 <c>mpSaveBtn.disabled</c> 置 false，
///    但之后用户再改 base_url / model / api_key，那个按钮**不会重新变灰**——
///    等于拿着旧结论去提交新参数。这里任何一项改动都会作废 <see cref="IsKeyVerified"/>。
///
/// 3. <b>新增严、编辑宽</b>。新增必须验证通过（服务端对"新增且 api_key 为空"直接 400）；
///    编辑时 Key 留空表示"沿用旧 Key"（见 API_REFERENCE.md §3.4），此时**不可能**去调验证接口
///    （<c>/api/providers/validate</c> 的 api_key 是必填），所以放开这道门禁，只校验 base_url / model 非空。
///
/// 需要由 View 承担的事（弹框、二次确认）一律走事件，VM 不引用 MessageBox：
/// 这与 <see cref="MainViewModel.AlertRequested"/> 是同一条边界约定。
/// </summary>
public sealed class ModelSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ChatlogApiClient _api;

    /// <summary>
    /// 保存 / 切换 / 删除成功后回刷主界面的钩子（MainViewModel.RefreshModelStateAsync）。
    /// 弹窗自己也有状态栏，但主界面右栏的 <c>ModelStatus</c> / <c>BalanceStatus</c> / AI 署名
    /// 住在 MainViewModel 里，必须由它自己去重取，否则关掉弹窗后主界面还是旧模型名。
    /// </summary>
    private readonly Func<Task> _onProvidersChanged;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, ProviderPreset> _presetMap = new(StringComparer.Ordinal);

    /// <summary>
    /// 正在把"服务端数据 / 预设"写进表单，此时不能让 setter 的副作用触发。
    /// 最典型的场景：编辑已有供应商时把 provider_type 写回下拉框——
    /// 如果不加这个开关，<see cref="ApplyPreset"/> 会立刻用预设值把该供应商真实的 base_url / model 覆盖掉。
    /// </summary>
    private bool _suppressFormEvents;

    private readonly AsyncRelayCommand _saveCommand;

    public ModelSettingsViewModel(ChatlogApiClient api, Func<Task> onProvidersChanged)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _onProvidersChanged = onProvidersChanged ?? throw new ArgumentNullException(nameof(onProvidersChanged));

        _saveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave);
        ValidateCommand = new AsyncRelayCommand(ValidateAsync);
        ActivateCommand = new AsyncRelayCommand(ActivateAsync);
        BeginEditCommand = new RelayCommand(BeginEdit);
        CancelEditCommand = new RelayCommand(ResetForm);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync);
    }

    // ==================== 对外事件（必须由 View 做的事） ====================

    /// <summary>需要弹一个提示框。对应原前端的 <c>window.alert</c>。</summary>
    public event Action<string>? AlertRequested;

    /// <summary>需要用户二次确认，返回是否确认。对应原前端的 <c>window.confirm</c>。</summary>
    public event Func<string, bool>? ConfirmRequested;

    // ==================== 供应商列表 ====================

    public ObservableCollection<ProviderItemViewModel> Providers { get; } = [];

    public bool HasProviders => Providers.Count > 0;

    public bool HasNoProviders => Providers.Count == 0;

    /// <summary>列表为空时的提示文案；对照原前端那句 <c>尚未添加模型供应商，请在下方填写并验证后保存。</c></summary>
    private string _placeholder = "";
    public string Placeholder
    {
        get => _placeholder;
        private set => SetProperty(ref _placeholder, value);
    }

    // ==================== 供应商类型预设 ====================

    public ObservableCollection<PresetOption> Presets { get; } = [];

    private string _selectedProviderType = "";
    public string SelectedProviderType
    {
        get => _selectedProviderType;
        set
        {
            // ComboBox 有一条会"反向写坏数据"的行为：当绑定给它的值在 Items 里找不到对应项时，
            // 它会把 SelectedValue 回写成 null。弹窗初次加载时 Presets 还没灌进来、绑定值又是空串，
            // 正好命中这条路径。null 一旦流下去会有两处爆掉：
            //   1) ApplyPreset → Dictionary.TryGetValue(null) 抛 ArgumentNullException；
            //   2) SaveProviderRequest.ProviderType 变 null，服务端会退回 custom。
            // 所以在这里做归一，它是"界面 → VM"的边界，边界上的校验不算多余。
            var normalized = value ?? string.Empty;
            if (!SetProperty(ref _selectedProviderType, normalized))
            {
                return;
            }

            if (!_suppressFormEvents)
            {
                // 对照 onProviderTypeChange()：换类型即用预设覆盖 base_url / model。
                // 这是"预设表由服务端下发、客户端不硬编码"的直接兑现。
                ApplyPreset(normalized);
            }
        }
    }

    // ==================== 表单字段 ====================

    private string _providerName = "";
    public string ProviderName
    {
        get => _providerName;
        set => SetFormField(ref _providerName, value);
    }

    private string _baseUrl = "";
    public string BaseUrl
    {
        get => _baseUrl;
        set => SetFormField(ref _baseUrl, value);
    }

    private string _model = "";
    public string Model
    {
        get => _model;
        set => SetFormField(ref _model, value);
    }

    private string _apiKey = "";
    public string ApiKey
    {
        get => _apiKey;
        set => SetFormField(ref _apiKey, value);
    }

    // ==================== 编辑态 ====================

    private string? _editingProviderId;

    /// <summary>非 null = 正在修改该 id；null = 新增。</summary>
    public string? EditingProviderId
    {
        get => _editingProviderId;
        private set
        {
            if (SetProperty(ref _editingProviderId, value))
            {
                OnPropertyChanged(nameof(IsEditing));
                OnPropertyChanged(nameof(FormTitle));
                OnPropertyChanged(nameof(SaveButtonText));
            }
        }
    }

    public bool IsEditing => _editingProviderId is not null;

    public string FormTitle => IsEditing ? "修改供应商" : "添加供应商";

    /// <summary>原前端的按钮文案是「添加 / 保存」；编辑态下必须换掉，否则用户不知道这次是更新而不是再存一份。</summary>
    public string SaveButtonText => IsEditing ? "保存修改" : "添加 / 保存";

    // ==================== 验证门禁 ====================

    private bool _isKeyVerified;
    public bool IsKeyVerified
    {
        get => _isKeyVerified;
        private set
        {
            if (SetProperty(ref _isKeyVerified, value))
            {
                RefreshSaveState();
            }
        }
    }

    private string _validateMessage = "";
    public string ValidateMessage
    {
        get => _validateMessage;
        private set => SetProperty(ref _validateMessage, value);
    }

    /// <summary>
    /// 保存按钮是否可用（见类注释第 3 条）。
    /// 注意这里只是"界面是否允许点"，真正的必填校验仍由服务端兜底（400）；
    /// 界面禁用的意义是"不让用户点了才被告知不行"，不是替代服务端校验。
    /// </summary>
    public bool CanSave => IsEditing
        ? BaseUrl.Trim().Length > 0 && Model.Trim().Length > 0
          && (ApiKey.Trim().Length == 0 || IsKeyVerified)
        : IsKeyVerified;

    // ==================== 命令 ====================

    public ICommand ValidateCommand { get; }
    public ICommand SaveCommand => _saveCommand;
    public ICommand ActivateCommand { get; }
    public ICommand BeginEditCommand { get; }
    public ICommand CancelEditCommand { get; }
    public ICommand DeleteCommand { get; }

    // ==================== 加载 ====================

    /// <summary>
    /// 弹窗打开时调用：拉供应商列表 + 预设，并把表单置为"新增"的初始态。
    /// 对照 <c>openModelModal()</c> 里的 <c>populateProviderTypeOptions()</c> + <c>renderProviderList()</c>。
    /// </summary>
    public async Task LoadAsync()
    {
        await LoadProvidersAsync();
        ResetForm();
    }

    private async Task LoadProvidersAsync()
    {
        ProviderListResponse data;
        try
        {
            data = await _api.GetProvidersAsync(_lifetime.Token);
        }
        catch (Exception ex) when (ex is ChatlogApiException or OperationCanceledException)
        {
            Placeholder = "模型配置读取失败";
            OnPropertyChanged(nameof(HasProviders));
            OnPropertyChanged(nameof(HasNoProviders));
            return;
        }

        // 预设表是服务端事实，只在第一次拿到时灌进下拉框：
        // 每次刷新都 Clear + 重填会把当前选中项打断（SelectedValue 先变空再变回），
        // 而且是没必要的——预设不会在运行期变化。
        if (Presets.Count == 0)
        {
            PopulatePresets(data.Presets);
        }

        Providers.Clear();
        foreach (var provider in data.Providers)
        {
            Providers.Add(new ProviderItemViewModel(provider));
        }

        Placeholder = Providers.Count == 0 ? "尚未添加模型供应商，请在下方填写并验证后保存。" : "";
        OnPropertyChanged(nameof(HasProviders));
        OnPropertyChanged(nameof(HasNoProviders));
    }

    private void PopulatePresets(IReadOnlyDictionary<string, ProviderPreset> presets)
    {
        _presetMap.Clear();
        foreach (var (key, preset) in presets)
        {
            _presetMap[key] = preset;
            Presets.Add(new PresetOption(key, string.IsNullOrEmpty(preset.Name) ? key : preset.Name));
        }
    }

    /// <summary>把表单恢复成"新增"的初始态。对照 <c>saveProvider()</c> 成功后的清空动作。</summary>
    private void ResetForm()
    {
        _suppressFormEvents = true;

        EditingProviderId = null;
        ProviderName = "";
        ApiKey = "";
        ValidateMessage = "";
        IsKeyVerified = false;

        if (Presets.Count > 0)
        {
            // 对照 populateProviderTypeOptions() 末尾那次 onProviderTypeChange()：
            // 类型回到第一项，并按预设重填 base_url / model / name。
            SelectedProviderType = Presets[0].Key;
            ApplyPreset(Presets[0].Key);
        }
        else
        {
            BaseUrl = "";
            Model = "";
        }

        _suppressFormEvents = false;
        RefreshSaveState();
    }

    /// <summary>用预设填充 base_url / model；名称只在用户没填过时才补（对照 <c>if (!mpName.value)</c>）。</summary>
    private void ApplyPreset(string key)
    {
        // 空 key 直接返回：预设表里没有"空类型"，而空串传进 TryGetValue 虽然不抛异常，
        // 但会走到"什么都没填充"的分支，语义上和"没选类型"不同，不如显式短路。
        if (string.IsNullOrEmpty(key) || !_presetMap.TryGetValue(key, out var preset))
        {
            return;
        }

        BaseUrl = preset.BaseUrl;
        Model = preset.Model;
        if (ProviderName.Length == 0)
        {
            ProviderName = preset.Name;
        }
    }

    /// <summary>
    /// 表单字段的统一 setter：值变了就作废"验证通过"的结论。
    /// 为什么必须作废：验证接口只对"当时提交的那一组 base_url / model / api_key"给出结论，
    /// 参数变了结论就失效；不作废的话，"验证通过才能保存"会退化成一道假门。
    /// </summary>
    private void SetFormField(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName) || _suppressFormEvents)
        {
            return;
        }

        if (IsKeyVerified || ValidateMessage.Length > 0)
        {
            IsKeyVerified = false;
            ValidateMessage = "";
        }

        RefreshSaveState();
    }

    private void RefreshSaveState()
    {
        OnPropertyChanged(nameof(CanSave));
        _saveCommand.RaiseCanExecuteChanged();
    }

    // ==================== 验证 ====================

    /// <summary>对照 <c>validateProvider()</c>。会真实调用一次模型（服务端超时 60 秒，客户端 180 秒）。</summary>
    private async Task ValidateAsync()
    {
        var baseUrl = BaseUrl.Trim();
        var model = Model.Trim();
        var apiKey = ApiKey.Trim();

        if (baseUrl.Length == 0 || model.Length == 0 || apiKey.Length == 0)
        {
            AlertRequested?.Invoke("请填写 Base URL、模型名和 API Key");
            return;
        }

        ValidateMessage = "正在验证……";
        try
        {
            var data = await _api.ValidateProviderAsync(
                new ValidateProviderRequest { BaseUrl = baseUrl, Model = model, ApiKey = apiKey },
                _lifetime.Token);

            ValidateMessage = "验证通过：" + (string.IsNullOrEmpty(data.Message) ? "Key 可用" : data.Message);
            IsKeyVerified = true;
        }
        catch (Exception ex) when (ex is ChatlogApiException or OperationCanceledException)
        {
            // 验证失败的原因可能来自供应商（401 / 余额不足 / 模型名不存在）也可能是本地服务不可达，
            // ChatlogApiClient 已经把 detail → message → status_info.msg 的优先级处理完，这里直接透出。
            ValidateMessage = "验证失败：" + ex.Message;
            IsKeyVerified = false;
        }
    }

    // ==================== 保存 ====================

    private async Task SaveAsync()
    {
        var apiKey = ApiKey.Trim();

        var request = new SaveProviderRequest
        {
            // 传入 = 更新，不传 = 新增（API_REFERENCE.md §3.4）。
            Id = EditingProviderId,
            ProviderType = SelectedProviderType,
            Name = ProviderName.Trim(),
            BaseUrl = BaseUrl.Trim(),
            Model = Model.Trim(),
            // 编辑时留空 → null → 序列化被忽略 → 服务端沿用旧 Key。
            // 这里刻意用 null 而不是空串：两者在服务端等价，但空串更容易在别处被误当成"清空 Key"。
            ApiKey = apiKey.Length == 0 ? null : apiKey,
            // balance_path / balance_parser 不传：服务端会按 provider_type 取预设（§3.4）。
            // 原前端也没有传——传了反而会在"自定义供应商改过预设"时把服务端的表覆盖掉。
        };

        try
        {
            await _api.SaveProviderAsync(request, _lifetime.Token);
        }
        catch (Exception ex) when (ex is ChatlogApiException or OperationCanceledException)
        {
            AlertRequested?.Invoke("保存失败：" + ex.Message);
            return;
        }

        ResetForm();
        await ReloadAsync();
    }

    // ==================== 切换 / 删除 ====================

    private async Task ActivateAsync(object? parameter)
    {
        if (parameter is not ProviderItemViewModel item)
        {
            return;
        }

        try
        {
            await _api.ActivateProviderAsync(item.Id, _lifetime.Token);
        }
        catch (Exception ex) when (ex is ChatlogApiException or OperationCanceledException)
        {
            AlertRequested?.Invoke("切换模型失败：" + ex.Message);
            return;
        }

        await ReloadAsync();
    }

    private async Task DeleteAsync(object? parameter)
    {
        if (parameter is not ProviderItemViewModel item)
        {
            return;
        }

        if (ConfirmRequested?.Invoke("确定删除该模型供应商？") != true)
        {
            return;
        }

        try
        {
            await _api.DeleteProviderAsync(item.Id, _lifetime.Token);
        }
        catch (Exception ex) when (ex is ChatlogApiException or OperationCanceledException)
        {
            AlertRequested?.Invoke("删除失败：" + ex.Message);
            return;
        }

        // 删掉的正是当前编辑对象时，表单必须退回"新增"，否则下一次保存会拿着一个不存在的 id 去打 404。
        if (string.Equals(EditingProviderId, item.Id, StringComparison.Ordinal))
        {
            ResetForm();
        }

        await ReloadAsync();
    }

    // ==================== 编辑 ====================

    /// <summary>
    /// 把某个供应商载入表单，并记住 id——这是网页版没有的能力（见类注释第 1 条）。
    /// 注意 ApiKey 不回填：服务端只会下发脱敏串，明文 Key 从一开始就不出服务端。
    /// </summary>
    private void BeginEdit(object? parameter)
    {
        if (parameter is not ProviderItemViewModel item)
        {
            return;
        }

        var model = Providers.FirstOrDefault(p => p.Id == item.Id);
        if (model is null)
        {
            return;
        }

        _suppressFormEvents = true;

        EditingProviderId = item.Id;
        // 回填"原始"字段而不是 Title / Subtitle：那两串是给人看的，
        // Title 带"（当前使用）"后缀、Subtitle 是拼接串，回填进去会把数据写坏。
        ProviderName = model.RawName;
        if (_presetMap.ContainsKey(model.ProviderType))
        {
            // provider_type 必须能在下拉框里选中，否则 ComboBox 会把 SelectedValue 写成 null，
            // 保存时 provider_type 就丢了（服务端会退回 custom，余额等预设能力也随之丢失）。
            SelectedProviderType = model.ProviderType;
        }

        BaseUrl = model.BaseUrl;
        Model = model.ModelName;

        // 进入编辑态时 Key 一律留空（= 沿用旧 Key）。若把上一次新增时输入的明文
        // 留在框里，用户会以为"这个 Key 就是当前供应商的 Key"，而它其实可能属于别的供应商。
        ApiKey = "";
        ValidateMessage = "";
        IsKeyVerified = false;

        _suppressFormEvents = false;
        RefreshSaveState();
    }

    // ==================== 回刷 ====================

    /// <summary>重取列表 + 让主界面重取它自己的状态文案。</summary>
    private async Task ReloadAsync()
    {
        await LoadProvidersAsync();
        await _onProvidersChanged();
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
