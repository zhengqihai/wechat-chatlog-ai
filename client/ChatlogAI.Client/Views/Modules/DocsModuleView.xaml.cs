using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using ChatlogAI.Client.ViewModels;
using Microsoft.Win32;

namespace ChatlogAI.Client.Views.Modules;

/// <summary>
/// 文档模块（T-F4）。View 只做 VM 做不了的事：
/// 1. 首次可见时触发加载（与 Contacts / Moments 同一挂法，避免启动就拉文档）；
/// 2. 弹提示框与系统「打开文件」（上传）、「是/否」确认（删除）。
///
/// 本控件不设置 DataContext，绑定与命令都继承 MainWindow 的 MainViewModel。
/// </summary>
public partial class DocsModuleView : UserControl
{
    private const long MaxFileBytes = 4 * 1024 * 1024;

    private DocsModuleViewModel? _wired;

    public DocsModuleView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private DocsModuleViewModel? DocsViewModel => (DataContext as MainViewModel)?.Docs;

    private void OnLoaded(object sender, RoutedEventArgs e) => EnsureWired();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_wired is { } docs)
        {
            docs.AlertRequested -= OnAlertRequested;
            docs.ConfirmRequested -= OnConfirmRequested;
            _wired = null;
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || DocsViewModel is not { } docs)
        {
            return;
        }

        EnsureWired();
        _ = docs.EnsureLoadedAsync();
    }

    private void EnsureWired()
    {
        if (DocsViewModel is not { } docs)
        {
            return;
        }

        if (!ReferenceEquals(_wired, docs))
        {
            if (_wired is { } previous)
            {
                previous.AlertRequested -= OnAlertRequested;
                previous.ConfirmRequested -= OnConfirmRequested;
            }

            docs.AlertRequested += OnAlertRequested;
            docs.ConfirmRequested += OnConfirmRequested;
            _wired = docs;
        }
    }

    private void OnAlertRequested(string message)
        => MessageBox.Show(Window.GetWindow(this), message, "Chatlog AI", MessageBoxButton.OK, MessageBoxImage.Warning);

    private bool OnConfirmRequested(string message)
        => MessageBox.Show(Window.GetWindow(this), message, "Chatlog AI", MessageBoxButton.YesNo, MessageBoxImage.Question)
           == MessageBoxResult.Yes;

    private async void OnUploadClick(object sender, RoutedEventArgs e)
    {
        if (DocsViewModel is not { } docs)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "上传知识文档",
            Filter = "文档 (*.md;*.txt;*.html;*.json)|*.md;*.txt;*.html;*.json|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        var path = dialog.FileName;
        if (new FileInfo(path).Length > MaxFileBytes)
        {
            OnAlertRequested("文档超过 4MB 上限。");
            return;
        }

        try
        {
            var content = await File.ReadAllTextAsync(path, Encoding.UTF8);
            await UploadWithOverwriteCheckAsync(docs, Path.GetFileName(path), content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            OnAlertRequested($"读取文件失败：{ex.Message}");
        }
    }

    private async void OnNewDocClick(object sender, RoutedEventArgs e)
    {
        if (DocsViewModel is not { } docs)
        {
            return;
        }

        var result = DocEditorWindow.Create(Window.GetWindow(this));
        if (result is not { } created)
        {
            return;
        }

        await UploadWithOverwriteCheckAsync(docs, created.Filename, created.Content);
    }

    /// <summary>
    /// 上传 / 编写共用的落库入口：先按文件名（忽略大小写）做同名校验，
    /// 已存在则弹确认，用户同意后才以覆盖语义提交，否则放弃。
    /// </summary>
    private async Task UploadWithOverwriteCheckAsync(DocsModuleViewModel docs, string filename, string content)
    {
        var overwrite = docs.HasDocumentNamed(filename);
        if (overwrite && !OnConfirmRequested($"文档「{filename}」已存在，是否覆盖原文？"))
        {
            return;
        }

        await docs.UploadAsync(filename, content, overwrite);
    }

    private async void OnDeleteDocClick(object sender, RoutedEventArgs e)
    {
        if (DocsViewModel is not { } docs)
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: DocItemViewModel item })
        {
            await docs.DeleteAsync(item);
        }
    }
}
