using System.Globalization;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.Services;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// 设置页的「群成员扫描」配置：开关、间隔、立即扫描、最近一次结果。
///
/// 扫描本身在 Python 侧常驻线程里做（启动一次 + 按间隔），这里只负责改配置与手动触发。
/// 为什么不在客户端定时扫描：客户端退出后就不该再有人"提醒"它扫描；
/// 而且快照必须连续采样才可能发现退群。
/// </summary>
public sealed class GroupWatchSettingsViewModel : ObservableObject
{
    private readonly ChatlogApiClient _api;

    public GroupWatchSettingsViewModel(ChatlogApiClient api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        ScanCommand = new AsyncRelayCommand(ScanAsync);
    }

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand ScanCommand { get; }

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    private string _intervalText = "120";
    public string IntervalText
    {
        get => _intervalText;
        set => SetProperty(ref _intervalText, value);
    }

    private string _statusText = "尚未读取扫描状态";
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private string _lastScanText = "";
    public string LastScanText
    {
        get => _lastScanText;
        private set => SetProperty(ref _lastScanText, value);
    }

    public async Task LoadAsync()
    {
        try
        {
            var config = await _api.GetGroupWatchConfigAsync();
            Enabled = config.Config.Enabled;
            IntervalText = config.Config.IntervalMinutes.ToString(CultureInfo.InvariantCulture);
            StatusText = config.Available
                ? "扫描由后端执行：启动一次，之后按间隔自动比对全部群的成员名单"
                : "本地数据源未就绪：成员扫描当前不可用（需 data_source=local 且密钥可用）";

            var status = await _api.GetGroupWatchStatusAsync();
            ApplyStatus(status);
        }
        catch (ChatlogApiException ex)
        {
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            StatusText = "读取扫描配置失败：" + ex.Message;
        }
    }

    private async Task SaveAsync()
    {
        if (!int.TryParse(IntervalText.Trim(), out var minutes) || minutes < 5 || minutes > 1440)
        {
            StatusText = "扫描间隔需为 5–1440 分钟";
            return;
        }

        try
        {
            var result = await _api.SaveGroupWatchConfigAsync(new GroupWatchConfigPatch
            {
                Enabled = Enabled,
                IntervalMinutes = minutes,
            });
            Enabled = result.Config.Enabled;
            IntervalText = result.Config.IntervalMinutes.ToString(CultureInfo.InvariantCulture);
            StatusText = string.Format(
                CultureInfo.InvariantCulture,
                "已保存：{0}，每 {1} 分钟扫描一次",
                Enabled ? "自动扫描开启" : "自动扫描已关闭",
                result.Config.IntervalMinutes);
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

    private async Task ScanAsync()
    {
        StatusText = "正在扫描全部群的成员名单…";
        try
        {
            var result = await _api.ScanGroupsAsync();
            StatusText = result.Groups == 0
                ? "扫描完成，但没有读到群（请确认微信本地数据可读）"
                : result.Baselines == result.Groups
                    ? $"已建立基线：{result.Groups} 个群、当前成员已记录。之后的扫描才能识别入群/退群"
                    : $"扫描完成：{result.Groups} 个群，新增 {result.Joined}，退群 {result.Left}";
            ApplyStatus(await _api.GetGroupWatchStatusAsync());
        }
        catch (ChatlogApiException ex)
        {
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            StatusText = "扫描失败：" + ex.Message;
        }
    }

    private void ApplyStatus(GroupWatchStatusResponse status)
    {
        LastScanText = status.LastScanAt is > 0
            ? "上次扫描：" + DateTimeOffset.FromUnixTimeSeconds((long)status.LastScanAt.Value)
                .LocalDateTime.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "尚未完成过扫描";
        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            LastScanText += " · " + status.LastError;
        }

        if (!status.Available)
        {
            StatusText = "本地数据源未就绪：成员扫描当前不可用";
        }
    }
}
