using ChatlogAI.Client.Models;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// 「已配置的模型供应商」列表的一行（对照 app.js 的 <c>renderProviderList()</c> 里的一次 forEach）。
///
/// 与 <see cref="ChatItemViewModel"/> 不同，这里**不需要**按 id 复用实例：
/// 会话列表是 10 秒轮询、且必须保住 ListBox 选中项的连续身份；
/// 而供应商列表只在「弹窗打开 / 保存 / 切换 / 删除」这几个用户动作后整体重建一次，
/// 没有选中项，也没有轮询，所以每次重建一批新实例是安全的。
/// </summary>
public sealed class ProviderItemViewModel : ObservableObject
{
    private readonly ProviderInfo _model;

    public ProviderItemViewModel(ProviderInfo model) => _model = model;

    public string Id => _model.Id;

    /// <summary>预设类型（deepseek / kimi / …），"编辑"时用来把下拉框选回原类型。</summary>
    public string ProviderType => _model.ProviderType;

    /// <summary>原始名称（可能为空）。展示用的回退逻辑在 <see cref="Title"/> 里，不要在编辑回填时用 Title。</summary>
    public string RawName => _model.Name;

    public string BaseUrl => _model.BaseUrl;

    public string ModelName => _model.Model;

    /// <summary>是否当前生效。直接取服务端下发的 <c>active</c> 字段，不再由界面比对 active_id 推导。</summary>
    public bool IsActive => _model.Active;

    /// <summary>对照 <c>(p.name || p.provider_type) + (active ? '（当前使用）' : '')</c>。</summary>
    public string Title
        => (string.IsNullOrEmpty(_model.Name) ? _model.ProviderType : _model.Name)
           + (_model.Active ? "（当前使用）" : "");

    /// <summary>对照 <c>(p.model) + ' · ' + (p.base_url) + ' · Key ' + (p.api_key_masked || '未设置')</c>。</summary>
    public string Subtitle
        => $"{_model.Model} · {_model.BaseUrl} · Key "
           + (string.IsNullOrEmpty(_model.ApiKeyMasked) ? "未设置" : _model.ApiKeyMasked);

    /// <summary>对照 <c>useBtn.textContent = active ? '使用中' : '使用'</c>。</summary>
    public string UseButtonText => _model.Active ? "使用中" : "使用";

    /// <summary>对照 <c>useBtn.disabled = active</c>。已经生效的这一行不让再点。</summary>
    public bool CanUse => !_model.Active;
}
