using System.Globalization;
using System.Text.RegularExpressions;
using ChatlogAI.Client.Models;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// 会话列表的一行（对应 app.js 的 <c>renderChatList()</c>）。
///
/// 为什么是**可变**对象而不是每次刷新重建：
/// 会话列表每 10 秒刷新一次，而 <c>ListBox.SelectedItem</c> 是按**对象引用**比对的。
/// 如果每次刷新都换一批新实例，选中项会在每次轮询后丢失（接着触发"取消选择"，
/// 把消息区清空）。所以这里坚持"按 id 复用实例、原地更新字段"。
/// </summary>
public sealed class ChatItemViewModel : ObservableObject
{
    private static readonly Regex TimePattern = new(@"(\d{4}-\d{2}-\d{2})\s+(\d{2}:\d{2})", RegexOptions.Compiled);

    private ChatSummary _model;

    public ChatItemViewModel(ChatSummary model) => _model = model;

    public string Id => _model.Id;

    /// <summary>与原前端一致：名称为空时退回 id。</summary>
    public string Name => string.IsNullOrEmpty(_model.Name) ? _model.Id : _model.Name;

    /// <summary>原前端用「群」/「微」两个字当头像占位。</summary>
    public string AvatarText => _model.IsGroup ? "群" : "微";

    public string TypeText => _model.IsGroup ? "群聊" : "私聊";

    /// <summary>供「会话洞察」列判断展示成员排行还是好友计数，避免再解一次 Name/Type。</summary>
    public bool IsGroup => _model.IsGroup;

    public string Preview => string.IsNullOrEmpty(_model.LastMessage)
        ? TypeText
        : _model.LastMessage;

    /// <summary>今天只显示 HH:mm，其余显示 MM-dd（对应 app.js 的 <c>shortTime()</c>）。</summary>
    public string TimeText => FormatShortTime(_model.LastTime);

    public bool HasUnread => _model.Unread > 0;

    /// <summary>未读数超过 99 显示 99+，与原前端一致。</summary>
    public string UnreadText => _model.Unread > 99
        ? "99+"
        : _model.Unread.ToString(CultureInfo.InvariantCulture);

    /// <summary>用新数据刷新本行；没有字段变化时不发通知。</summary>
    public void Update(ChatSummary model)
    {
        if (ReferenceEquals(_model, model))
        {
            return;
        }

        _model = model;

        // 逐项通知而不是发一个空 PropertyChanged：WPF 对空名的处理是"所有属性都变了"，
        // 在虚拟化列表里代价更高，而且不利于后续做单元格级动画。
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(AvatarText));
        OnPropertyChanged(nameof(TypeText));
        OnPropertyChanged(nameof(IsGroup));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(HasUnread));
        OnPropertyChanged(nameof(UnreadText));
    }

    private static string FormatShortTime(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var match = TimePattern.Match(value);
        if (!match.Success)
        {
            return value.Length <= 16 ? value : value[..16];
        }

        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return match.Groups[1].Value == today
            ? match.Groups[2].Value
            : match.Groups[1].Value[5..];
    }
}
