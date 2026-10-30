using System.IO;
using System.Windows;
using System.Windows.Controls;
using ChatlogAI.Client.Bootstrap;
using Microsoft.Win32;

namespace ChatlogAI.Client.Views;

/// <summary>
/// 「运行日志」查看窗口（T-F2）：列出 LogDir 下的三个子进程日志，选中后只读查看，并支持另存为。
///
/// 纯只读 + 导出，不读写业务状态，因此没有 ViewModel（与 <see cref="AboutWindow" /> 同一姿态）。
/// 日志文件可能在几十 MB 量级，为避免 TextBox 一次性吃进大文件卡死，只显示末尾一段。
/// </summary>
public partial class LogViewerWindow : Window
{
    /// <summary>LogDir 下要列出的日志文件（与 StartupWindow 的 tail 列表保持一致）。</summary>
    private static readonly string[] LogFileNames = { "chatlog.log", "wechat.log", "wechat-broker.log" };

    /// <summary>单个日志最多加载的字节数（末尾）；过大只显示尾部，避免 TextBox 卡死。</summary>
    private const long MaxViewBytes = 2 * 1024 * 1024;

    public LogViewerWindow()
    {
        InitializeComponent();
        LogFileCombo.ItemsSource = LogFileNames;
        LogFileCombo.SelectedIndex = 0;
    }

    /// <summary>唯一入口：设置 Owner 再 ShowDialog，保证居中 + 真模态。</summary>
    public static void Show(Window owner) => new LogViewerWindow { Owner = owner }.ShowDialog();

    private void OnLogFileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (LogFileCombo.SelectedItem is string name)
        {
            LoadLog(name);
        }
    }

    private void LoadLog(string name)
    {
        var path = AppPaths.Instance.LogFile(name);
        try
        {
            LogContent.Text = File.Exists(path)
                ? ReadTail(path)
                : $"（文件不存在：{path}）";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogContent.Text = $"读取失败：{ex.Message}";
        }
    }

    private static string ReadTail(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length <= MaxViewBytes)
        {
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        stream.Seek(-MaxViewBytes, SeekOrigin.End);
        using var tailReader = new StreamReader(stream);
        return $"……（文件过大，仅显示末尾 {MaxViewBytes / 1024 / 1024}MB）\n{tailReader.ReadToEnd()}";
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (LogFileCombo.SelectedItem is not string name)
        {
            return;
        }

        var source = AppPaths.Instance.LogFile(name);
        if (!File.Exists(source))
        {
            MessageBox.Show(this, "该日志文件不存在。", "导出日志", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = name,
            Filter = "日志文件 (*.log)|*.log|所有文件 (*.*)|*.*",
            Title = "导出日志",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.Copy(source, dialog.FileName, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "导出日志", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
