using ChatlogAI.Client.Models;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// 消息区的一行（对应 app.js 的 <c>renderMessages()</c>）。
/// 每一行都由后端数据直接投影，没有可变状态，因此不需要 <see cref="ObservableObject"/>。
/// </summary>
public sealed class MessageItemViewModel
{
    public MessageItemViewModel(ChatMessage model) => Model = model;

    public ChatMessage Model { get; }

    /// <summary>自己发的消息靠右显示（原前端给 <c>.message.mine</c> 加样式）。</summary>
    public bool IsSelf => Model.IsSelf;

    public string Sender => string.IsNullOrEmpty(Model.Sender) ? "未知发送者" : Model.Sender;

    public string Time => Model.Time;

    public string Content => string.IsNullOrEmpty(Model.Content) ? "[消息]" : Model.Content;

    /// <summary>图片消息在有可用地址时为 true；渲染层用它决定显示图片还是文本。</summary>
    public bool IsImage => Model.IsImage;

    /// <summary>
    /// 图片地址（后端代理到本服务的 /api/media/image）。
    /// 非图片消息刻意返回 <c>null</c> 而不是空串：空串绑到 <c>Image.Source</c> 会让 WPF 的类型转换器
    /// 每次转换失败并在输出窗口刷绑定错误，<c>null</c> 则是"没有源"，静默且正确。
    /// </summary>
    public string? ImageUrl => Model.IsImage ? Model.ImageUrl : null;
}
