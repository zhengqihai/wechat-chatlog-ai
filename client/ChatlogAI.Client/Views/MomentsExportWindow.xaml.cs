using System.IO;
using System.Windows;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.ViewModels;
using Microsoft.Win32;

namespace ChatlogAI.Client.Views;

/// <summary>
/// 「导出朋友圈」对话框（T23，规格见 docs/DOTNET_CLIENT_PLAN.md §14.8.4）。
///
/// 与 ExportWindow 同一形态与同一条 MVVM 边界：View 只做三件 VM 做不了的事 ——
/// 1. 弹系统「另存为」并回传路径（<c>Microsoft.Win32</c> 是 View 的地盘）；
/// 2. 把"该关窗了"翻译成 <c>Close()</c>；
/// 3. 关窗时释放 VM（连带取消在途的轮询与下载）。
///
/// 关窗**不**算失败：导出任务在微信数据源侧继续跑，本窗口只是不再跟踪它的进度（见 VM 注释）。
/// </summary>
public partial class MomentsExportWindow : Window
{
    private readonly MomentsExportViewModel _viewModel;

    public MomentsExportWindow(MomentsExportViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();

        DataContext = _viewModel;
        _viewModel.SaveFilePicker = PickSavePath;
        _viewModel.CloseRequested += OnCloseRequested;
        Closed += OnClosed;
    }

    /// <summary>弹系统「另存为」；用户取消返回 <c>null</c>，VM 据此放弃本次保存（不算失败）。</summary>
    private string? PickSavePath(ExportSaveRequest request)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存朋友圈导出",
            FileName = request.DefaultFileName,
            Filter = request.Filter,
            // AddExtension 只在"用户自己把扩展名删了"时才起作用；DefaultExt 兜的就是这种情况，
            // 否则会存出一个没有后缀、双击打不开的文件（与 ExportWindow 同一处理）。
            AddExtension = true,
            DefaultExt = Path.GetExtension(request.DefaultFileName).TrimStart('.'),
            OverwritePrompt = true,
        };

        // 「文档」目录在极端情况下取不到（空串）。此时不设置，交给系统决定默认位置。
        if (!string.IsNullOrEmpty(request.DefaultDirectory))
        {
            dialog.InitialDirectory = request.DefaultDirectory;
        }

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void OnCloseRequested() => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        // VM 的生命周期跟随本窗口：窗口关了就不该再有绑定在打它。
        // Dispose 会取消在途的轮询与下载 —— 用户中途关窗时，等待就此结束，而不是等它跑完再丢掉结果。
        _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel.SaveFilePicker = null;
        _viewModel.Dispose();

        Closed -= OnClosed;
    }
}
