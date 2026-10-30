using ChatlogAI.Client.Models;
using ChatlogAI.Client.Services;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// 设置页的「防撤回」开关（T-F5）。
///
/// 与 <see cref="GroupWatchSettingsViewModel"/> 同一套路：只负责把开关写进 config.json，
/// 不自己读消息。撤回检测在 Python 侧「打开会话读消息」时现算（纯读、无持久化），
/// 因为微信这个版本撤回后原文仍留在库里，不需要任何本地备份。
/// </summary>
public sealed class AntiRecallSettingsViewModel : ObservableObject
{
    private readonly ChatlogApiClient _api;

    public AntiRecallSettingsViewModel(ChatlogApiClient api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        SaveCommand = new AsyncRelayCommand(SaveAsync);
    }

    public AsyncRelayCommand SaveCommand { get; }

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    private bool _available = true;
    public bool Available
    {
        get => _available;
        private set => SetProperty(ref _available, value);
    }

    private string _statusText = "尚未读取";
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public async Task LoadAsync()
    {
        try
        {
            var data = await _api.GetAntiRecallConfigAsync();
            Enabled = data.Enabled;
            Available = data.Available;
            StatusText = data.Available
                ? "开启后，打开会话时会把好友/群成员撤回的消息标出来并显示原文"
                : "当前数据源不支持（需 data_source=local 且本地数据可读）";
        }
        catch (ChatlogApiException ex)
        {
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            StatusText = "读取防撤回配置失败：" + ex.Message;
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            var data = await _api.SaveAntiRecallConfigAsync(
                new AntiRecallConfigPatch { Enabled = Enabled });
            Enabled = data.Enabled;
            Available = data.Available;
            StatusText = data.Enabled ? "已开启防撤回" : "已关闭防撤回";
        }
        catch (ChatlogApiException ex)
        {
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            StatusText = "保存失败：" + ex.Message;
        }
    }
}
