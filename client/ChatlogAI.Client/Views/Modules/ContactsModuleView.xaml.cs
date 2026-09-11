using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.ViewModels;
using Microsoft.Win32;

namespace ChatlogAI.Client.Views.Modules;

/// <summary>
/// 联系人模块（T22）。原 T16 占位就地替换，MainWindow.xaml 不再改动。
///
/// View 只做 VM 做不了、也不该做的事：
/// 1. 在模块<b>首次可见</b>时触发一次加载（MainViewModel 构造时不预取全量通讯录）；
/// 2. 弹提示框与系统「另存为」—— 与 <c>ExportWindow</c> / <c>MainWindow</c> 同一条 MVVM 边界；
/// 3. 把回车翻译成「应用」。
///
/// 本控件不设置 DataContext，绑定与命令都继承 MainWindow 的 MainViewModel。
/// </summary>
public partial class ContactsModuleView : UserControl
{
    private ContactsModuleViewModel? _wired;

    public ContactsModuleView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private ContactsModuleViewModel? ContactsViewModel => (DataContext as MainViewModel)?.Contacts;

    private void OnLoaded(object sender, RoutedEventArgs e) => EnsureWired();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 主 VM 比窗口活得久（App 在退出时才 Dispose 它），事件必须在这里摘掉，
        // 否则窗口已关闭后 VM 再抛事件，就会打到一棵脱离可视树的控件上。
        if (_wired is { } contacts)
        {
            contacts.AlertRequested -= OnAlertRequested;
            _wired = null;
        }
    }

    /// <summary>
    /// 首次可见才加载通讯录（一次返回全部、实测 495 条，而用户可能根本不进这个模块）。
    ///
    /// 这里**只**监听 <see cref="UIElement.IsVisibleChanged"/>、不监听 Loaded：
    /// Loaded 与可见性无关，挂在它上面等于应用一启动就把通讯录拉下来，与要求正好相反。
    ///
    /// 失败路径不需要在这里额外处理：<c>EnsureLoadedAsync</c> 的"已加载"标记只在成功之后置位，
    /// 所以首次 503 之后再次进入本模块（或点「刷新」）都会重试。
    /// </summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || ContactsViewModel is not { } contacts)
        {
            return;
        }

        EnsureWired();
        _ = contacts.EnsureLoadedAsync();
    }

    /// <summary>搜索框回车 = 点「应用」：搜索是服务端过滤，不能每敲一个字符请求一次。</summary>
    private void OnKeywordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ContactsViewModel is not { } contacts)
        {
            return;
        }

        e.Handled = true;

        if (contacts.ApplyCommand.CanExecute(null))
        {
            contacts.ApplyCommand.Execute(null);
        }
    }

    /// <summary>
    /// 挂上"只有 View 能做"的两件事：弹提示框、弹系统「另存为」。
    /// 做成幂等，是为了两个入口（Loaded / 首次可见）都能安全地调一次。
    /// </summary>
    private void EnsureWired()
    {
        if (ContactsViewModel is not { } contacts)
        {
            return;
        }

        if (!ReferenceEquals(_wired, contacts))
        {
            // 事件只能在声明类型内部用 ?. 取消订阅（外部类型上 `?.Event -=` 是编译错误），
            // 所以这里显式判空。
            if (_wired is { } previous)
            {
                previous.AlertRequested -= OnAlertRequested;
            }

            contacts.AlertRequested += OnAlertRequested;
            _wired = contacts;
        }

        contacts.SaveFilePicker = PickSavePath;
    }

    private void OnAlertRequested(string message)
        => MessageBox.Show(Window.GetWindow(this), message, "Chatlog AI", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>弹系统「另存为」；用户取消返回 <c>null</c>，VM 据此直接放弃本次导出（不算失败）。</summary>
    private string? PickSavePath(ExportSaveRequest request)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出联系人",
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

        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
    }
}
