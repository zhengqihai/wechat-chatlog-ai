using System.Windows;
using ChatlogAI.Client.ViewModels;

namespace ChatlogAI.Client.Views;

/// <summary>
/// 「自动回复设置」弹窗（T10），对照 <c>app/static/index.html</c> 的 <c>#autoReplyModal</c>。
///
/// View 只做四件 VM 做不了的事（都在 MVVM 的硬边界上）：
/// 1. 把"需要弹一个提示"翻译成 <c>MessageBox</c>（对应原前端的 <c>window.alert</c>）；
/// 2. 把"需要一次确认"翻译成 <c>MessageBox.OKCancel</c> 并回传结果（对应 <c>window.confirm</c>）；
/// 3. 把"该关窗了"翻译成 <c>Close()</c>；
/// 4. 关窗时释放 VM（连带取消在途请求）。
///
/// 表单状态、勾选上限、保存与测试的全部逻辑都在 <see cref="AutoReplyViewModel"/> 里，
/// 本文件不出现任何业务流程。
/// </summary>
public partial class AutoReplyWindow : Window
{
    private readonly AutoReplyViewModel _viewModel;

    public AutoReplyWindow(AutoReplyViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();

        DataContext = _viewModel;
        _viewModel.AlertRequested += OnAlertRequested;
        _viewModel.ConfirmRequested += OnConfirmRequested;
        _viewModel.CloseRequested += OnCloseRequested;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// 是否保存成功。主界面据此决定要不要立刻回读一次配置。
    ///
    /// 与 <see cref="ExportWindow.ResultMessage"/> 同理：本窗口的关闭原因有三种
    /// （保存成功 / 点取消 / 点右上角关闭），用 <c>DialogResult</c> 只有两态，
    /// 调用方还得去猜"false 是保存失败还是用户没保存"。单独一个标志位语义唯一。
    /// </summary>
    public bool Saved { get; private set; }

    /// <summary>
    /// 不 await：让窗口先显示出来，配置与候选会话在后台拉（表单里本来就有"正在加载"的空态）。
    ///
    /// <see cref="AutoReplyViewModel.InitializeAsync"/> 的契约是"不抛异常"：
    /// 请求失败转成 <c>AlertRequested</c>，关窗取消转成提前返回。
    /// 因此这里可以安全地丢弃这个 Task，而不必写成 <c>async void</c>
    /// —— 那种写法一旦有异常逃逸会直接崩掉进程。
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e) => _ = _viewModel.InitializeAsync();

    /// <summary>「取消」：直接关窗，未保存的编辑全部丢弃（与原前端 <c>closeAutoReplyModal()</c> 一致）。</summary>
    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 「关联文档」（T-F4）：点开多选器。取消时返回 null，保持该会话原有关联不变。
    /// </summary>
    private void OnPickDocsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GuardTargetItemViewModel item })
        {
            return;
        }

        var selected = DocPickerWindow.Pick(this, _viewModel.Docs, item.DocIds);
        if (selected is not null)
        {
            item.SetDocIds(selected);
        }
    }

    private void OnAlertRequested(string message)
        => MessageBox.Show(this, message, "Chatlog AI", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>
    /// 对应原前端的 <c>window.confirm</c>：只有"要真实往微信发一条测试消息"会用到，
    /// 所以默认按钮给 <c>No</c>，避免用户一路回车就把消息发出去。
    /// </summary>
    private bool OnConfirmRequested(string message)
        => MessageBox.Show(
               this,
               message,
               "Chatlog AI",
               MessageBoxButton.OKCancel,
               MessageBoxImage.Question,
               MessageBoxResult.Cancel) == MessageBoxResult.OK;

    private void OnCloseRequested()
    {
        Saved = true;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // VM 的生命周期跟随本窗口：窗口关了就不该再有绑定在打它，
        // 也不能让"已经没人看的保存请求"跑完后再回来碰控件。
        _viewModel.AlertRequested -= OnAlertRequested;
        _viewModel.ConfirmRequested -= OnConfirmRequested;
        _viewModel.CloseRequested -= OnCloseRequested;
        Loaded -= OnLoaded;
        Closed -= OnClosed;

        _viewModel.CancelPending();
    }
}
