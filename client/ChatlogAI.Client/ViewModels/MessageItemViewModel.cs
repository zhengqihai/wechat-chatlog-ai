using ChatlogAI.Client.Models;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// 消息区的一行（对应 app.js 的 <c>renderMessages()</c>）。
/// 大部分字段都由后端数据直接投影，唯一的可变状态是「图片是否已放大」——
/// 它必须跟着行走，因此这里继承 <see cref="ObservableObject"/> 发出属性变更。
/// </summary>
public sealed class MessageItemViewModel : ObservableObject
{
    private bool _isFullImage;

    public MessageItemViewModel(ChatMessage model) => Model = model;

    public ChatMessage Model { get; }

    /// <summary>自己发的消息靠右显示（原前端给 <c>.message.mine</c> 加样式）。</summary>
    public bool IsSelf => Model.IsSelf;

    /// <summary>防撤回（T-F5）：是「别人撤回」的提示，居中灰字展示。</summary>
    public bool IsRecallNotice => Model.RecallNotice;

    /// <summary>防撤回（T-F5）：被撤回的原文（后端从备份恢复），空串表示未恢复。</summary>
    public string RecalledContent => Model.RecalledContent;

    /// <summary>防撤回（T-F5）：被撤回图片的地址；非图片撤回返回 null（避免绑定转换报错）。</summary>
    public string? RecalledImageUrl => string.IsNullOrEmpty(Model.RecalledImageUrl) ? null : Model.RecalledImageUrl;

    /// <summary>防撤回（T-F5）：是否有被撤回的图片可显示。</summary>
    public bool IsRecalledImage => !string.IsNullOrEmpty(Model.RecalledImageUrl);

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

    /// <summary>
    /// 点击图片时查看的原图地址（把缩略图请求的 <c>kind=thumb</c> 换成 <c>kind=orig</c>）。
    /// 后端本地图片代理按 kind 返回不同分辨率（thumb 缩略图 / orig 原图 / hd 高清）。
    /// </summary>
    public string? FullImageUrl => Model.IsImage && !string.IsNullOrEmpty(Model.ImageUrl)
        ? Model.ImageUrl.Replace("kind=thumb", "kind=orig", StringComparison.Ordinal)
        : null;

    /// <summary>图片当前是否处于「查看原图」状态。</summary>
    public bool IsFullImage
    {
        get => _isFullImage;
        private set
        {
            if (!SetProperty(ref _isFullImage, value))
            {
                return;
            }

            // Source 绑的是 DisplayImageUrl、ToolTip 绑的是 ImageTooltip，
            // 它们都是 IsFullImage 的派生值，必须显式通知，否则点击后界面不刷新。
            OnPropertyChanged(nameof(DisplayImageUrl));
            OnPropertyChanged(nameof(ImageTooltip));
        }
    }

    /// <summary>实际绑定到 <c>Image.Source</c> 的地址：未放大时是缩略图，放大后是原图。</summary>
    public string? DisplayImageUrl => IsFullImage ? FullImageUrl : ImageUrl;

    /// <summary>图片 ToolTip 随状态切换，提示用户再点一下可缩回/放大。</summary>
    public string ImageTooltip => IsFullImage ? "点击查看缩略图" : "点击查看原图";

    /// <summary>切换缩略图 / 原图。点击图片时由 View 调用。</summary>
    public void ToggleImage() => IsFullImage = !IsFullImage;
}
