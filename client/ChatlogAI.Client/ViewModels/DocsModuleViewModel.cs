using System.Collections.ObjectModel;
using System.Globalization;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.Services;

namespace ChatlogAI.Client.ViewModels;

/// <summary>
/// 「文档」模块（T-F4）的视图模型：知识文档的列表 + 上传 + 删除。
///
/// 上传 / 删除的文件对话框与确认框是 View 的职责，因此这里只暴露公开方法
/// （<see cref="UploadAsync"/> / <see cref="DeleteAsync"/>），由 DocsModuleView 的 code-behind 调用。
/// </summary>
public sealed class DocsModuleViewModel : ObservableObject
{
    private readonly ChatlogApiClient _api;
    private bool _hasLoaded;
    private string _placeholder = "正在加载文档……";

    public DocsModuleViewModel(ChatlogApiClient api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public event Action<string>? AlertRequested;

    public event Func<string, bool>? ConfirmRequested;

    public ObservableCollection<DocItemViewModel> Docs { get; } = [];

    public bool HasDocs => Docs.Count > 0;

    public string Placeholder
    {
        get => _placeholder;
        private set => SetProperty(ref _placeholder, value);
    }

    /// <summary>模块首次可见时调用：只加载一次，之后用户手动刷新。</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_hasLoaded)
        {
            return;
        }

        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        try
        {
            var data = await _api.GetDocsAsync();
            Docs.Clear();
            foreach (var doc in data.Docs)
            {
                Docs.Add(new DocItemViewModel(doc));
            }

            _hasLoaded = true;
            Placeholder = Docs.Count == 0 ? "暂无文档，点击「上传文档」添加" : "";
        }
        catch (ChatlogApiException ex)
        {
            Placeholder = "文档读取失败";
            AlertRequested?.Invoke($"读取文档列表失败：{ex.Message}");
        }

        OnPropertyChanged(nameof(HasDocs));
    }

    public async Task UploadAsync(string filename, string content, bool overwrite = false)
    {
        try
        {
            await _api.UploadDocAsync(new DocUploadRequest
            {
                Filename = filename,
                Content = content,
                Overwrite = overwrite,
            });
            await RefreshAsync();
        }
        catch (ChatlogApiException ex)
        {
            AlertRequested?.Invoke($"上传文档失败：{ex.Message}");
        }
    }

    /// <summary>是否存在同名文档（忽略大小写，供 View 在上传/保存前决定是否弹覆盖确认）。</summary>
    public bool HasDocumentNamed(string filename)
        => Docs.Any(item => string.Equals(item.Filename, filename, StringComparison.OrdinalIgnoreCase));

    public async Task DeleteAsync(DocItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (ConfirmRequested?.Invoke($"确定删除文档「{item.Filename}」？") != true)
        {
            return;
        }

        try
        {
            await _api.DeleteDocAsync(item.Id);
            await RefreshAsync();
        }
        catch (ChatlogApiException ex)
        {
            AlertRequested?.Invoke($"删除文档失败：{ex.Message}");
        }
    }
}

/// <summary>文档列表的一行：只读投影，无可变状态。</summary>
public sealed class DocItemViewModel
{
    public DocItemViewModel(DocItem model) => Model = model;

    public DocItem Model { get; }

    public string Id => Model.Id;

    public string Filename => Model.Filename;

    public string SizeText => FormatSize(Model.Size);

    public string UploadedAtText => FormatTime(Model.UploadedAt);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        return $"{bytes / 1024.0 / 1024.0:0.##} MB";
    }

    private static string FormatTime(long seconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "";
        }
    }
}
