using System.Windows;
using ChatlogAI.Client.Models;
using ChatlogAI.Client.ViewModels;

namespace ChatlogAI.Client.Views;

/// <summary>
/// 「定时发送任务」编辑弹窗。
///
/// 与 AutoReplyWindow 的分工完全一致：View 只做 VM 做不了的四件事
/// （弹提示、要确认、关窗、释放 VM），表单与校验全在
/// <see cref="ScheduledSendEditorViewModel"/> 里。
///
/// <see cref="SavedTask"/> 是"用户是否保存了任务"的唯一出口：
/// 用单个属性而不是 <c>DialogResult</c>，因为关窗有三种原因
/// （保存 / 取消 / 右上角关闭），而 Task 非空本身就等价于"保存了"。
/// </summary>
public partial class ScheduledSendWindow : Window
{
    private readonly ScheduledSendEditorViewModel _viewModel;

    public ScheduledSendWindow(ScheduledSendEditorViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();

        DataContext = _viewModel;
        _viewModel.AlertRequested += OnAlertRequested;
        _viewModel.ConfirmRequested += OnConfirmRequested;
        _viewModel.SaveRequested += OnSaveRequested;
        _viewModel.CloseRequested += OnCloseRequested;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>用户保存的任务；未保存时为 <c>null</c>。主界面据此决定要不要整份提交。</summary>
    public ScheduledSendTask? SavedTask { get; private set; }

    /// <summary>
    /// 不 await：让窗口先显示，会话列表在后台拉（下拉框本来就有"正在读取"的占位）。
    /// <see cref="ScheduledSendEditorViewModel.InitializeAsync"/> 的契约是不抛异常，
    /// 因此可以安全丢弃这个 Task，而不必写成 <c>async void</c>（异常逃逸会崩进程）。
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e) => _ = _viewModel.InitializeAsync();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 「关联文档」（T-F4）：点开多选器。取消时返回 null，保持原有关联不变。
    /// 与 AutoReplyWindow 不同，这里只有一个编辑器 VM，直接取 _viewModel，无需按 DataContext 定位行。
    /// </summary>
    private void OnPickDocsClick(object sender, RoutedEventArgs e)
    {
        var selected = DocPickerWindow.Pick(this, _viewModel.Docs, _viewModel.DocIds);
        if (selected is not null)
        {
            _viewModel.SetDocIds(selected);
        }
    }

    private void OnAlertRequested(string message)
        => MessageBox.Show(this, message, "Chatlog AI", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>只有"要真实往微信发一条消息"会用到，默认按钮给 No，避免一路回车就发出去。</summary>
    private bool OnConfirmRequested(string message)
        => MessageBox.Show(
               this,
               message,
               "Chatlog AI",
               MessageBoxButton.OKCancel,
               MessageBoxImage.Question,
               MessageBoxResult.Cancel) == MessageBoxResult.OK;

    private void OnSaveRequested(ScheduledSendTask task) => SavedTask = task;

    private void OnCloseRequested() => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        // VM 的生命周期跟随本窗口：窗口关了就不该再有绑定在打它，
        // 也不能让"已经没人看的预览请求"跑完后再回来碰控件。
        _viewModel.AlertRequested -= OnAlertRequested;
        _viewModel.ConfirmRequested -= OnConfirmRequested;
        _viewModel.SaveRequested -= OnSaveRequested;
        _viewModel.CloseRequested -= OnCloseRequested;
        Loaded -= OnLoaded;
        Closed -= OnClosed;

        _viewModel.CancelPending();
    }
}
