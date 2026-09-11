using System.Windows;
using System.Windows.Controls;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.ViewModels;

namespace ChatlogAI.Client.Views.Modules;

/// <summary>
/// 定时发送模块（T16）。原 MainWindow 里那一栏的交互（新建 / 编辑任务）随之搬到这里。
///
/// 弹窗的 Owner 用 <see cref="Window.GetWindow"/> 现取，理由同 ChatModuleView：
/// 子模块不该反向依赖宿主窗口类型。
///
/// 本控件不设置 DataContext，绑定与命令（RelativeSource=Window）都继承 MainWindow 的 MainViewModel。
/// </summary>
public partial class ScheduledSendModuleView : UserControl
{
    public ScheduledSendModuleView() => InitializeComponent();

    private void OnScheduledTaskAddClick(object sender, RoutedEventArgs e) => OpenScheduledEditor(null);

    private void OnScheduledTaskEditClick(object sender, RoutedEventArgs e)
    {
        var task = (sender as FrameworkElement)?.DataContext is ScheduledTaskItemViewModel item
            ? item.Task
            : null;

        if (task is null)
        {
            return;
        }

        OpenScheduledEditor(task);
    }

    /// <summary>
    /// 打开任务编辑器。<paramref name="task"/> 为 <c>null</c> 表示新建。
    ///
    /// 弹窗只负责"编一条任务"，保存后的整份提交交给面板 VM：
    /// 服务端 <c>tasks</c> 是整份替换语义，两处都写必然互相覆盖。
    /// 每次打开都新建一份弹窗 VM，理由与 T9/T10 相同（表单里全是未提交的编辑中状态）。
    /// </summary>
    private void OpenScheduledEditor(ScheduledSendTask? task)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var window = new ScheduledSendWindow(viewModel.CreateScheduledSendEditorViewModel(task))
        {
            Owner = Window.GetWindow(this),
        };
        window.ShowDialog();

        if (window.SavedTask is { } saved)
        {
            // 不 await：面板内部自己兜异常（保存失败会弹提示），这里等它只会卡住 UI 线程。
            _ = viewModel.ScheduledSend.UpsertTaskAsync(saved);
        }
    }
}
