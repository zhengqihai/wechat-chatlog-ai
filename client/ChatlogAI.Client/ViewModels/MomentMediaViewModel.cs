using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.Services;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// 九宫格里的一格（T23）。
///
/// 这一格同时承担三件事，缺一不可：
/// 1. <b>取图请求的构造</b>：一律经 <c>SnsMediaRequest.ForPostMedia</c>，本类不碰 attrs 里的任何字段
///    （§14.8.1.1 实测：原图与缩略图是**两个不同的 token**，拆字段必错）。
/// 2. <b>能不能取图的判据</b>：<see cref="SnsMedia.CanRenderImage"/> 为 false 的媒体（实测视频号那条
///    thumb / urlAttrs / thumbAttrs / size 全空）从一开始就落到"无缩略图"，**一次请求都不发**。
/// 3. <b>一格的失败不外溢</b>：状态机只在 NotLoaded → Loading → Loaded / Failed 之间走，
///    任何一格失败都只影响它自己，整张卡片、其它格子照常渲染。
///
/// 请求的<b>触发点</b>刻意不在这里，而是"格子的 UI 元素进入可视区"（见 <see cref="RequestLoad"/>）：
/// 条目 VM 是整批创建的（容器虚拟化只回收容器、不回收 VM），若在建 VM 时就发请求，
/// 一页 20 条 × 最多 9 张 = 180 张图会同时涌向后端，再叠加上游逐张解密取图，必然雪崩。
/// </summary>
public sealed class MomentMediaViewModel : ObservableObject
{
    /// <summary>
    /// 一格的加载状态。刻意做成状态机而不是几个互相独立的 bool：
    /// "未加载"与"加载失败"必须可区分，否则重试判据写错就会让格子永久空白（见 <see cref="ResetToNotLoaded"/>）。
    /// </summary>
    private enum LoadingState
    {
        NotLoaded,
        Loading,
        Loaded,
        Failed,
        Unavailable,
    }

    private readonly MomentMediaLoader _loader;
    private readonly SnsPost _post;
    private readonly int _index;

    private LoadingState _state;
    private ImageSource? _image;
    private string _stateText = "";
    private string _toolTipText = "";

    internal MomentMediaViewModel(
        MomentMediaLoader loader,
        SnsPost post,
        SnsMedia media,
        int index,
        bool isSingle,
        string viewerCaption)
    {
        _loader = loader;
        _post = post;
        _index = index;
        Media = media;
        ViewerCaption = viewerCaption;

        // 单图按自己的宽高比给一个较大的格子（朋友圈的"一张图"本来就该比九宫格里的缩略图大），
        // 多图统一用正方形：上游给的缩略图就是按正方形裁的，强行按各自比例排会让每一行高低不齐。
        // 两边都夹在合理区间：极端长图 / 宽图不至于把卡片撑破或压成一条线。
        if (isSingle)
        {
            CellWidth = 220;
            CellHeight = Math.Clamp(220 / media.AspectRatio, 90, 300);
        }
        else
        {
            CellWidth = 96;
            CellHeight = 96;
        }

        if (!media.CanRenderImage)
        {
            _state = LoadingState.Unavailable;
            _stateText = "无缩略图";
            _toolTipText = media.Url.Length > 0
                ? "上游未提供取图参数，只有外链地址：" + media.Url
                : "上游未提供取图参数";
        }
    }

    /// <summary>原始 DTO。取图请求与大小都从这里取，本类不复制字段。</summary>
    public SnsMedia Media { get; }

    /// <summary>格子的显示尺寸（见构造器注释）。由卡片 VM 决定"是不是单图"后传进来。</summary>
    public double CellWidth { get; }

    public double CellHeight { get; }

    /// <summary>取到的图（缩略图；大图走查看器）。加载中 / 失败 / 无参数时为 <c>null</c>。</summary>
    public ImageSource? Image
    {
        get => _image;
        private set
        {
            if (SetProperty(ref _image, value))
            {
                OnPropertyChanged(nameof(HasImage));
            }
        }
    }

    public bool HasImage => _image is not null;

    /// <summary>格子里的状态文案：加载中为空（只留底色占位块，避免九宫格里一片文字）、失败为"加载失败"、无参数为"无缩略图"。</summary>
    public string StateText
    {
        get => _stateText;
        private set
        {
            if (SetProperty(ref _stateText, value))
            {
                OnPropertyChanged(nameof(HasStateText));
            }
        }
    }

    public bool HasStateText => _stateText.Length > 0;

    public string ToolTipText
    {
        get => _toolTipText;
        private set => SetProperty(ref _toolTipText, value);
    }

    /// <summary>能否点开大图。上游没给取图参数的那些格子点了也没意义，这里直接挡掉。</summary>
    public bool CanOpenViewer => Media.CanRenderImage;

    /// <summary>大图查看器顶部的说明（"发布人 · 时间"）。格子自己拼不出这句话——它只有 media。
    /// 由卡片在构造格子时传进来，避免查看器还要反查"这张图属于哪张卡片"。</summary>
    public string ViewerCaption { get; }

    /// <summary>缓存键的第三段（variant）。</summary>
    internal string CacheKey(bool fullSize)
    {
        // media_id 实测可能为空（视频号那条），此时用下标兜底：
        // 同一张动态里下标稳定，足以区分不同的图。
        var mediaId = Media.Id.Length > 0 ? Media.Id : _index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"{_post.Id}|{mediaId}|{(fullSize ? "full" : "thumb")}";
    }

    internal SnsMediaRequest BuildRequest(bool fullSize) => SnsMediaRequest.ForPostMedia(_post, Media, _index, fullSize);

    /// <summary>
    /// 由 View 在格子的 UI 元素进入可视区（Loaded）时调用。幂等：只有"未加载"才排队，
    /// 于是来回滚动、容器被虚拟化重建都不会重复打后端。
    ///
    /// 注意这里**不做**"滚出视野就取消在途请求"：本地回环 + 并发已限到 4，收益本就不大，
    /// 而取消必须配套"状态复位"（见 <see cref="ResetToNotLoaded"/>），写漏一步就是永久空白格。
    /// 取消只发生在模块退出时（见 <see cref="MomentMediaLoader.Dispose"/>）。
    /// </summary>
    public void RequestLoad()
    {
        if (!Media.CanRenderImage || _state != LoadingState.NotLoaded)
        {
            return;
        }

        _state = LoadingState.Loading;

        // 加载中不写字：这一格的底色块本身就是占位，写满"正在加载…"反而比空白更吵。
        StateText = "";
        _loader.RequestThumbnail(this);
    }

    internal void ApplyImage(ImageSource image)
    {
        _state = LoadingState.Loaded;
        StateText = "";
        ToolTipText = "";
        Image = image;
    }

    /// <param name="detail">失败原因（进 ToolTip）。格子里只写"加载失败"，不让长文案挤破九宫格。</param>
    internal void MarkFailed(string detail)
    {
        _state = LoadingState.Failed;
        StateText = "加载失败";
        ToolTipText = string.IsNullOrWhiteSpace(detail) ? "加载失败" : detail;
    }

    /// <summary>
    /// 复位成"未加载"，让这一格可以再次发请求。
    ///
    /// 这个方法存在的原因是一个很容易写错的坑：若取消后不复位，滚回来 / 重新进入模块时
    /// <see cref="RequestLoad"/> 会因为"状态不是 NotLoaded"而直接返回，这一格就**永久空白**，
    /// 看起来像图片坏了，极难排查。
    /// </summary>
    internal void ResetToNotLoaded()
    {
        _state = LoadingState.NotLoaded;
        StateText = "";
        ToolTipText = "";
        Image = null;
    }
}

/// <summary>
/// 朋友圈图片的取图通道（T23）：并发限流 + 本地缓存 + 统一的生命周期取消。
///
/// 为什么需要一个独立的加载器、而不是把请求写在格子里：
/// 1. <b>并发上限</b>。一屏可能同时有几十个格子进入可视区，加上上游是"逐张解密取图"，
///    不限流就是一次自杀式的请求风暴。这里限 4，超出的排队。
/// 2. <b>缓存要跨卡片、跨虚拟化活下来</b>。格子 VM 挂在卡片 VM 上、随卡片被丢弃，
///    而用户上下滚动会反复经过同一批图；缓存挂在加载器（模块级）上，来回滚动不再打后端。
/// 3. <b>取消只有一处出口</b>。模块退出时整个令牌源取消，在途请求一次性了结。
///
/// 缓存值是已经 <c>Freeze()</c> 的 <see cref="BitmapImage"/>：冻结后可跨线程使用，
/// 也不会因为源流被关掉而变成空图（见 <see cref="CreateImage"/>）。
/// </summary>
internal sealed class MomentMediaLoader : IDisposable
{
    /// <summary>同时在途的取图请求上限。</summary>
    private const int MaxConcurrency = 4;

    private readonly ChatlogApiClient _api;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(MaxConcurrency, MaxConcurrency);
    private readonly Dictionary<string, ImageSource> _cache = new(StringComparer.Ordinal);

    public MomentMediaLoader(ChatlogApiClient api) => _api = api ?? throw new ArgumentNullException(nameof(api));

    /// <summary>排一张缩略图（不等待，结果由格子 VM 自己回写）。</summary>
    public void RequestThumbnail(MomentMediaViewModel cell) => _ = LoadThumbnailAsync(cell);

    /// <summary>
    /// 取原图给大图查看器用。与缩略图共用并发闸门与缓存（缓存键含 variant，两套图不会互相覆盖）。
    ///
    /// 返回值：拿到图 → (image, "")；取不到 → (null, 原因)；调用方取消 → 抛 <see cref="OperationCanceledException"/>。
    /// 之所以把"取消"与"失败"分开：用户关掉查看器时不该弹"加载失败"。
    /// </summary>
    public async Task<(ImageSource? Image, string Error)> LoadFullAsync(
        MomentMediaViewModel cell,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;

        await _gate.WaitAsync(token);
        try
        {
            if (_cache.TryGetValue(cell.CacheKey(fullSize: true), out var cached))
            {
                return (cached, "");
            }

            var (bytes, _) = await _api.GetSnsMediaAsync(cell.BuildRequest(fullSize: true), token);
            if (CreateImage(bytes) is not { } image)
            {
                return (null, "上游返回的数据不是可显示的图片");
            }

            _cache[cell.CacheKey(fullSize: true)] = image;
            return (image, "");
        }
        catch (ChatlogApiException ex)
        {
            return (null, string.IsNullOrWhiteSpace(ex.Message) ? "原图加载失败" : ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoadThumbnailAsync(MomentMediaViewModel cell)
    {
        var acquired = false;
        try
        {
            try
            {
                await _gate.WaitAsync(_lifetime.Token);
                acquired = true;
            }
            catch (OperationCanceledException)
            {
                cell.ResetToNotLoaded();
                return;
            }

            if (_cache.TryGetValue(cell.CacheKey(fullSize: false), out var cached))
            {
                cell.ApplyImage(cached);
                return;
            }

            var (bytes, _) = await _api.GetSnsMediaAsync(cell.BuildRequest(fullSize: false), _lifetime.Token);
            if (CreateImage(bytes) is not { } image)
            {
                cell.MarkFailed("上游返回的数据不是可显示的图片");
                return;
            }

            _cache[cell.CacheKey(fullSize: false)] = image;
            cell.ApplyImage(image);
        }
        catch (OperationCanceledException)
        {
            // 模块退出 / 请求被取消：必须把状态复位成"未加载"（见 ResetToNotLoaded 的注释）。
            cell.ResetToNotLoaded();
        }
        catch (ChatlogApiException ex)
        {
            // 单张失败只让这一格显示失败：一张图取不到不该影响其它格，更不该炸掉整张卡片。
            cell.MarkFailed(ex.Message);
        }
        finally
        {
            if (acquired)
            {
                _gate.Release();
            }
        }
    }

    private static ImageSource? CreateImage(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            // OnLoad：立刻解码进内存，之后才能关掉流（BitmapImage 默认延迟解码，流一关就成空图）。
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            // 冻结后跨线程可用，也免去每次重绘的校验开销。
            image.Freeze();
            return image;
        }
        catch (NotSupportedException)
        {
            // 上游返回的不是图片（错误页 / 空体）：退化成"加载失败"占位，不炸掉九宫格。
            return null;
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();

        // 刻意**不** Dispose 这里的 SemaphoreSlim：取消之后，在途请求的 finally 仍会 Release 一次，
        // 万一它晚于 Dispose 执行，"释放一个已 Dispose 的信号量"就会抛 ObjectDisposedException ——
        // 而这个异常发生在一个 `_ = ...` 的任务里，没有任何人会看到它，比"不释放"难查得多。
        // （SemaphoreSlim 只有在用过 AvailableWaitHandle 时才真的需要释放，这里没有用。）
    }
}
