using System.Globalization;
using ChatlogAI.Client.Models;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// 左侧「定时发送」列表的一行。
///
/// 与 <see cref="ChatItemViewModel"/> 不同，这里**不需要**按 id 复用实例：
/// 定时任务只在"保存 / 删除"这类用户动作后整体重建，没有 10 秒轮询、也没有选中项语义。
/// 唯一需要可变的是勾选态（<see cref="Enabled"/>），改完要立刻落盘。
/// </summary>
public sealed class ScheduledTaskItemViewModel : ObservableObject
{
    private static readonly string[] WeekdayNames = ["", "一", "二", "三", "四", "五", "六", "日"];

    private readonly Action _onEnabledChanged;

    private ScheduledSendTask _task;
    private bool _enabled;
    private string _nextFireText = "下次：—";
    private string _lastSentText = "从未发送";

    /// <param name="task">服务端下发的任务。</param>
    /// <param name="onEnabledChanged">勾选态被用户改动时回调（由面板负责落盘）。</param>
    public ScheduledTaskItemViewModel(ScheduledSendTask task, Action onEnabledChanged)
    {
        _task = task;
        _enabled = task.Enabled;
        _onEnabledChanged = onEnabledChanged;
    }

    /// <summary>当前任务的原始模型（界面回填时用）。</summary>
    public ScheduledSendTask Task => _task;

    public string Id => _task.Id;

    public string TargetName => string.IsNullOrEmpty(_task.TargetName) ? _task.TargetId : _task.TargetName;

    public string TargetTypeText => _task.IsGroup ? "群聊" : "好友";

    /// <summary>规则摘要，例如「每天 09:00 · 冷却 60 分」。</summary>
    public string RuleText => BuildRule(_task);

    /// <summary>"含专属提示词"标记，空串时界面不显示这一小块。</summary>
    public string PromptBadge => string.IsNullOrWhiteSpace(_task.Prompt) ? "" : "专属提示词";

    /// <summary>
    /// 本任务是否启用。setter 会触发落盘——面板不知道用户点了哪一行，
    /// 只能由行自己回报（勾选态是这一行唯一的可变状态）。
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                _onEnabledChanged();
            }
        }
    }

    public string NextFireText
    {
        get => _nextFireText;
        private set => SetProperty(ref _nextFireText, value);
    }

    public string LastSentText
    {
        get => _lastSentText;
        private set => SetProperty(ref _lastSentText, value);
    }

    /// <summary>提交给服务端的形态。</summary>
    public ScheduledSendTask ToModel() => _task with { Enabled = Enabled };

    /// <summary>用服务端的运行状态刷新"下次触发 / 上次发送"两行文案。</summary>
    public void UpdateStatus(ScheduledSendTaskStatus? status)
    {
        if (!Enabled)
        {
            NextFireText = "下次：已暂停";
        }
        else if (status?.NextFireAt is { } next)
        {
            NextFireText = "下次：" + FormatFireTime(next);
        }
        else
        {
            NextFireText = "下次：—";
        }

        LastSentText = status?.LastSentAt is { } last
            ? "上次：" + FormatFireTime(last)
            : "从未发送";
    }

    private static string BuildRule(ScheduledSendTask task)
    {
        var rule = task.Mode switch
        {
            ScheduledSendModes.Weekly => $"每周{WeekdayText(task.Weekdays)} {task.TimeOfDay}",
            ScheduledSendModes.Interval => $"每 {FormatInterval(task.IntervalMinutes)}",
            ScheduledSendModes.Once => $"仅一次 {task.RunAt.Replace('T', ' ')}",
            _ => $"每天 {task.TimeOfDay}",
        };

        if (task.CooldownMinutes > 0)
        {
            rule += $" · 冷却 {task.CooldownMinutes} 分";
        }

        return rule;
    }

    private static string WeekdayText(IReadOnlyList<int> days)
    {
        if (days.Count == 0)
        {
            return "（未选）";
        }

        return string.Join("、", days
            .Where(day => day >= 1 && day <= 7)
            .OrderBy(day => day)
            .Select(day => WeekdayNames[day]));
    }

    private static string FormatInterval(int minutes)
        => minutes % 60 == 0 ? $"{minutes / 60} 小时" : $"{minutes} 分钟";

    /// <summary>今天 / 明天 / MM-dd 三档，与原前端的短时间展示习惯一致。</summary>
    private static string FormatFireTime(double epoch)
    {
        var moment = DateTimeOffset.FromUnixTimeSeconds((long)epoch).ToLocalTime();
        var today = DateTimeOffset.Now.Date;

        var dayText = moment.Date == today
            ? "今天"
            : moment.Date == today.AddDays(1)
                ? "明天"
                : moment.ToString("MM-dd", CultureInfo.InvariantCulture);

        return $"{dayText} {moment:HH:mm}";
    }
}
