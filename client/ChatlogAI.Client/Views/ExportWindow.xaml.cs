using System.IO;
using System.Windows;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.ViewModels;
using Microsoft.Win32;

namespace ChatlogAI.Client.Views;

/// <summary>
/// 「导出聊天记录」对话框（T11，规格见 docs/DOTNET_CLIENT_PLAN.md §8）。
///
/// View 只做三件 VM 做不了的事（都与 <c>Microsoft.Win32</c> 有关）：
/// 1. 弹系统「另存为」并回传路径 —— VM 不引用 Win32，这是 MVVM 的硬边界；
/// 2. 把"该关窗了"翻译成 <c>Close()</c>；
/// 3. 关窗时释放 VM（连带取消在途请求）。
///
/// 数据从哪来、怎么排版、写不写 BOM，全部在 <see cref="ExportViewModel"/> 与
/// <c>ChatExporter</c> 里，本文件不出现任何业务流程。
/// </summary>
public partial class ExportWindow : Window
{
    private readonly ExportViewModel _viewModel;

    public ExportWindow(ExportViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();

        DataContext = _viewModel;
        _viewModel.SaveFilePicker = PickSavePath;
        _viewModel.CloseRequested += OnCloseRequested;
        Closed += OnClosed;
    }

    /// <summary>
    /// 导出成功后的提示文案（含落盘路径与条数），由主界面在窗口关闭后弹一次 MessageBox；
    /// 未成功或用户中途关闭时为 <c>null</c>。
    ///
    /// 为什么不用 <c>DialogResult</c> 表达"成功"：本窗口的关闭原因有三种（成功 / 取消 / 关窗），
    /// 而 DialogResult 只有 true/false 两态，用它还得让调用方去猜"false 是失败还是没点导出"。
    /// 直接把结果文案作为唯一出口，语义唯一。
    /// </summary>
    public string? ResultMessage => _viewModel.Succeeded ? _viewModel.ResultMessage : null;

    /// <summary>弹系统「另存为」；用户取消返回 <c>null</c>，VM 据此直接放弃本次导出。</summary>
    private string? PickSavePath(ExportSaveRequest request)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出聊天记录",
            FileName = request.DefaultFileName,
            Filter = request.Filter,
            // AddExtension 只在"用户自己把扩展名删了"时才起作用；DefaultExt 兜的是这种情况，
            // 否则会存出一个没有后缀、双击打不开的文件。
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
        // Dispose 会先取消在途请求再释放 HttpClient —— 用户在导出中途关窗，
        // 请求在这里被取消，而不是等它跑完再把结果丢掉。
        _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel.SaveFilePicker = null;
        _viewModel.Dispose();

        Closed -= OnClosed;
    }
}
